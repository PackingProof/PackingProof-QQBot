using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PackingProof.QQBot;

public sealed class PackingProofClient(
    HttpClient http,
    QQBotConfiguration configuration,
    ExtensionCredentialState credential,
    Func<CancellationToken, Task<QQBotConfiguration?>>? recoverConfiguration = null)
{
    private readonly HttpClient _http = http;
    private QQBotConfiguration _configuration = configuration;
    private readonly ExtensionCredentialState _credential = credential;
    private readonly Func<CancellationToken, Task<QQBotConfiguration?>>? _recoverConfiguration = recoverConfiguration;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static async Task<ExtensionCredentialState> EnrollAsync(HttpClient http, QQBotConfiguration config, CancellationToken cancellationToken)
    {
        Uri capabilitiesUri = new(config.PackingProofBaseUrl.TrimEnd('/') + "/api/extensions/v1/capabilities");
        HttpResponseMessage capabilitiesResponse;
        try { capabilitiesResponse = await http.GetAsync(capabilitiesUri, cancellationToken); }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"无法连接 PackingProof（{config.PackingProofBaseUrl}），请确认地址和主程序的 Web 服务", exception);
        }
        using (capabilitiesResponse)
        {
            if (!capabilitiesResponse.IsSuccessStatusCode)
                throw new InvalidOperationException(await ReadCapabilitiesFailureAsync(capabilitiesResponse, cancellationToken));

            using JsonDocument capabilities = await JsonDocument.ParseAsync(await capabilitiesResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!capabilities.RootElement.TryGetProperty("extensionApiEnabled", out JsonElement extensionApiEnabled)
                || !extensionApiEnabled.GetBoolean())
                throw new InvalidOperationException("PackingProof 尚未启用扩展 API");
            if (!capabilities.RootElement.TryGetProperty("features", out JsonElement features)
                || !features.TryGetProperty("recordingSearch", out JsonElement recordingSearch)
                || !recordingSearch.GetBoolean()
                || !features.TryGetProperty("recordingDownload", out JsonElement recordingDownload)
                || !recordingDownload.GetBoolean()
                || !features.TryGetProperty("recordingDelivery", out JsonElement recordingDelivery)
                || !recordingDelivery.GetBoolean())
                throw new InvalidOperationException("当前 PackingProof 不支持机器人录像查询、下载或交付副本");
        }

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
        try
        {
            return await SendOnceAsync(method, path, value, cancellationToken);
        }
        catch (HttpRequestException) when (_recoverConfiguration != null)
        {
            QQBotConfiguration? recovered = await _recoverConfiguration(cancellationToken);
            if (recovered == null) throw;
            _configuration = recovered;
            return await SendOnceAsync(method, path, value, cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? value, CancellationToken cancellationToken)
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

    private static async Task<string> ReadCapabilitiesFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
            return "目标地址未提供扩展 API，请确认 PackingProof 地址和主程序版本";

        string detail = "";
        try
        {
            using JsonDocument body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            detail = ReadError(body, "");
        }
        catch (JsonException) { }

        return string.IsNullOrWhiteSpace(detail)
            ? $"读取 PackingProof 扩展能力失败（HTTP {(int)response.StatusCode}）"
            : $"读取 PackingProof 扩展能力失败（HTTP {(int)response.StatusCode}：{detail}）";
    }
}

public sealed class RecordingQuery
{
    public string QueryId { get; init; } = "";
    public string Status { get; init; } = "";
    public string Message { get; init; } = "";
    public int TotalMatches { get; init; }
    public bool Truncated { get; init; }
    public Recording[] Recordings { get; init; } = [];
}

public sealed class Recording
{
    public long RecordingId { get; init; }
    public string Status { get; init; } = "";
    public DateTime RecordedAt { get; init; }
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
