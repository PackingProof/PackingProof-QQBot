using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PackingProof.QqBot;

public sealed class QqClient(HttpClient http, QqBotConfiguration configuration, QqBotSecrets secrets)
{
    private readonly HttpClient _http = http;
    private readonly QqBotConfiguration _configuration = configuration;
    private readonly QqBotSecrets _secrets = secrets;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public async Task<string> GetGatewayAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/gateway", null, cancellationToken);
        using JsonDocument body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        EnsureSuccess(response, body, "获取 QQ 网关失败");
        return body.RootElement.GetProperty("url").GetString() ?? throw new InvalidDataException("QQ 网关地址为空");
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken != null && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _accessToken;
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken != null && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _accessToken;
            using HttpResponseMessage response = await _http.PostAsJsonAsync("https://api.bot.qq.com/app/getAppAccessToken", new { appId = _configuration.AppId, clientSecret = _secrets.AppSecret }, cancellationToken);
            using JsonDocument body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            EnsureSuccess(response, body, "获取 QQ Access Token 失败");
            _accessToken = body.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidDataException("QQ Access Token 为空");
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(body.RootElement.GetProperty("expires_in").GetInt32());
            return _accessToken;
        }
        finally { _tokenGate.Release(); }
    }

    public async Task SendTextAsync(string groupOpenId, string content, string? messageId, int sequence, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?> { ["msg_type"] = 0, ["content"] = content };
        if (!string.IsNullOrWhiteSpace(messageId)) { body["msg_id"] = messageId; body["msg_seq"] = sequence; }
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/v2/groups/" + Uri.EscapeDataString(groupOpenId) + "/messages", body, cancellationToken);
        using JsonDocument payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        EnsureSuccess(response, payload, "发送 QQ 群消息失败");
    }

    public async Task SendRecordingAsync(string groupOpenId, string filePath, string fileName, string videoCodec, string messageId, int sequence, CancellationToken cancellationToken)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists) throw new FileNotFoundException("待发送录像不存在", filePath);
        if (file.Length > 200L * 1024 * 1024) throw new InvalidOperationException("录像超过 QQ 单文件 200 MB 上限");
        int fileType = string.Equals(videoCodec, "h264", StringComparison.OrdinalIgnoreCase) && file.Length <= 30L * 1024 * 1024 ? 2 : 4;
        FileHashes hashes = await GetHashesAsync(filePath, cancellationToken);
        string root = "/v2/groups/" + Uri.EscapeDataString(groupOpenId);
        using HttpResponseMessage prepared = await SendAsync(HttpMethod.Post, root + "/upload_prepare", new { file_type = fileType, file_size = file.Length.ToString(), file_name = fileName, md5 = hashes.Md5, sha1 = hashes.Sha1, md5_10m = hashes.FirstTenMegabytesMd5 }, cancellationToken);
        using JsonDocument preparationBody = await JsonDocument.ParseAsync(await prepared.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        EnsureSuccess(prepared, preparationBody, "准备 QQ 录像上传失败");
        string uploadId = preparationBody.RootElement.GetProperty("upload_id").GetString() ?? throw new InvalidDataException("QQ 上传任务为空");
        foreach (JsonElement part in preparationBody.RootElement.GetProperty("parts").EnumerateArray().OrderBy(value => value.GetProperty("index").GetInt32()))
        {
            int index = part.GetProperty("index").GetInt32();
            int size = checked((int)long.Parse(part.GetProperty("block_size").GetString()!));
            string url = part.GetProperty("presigned_url").GetString() ?? throw new InvalidDataException("QQ 分片地址为空");
            byte[] bytes = await ReadPartAsync(filePath, (long)index * size, size, cancellationToken);
            using (var put = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(bytes) })
            using (HttpResponseMessage uploaded = await _http.SendAsync(put, cancellationToken))
                if (!uploaded.IsSuccessStatusCode) throw new InvalidOperationException($"上传 QQ 录像分片失败（HTTP {(int)uploaded.StatusCode}）");
            using HttpResponseMessage finished = await SendAsync(HttpMethod.Post, root + "/upload_part_finish", new { upload_id = uploadId, part_index = index, block_size = bytes.Length.ToString(), md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant() }, cancellationToken);
            if (!finished.IsSuccessStatusCode) throw new InvalidOperationException("确认 QQ 录像分片失败");
        }
        using HttpResponseMessage merged = await SendAsync(HttpMethod.Post, root + "/files", new { file_type = fileType, file_name = fileName, srv_send_msg = false, upload_id = uploadId }, cancellationToken);
        using JsonDocument mergedBody = await JsonDocument.ParseAsync(await merged.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        EnsureSuccess(merged, mergedBody, "合并 QQ 录像上传失败");
        string fileInfo = mergedBody.RootElement.GetProperty("file_info").GetString() ?? throw new InvalidDataException("QQ 文件信息为空");
        using HttpResponseMessage sent = await SendAsync(HttpMethod.Post, root + "/messages", new { msg_type = 7, msg_id = messageId, msg_seq = sequence, media = new { file_info = fileInfo } }, cancellationToken);
        using JsonDocument sentBody = await JsonDocument.ParseAsync(await sent.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        EnsureSuccess(sent, sentBody, "发送 QQ 录像失败");
    }

    public async Task RunGatewayAsync(Func<GroupMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RunGatewayConnectionAsync(handler, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) { Console.Error.WriteLine($"QQ 网关已断开：{exception.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private async Task RunGatewayConnectionAsync(Func<GroupMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(await GetGatewayAsync(cancellationToken)), cancellationToken);
        using JsonDocument hello = JsonDocument.Parse(await ReceiveAsync(socket, cancellationToken));
        if (hello.RootElement.GetProperty("op").GetInt32() != 10) throw new InvalidDataException("QQ 网关未返回 Hello");
        await SendGatewayAsync(socket, new { op = 2, d = new { token = "QQBot " + await GetAccessTokenAsync(cancellationToken), intents = 1 << 25, shard = new[] { 0, 1 }, properties = new { os = "windows" } } }, cancellationToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(hello.RootElement.GetProperty("d").GetProperty("heartbeat_interval").GetInt32()));
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeat = Task.Run(async () => { while (await timer.WaitForNextTickAsync(heartbeatCancellation.Token)) await SendGatewayAsync(socket, new { op = 1, d = (object?)null }, heartbeatCancellation.Token); });
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using JsonDocument payload = JsonDocument.Parse(await ReceiveAsync(socket, cancellationToken));
                int op = payload.RootElement.GetProperty("op").GetInt32();
                if (op is 7 or 9) throw new InvalidOperationException("QQ 网关要求重新连接");
                if (op != 0 || !payload.RootElement.TryGetProperty("t", out JsonElement type) || type.GetString() is not ("GROUP_AT_MESSAGE_CREATE" or "GROUP_MESSAGE_CREATE")) continue;
                GroupMessage? message = payload.RootElement.GetProperty("d").Deserialize<GroupMessage>();
                if (message != null) _ = Task.Run(() => handler(message, cancellationToken), CancellationToken.None);
            }
        }
        finally { heartbeatCancellation.Cancel(); try { await heartbeat; } catch (OperationCanceledException) { } }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, "https://api.bot.qq.com" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", await GetAccessTokenAsync(cancellationToken));
        if (body != null) request.Content = JsonContent.Create(body);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        await using var output = new MemoryStream();
        WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(buffer, cancellationToken); if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("QQ 关闭了网关连接"); await output.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken); } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static async Task SendGatewayAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, cancellationToken);

    private static void EnsureSuccess(HttpResponseMessage response, JsonDocument body, string fallback)
    {
        if (response.IsSuccessStatusCode) return;
        string detail = body.RootElement.TryGetProperty("message", out JsonElement message) ? message.GetString() ?? fallback : fallback;
        throw new InvalidOperationException($"{fallback}（HTTP {(int)response.StatusCode}：{detail}）");
    }

    private static async Task<byte[]> ReadPartAsync(string path, long offset, int capacity, CancellationToken cancellationToken)
    {
        var buffer = new byte[capacity];
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, capacity, FileOptions.Asynchronous);
        input.Position = offset;
        int read = 0;
        while (read < capacity)
        {
            int count = await input.ReadAsync(buffer.AsMemory(read, capacity - read), cancellationToken);
            if (count == 0) break;
            read += count;
        }
        return read == capacity ? buffer : buffer[..read];
    }

    private static async Task<FileHashes> GetHashesAsync(string path, CancellationToken cancellationToken)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using IncrementalHash sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using IncrementalHash first = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[1024 * 1024]; long remaining = 10_002_432;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.Asynchronous);
        while (true) { int count = await input.ReadAsync(buffer, cancellationToken); if (count == 0) break; md5.AppendData(buffer, 0, count); sha1.AppendData(buffer, 0, count); int take = (int)Math.Min(remaining, count); if (take > 0) first.AppendData(buffer, 0, take); remaining -= take; }
        return new FileHashes(Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(), Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(), Convert.ToHexString(first.GetHashAndReset()).ToLowerInvariant());
    }

    private sealed record FileHashes(string Md5, string Sha1, string FirstTenMegabytesMd5);
}

public sealed class GroupMessage
{
    public string Id { get; init; } = "";
    public string Content { get; init; } = "";
    [JsonPropertyName("group_openid")]
    public string GroupOpenid { get; init; } = "";
}
