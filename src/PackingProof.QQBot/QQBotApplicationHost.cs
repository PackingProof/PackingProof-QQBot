using System.Windows;

namespace PackingProof.QQBot;

internal sealed class QQBotApplicationHost : IDisposable
{
    private readonly QQBotStateStore _store;
    private readonly bool _startInBackground;
    private readonly EventWaitHandle? _activationEvent;
    private readonly QQBotRuntime _runtime;
    private TrayIconService? _trayIcon;
    private RegisteredWaitHandle? _activationRegistration;
    private QQBotManagerWindow? _window;
    private System.Windows.Application? _application;
    private bool _exiting;

    public QQBotApplicationHost(QQBotStateStore store, bool startInBackground, EventWaitHandle? activationEvent)
    {
        _store = store;
        _startInBackground = startInBackground;
        _activationEvent = activationEvent;
        _runtime = new QQBotRuntime(store);
    }

    public int Run()
    {
        _application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _window = new QQBotManagerWindow(_store, _runtime);
        _window.Closing += OnWindowClosing;
        if (_activationEvent != null)
        {
            _activationRegistration = ThreadPool.RegisterWaitForSingleObject(_activationEvent, (_, _) =>
                _application.Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, false);
        }
        CreateTrayIcon();
        QQBotConfiguration? configuration = _store.LoadConfiguration();
        // 自动更新会换到新的版本目录：开了开机自启就把注册表里的路径改成当前这一份。
        WindowsStartup.RefreshIfEnabled(configuration?.StartWithWindows == true);
        if (CanAutoStart(configuration, _store.LoadSecrets()))
        {
            _runtime.Start();
        }
        if (!_startInBackground)
        {
            ShowWindow();
        }
        _application.Run();
        return 0;
    }

    internal static bool CanAutoStart(QQBotConfiguration? configuration, QQBotSecrets? secrets) =>
        configuration != null
        && !string.IsNullOrWhiteSpace(configuration.AppId)
        && secrets?.ExtensionCredential != null
        && !string.IsNullOrWhiteSpace(secrets.AppSecret)
        && !string.IsNullOrWhiteSpace(secrets.ExtensionCredential.Credential);

    private void CreateTrayIcon()
    {
        if (_window == null) return;
        _trayIcon = new TrayIconService(_window, ShowWindow, Exit);
    }

    private void ShowWindow()
    {
        if (_window == null || _exiting) return;
        if (!_window.IsVisible) _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs eventArgs)
    {
        if (_exiting) return;
        // 关闭按钮只隐藏窗口。窗口一旦真正关闭，托盘或单实例激活再调用 Show 会抛异常。
        eventArgs.Cancel = true;
        _window?.Hide();
    }

    private void Exit()
    {
        _exiting = true;
        _runtime.Stop();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _application?.Shutdown();
    }

    public void Dispose()
    {
        _activationRegistration?.Unregister(null);
        _trayIcon?.Dispose();
        _runtime.Dispose();
    }
}
