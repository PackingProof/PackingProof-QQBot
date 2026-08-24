using System.Collections.Concurrent;
using System.Text;

namespace PackingProof.QQBot;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("此适配器只能在 Windows 上运行"); return 1; }
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        Console.Error.OutputEncoding = Encoding.UTF8;
        var store = new QQBotStateStore();
        try
        {
            return args.FirstOrDefault()?.ToLowerInvariant() switch
            {
                "--configure" => await ConfigureAsync(store),
                "--delivery-settings" => ConfigureDeliverySettings(store),
                "--allow-group" => AllowGroup(store, args.Skip(1).FirstOrDefault()),
                "--run" => await RunAsync(store),
                "--status" => Status(store),
                _ => Usage()
            };
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
    }

    private static async Task<int> ConfigureAsync(QQBotStateStore store)
    {
        string appId = Required("QQ AppID");
        string secret = Required("QQ AppSecret", hidden: true);
        string host = Optional("PackingProof 地址", "http://127.0.0.1:5280").TrimEnd('/');
        if (!Uri.TryCreate(host, UriKind.Absolute, out _)) throw new InvalidDataException("PackingProof 地址无效");
        var config = new QQBotConfiguration { AppId = appId, PackingProofBaseUrl = host, ExtensionInstanceId = "qqbot-" + Guid.NewGuid().ToString("N") };
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        Console.WriteLine("请在 PackingProof 弹出的窗口批准录像查询、下载和交付副本权限");
        ExtensionCredentialState credential = await PackingProofClient.EnrollAsync(http, config, CancellationToken.None);
        store.Save(config, new QQBotSecrets { AppSecret = secret, ExtensionCredential = credential });
        Console.WriteLine("配置已保存。接下来双击“启动机器人”，在目标群 @机器人发送一个单号；控制台会显示加群白名单所需的群 OpenID");
        return 0;
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

    private static async Task<int> RunAsync(QQBotStateStore store)
    {
        QQBotConfiguration config = Config(store); QQBotSecrets secrets = Secrets(store);
        if (secrets.ExtensionCredential == null) throw new InvalidOperationException("缺少扩展凭据，请重新运行 --configure");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        using var cancelled = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancelled.Cancel(); };
        var service = new QueryService(config, new PackingProofClient(http, config, secrets.ExtensionCredential), new QQClient(http, config, secrets));
        Console.WriteLine("QQ 机器人已启动，按 Ctrl+C 停止");
        await service.RunAsync(cancelled.Token);
        return 0;
    }

    private static int Status(QQBotStateStore store) { QQBotConfiguration c = Config(store); Console.WriteLine($"PackingProof 地址：{c.PackingProofBaseUrl}\n允许群数量：{c.AllowedGroupOpenIds.Length}\n交付策略：{c.DeliveryProfile}\n交付上限：{c.DeliveryMaxSizeMb} MB\n状态目录：{store.DirectoryPath}"); return 0; }
    private static QQBotConfiguration Config(QQBotStateStore store) => (store.LoadConfiguration() ?? throw new InvalidOperationException("尚未配置，请先运行 --configure")).ValidateDeliverySettings();
    private static QQBotSecrets Secrets(QQBotStateStore store) => store.LoadSecrets() ?? throw new InvalidOperationException("缺少受保护密钥，请重新运行 --configure");
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
    private static int Usage() { Console.WriteLine("请使用发布包中的“配置机器人”“启动机器人”“添加群白名单”和“视频发送设置”快捷方式"); return 0; }
}

internal sealed class QueryService(QQBotConfiguration config, PackingProofClient packingProof, QQClient qq)
{
    private readonly ConcurrentDictionary<string, byte> _handled = new(StringComparer.Ordinal);
    public Task RunAsync(CancellationToken cancellationToken) => qq.RunGatewayAsync(HandleAsync, cancellationToken);
    private async Task HandleAsync(GroupMessage message, CancellationToken cancellationToken)
    {
        if (!_handled.TryAdd(message.Id, 0)) return;
        if (!config.AllowedGroupOpenIds.Contains(message.GroupOpenid, StringComparer.Ordinal))
        {
            Console.WriteLine($"发现未授权群。请关闭机器人后双击“添加群白名单”，并粘贴此群 OpenID：{message.GroupOpenid}");
            return;
        }
        if (!TrackingNumberParser.TryParse(message.Content, out string number)) return;
        await qq.SendTextAsync(message.GroupOpenid, $"正在查询单号 {number} 的录像", message.Id, 1, cancellationToken);
        RecordingQuery query = await packingProof.CreateQueryAsync(number, cancellationToken);
        while (query.Status is "queued" or "searching" or "preparing") { await Task.Delay(1000, cancellationToken); query = await packingProof.GetQueryAsync(query.QueryId, cancellationToken); }
        if (query.Status == "not_found") { await qq.SendTextAsync(message.GroupOpenid, $"未找到单号 {number} 的精确匹配录像", message.Id, 2, cancellationToken); return; }
        if (query.Status is not ("ready" or "completed")) { await qq.SendTextAsync(message.GroupOpenid, string.IsNullOrWhiteSpace(query.Message) ? "录像查询失败" : query.Message, message.Id, 2, cancellationToken); return; }
        int sequence = 2;
        foreach (Recording recording in query.Recordings.Where(item => item.Status == "ready" && item.DownloadUrl != null).Take(3))
        {
            bool requiresDelivery = recording.FileSizeBytes > config.DeliveryMaxSizeMb * 1024L * 1024L;
            RecordingDelivery? delivery = null;
            string fileName = NormalizeFileName(recording.FileName, number + ".mp4");
            string videoCodec = recording.VideoCodec;
            string downloadKind = "录像";
            try
            {
                if (requiresDelivery)
                {
                    await qq.SendTextAsync(message.GroupOpenid, $"录像时长 {FormatDuration(recording.DurationSeconds)}，原片 {FormatMegabytes(recording.FileSizeBytes)}，正在生成不超过 {config.DeliveryMaxSizeMb} MB 的交付副本", message.Id, sequence++, cancellationToken);
                    delivery = await packingProof.CreateDeliveryAsync(query.QueryId, recording.RecordingId, config.DeliveryProfile, config.DeliveryMaxSizeMb, cancellationToken);
                    while (delivery.Status is "queued" or "transcoding" or "downloading")
                    {
                        await Task.Delay(1000, cancellationToken);
                        delivery = await packingProof.GetDeliveryAsync(query.QueryId, recording.RecordingId, delivery.DeliveryId, cancellationToken);
                    }
                    if (delivery.Status is not ("ready" or "completed"))
                    {
                        await qq.SendTextAsync(message.GroupOpenid, DeliveryFailureText(delivery.ErrorCode), message.Id, sequence++, cancellationToken);
                        continue;
                    }
                    fileName = NormalizeFileName(delivery.FileName, number + "_转码.mp4");
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
                    await qq.SendRecordingAsync(message.GroupOpenid, temporary, fileName, videoCodec, message.Id, sequence++, cancellationToken);
                }
                finally { try { File.Delete(temporary); } catch { } }
            }
            catch (Exception exception) { Console.Error.WriteLine($"转发{downloadKind}失败：{exception.Message}"); await qq.SendTextAsync(message.GroupOpenid, "录像已找到，但转发到 QQ 失败，请稍后重试", message.Id, sequence++, cancellationToken); }
        }
    }

    private static string NormalizeFileName(string value, string fallback)
    {
        string name = Path.GetFileName(value?.Trim() ?? "");
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private static string FormatMegabytes(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";
    private static string FormatDuration(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss");

    private static string DeliveryFailureText(string errorCode) => errorCode switch
    {
        "delivery_size_limit_unreachable" => "录像已找到，但在不切割的前提下无法压入当前大小限制",
        "delivery_ffmpeg_unavailable" => "录像已找到，但主机未找到 FFmpeg，无法生成交付副本",
        "delivery_profile_unsupported" => "录像已找到，但当前交付预设不支持该录像编码",
        "delivery_cache_limit_exceeded" => "录像已找到，但主机转码缓存空间不足",
        "delivery_duration_unavailable" => "录像已找到，但缺少有效时长，无法计算目标码率",
        _ => "录像已找到，但生成交付副本失败，请稍后重试"
    };
}
