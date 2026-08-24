using System.Windows;
using System.Windows.Controls;

namespace PackingProof.QQBot;

internal sealed class QQBotManagerWindow : Window
{
    private readonly QQBotStateStore _store;
    private readonly TextBox _appId = new();
    private readonly PasswordBox _appSecret = new();
    private readonly TextBox _host = new();
    private readonly TextBox _groupOpenId = new();
    private readonly ListBox _groups = new();
    private readonly TextBox _size = new();
    private readonly ComboBox _profile = new();
    private readonly TextBlock _status = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;

    public QQBotManagerWindow(QQBotStateStore store)
    {
        _store = store;
        Title = "PackingProof QQBot";
        Width = 620;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildContent();
        Load();
        Closed += (_, _) => _runCancellation?.Cancel();
    }

    private UIElement BuildContent()
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "PackingProof QQBot", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "首次填写后会在 PackingProof 中请求授权。AppSecret 仅加密保存在当前 Windows 用户账户", Margin = new Thickness(0, 6, 0, 18), TextWrapping = TextWrapping.Wrap });
        AddField(panel, "QQ AppID", _appId);
        AddField(panel, "QQ AppSecret", _appSecret);
        AddField(panel, "PackingProof 地址", _host);
        panel.Children.Add(Row(Button("保存并授权", SaveAsync), Button("测试主机", TestHostAsync)));
        panel.Children.Add(new Separator { Margin = new Thickness(0, 18, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "QQ 群白名单", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_groups);
        panel.Children.Add(Row(_groupOpenId, Button("添加", AddGroup), Button("删除选中", RemoveGroup)));
        panel.Children.Add(new Separator { Margin = new Thickness(0, 18, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "视频发送", FontWeight = FontWeights.SemiBold });
        _profile.Items.Add("保持原编码并降低码率");
        _profile.Items.Add("转为 H.265");
        panel.Children.Add(Row(new TextBlock { Text = "最大大小（MB）", VerticalAlignment = VerticalAlignment.Center }, _size, _profile, Button("保存视频设置", SaveDelivery)));
        panel.Children.Add(new Separator { Margin = new Thickness(0, 18, 0, 12) });
        panel.Children.Add(Row(Button("启动机器人", StartAsync), Button("停止机器人", Stop), Button("切换开机自动启动", ToggleStartup)));
        panel.Children.Add(_status);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static void AddField(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 3) });
        control.MinWidth = 300;
        panel.Children.Add(control);
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (UIElement child in children) { child.Margin = new Thickness(0, 0, 8, 0); panel.Children.Add(child); }
        return panel;
    }

    private static Button Button(string text, RoutedEventHandler handler) => new() { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), }.Also(button => button.Click += handler);

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
    }

    private void RefreshGroups(QQBotConfiguration? config = null)
    {
        _groups.ItemsSource = (config ?? _store.LoadConfiguration())?.AllowedGroupOpenIds ?? [];
    }

    private async void SaveAsync(object sender, RoutedEventArgs eventArgs)
    {
        try { SetStatus("正在请求 PackingProof 授权"); await Program.SaveConfigurationAsync(_store, _appId.Text.Trim(), _appSecret.Password, _host.Text.Trim(), CancellationToken.None); Load(); SetStatus("配置已保存"); }
        catch (Exception exception) { SetStatus(exception.Message); }
    }

    private async void TestHostAsync(object sender, RoutedEventArgs eventArgs)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        PackingProofHostInfo? host = await new PackingProofHostDiscovery(http).ProbeAsync(_host.Text, CancellationToken.None);
        SetStatus(host == null ? "未找到可用的 PackingProof 主机" : $"已连接：{host.NodeName}｜{host.BaseUrl}");
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

    private async void StartAsync(object sender, RoutedEventArgs eventArgs)
    {
        if (_runTask != null) return;
        _runCancellation = new CancellationTokenSource();
        _runTask = Program.RunAsync(_store, _runCancellation.Token);
        SetStatus("机器人正在启动");
        try { await _runTask; SetStatus("机器人已停止"); } catch (Exception exception) { SetStatus(exception.Message); } finally { _runTask = null; _runCancellation?.Dispose(); _runCancellation = null; }
    }

    private void Stop(object sender, RoutedEventArgs eventArgs) => _runCancellation?.Cancel();
    private void ToggleStartup(object sender, RoutedEventArgs eventArgs)
    {
        QQBotConfiguration config = RequireConfig();
        bool enabled = !config.StartWithWindows;
        WindowsStartup.SetEnabled(enabled);
        _store.Save(config with { StartWithWindows = enabled }, RequireSecrets());
        SetStatus(enabled ? "已设置登录 Windows 后后台启动" : "已关闭开机自动启动");
    }
    private QQBotConfiguration RequireConfig() => _store.LoadConfiguration() ?? throw new InvalidOperationException("请先保存并授权");
    private QQBotSecrets RequireSecrets() => _store.LoadSecrets() ?? throw new InvalidOperationException("请先保存并授权");
    private void SetStatus(string text) => _status.Text = text;
}

internal static class WpfControlExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
