using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PackingProof.QqBot;

public sealed class PackingProofClient(HttpClient http, QqBotConfiguration configuration, ExtensionCredentialState credential)
{
    private readonly HttpClient _http = http;
    private readonly QqBotConfiguration _configuration = configuration;
    private readonly ExtensionCredentialState _credential = credential;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static async Task<ExtensionCredentialState> EnrollAsync(HttpClient http, QqBotConfiguration config, CancellationToken cancellationToken)
    {
        using HttpResponseMessage capabilitiesResponse = await http.GetAsync(new Uri(config.PackingProofBaseUrl.TrimEnd('/') + "/api/extensions/v1/capabilities"), cancellationToken);
        using JsonDocument capabilities = await JsonDocument.ParseAsync(await capabilitiesResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!capabilitiesResponse.IsSuccessStatusCode || !capabilities.RootElement.GetProperty("extensionApiEnabled").GetBoolean())
            throw new InvalidOperationException("PackingProof 尚未启用扩展 API");
        JsonElement features = capabilities.RootElement.GetProperty("features");
        if (!features.GetProperty("recordingSearch").GetBoolean()
            || !features.GetProperty("recordingDownload").GetBoolean()
            || !features.GetProperty("recordingDelivery").GetBoolean())
            throw new InvalidOperationException("当前 PackingProof 不支持机器人录像查询、下载或交付副本");

        var request = new
        {
            requestId = "enroll-" + Guid.NewGuid().ToString("N"),
            requestSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            extensionInstanceId = config.ExtensionInstanceId,
            providerId = "packingproof.qq-bot",
            displayName = "PackingProof QQ 群机器人",
            version = "1.0",
            source = "PackingProof QQ bot adapter",
            requestedPermissions = new[] { "recordings.search", "recordings.download", "recordings.delivery" },
            requestedCapabilities = Array.Empty<string>()
        };
        using HttpResponseMessage response = await http.PostAsJsonAsync(new Uri(config.PackingProofBaseUrl.TrimEnd('/') + "/api/extensions/v1/enroll"), request, cancellationToken);
        using JsonDocument body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(body, "PackingProof 扩展授权失败"));
        return new ExtensionCredentialState
        {
            ExtensionInstanceId = body.RootElement.GetProperty("extensionInstanceId").GetString() ?? "",
            Credential = body.RootElement.GetProperty("credential").GetString() ?? "",
            CredentialGeneration = body.RootElement.GetProperty("credentialGeneration").GetInt32()
        };
    }

    public async Task<RecordingQuery> CreateQueryAsync(string trackingNumber, CancellationToken cancellationToken) =>
        await ReadQueryAsync(await SendAsync(HttpMethod.Post, "/api/extensions/v1/recording-queries", new { trackingNumber }, cancellationToken), cancellationToken);

    public async Task<RecordingQuery> GetQueryAsync(string queryId, CancellationToken cancellationToken) =>
        await ReadQueryAsync(await SendAsync(HttpMethod.Get, "/api/extensions/v1/recording-queries/" + Uri.EscapeDataString(queryId), null, cancellationToken), cancellationToken);

    public Task<HttpResponseMessage> DownloadAsync(string queryId, long recordingId, CancellationToken cancellationToken) => SendAsync(
        HttpMethod.Get, $"/api/extensions/v1/recording-queries/{Uri.EscapeDataString(queryId)}/recordings/{recordingId}/download", null, cancellationToken);

    public async Task<RecordingDelivery> CreateDeliveryAsync(string queryId, long recordingId, string profile, int maxFileSizeMb, CancellationToken cancellationToken) =>
        await ReadDeliveryAsync(await SendAsync(
            HttpMethod.Post,
            $"/api/extensions/v1/recording-queries/{Uri.EscapeDataString(queryId)}/recordings/{recordingId}/deliveries",
            new { profile, maxFileSizeMb },
            cancellationToken), cancellationToken);

    public async Task<RecordingDelivery> GetDeliveryAsync(string queryId, long recordingId, string deliveryId, CancellationToken cancellationToken) =>
        await ReadDeliveryAsync(await SendAsync(
            HttpMethod.Get,
            $"/api/extensions/v1/recording-queries/{Uri.EscapeDataString(queryId)}/recordings/{recordingId}/deliveries/{Uri.EscapeDataString(deliveryId)}",
            null,
            cancellationToken), cancellationToken);

    public Task<HttpResponseMessage> DownloadDeliveryAsync(string queryId, long recordingId, string deliveryId, CancellationToken cancellationToken) => SendAsync(
        HttpMethod.Get,
        $"/api/extensions/v1/recording-queries/{Uri.EscapeDataString(queryId)}/recordings/{recordingId}/deliveries/{Uri.EscapeDataString(deliveryId)}/download",
        null,
        cancellationToken);

    public async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/extensions/v1/heartbeat", new { version = "1.0", capabilities = Array.Empty<string>(), dataCount = 0 }, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("PackingProof 扩展心跳失败");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? value, CancellationToken cancellationToken)
    {
        byte[] body = value == null ? [] : JsonSerializer.SerializeToUtf8Bytes(value, _json);
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        string canonical = string.Join('\n', "packingproof-extension-request-v1", "1", _credential.CredentialGeneration.ToString(CultureInfo.InvariantCulture), method.Method.ToUpperInvariant(), path, timestamp, nonce, hash, _credential.ExtensionInstanceId);
        string signature = Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(_credential.Credential), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var request = new HttpRequestMessage(method, new Uri(_configuration.PackingProofBaseUrl.TrimEnd('/') + path));
        request.Headers.Add("X-PackingProof-Extension-Version", "1");
        request.Headers.Add("X-PackingProof-Extension-Id", _credential.ExtensionInstanceId);
        request.Headers.Add("X-PackingProof-Extension-Credential-Generation", _credential.CredentialGeneration.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-PackingProof-Extension-Timestamp", timestamp);
        request.Headers.Add("X-PackingProof-Extension-Nonce", nonce);
        request.Headers.Add("X-PackingProof-Extension-Content-SHA256", hash);
        request.Headers.Add("X-PackingProof-Extension-Signature", signature);
        if (value != null) request.Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } };
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<RecordingQuery> ReadQueryAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            using JsonDocument body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(body, "PackingProof 录像查询失败"));
            return body.RootElement.Deserialize<RecordingQuery>(_json) ?? throw new InvalidDataException("录像查询响应无效");
        }
    }

    private async Task<RecordingDelivery> ReadDeliveryAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            using JsonDocument body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(body, "PackingProof 交付副本请求失败"));
            return body.RootElement.Deserialize<RecordingDelivery>(_json) ?? throw new InvalidDataException("交付副本响应无效");
        }
    }

    private static string ReadError(JsonDocument body, string fallback) => body.RootElement.TryGetProperty("error", out JsonElement error) ? error.GetString() ?? fallback : fallback;
}

public sealed class RecordingQuery
{
    public string QueryId { get; init; } = "";
    public string Status { get; init; } = "";
    public string Message { get; init; } = "";
    public Recording[] Recordings { get; init; } = [];
}

public sealed class Recording
{
    public long RecordingId { get; init; }
    public string Status { get; init; } = "";
    public long FileSizeBytes { get; init; }
    public double DurationSeconds { get; init; }
    public string VideoCodec { get; init; } = "";
    public string FileName { get; init; } = "";
    public string? DownloadUrl { get; init; }
}

public sealed class RecordingDelivery
{
    public string DeliveryId { get; init; } = "";
    public string Status { get; init; } = "";
    public int Progress { get; init; }
    public long FileSizeBytes { get; init; }
    public double DurationSeconds { get; init; }
    public string VideoCodec { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ErrorCode { get; init; } = "";
    public string? DownloadUrl { get; init; }
}
