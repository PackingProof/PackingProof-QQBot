using System.Windows;

namespace PackingProof.QQBot;

internal sealed class QQBotApplicationHost : IDisposable
{
    private readonly QQBotStateStore _store;
    private readonly bool _startInBackground;
    private readonly EventWaitHandle? _activationEvent;
    private readonly QQBotRuntime _runtime;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private RegisteredWaitHandle? _activationRegistration;
    private QQBotManagerWindow? _window;
    private Application? _application;

    public QQBotApplicationHost(QQBotStateStore store, bool startInBackground, EventWaitHandle? activationEvent)
    {
        _store = store;
        _startInBackground = startInBackground;
        _activationEvent = activationEvent;
        _runtime = new QQBotRuntime(store);
    }

    public int Run()
    {
        _application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _window = new QQBotManagerWindow(_store, _runtime);
        _window.Closing += OnWindowClosing;
        if (_activationEvent != null)
        {
            _activationRegistration = ThreadPool.RegisterWaitForSingleObject(_activationEvent, (_, _) =>
                _application.Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, false);
        }
        CreateTrayIcon();
        if (_startInBackground)
        {
            _runtime.Start();
        }
        else
        {
            ShowWindow();
        }
        _application.Run();
        return 0;
    }

    private void CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开管理界面", null, (_, _) => _application?.Dispatcher.BeginInvoke(ShowWindow));
        menu.Items.Add("退出 QQBot", null, (_, _) => _application?.Dispatcher.BeginInvoke(Exit));
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "PackingProof QQBot",
            ContextMenuStrip = menu,
            Visible = true,
            Icon = System.Drawing.SystemIcons.Application
        };
        _trayIcon.DoubleClick += (_, _) => _application?.Dispatcher.BeginInvoke(ShowWindow);
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs eventArgs)
    {
        if (!_runtime.IsRunning) return;
        eventArgs.Cancel = true;
        _window?.Hide();
    }

    private void Exit()
    {
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
