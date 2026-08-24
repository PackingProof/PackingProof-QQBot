using System.Collections.Concurrent;

namespace PackingProof.QqBot;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("此适配器只能在 Windows 上运行"); return 1; }
        var store = new QqBotStateStore();
        try
        {
            return args.FirstOrDefault()?.ToLowerInvariant() switch
            {
                "--configure" => await ConfigureAsync(store),
                "--allow-group" => AllowGroup(store, args.Skip(1).FirstOrDefault()),
                "--run" => await RunAsync(store),
                "--status" => Status(store),
                _ => Usage()
            };
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
    }

    private static async Task<int> ConfigureAsync(QqBotStateStore store)
    {
        string appId = Required("QQ AppID");
        string secret = Required("QQ AppSecret", hidden: true);
        string host = Optional("PackingProof 地址", "http://127.0.0.1:5280").TrimEnd('/');
        if (!Uri.TryCreate(host, UriKind.Absolute, out _)) throw new InvalidDataException("PackingProof 地址无效");
        var config = new QqBotConfiguration { AppId = appId, PackingProofBaseUrl = host, ExtensionInstanceId = "qqbot-" + Guid.NewGuid().ToString("N") };
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        Console.WriteLine("请在 PackingProof 弹出的窗口批准录像查询和下载权限");
        ExtensionCredentialState credential = await PackingProofClient.EnrollAsync(http, config, CancellationToken.None);
        store.Save(config, new QqBotSecrets { AppSecret = secret, ExtensionCredential = credential });
        Console.WriteLine("配置已加密保存。启动后在目标群 @机器人发送单号，再用控制台显示的群 OpenID 加白名单");
        return 0;
    }

    private static int AllowGroup(QqBotStateStore store, string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) throw new ArgumentException("用法：--allow-group <群 OpenID>");
        QqBotConfiguration config = Config(store); QqBotSecrets secrets = Secrets(store);
        store.Save(config with { AllowedGroupOpenIds = config.AllowedGroupOpenIds.Append(group.Trim()).Distinct(StringComparer.Ordinal).Order().ToArray() }, secrets);
        Console.WriteLine("已加入群白名单"); return 0;
    }

    private static async Task<int> RunAsync(QqBotStateStore store)
    {
        QqBotConfiguration config = Config(store); QqBotSecrets secrets = Secrets(store);
        if (secrets.ExtensionCredential == null) throw new InvalidOperationException("缺少扩展凭据，请重新运行 --configure");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        using var cancelled = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancelled.Cancel(); };
        var service = new QueryService(config, new PackingProofClient(http, config, secrets.ExtensionCredential), new QqClient(http, config, secrets));
        Console.WriteLine("QQ 机器人已启动，按 Ctrl+C 停止");
        await service.RunAsync(cancelled.Token);
        return 0;
    }

    private static int Status(QqBotStateStore store) { QqBotConfiguration c = Config(store); Console.WriteLine($"PackingProof 地址：{c.PackingProofBaseUrl}\n允许群数量：{c.AllowedGroupOpenIds.Length}\n状态目录：{store.DirectoryPath}"); return 0; }
    private static QqBotConfiguration Config(QqBotStateStore store) => store.LoadConfiguration() ?? throw new InvalidOperationException("尚未配置，请先运行 --configure");
    private static QqBotSecrets Secrets(QqBotStateStore store) => store.LoadSecrets() ?? throw new InvalidOperationException("缺少受保护密钥，请重新运行 --configure");
    private static string Optional(string label, string defaultValue) { Console.Write($"{label}（默认 {defaultValue}）："); return Console.ReadLine()?.Trim() is { Length: > 0 } value ? value : defaultValue; }
    private static string Required(string label, bool hidden = false)
    {
        Console.Write(label + "：");
        if (!hidden) return Console.ReadLine()?.Trim() is { Length: > 0 } value ? value : throw new InvalidDataException(label + "不能为空");
        var chars = new List<char>(); ConsoleKeyInfo key;
        while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter) { if (key.Key == ConsoleKey.Backspace && chars.Count > 0) chars.RemoveAt(chars.Count - 1); else if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar); }
        Console.WriteLine(); return chars.Count > 0 ? new string(chars.ToArray()) : throw new InvalidDataException(label + "不能为空");
    }
    private static int Usage() { Console.WriteLine("--configure | --run | --allow-group <群 OpenID> | --status"); return 0; }
}

internal sealed class QueryService(QqBotConfiguration config, PackingProofClient packingProof, QqClient qq)
{
    private readonly ConcurrentDictionary<string, byte> _handled = new(StringComparer.Ordinal);
    public Task RunAsync(CancellationToken cancellationToken) => qq.RunGatewayAsync(HandleAsync, cancellationToken);
    private async Task HandleAsync(GroupMessage message, CancellationToken cancellationToken)
    {
        if (!_handled.TryAdd(message.Id, 0)) return;
        if (!config.AllowedGroupOpenIds.Contains(message.GroupOpenid, StringComparer.Ordinal)) { Console.WriteLine($"未授权群 OpenID：{message.GroupOpenid}"); return; }
        if (!TrackingNumberParser.TryParse(message.Content, out string number)) return;
        await qq.SendTextAsync(message.GroupOpenid, $"正在查询单号 {number} 的录像", message.Id, 1, cancellationToken);
        RecordingQuery query = await packingProof.CreateQueryAsync(number, cancellationToken);
        while (query.Status is "queued" or "searching" or "preparing") { await Task.Delay(1000, cancellationToken); query = await packingProof.GetQueryAsync(query.QueryId, cancellationToken); }
        if (query.Status == "not_found") { await qq.SendTextAsync(message.GroupOpenid, $"未找到单号 {number} 的精确匹配录像", message.Id, 2, cancellationToken); return; }
        if (query.Status is not ("ready" or "completed")) { await qq.SendTextAsync(message.GroupOpenid, string.IsNullOrWhiteSpace(query.Message) ? "录像查询失败" : query.Message, message.Id, 2, cancellationToken); return; }
        int sequence = 2;
        foreach (Recording recording in query.Recordings.Where(item => item.Status == "ready" && item.DownloadUrl != null).Take(3))
        {
            if (recording.FileSizeBytes > 200L * 1024 * 1024) { await qq.SendTextAsync(message.GroupOpenid, "录像超过 QQ 单文件 200 MB 上限，暂不支持发送", message.Id, sequence++, cancellationToken); continue; }
            string temporary = Path.Combine(Path.GetTempPath(), "PackingProof-QqBot-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                using HttpResponseMessage download = await packingProof.DownloadAsync(query.QueryId, recording.RecordingId, cancellationToken);
                if (!download.IsSuccessStatusCode) throw new InvalidOperationException("下载录像失败");
                await using Stream source = await download.Content.ReadAsStreamAsync(cancellationToken);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous)) await source.CopyToAsync(output, cancellationToken);
                await qq.SendRecordingAsync(message.GroupOpenid, temporary, number + ".mp4", recording.VideoCodec, message.Id, sequence++, cancellationToken);
            }
            catch (Exception exception) { Console.Error.WriteLine($"转发录像失败：{exception.Message}"); await qq.SendTextAsync(message.GroupOpenid, "录像已找到，但转发到 QQ 失败，请稍后重试", message.Id, sequence++, cancellationToken); }
            finally { try { File.Delete(temporary); } catch { } }
        }
    }
}
