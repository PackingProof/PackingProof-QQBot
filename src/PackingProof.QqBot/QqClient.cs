using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
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
}

public sealed class GroupMessage
{
    public string Id { get; init; } = "";
    public string Content { get; init; } = "";
    [JsonPropertyName("group_openid")]
    public string GroupOpenid { get; init; } = "";
}
