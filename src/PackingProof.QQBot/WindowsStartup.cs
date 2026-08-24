using Microsoft.Win32;

namespace PackingProof.QQBot;

internal static class WindowsStartup
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "PackingProof QQBot";

    public static bool IsEnabled(IWindowsStartupRegistry? registry = null) => (registry ?? new CurrentUserWindowsStartupRegistry()).GetValue(ValueName) != null;

    public static void SetEnabled(bool enabled, string? executablePath = null, IWindowsStartupRegistry? registry = null)
    {
        IWindowsStartupRegistry target = registry ?? new CurrentUserWindowsStartupRegistry();
        if (!enabled) { target.DeleteValue(ValueName); return; }
        string executable = executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("无法确定 QQBot 程序路径");
        target.SetValue(ValueName, BuildCommand(executable));
    }

    internal static string BuildCommand(string executablePath) => $"\"{executablePath}\" --background";

    internal interface IWindowsStartupRegistry
    {
        string? GetValue(string valueName);
        void SetValue(string valueName, string value);
        void DeleteValue(string valueName);
    }

    private sealed class CurrentUserWindowsStartupRegistry : IWindowsStartupRegistry
    {
        public string? GetValue(string valueName)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
            return key.GetValue(valueName) as string;
        }

        public void SetValue(string valueName, string value)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(valueName, value, RegistryValueKind.String);
        }

        public void DeleteValue(string valueName)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
