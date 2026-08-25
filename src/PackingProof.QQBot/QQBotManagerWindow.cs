using System.Windows;
using System.Diagnostics;
using WpfBorder = System.Windows.Controls.Border;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfControl = System.Windows.Controls.Control;
using WpfColumnDefinition = System.Windows.Controls.ColumnDefinition;
using WpfClipboard = System.Windows.Clipboard;
using WpfGrid = System.Windows.Controls.Grid;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfPanel = System.Windows.Controls.Panel;
using WpfPasswordBox = System.Windows.Controls.PasswordBox;
using WpfRowDefinition = System.Windows.Controls.RowDefinition;
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
    private readonly WpfListBox _groups = new();
    private readonly WpfTextBox _size = new();
    private readonly WpfComboBox _profile = new();
    private readonly WpfListBox _foundHosts = new();
    private readonly WpfListBox _logs = new() { MaxHeight = 150 };
    private readonly WpfTextBlock _hostInfo = new();
    private readonly WpfTextBlock _status = new();
    private WpfButton? _startupButton;
    private WpfButton? _useSelectedHostButton;

    public QQBotManagerWindow(QQBotStateStore store, QQBotRuntime runtime)
    {
        _store = store;
        _runtime = runtime;
        Title = "PackingProof QQBot";
        Width = 860;
        Height = 760;
        MinWidth = 720;
        MinHeight = 680;
        Background = Brush(245, 247, 250);
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildContent();
        Load();
        _runtime.StatusChanged += OnRuntimeStatusChanged;
        _store.GroupsChanged += OnGroupsChanged;
        QQBotLog.Written += OnLogWritten;
        Closed += (_, _) =>
        {
            _runtime.StatusChanged -= OnRuntimeStatusChanged;
            _store.GroupsChanged -= OnGroupsChanged;
            QQBotLog.Written -= OnLogWritten;
        };
    }

    private UIElement BuildContent()
    {
        var panel = new WpfStackPanel { Margin = new Thickness(28, 24, 28, 32) };
        panel.Children.Add(new WpfTextBlock { Text = "PackingProof QQBot", FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brush(31, 41, 55) });
        panel.Children.Add(new WpfTextBlock { Text = "QQ 私聊或群里发单号，自动查询并回传录像", Foreground = Brush(100, 116, 139), FontSize = 14, Margin = new Thickness(0, 4, 0, 14) });

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Foreground = Brush(30, 64, 175);
        var controls = new WpfStackPanel();
        controls.Children.Add(_status);
        _startupButton = Button("", ToggleStartup);
        controls.Children.Add(Row(PrimaryButton("启动机器人", StartAsync), Button("停止机器人", Stop), _startupButton, Button("使用教程", OpenUserGuide)));
        panel.Children.Add(new WpfBorder { Background = Brush(239, 246, 255), BorderBrush = Brush(191, 219, 254), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10), Child = controls, Margin = new Thickness(0, 0, 0, 14) });

        var connection = FormGrid(7);
        AddField(connection, 0, "QQ AppID", _appId);
        AddField(connection, 1, "QQ AppSecret", _appSecret);
        AddField(connection, 2, "PackingProof 地址", _host);
        WpfStackPanel authorizationActions = Row(PrimaryButton("保存并授权", SaveAsync), Button("测试连接", TestHostAsync), Button("搜索局域网主机", SearchHostsAsync));
        WpfGrid.SetRow(authorizationActions, 3);
        WpfGrid.SetColumn(authorizationActions, 1);
        connection.Children.Add(authorizationActions);
        _hostInfo.TextWrapping = TextWrapping.Wrap;
        _hostInfo.Foreground = Brush(71, 85, 105);
        _hostInfo.Margin = new Thickness(0, 10, 0, 4);
        WpfGrid.SetRow(_hostInfo, 4);
        WpfGrid.SetColumn(_hostInfo, 1);
        connection.Children.Add(_hostInfo);
        _foundHosts.MaxHeight = 110;
        _foundHosts.Visibility = Visibility.Collapsed;
        _foundHosts.Margin = new Thickness(0, 6, 0, 0);
        WpfGrid.SetRow(_foundHosts, 5);
        WpfGrid.SetColumn(_foundHosts, 1);
        connection.Children.Add(_foundHosts);
        _useSelectedHostButton = Button("使用选中主机", UseSelectedHost);
        _useSelectedHostButton.Visibility = Visibility.Collapsed;
        WpfStackPanel selectedHostActions = Row(_useSelectedHostButton);
        WpfGrid.SetRow(selectedHostActions, 6);
        WpfGrid.SetColumn(selectedHostActions, 1);
        connection.Children.Add(selectedHostActions);
        panel.Children.Add(Card("连接与授权", connection));

        var groups = FormGrid(2);
        _groups.MaxHeight = 130;
        _groups.Margin = new Thickness(0, 0, 0, 8);
        WpfGrid.SetColumnSpan(_groups, 2);
        groups.Children.Add(_groups);
        var groupHint = new WpfTextBlock
        {
            Text = "在群里 @ 机器人后会自动显示。选择群后决定是否允许使用",
            Foreground = Brush(100, 116, 139),
            VerticalAlignment = VerticalAlignment.Center
        };
        WpfGrid.SetRow(groupHint, 1);
        groups.Children.Add(groupHint);
        WpfStackPanel groupActions = Row(
            PrimaryButton("允许选中群", AllowSelectedGroup),
            Button("停用选中群", DisableSelectedGroup),
            Button("复制 OpenID", CopySelectedGroupOpenId));
        WpfGrid.SetRow(groupActions, 1);
        WpfGrid.SetColumn(groupActions, 1);
        groups.Children.Add(groupActions);
        panel.Children.Add(Card("发现的 QQ 群", groups));

        var delivery = FormGrid(1);
        _profile.Items.Add("保持原编码并降低码率");
        _profile.Items.Add("转为 H.265");
        _size.Width = 68;
        _size.Height = 32;
        _size.VerticalContentAlignment = VerticalAlignment.Center;
        _profile.MinWidth = 220;
        _profile.Height = 32;
        _profile.VerticalContentAlignment = VerticalAlignment.Center;
        var deliveryLabel = Label("视频上限（MB）");
        delivery.Children.Add(deliveryLabel);
        WpfStackPanel deliveryActions = Row(_size, _profile, Button("保存视频设置", SaveDelivery));
        WpfGrid.SetColumn(deliveryActions, 1);
        delivery.Children.Add(deliveryActions);
        panel.Children.Add(Card("视频发送", delivery));

        _logs.MaxHeight = 160;
        _logs.Background = Brush(248, 250, 252);
        var logPanel = new WpfStackPanel();
        logPanel.Children.Add(_logs);
        logPanel.Children.Add(Row(Button("复制选中日志", CopySelectedLog)));
        panel.Children.Add(Card("运行日志", logPanel));
        return new WpfScrollViewer { Content = panel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Background = Brush(245, 247, 250) };
    }

    private static WpfBorder Card(string title, UIElement content)
    {
        var panel = new WpfStackPanel { Margin = new Thickness(18, 14, 18, 16) };
        panel.Children.Add(new WpfTextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brush(30, 41, 59) });
        if (content is FrameworkElement element) element.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(content);
        return new WpfBorder { Background = System.Windows.Media.Brushes.White, BorderBrush = Brush(226, 232, 240), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = panel, Margin = new Thickness(0, 0, 0, 14) };
    }

    private static WpfGrid FormGrid(int rowCount)
    {
        var grid = new WpfGrid();
        grid.ColumnDefinitions.Add(new WpfColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new WpfColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int row = 0; row < rowCount; row++) grid.RowDefinitions.Add(new WpfRowDefinition { Height = GridLength.Auto });
        return grid;
    }

    private static WpfTextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brush(51, 65, 85),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 18, 8)
    };

    private static void AddField(WpfGrid grid, int row, string label, WpfControl control)
    {
        WpfTextBlock fieldLabel = Label(label);
        WpfGrid.SetRow(fieldLabel, row);
        grid.Children.Add(fieldLabel);
        control.MinWidth = 0;
        control.Height = 32;
        control.VerticalContentAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(0, 0, 0, 8);
        WpfGrid.SetRow(control, row);
        WpfGrid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private static WpfStackPanel Row(params UIElement[] children)
    {
        var panel = new WpfStackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        foreach (UIElement child in children)
        {
            if (child is FrameworkElement element) element.Margin = new Thickness(0, 0, 8, 0);
            panel.Children.Add(child);
        }
        return panel;
    }

    private static WpfButton Button(string text, RoutedEventHandler handler) => CreateButton(text, handler, false);
    private static WpfButton PrimaryButton(string text, RoutedEventHandler handler) => CreateButton(text, handler, true);
    private static WpfButton CreateButton(string text, RoutedEventHandler handler, bool primary) => new WpfButton
    {
        Content = text,
        Padding = new Thickness(14, 6, 14, 6),
        MinHeight = 32,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = primary ? Brush(37, 99, 235) : System.Windows.Media.Brushes.White,
        Foreground = primary ? System.Windows.Media.Brushes.White : Brush(51, 65, 85),
        BorderBrush = primary ? Brush(37, 99, 235) : Brush(203, 213, 225),
        BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand
    }.Also(button => button.Click += handler);

    private static System.Windows.Media.SolidColorBrush Brush(byte red, byte green, byte blue) => new(System.Windows.Media.Color.FromRgb(red, green, blue));

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
        _logs.Items.Clear();
        foreach (string entry in QQBotLog.Snapshot()) _logs.Items.Add(entry);
        if (_startupButton != null) _startupButton.Content = config?.StartWithWindows == true ? "关闭开机自动启动" : "登录 Windows 后自动启动";
        SetStatus(config == null ? "请填写 QQ AppID、AppSecret 和 PackingProof 地址，然后保存并授权" : "管理器已就绪，可启动机器人或修改设置");
    }

    private void RefreshGroups(QQBotConfiguration? config = null)
    {
        string? selectedOpenId = (_groups.SelectedItem as QQGroupDisplayItem)?.OpenId;
        QQGroupDisplayItem[] items = BuildGroupItems(config ?? _store.LoadConfiguration());
        _groups.ItemsSource = items;
        _groups.SelectedItem = items.FirstOrDefault(item => string.Equals(item.OpenId, selectedOpenId, StringComparison.Ordinal));
    }

    private async void SaveAsync(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            SetStatus("正在请求 PackingProof 授权，请在主程序窗口中批准");
            await Program.SaveConfigurationAsync(_store, _appId.Text.Trim(), _appSecret.Password, _host.Text.Trim(), CancellationToken.None);
            Load();
            await _runtime.RestartAsync();
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
            _foundHosts.Visibility = hosts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (_useSelectedHostButton != null) _useSelectedHostButton.Visibility = hosts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
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

    private void AllowSelectedGroup(object sender, RoutedEventArgs eventArgs)
    {
        if (_groups.SelectedItem is not QQGroupDisplayItem group) { SetStatus("请先选择一个群"); return; }
        QQBotConfiguration config = _store.SetGroupAllowed(group.OpenId, true);
        RefreshGroups(config);
        SetStatus("已允许该群，立即生效");
    }

    private void DisableSelectedGroup(object sender, RoutedEventArgs eventArgs)
    {
        if (_groups.SelectedItem is not QQGroupDisplayItem group) { SetStatus("请先选择一个群"); return; }
        QQBotConfiguration config = _store.SetGroupAllowed(group.OpenId, false);
        RefreshGroups(config);
        SetStatus("已停用该群，立即生效");
    }

    private void CopySelectedGroupOpenId(object sender, RoutedEventArgs eventArgs)
    {
        if (_groups.SelectedItem is not QQGroupDisplayItem group) { SetStatus("请先选择一个群"); return; }
        try { WpfClipboard.SetText(group.OpenId); SetStatus("群 OpenID 已复制"); }
        catch (Exception exception) { SetStatus("复制失败：" + exception.Message); }
    }

    private void CopySelectedLog(object sender, RoutedEventArgs eventArgs)
    {
        if (_logs.SelectedItem is not string entry) { SetStatus("请先选择一条日志"); return; }
        try { WpfClipboard.SetText(entry); SetStatus("日志已复制"); }
        catch (Exception exception) { SetStatus("复制失败：" + exception.Message); }
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
    private void OpenUserGuide(object sender, RoutedEventArgs eventArgs)
    {
        string guidePath = Path.Combine(AppContext.BaseDirectory, "使用说明.md");
        if (!File.Exists(guidePath)) { SetStatus("未找到《使用说明》，请确认发布包文件完整"); return; }
        try { Process.Start(new ProcessStartInfo(guidePath) { UseShellExecute = true }); }
        catch (Exception exception) { SetStatus("打开使用教程失败：" + exception.Message); }
    }
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
            : $"当前主机：{(string.IsNullOrWhiteSpace(config.PackingProofNodeName) ? "名称待确认" : config.PackingProofNodeName)}｜{config.PackingProofBaseUrl}";
    }
    private static string FormatHost(PackingProofHostInfo host) => $"主机：{host.NodeName}｜地址：{host.BaseUrl}";
    private void OnRuntimeStatusChanged(string status) => Dispatcher.BeginInvoke(() => SetStatus(status));
    private void OnGroupsChanged() => Dispatcher.BeginInvoke(RefreshGroups);
    private void OnLogWritten(string entry) => Dispatcher.BeginInvoke(() =>
    {
        _logs.Items.Add(entry);
        while (_logs.Items.Count > 100) _logs.Items.RemoveAt(0);
        _logs.ScrollIntoView(entry);
    });
    private void SetStatus(string text)
    {
        _status.Text = $"{DateTime.Now:HH:mm:ss} {text}";
    }

    internal static QQGroupDisplayItem[] BuildGroupItems(QQBotConfiguration? config)
    {
        if (config == null) return [];
        var known = config.KnownGroups
            .Where(group => !string.IsNullOrWhiteSpace(group.OpenId))
            .GroupBy(group => group.OpenId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.LastSeenAtUtc).First(), StringComparer.Ordinal);
        return known.Keys
            .Concat(config.AllowedGroupOpenIds)
            .Where(openId => !string.IsNullOrWhiteSpace(openId))
            .Distinct(StringComparer.Ordinal)
            .Select(openId => new QQGroupDisplayItem(
                openId,
                config.AllowedGroupOpenIds.Contains(openId, StringComparer.Ordinal),
                known.TryGetValue(openId, out QQKnownGroup? group) ? group.LastSeenAtUtc : null))
            .OrderByDescending(item => item.LastSeenAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.OpenId, StringComparer.Ordinal)
            .ToArray();
    }
}

internal sealed record QQGroupDisplayItem(string OpenId, bool IsAllowed, DateTimeOffset? LastSeenAtUtc)
{
    public override string ToString() =>
        $"{(IsAllowed ? "已允许" : "未允许")}｜{OpenId}｜{(LastSeenAtUtc.HasValue ? "最后出现 " + LastSeenAtUtc.Value.ToLocalTime().ToString("MM-dd HH:mm") : "出现时间未知")}";
}

internal static class WpfControlExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
