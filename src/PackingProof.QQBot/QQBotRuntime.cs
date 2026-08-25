namespace PackingProof.QQBot;

internal sealed class QQBotRuntime(QQBotStateStore store) : IDisposable
{
    private readonly QQBotStateStore _store = store;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;

    public event Action<string>? StatusChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _runTask is { IsCompleted: false };
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_runTask is { IsCompleted: false }) return;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _runTask = RunCoreAsync(_cancellation.Token);
            _ = ObserveCompletionAsync(_runTask);
        }
        PublishStatus("机器人正在启动");
    }

    public void Stop() => _cancellation?.Cancel();

    public async Task RestartAsync()
    {
        Task? previous;
        lock (_gate) previous = _runTask is { IsCompleted: false } ? _runTask : null;
        Stop();
        if (previous != null)
        {
            try { await previous; }
            catch (OperationCanceledException) { }
            catch { }
        }
        Start();
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        QQBotConfiguration configuration = Program.Config(_store);
        QQBotSecrets secrets = Program.Secrets(_store);
        if (secrets.ExtensionCredential == null) throw new InvalidOperationException("缺少扩展凭据，请在管理界面中重新授权");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        configuration = await Program.ResolvePackingProofHostAsync(_store, configuration, secrets, http, cancellationToken);
        var packingProof = new PackingProofClient(http, configuration, secrets.ExtensionCredential, async token =>
        {
            QQBotConfiguration recovered = await Program.ResolvePackingProofHostAsync(_store, configuration, secrets, http, token);
            bool changed = !string.Equals(recovered.PackingProofBaseUrl, configuration.PackingProofBaseUrl, StringComparison.OrdinalIgnoreCase);
            configuration = recovered;
            if (changed) PublishStatus($"已恢复 PackingProof 主机：{configuration.PackingProofBaseUrl}");
            return changed ? recovered : null;
        });

        PublishStatus("正在连接 QQ 网关");
        var service = new QueryService(configuration, packingProof, new QQClient(http, configuration, secrets), _store);
        await service.RunAsync(cancellationToken);
    }

    private async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task;
            PublishStatus("机器人已停止");
        }
        catch (OperationCanceledException)
        {
            PublishStatus("机器人已停止");
        }
        catch (Exception exception)
        {
            PublishStatus("机器人无法启动：" + exception.Message);
        }
    }

    public void Dispose()
    {
        Stop();
        _cancellation?.Dispose();
    }

    private void PublishStatus(string status)
    {
        QQBotLog.Write(status);
        StatusChanged?.Invoke(status);
    }
}
