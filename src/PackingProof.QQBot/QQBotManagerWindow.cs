using System.Windows;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfControl = System.Windows.Controls.Control;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfPanel = System.Windows.Controls.Panel;
using WpfPasswordBox = System.Windows.Controls.PasswordBox;
using WpfScrollViewer = System.Windows.Controls.ScrollViewer;
using WpfSeparator = System.Windows.Controls.Separator;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace PackingProof.QQBot;

internal sealed class QQBotManagerWindow : Window
{
    private readonly QQBotStateStore _store;
    private readonly QQBotRuntime _runtime;
    private readonly WpfTextBox _appId = new();
    private readonly WpfPasswordBox _appSecret = new();
    private readonly WpfTextBox _host = new();
    private readonly WpfTextBox _groupOpenId = new();
    private readonly WpfListBox _groups = new();
    private readonly WpfTextBox _size = new();
    private readonly WpfComboBox _profile = new();
    private readonly WpfListBox _foundHosts = new();
    private readonly WpfTextBlock _hostInfo = new();
    private readonly WpfTextBlock _status = new();
    private WpfButton? _startupButton;

    public QQBotManagerWindow(QQBotStateStore store, QQBotRuntime runtime)
    {
        _store = store;
        _runtime = runtime;
        Title = "PackingProof QQBot";
        Width = 620;
        Height = 760;
        MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildContent();
        Load();
        _runtime.StatusChanged += OnRuntimeStatusChanged;
        Closed += (_, _) => _runtime.StatusChanged -= OnRuntimeStatusChanged;
    }

    private UIElement BuildContent()
    {
        var panel = new WpfStackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new WpfTextBlock { Text = "PackingProof QQBot", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new WpfTextBlock { Text = "首次填写后会在 PackingProof 中请求授权。AppSecret 仅加密保存在当前 Windows 用户账户", Margin = new Thickness(0, 6, 0, 18), TextWrapping = TextWrapping.Wrap });
        AddField(panel, "QQ AppID", _appId);
        AddField(panel, "QQ AppSecret", _appSecret);
        AddField(panel, "PackingProof 地址", _host);
        panel.Children.Add(Row(Button("保存并授权", SaveAsync), Button("测试主机", TestHostAsync), Button("搜索局域网主机", SearchHostsAsync)));
        panel.Children.Add(_hostInfo);
        panel.Children.Add(_foundHosts);
        panel.Children.Add(Row(Button("使用选中主机", UseSelectedHost)));
        panel.Children.Add(new WpfSeparator { Margin = new Thickness(0, 18, 0, 12) });
        panel.Children.Add(new WpfTextBlock { Text = "QQ 群白名单", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_groups);
        panel.Children.Add(Row(_groupOpenId, Button("添加", AddGroup), Button("删除选中", RemoveGroup)));
        panel.Children.Add(new WpfSeparator { Margin = new Thickness(0, 18, 0, 12) });
        panel.Children.Add(new WpfTextBlock { Text = "视频发送", FontWeight = FontWeights.SemiBold });
        _profile.Items.Add("保持原编码并降低码率");
        _profile.Items.Add("转为 H.265");
        panel.Children.Add(Row(new WpfTextBlock { Text = "最大大小（MB）", VerticalAlignment = VerticalAlignment.Center }, _size, _profile, Button("保存视频设置", SaveDelivery)));
        panel.Children.Add(new WpfSeparator { Margin = new Thickness(0, 18, 0, 12) });
        _startupButton = Button("", ToggleStartup);
        panel.Children.Add(Row(Button("启动机器人", StartAsync), Button("停止机器人", Stop), _startupButton));
        panel.Children.Add(new WpfTextBlock { Text = "运行日志", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 3) });
        panel.Children.Add(_status);
        return new WpfScrollViewer { Content = panel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
    }

    private static void AddField(WpfPanel panel, string label, WpfControl control)
    {
        panel.Children.Add(new WpfTextBlock { Text = label, Margin = new Thickness(0, 6, 0, 3) });
        control.MinWidth = 300;
        panel.Children.Add(control);
    }

    private static WpfStackPanel Row(params UIElement[] children)
    {
        var panel = new WpfStackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (UIElement child in children)
        {
            if (child is FrameworkElement element) element.Margin = new Thickness(0, 0, 8, 0);
            panel.Children.Add(child);
        }
        return panel;
    }

    private static WpfButton Button(string text, RoutedEventHandler handler) => new WpfButton { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), }.Also(button => button.Click += handler);

    private void Load()
    {
        QQBotConfiguration? config = _store.LoadConfiguration();
        QQBotSecrets? secrets = _store.LoadSecrets();
        _appId.Text = config?.AppId ?? "";
        _appSecret.Password = secrets?.AppSecret ?? "";
        _host.Text = config?.PackingProofBaseUrl ?? "http://127.0.0.1:5280";
        _size.Text = (config?.DeliveryMaxSizeMb ?? QQBotConfiguration.DefaultDeliveryMaxSizeMb).ToString();
        _profile.SelectedIndex = config?.DeliveryProfile == QQBotConfiguration.H265TargetSizeProfile ? 1 : 0;
        RefreshGroups(config);
        RefreshHostInfo(config);
        if (_startupButton != null) _startupButton.Content = config?.StartWithWindows == true ? "关闭开机自动启动" : "登录 Windows 后自动启动";
    }

    private void RefreshGroups(QQBotConfiguration? config = null)
    {
        _groups.ItemsSource = (config ?? _store.LoadConfiguration())?.AllowedGroupOpenIds ?? [];
    }

    private async void SaveAsync(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            SetStatus("正在请求 PackingProof 授权，请在主程序窗口中批准");
            await Program.SaveConfigurationAsync(_store, _appId.Text.Trim(), _appSecret.Password, _host.Text.Trim(), CancellationToken.None);
            Load();
            _runtime.Start();
            SetStatus("配置已保存，正在启动机器人");
        }
        catch (Exception exception) { SetStatus(exception.Message); }
    }

    private async void TestHostAsync(object sender, RoutedEventArgs eventArgs)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        PackingProofHostInfo? host = await new PackingProofHostDiscovery(http).ProbeAsync(_host.Text, CancellationToken.None);
        if (host == null) { SetStatus("未找到可用的 PackingProof 主机"); return; }
        _host.Text = host.BaseUrl;
        _hostInfo.Text = FormatHost(host);
        SetStatus("主机连接正常。首次使用或更换主机后，请保存并授权");
    }

    private async void SearchHostsAsync(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            SetStatus("正在搜索局域网中的 PackingProof 主机");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            IReadOnlyList<PackingProofHostInfo> hosts = await new PackingProofHostDiscovery(http).DiscoverAsync(CancellationToken.None);
            _foundHosts.ItemsSource = hosts;
            SetStatus(hosts.Count == 0 ? "没有找到主机，请确认主程序已打开且在同一局域网" : $"找到 {hosts.Count} 台主机。选择后仍需保存并重新授权");
        }
        catch (Exception exception) { SetStatus("搜索主机失败：" + exception.Message); }
    }

    private void UseSelectedHost(object sender, RoutedEventArgs eventArgs)
    {
        if (_foundHosts.SelectedItem is not PackingProofHostInfo host) { SetStatus("请先选择一台主机"); return; }
        _host.Text = host.BaseUrl;
        _hostInfo.Text = FormatHost(host);
        SetStatus("已选择新主机。为保护录像权限，请点击“保存并授权”完成确认");
    }

    private void AddGroup(object sender, RoutedEventArgs eventArgs)
    {
        QQBotConfiguration config = RequireConfig(); QQBotSecrets secrets = RequireSecrets();
        string group = _groupOpenId.Text.Trim();
        if (group.Length == 0) { SetStatus("请输入群 OpenID"); return; }
        config = config with { AllowedGroupOpenIds = config.AllowedGroupOpenIds.Append(group).Distinct(StringComparer.Ordinal).Order().ToArray() };
        _store.Save(config, secrets); _groupOpenId.Clear(); RefreshGroups(config); SetStatus("已加入群白名单");
    }

    private void RemoveGroup(object sender, RoutedEventArgs eventArgs)
    {
        if (_groups.SelectedItem is not string group) return;
        QQBotConfiguration config = RequireConfig();
        config = config with { AllowedGroupOpenIds = config.AllowedGroupOpenIds.Where(item => item != group).ToArray() };
        _store.Save(config, RequireSecrets()); RefreshGroups(config); SetStatus("已删除群白名单");
    }

    private void SaveDelivery(object sender, RoutedEventArgs eventArgs)
    {
        if (!int.TryParse(_size.Text, out int size)) { SetStatus("视频大小必须是数字"); return; }
        QQBotConfiguration config = (RequireConfig() with { DeliveryMaxSizeMb = size, DeliveryProfile = _profile.SelectedIndex == 1 ? QQBotConfiguration.H265TargetSizeProfile : QQBotConfiguration.SourceCodecTargetSizeProfile }).ValidateDeliverySettings();
        _store.Save(config, RequireSecrets()); SetStatus("视频设置已保存");
    }

    private void StartAsync(object sender, RoutedEventArgs eventArgs)
    {
        try { _runtime.Start(); SetStatus("机器人正在启动"); }
        catch (Exception exception) { SetStatus(exception.Message); }
    }

    private void Stop(object sender, RoutedEventArgs eventArgs) { _runtime.Stop(); SetStatus("正在停止机器人"); }
    private void ToggleStartup(object sender, RoutedEventArgs eventArgs)
    {
        QQBotConfiguration config = RequireConfig();
        bool enabled = !config.StartWithWindows;
        WindowsStartup.SetEnabled(enabled);
        _store.Save(config with { StartWithWindows = enabled }, RequireSecrets());
        if (_startupButton != null) _startupButton.Content = enabled ? "关闭开机自动启动" : "登录 Windows 后自动启动";
        SetStatus(enabled ? "已设置登录 Windows 后后台启动" : "已关闭开机自动启动");
    }
    private QQBotConfiguration RequireConfig() => _store.LoadConfiguration() ?? throw new InvalidOperationException("请先保存并授权");
    private QQBotSecrets RequireSecrets() => _store.LoadSecrets() ?? throw new InvalidOperationException("请先保存并授权");
    private void RefreshHostInfo(QQBotConfiguration? config)
    {
        _hostInfo.Text = config == null
            ? "当前主机：尚未配置"
            : $"当前主机：{config.PackingProofBaseUrl}｜nodeId：{(string.IsNullOrWhiteSpace(config.PackingProofNodeId) ? "等待首次验证" : config.PackingProofNodeId)}";
    }
    private static string FormatHost(PackingProofHostInfo host) => $"主机：{host.NodeName}｜地址：{host.BaseUrl}｜nodeId：{host.NodeId}";
    private void OnRuntimeStatusChanged(string status) => Dispatcher.BeginInvoke(() => SetStatus(status));
    private void SetStatus(string text)
    {
        _status.Text = $"{DateTime.Now:HH:mm:ss} {text}";
    }
}

internal static class WpfControlExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
