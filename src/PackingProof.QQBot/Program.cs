using System.Collections.Concurrent;

namespace PackingProof.QQBot;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        var store = new QQBotStateStore();
        try
        {
            return args.FirstOrDefault()?.ToLowerInvariant() switch
            {
                "--background" => RunBackground(store),
                "--run" => RunBackground(store),
                "--status" => Status(store),
                _ => RunManager(store)
            };
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(exception.Message, "PackingProof QQBot", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 1;
        }
    }

    private static async Task<int> ConfigureAsync(QQBotStateStore store)
    {
        string appId = Required("QQ AppID");
        string secret = Required("QQ AppSecret", hidden: true);
        string host = Optional("PackingProof 地址", "http://127.0.0.1:5280").TrimEnd('/');
        if (!Uri.TryCreate(host, UriKind.Absolute, out _)) throw new InvalidDataException("PackingProof 地址无效");
        await SaveConfigurationAsync(store, appId, secret, host, CancellationToken.None);
        Console.WriteLine("配置已保存，机器人会在打开 QQBot 后自动启动。可在私聊中发送单号，也可在目标群 @机器人发送单号");
        return 0;
    }

    internal static async Task SaveConfigurationAsync(QQBotStateStore store, string appId, string secret, string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appId)) throw new InvalidDataException("QQ AppID 不能为空");
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidDataException("QQ AppSecret 不能为空");
        if (!Uri.TryCreate(host, UriKind.Absolute, out _)) throw new InvalidDataException("PackingProof 地址无效");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        PackingProofHostInfo? packingProofHost = await new PackingProofHostDiscovery(http).ProbeAsync(host, CancellationToken.None);
        if (packingProofHost == null) throw new InvalidOperationException("无法确认 PackingProof 主机，请检查地址、局域网连接和主程序版本");
        QQBotConfiguration config = CreateConfiguration(store.LoadConfiguration(), appId, packingProofHost);
        ExtensionCredentialState credential = await PackingProofClient.EnrollAsync(http, config, cancellationToken);
        store.Save(config, new QQBotSecrets { AppSecret = secret, ExtensionCredential = credential });
    }

    internal static QQBotConfiguration CreateConfiguration(QQBotConfiguration? existing, string appId, PackingProofHostInfo packingProofHost) =>
        new QQBotConfiguration
        {
            AppId = appId,
            PackingProofBaseUrl = packingProofHost.BaseUrl,
            PackingProofNodeId = packingProofHost.NodeId,
            PackingProofNodeName = packingProofHost.NodeName,
            ExtensionInstanceId = string.IsNullOrWhiteSpace(existing?.ExtensionInstanceId) ? "qqbot-" + Guid.NewGuid().ToString("N") : existing.ExtensionInstanceId,
            AllowedGroupOpenIds = existing?.AllowedGroupOpenIds ?? [],
            KnownGroups = existing?.KnownGroups ?? [],
            DeliveryMaxSizeMb = existing?.DeliveryMaxSizeMb ?? QQBotConfiguration.DefaultDeliveryMaxSizeMb,
            DeliveryProfile = existing?.DeliveryProfile ?? QQBotConfiguration.SourceCodecTargetSizeProfile,
            StartWithWindows = existing?.StartWithWindows ?? false
        }.ValidateDeliverySettings();

    private static async Task<int> StartAsync(QQBotStateStore store)
    {
        if (store.LoadConfiguration() == null || store.LoadSecrets() == null)
        {
            Console.WriteLine("首次使用，请按提示完成机器人配置");
            await ConfigureAsync(store);
        }
        return await RunAsync(store);
    }

    private static int AllowGroup(QQBotStateStore store, string? group)
    {
        group = string.IsNullOrWhiteSpace(group) ? Required("群 OpenID") : group.Trim();
        QQBotConfiguration config = Config(store); QQBotSecrets secrets = Secrets(store);
        store.Save(config with { AllowedGroupOpenIds = config.AllowedGroupOpenIds.Append(group).Distinct(StringComparer.Ordinal).Order().ToArray() }, secrets);
        Console.WriteLine("已加入群白名单"); return 0;
    }

    private static int ConfigureDeliverySettings(QQBotStateStore store)
    {
        QQBotConfiguration config = Config(store);
        QQBotSecrets secrets = Secrets(store);
        Console.WriteLine("视频发送设置：原片不超过限制时直接发送，超限时由 PackingProof 主机生成临时副本");
        int size = ReadInt("单个视频最大大小（MB）", config.DeliveryMaxSizeMb, QQBotConfiguration.MinimumDeliveryMaxSizeMb, QQBotConfiguration.MaximumDeliveryMaxSizeMb);
        Console.WriteLine("1. 优先保持原视频编码（推荐）\n2. 超限时转为 H.265，体积更小");
        string selection = Optional("请输入 1 或 2", config.DeliveryProfile == QQBotConfiguration.H265TargetSizeProfile ? "2" : "1");
        string profile = selection == "2" ? QQBotConfiguration.H265TargetSizeProfile : QQBotConfiguration.SourceCodecTargetSizeProfile;
        store.Save((config with { DeliveryMaxSizeMb = size, DeliveryProfile = profile }).ValidateDeliverySettings(), secrets);
        Console.WriteLine($"已保存：最大 {size} MB，{(profile == QQBotConfiguration.H265TargetSizeProfile ? "转 H.265" : "保持原视频编码")}");
        return 0;
    }

    internal static async Task<int> RunAsync(QQBotStateStore store, CancellationToken cancellationToken = default)
    {
        QQBotConfiguration config = Config(store); QQBotSecrets secrets = Secrets(store);
        if (secrets.ExtensionCredential == null) throw new InvalidOperationException("缺少扩展凭据，请重新运行 --configure");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        config = await ResolvePackingProofHostAsync(store, config, secrets, http, cancellationToken);
        var service = new QueryService(config, new PackingProofClient(http, config, secrets.ExtensionCredential), new QQClient(http, config, secrets), store);
        Console.WriteLine("QQ 机器人已启动，按 Ctrl+C 停止");
        await service.RunAsync(cancellationToken);
        return 0;
    }

    private static int Status(QQBotStateStore store) { QQBotConfiguration c = Config(store); Console.WriteLine($"PackingProof 地址：{c.PackingProofBaseUrl}\n允许群数量：{c.AllowedGroupOpenIds.Length}\n交付策略：{c.DeliveryProfile}\n交付上限：{c.DeliveryMaxSizeMb} MB\n状态目录：{store.DirectoryPath}"); return 0; }
    internal static QQBotConfiguration Config(QQBotStateStore store) => (store.LoadConfiguration() ?? throw new InvalidOperationException("尚未配置，请先在管理界面中保存并授权")).ValidateDeliverySettings();
    internal static QQBotSecrets Secrets(QQBotStateStore store) => store.LoadSecrets() ?? throw new InvalidOperationException("缺少受保护密钥，请先在管理界面中重新授权");

    internal static async Task<QQBotConfiguration> ResolvePackingProofHostAsync(QQBotStateStore store, QQBotConfiguration config, QQBotSecrets secrets, HttpClient http, CancellationToken cancellationToken)
    {
        var discovery = new PackingProofHostDiscovery(http);
        PackingProofHostInfo? current = await discovery.ProbeAsync(config.PackingProofBaseUrl, cancellationToken);
        if (current == null && !string.IsNullOrWhiteSpace(config.PackingProofNodeId))
        {
            QQBotLog.Write("PackingProof 地址不可用，正在局域网查找原来的主机");
            current = await discovery.FindByNodeIdAsync(config.PackingProofNodeId, config.PackingProofBaseUrl, cancellationToken);
        }
        if (current == null) throw new InvalidOperationException("无法连接 PackingProof 主机，请确认主程序正在运行且与机器人在同一局域网");
        if (!string.IsNullOrWhiteSpace(config.PackingProofNodeId)
            && !string.Equals(config.PackingProofNodeId, current.NodeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前地址不是已授权的 PackingProof 主机，为保护授权凭据未自动切换");
        QQBotConfiguration resolved = config with { PackingProofBaseUrl = current.BaseUrl, PackingProofNodeId = current.NodeId, PackingProofNodeName = current.NodeName };
        if (resolved != config) store.Save(resolved, secrets);
        return resolved;
    }
    private static string Optional(string label, string defaultValue) { Console.Write($"{label}（默认 {defaultValue}）："); return Console.ReadLine()?.Trim() is { Length: > 0 } value ? value : defaultValue; }
    private static int ReadInt(string label, int defaultValue, int minimum, int maximum)
    {
        string value = Optional(label, defaultValue.ToString());
        if (!int.TryParse(value, out int result) || result < minimum || result > maximum)
            throw new InvalidDataException($"{label} 必须在 {minimum} 到 {maximum} 之间");
        return result;
    }
    private static string Required(string label, bool hidden = false)
    {
        Console.Write(label + "：");
        if (!hidden) return Console.ReadLine()?.Trim() is { Length: > 0 } value ? value : throw new InvalidDataException(label + "不能为空");
        var chars = new List<char>(); ConsoleKeyInfo key;
        while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter) { if (key.Key == ConsoleKey.Backspace && chars.Count > 0) chars.RemoveAt(chars.Count - 1); else if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar); }
        Console.WriteLine(); return chars.Count > 0 ? new string(chars.ToArray()) : throw new InvalidDataException(label + "不能为空");
    }
    private static int RunManager(QQBotStateStore store)
    {
        using var singleInstance = new QQBotSingleInstance();
        if (!singleInstance.TryAcquire())
        {
            QQBotSingleInstance.TryActivateExisting();
            return 0;
        }
        using var host = new QQBotApplicationHost(store, false, singleInstance.ActivationEvent);
        return host.Run();
    }

    private static int RunBackground(QQBotStateStore store)
    {
        using var singleInstance = new QQBotSingleInstance();
        if (!singleInstance.TryAcquire())
        {
            QQBotSingleInstance.TryActivateExisting();
            return 0;
        }
        using var host = new QQBotApplicationHost(store, true, singleInstance.ActivationEvent);
        return host.Run();
    }
}

internal sealed class QueryService(QQBotConfiguration config, PackingProofClient packingProof, QQClient qq, QQBotStateStore? store = null)
{
    private const int RecordingsPerReplyBatch = 3;
    private readonly ConcurrentDictionary<string, byte> _handled = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingRecordingBatch> _pendingBatches = new(StringComparer.Ordinal);

    public Task RunAsync(CancellationToken cancellationToken) => qq.RunGatewayAsync(HandleAsync, cancellationToken);

    internal async Task HandleAsync(QQIncomingMessage message, CancellationToken cancellationToken)
    {
        if (!_handled.TryAdd(message.Id, 0)) return;
        QQBotConfiguration currentConfig = message.IsGroup && store != null
            ? store.RecordGroupSeen(message.RecipientOpenid, DateTimeOffset.UtcNow)
            : config;
        if (message.IsGroup && !currentConfig.AllowedGroupOpenIds.Contains(message.RecipientOpenid, StringComparer.Ordinal))
        {
            QQBotLog.Write($"群尚未允许，已在管理器中显示：{message.RecipientOpenid}");
            await qq.SendTextAsync(
                message,
                "已收到群消息，但这个群尚未允许使用。请在 QQBot 管理器的“发现的 QQ 群”中开启",
                message.Id,
                1,
                cancellationToken);
            return;
        }
        if (string.Equals(message.Content.Trim(), "继续", StringComparison.Ordinal))
        {
            await ContinueAsync(message, cancellationToken);
            return;
        }
        if (!TrackingNumberParser.TryParse(message.Content, out string number))
        {
            if (!message.IsGroup)
                await qq.SendTextAsync(message, "请直接发送完整快递单号，例如 SF1234567890", message.Id, 1, cancellationToken);
            return;
        }
        RecordingQuery query = await packingProof.CreateQueryAsync(number, cancellationToken);
        while (query.Status is "queued" or "searching" or "preparing") { await Task.Delay(1000, cancellationToken); query = await packingProof.GetQueryAsync(query.QueryId, cancellationToken); }
        if (query.Status == "not_found") { await qq.SendTextAsync(message, $"未找到单号 {number} 的关联录像", message.Id, 1, cancellationToken); return; }
        if (query.Status is not ("ready" or "completed")) { await qq.SendTextAsync(message, string.IsNullOrWhiteSpace(query.Message) ? "录像查询失败" : query.Message, message.Id, 1, cancellationToken); return; }
        Recording[] recordings = GetSendableRecordings(query);
        if (recordings.Length == 0)
        {
            await qq.SendTextAsync(message, "已找到录像，但当前没有可发送的文件，请稍后重试", message.Id, 1, cancellationToken);
            return;
        }

        int nextIndex = await SendRecordingBatchAsync(message, number, query, recordings, 0, cancellationToken);
        SavePendingBatch(message, number, query.QueryId, recordings.Length, nextIndex);
    }

    private async Task ContinueAsync(QQIncomingMessage message, CancellationToken cancellationToken)
    {
        string conversationKey = ConversationKey(message);
        if (!_pendingBatches.TryGetValue(conversationKey, out PendingRecordingBatch? pending))
        {
            if (!message.IsGroup)
                await qq.SendTextAsync(message, "没有待继续发送的录像，请重新发送单号", message.Id, 1, cancellationToken);
            return;
        }

        RecordingQuery query = await packingProof.GetQueryAsync(pending.QueryId, cancellationToken);
        if (query.Status is not ("ready" or "completed"))
        {
            _pendingBatches.TryRemove(conversationKey, out _);
            await qq.SendTextAsync(message, "待发送录像已失效，请重新发送单号", message.Id, 1, cancellationToken);
            return;
        }

        Recording[] recordings = GetSendableRecordings(query);
        if (pending.NextIndex >= recordings.Length)
        {
            _pendingBatches.TryRemove(conversationKey, out _);
            await qq.SendTextAsync(message, "录像已经全部发送完毕", message.Id, 1, cancellationToken);
            return;
        }

        int nextIndex = await SendRecordingBatchAsync(message, pending.TrackingNumber, query, recordings, pending.NextIndex, cancellationToken);
        SavePendingBatch(message, pending.TrackingNumber, pending.QueryId, recordings.Length, nextIndex);
    }

    private async Task<int> SendRecordingBatchAsync(
        QQIncomingMessage message,
        string trackingNumber,
        RecordingQuery query,
        IReadOnlyList<Recording> recordings,
        int startIndex,
        CancellationToken cancellationToken)
    {
        Recording[] batch = recordings.Skip(startIndex).Take(RecordingsPerReplyBatch).ToArray();
        int nextIndex = startIndex + batch.Length;
        int remainingCount = recordings.Count - nextIndex;
        await qq.SendTextAsync(message, BuildRecordingSummary(trackingNumber, query, batch, config.DeliveryMaxSizeMb, remainingCount), message.Id, 1, cancellationToken);
        int sequence = 2;
        foreach (Recording recording in batch)
        {
            await SendRecordingAsync(message, query, trackingNumber, recording, sequence++, cancellationToken);
        }
        return nextIndex;
    }

    private async Task SendRecordingAsync(QQIncomingMessage message, RecordingQuery query, string trackingNumber, Recording recording, int sequence, CancellationToken cancellationToken)
    {
        bool requiresDelivery = recording.FileSizeBytes > config.DeliveryMaxSizeMb * 1024L * 1024L;
        RecordingDelivery? delivery = null;
        string fileName = NormalizeFileName(recording.FileName, trackingNumber + ".mp4");
        string videoCodec = recording.VideoCodec;
        string downloadKind = "录像";
        try
        {
            if (requiresDelivery)
            {
                delivery = await packingProof.CreateDeliveryAsync(query.QueryId, recording.RecordingId, config.DeliveryProfile, config.DeliveryMaxSizeMb, cancellationToken);
                while (delivery.Status is "queued" or "transcoding" or "downloading")
                {
                    await Task.Delay(1000, cancellationToken);
                    delivery = await packingProof.GetDeliveryAsync(query.QueryId, recording.RecordingId, delivery.DeliveryId, cancellationToken);
                }
                if (delivery.Status is not ("ready" or "completed"))
                {
                    await qq.SendTextAsync(message, DeliveryFailureText(delivery.ErrorCode), message.Id, sequence, cancellationToken);
                    return;
                }
                fileName = NormalizeFileName(delivery.FileName, trackingNumber + "_转码.mp4");
                videoCodec = delivery.VideoCodec;
                downloadKind = "交付副本";
            }

            string extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".mp4";
            string temporary = Path.Combine(Path.GetTempPath(), "PackingProof-QQBot-" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                using HttpResponseMessage download = delivery == null
                    ? await packingProof.DownloadAsync(query.QueryId, recording.RecordingId, cancellationToken)
                    : await packingProof.DownloadDeliveryAsync(query.QueryId, recording.RecordingId, delivery.DeliveryId, cancellationToken);
                if (!download.IsSuccessStatusCode) throw new InvalidOperationException("下载录像失败");
                await using Stream source = await download.Content.ReadAsStreamAsync(cancellationToken);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous)) await source.CopyToAsync(output, cancellationToken);
                await qq.SendRecordingAsync(message, temporary, fileName, videoCodec, message.Id, sequence, cancellationToken);
            }
            finally { try { File.Delete(temporary); } catch { } }
        }
        catch (Exception exception)
        {
            QQBotLog.Write($"转发{downloadKind}失败：{exception.Message}");
            await qq.SendTextAsync(message, "录像已找到，但转发到 QQ 失败，请稍后重试", message.Id, sequence, cancellationToken);
        }
    }

    private static Recording[] GetSendableRecordings(RecordingQuery query) => query.Recordings
        .Where(item => item.Status is "ready" or "completed" && item.DownloadUrl != null)
        .ToArray();

    private void SavePendingBatch(QQIncomingMessage message, string trackingNumber, string queryId, int recordingCount, int nextIndex)
    {
        string conversationKey = ConversationKey(message);
        if (nextIndex < recordingCount)
            _pendingBatches[conversationKey] = new PendingRecordingBatch(trackingNumber, queryId, nextIndex);
        else
            _pendingBatches.TryRemove(conversationKey, out _);
    }

    private static string ConversationKey(QQIncomingMessage message) => (message.IsGroup ? "group:" : "user:") + message.RecipientOpenid;

    private static string NormalizeFileName(string value, string fallback)
    {
        string name = Path.GetFileName(value?.Trim() ?? "");
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private static string FormatMegabytes(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";
    private static string FormatDuration(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss");

    internal static string BuildRecordingSummary(
        string trackingNumber,
        RecordingQuery query,
        IReadOnlyList<Recording> recordings,
        int deliveryMaxSizeMb,
        int remainingCount = 0)
    {
        int total = Math.Max(query.TotalMatches, query.Recordings.Length);
        string prefix = $"单号 {trackingNumber} 找到 {total} 段录像";
        if (total > recordings.Count || query.Truncated)
            prefix += $"，本次发送 {recordings.Count} 段";
        if (remainingCount > 0)
            prefix += $"，还剩 {remainingCount} 段。回复“继续”即可发送下一批";

        var lines = new List<string> { prefix + "：" };
        for (int index = 0; index < recordings.Count; index++)
        {
            Recording recording = recordings[index];
            string recordedAt = recording.RecordedAt == default
                ? "时间未知"
                : recording.RecordedAt.ToString("MM-dd HH:mm");
            string delivery = recording.FileSizeBytes > deliveryMaxSizeMb * 1024L * 1024L
                ? "将生成交付副本后发送"
                : "准备发送原片";
            lines.Add($"{index + 1}. {recordedAt}｜{FormatDuration(recording.DurationSeconds)}｜{FormatMegabytes(recording.FileSizeBytes)}｜{delivery}");
        }
        return string.Join('\n', lines);
    }

    private static string DeliveryFailureText(string errorCode) => errorCode switch
    {
        "delivery_size_limit_unreachable" => "录像已找到，但在不切割的前提下无法压入当前大小限制",
        "delivery_ffmpeg_unavailable" => "录像已找到，但主机未找到 FFmpeg，无法生成交付副本",
        "delivery_profile_unsupported" => "录像已找到，但当前交付预设不支持该录像编码",
        "delivery_cache_limit_exceeded" => "录像已找到，但主机转码缓存空间不足",
        "delivery_duration_unavailable" => "录像已找到，但缺少有效时长，无法计算目标码率",
        _ => "录像已找到，但生成交付副本失败，请稍后重试"
    };

    private sealed record PendingRecordingBatch(string TrackingNumber, string QueryId, int NextIndex);
}
