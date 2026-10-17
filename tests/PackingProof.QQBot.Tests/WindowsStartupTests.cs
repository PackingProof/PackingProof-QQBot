using PackingProof.QQBot;

namespace PackingProof.QQBot.Tests;

public sealed class WindowsStartupTests
{
    [Fact]
    public void SetEnabled_WritesQuotedPathAndBackgroundArgument()
    {
        var registry = new FakeRegistry();

        WindowsStartup.SetEnabled(true, @"C:\Program Files\PackingProof QQBot\PackingProof.QQBot.exe", registry);

        Assert.True(WindowsStartup.IsEnabled(registry));
        Assert.Equal("\"C:\\Program Files\\PackingProof QQBot\\PackingProof.QQBot.exe\" --background", registry.Value);
    }

    [Fact]
    public void SetEnabled_FalseRemovesCurrentUserStartupValue()
    {
        var registry = new FakeRegistry { Value = "old" };

        WindowsStartup.SetEnabled(false, registry: registry);

        Assert.False(WindowsStartup.IsEnabled(registry));
    }

    /// <summary>
    /// 扩展更新会换到新的版本目录：启动时要把自启动项里的路径刷成当前这一份，
    /// 否则开机自启一直指向旧版本目录。
    /// </summary>
    [Fact]
    public void RefreshIfEnabled_RewritesPathToTheCurrentExecutable()
    {
        var registry = new FakeRegistry { Value = "\"D:\\old-version\\PackingProof.QQBot.exe\" --background" };

        WindowsStartup.RefreshIfEnabled(true, @"D:\new-version\PackingProof.QQBot.exe", registry);

        Assert.Equal("\"D:\\new-version\\PackingProof.QQBot.exe\" --background", registry.Value);
    }

    [Fact]
    public void RefreshIfEnabled_DoesNothingWhenStartupIsDisabled()
    {
        var registry = new FakeRegistry();

        WindowsStartup.RefreshIfEnabled(false, @"D:\new-version\PackingProof.QQBot.exe", registry);

        Assert.Null(registry.Value);
    }

    private sealed class FakeRegistry : WindowsStartup.IWindowsStartupRegistry
    {
        public string? Value { get; set; }
        public string? GetValue(string valueName) => Value;
        public void SetValue(string valueName, string value) => Value = value;
        public void DeleteValue(string valueName) => Value = null;
    }
}
