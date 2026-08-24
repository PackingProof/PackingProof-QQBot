using Microsoft.Win32;

namespace PackingProof.QQBot;

internal static class WindowsStartup
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "PackingProof QQBot";

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled) { key.DeleteValue(ValueName, throwOnMissingValue: false); return; }
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定 QQBot 程序路径");
        key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
    }
}
