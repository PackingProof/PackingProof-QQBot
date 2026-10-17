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

    /// <summary>
    /// 启动时把自启动项里的路径刷成"当前这一份程序"。扩展更新会换到新的版本目录，
    /// 不刷新的话开机自启仍指向旧版本目录（旧目录保留着，能启动，但一直是旧版）。
    /// 没开自启动就什么都不做。
    /// </summary>
    public static void RefreshIfEnabled(
        bool startWithWindows,
        string? executablePath = null,
        IWindowsStartupRegistry? registry = null)
    {
        if (!startWithWindows) return;
        SetEnabled(true, executablePath, registry);
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
