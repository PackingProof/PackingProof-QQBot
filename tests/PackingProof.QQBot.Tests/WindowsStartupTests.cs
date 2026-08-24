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

    private sealed class FakeRegistry : WindowsStartup.IWindowsStartupRegistry
    {
        public string? Value { get; set; }
        public string? GetValue(string valueName) => Value;
        public void SetValue(string valueName, string value) => Value = value;
        public void DeleteValue(string valueName) => Value = null;
    }
}
