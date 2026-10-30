using Microsoft.Win32;
using System.Text;

namespace PackingProof.QQBot;

internal static class WindowsStartup
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "PackingProof QQBot";
    internal const string LauncherFileName = "launch-with-runtime.vbs";
    internal const string RuntimeRootFileName = "startup-runtime-root.txt";
    private const string BackgroundArgument = "--background";

    /// <summary>
    /// 自启动不能直接拉起框架依赖的 apphost：注册表启动没有主程序通过 DOTNET_ROOT 注入的共享运行时，
    /// Windows 会弹出“安装 .NET”的提示。这里生成一个无控制台窗口的 VBS 中转脚本，由它把运行时路径
    /// 放进进程环境后再启动 QQBot。脚本本身放到持久数据目录，随版本变化只更新注册表参数。
    /// </summary>
    private const string LauncherScript = """
        Option Explicit
        Dim shell, target, dotnetRoot, command, i, argument
        If WScript.Arguments.Count < 1 Then WScript.Quit 2
        target = WScript.Arguments(0)
        Set shell = CreateObject("WScript.Shell")
        If WScript.Arguments.Count >= 2 Then
            dotnetRoot = WScript.Arguments(1)
            If Len(dotnetRoot) > 0 Then shell.Environment("PROCESS")("DOTNET_ROOT") = dotnetRoot
        End If
        command = Chr(34) & target & Chr(34)
        For i = 2 To WScript.Arguments.Count - 1
            argument = Replace(WScript.Arguments(i), Chr(34), Chr(34) & Chr(34))
            command = command & " " & Chr(34) & argument & Chr(34)
        Next
        shell.Run command, 0, False
        """;

    public static bool IsEnabled(IWindowsStartupRegistry? registry = null) => (registry ?? new CurrentUserWindowsStartupRegistry()).GetValue(ValueName) != null;

    public static void SetEnabled(
        bool enabled,
        string? executablePath = null,
        IWindowsStartupRegistry? registry = null,
        string? dataDirectory = null,
        string? runtimeRoot = null)
    {
        IWindowsStartupRegistry target = registry ?? new CurrentUserWindowsStartupRegistry();
        if (!enabled) { target.DeleteValue(ValueName); return; }
        string executable = executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("无法确定 QQBot 程序路径");
        string startupDirectory = dataDirectory ?? Path.GetDirectoryName(executable) ?? throw new InvalidOperationException("无法确定 QQBot 数据目录");
        string launcherPath = WriteLauncherScript(startupDirectory);
        string dotnetRoot = ResolveRuntimeRoot(
            runtimeRoot ?? Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            startupDirectory);
        target.SetValue(ValueName, BuildCommand(executable, dotnetRoot, launcherPath));
    }

    /// <summary>
    /// 启动时把自启动项里的路径刷成"当前这一份程序"。扩展更新会换到新的版本目录，
    /// 不刷新的话开机自启仍指向旧版本目录（旧目录保留着，能启动，但一直是旧版）。
    /// 没开自启动就什么都不做。
    /// </summary>
    public static void RefreshIfEnabled(
        bool startWithWindows,
        string? executablePath = null,
        IWindowsStartupRegistry? registry = null,
        string? dataDirectory = null,
        string? runtimeRoot = null)
    {
        if (!startWithWindows) return;
        SetEnabled(true, executablePath, registry, dataDirectory, runtimeRoot);
    }

    internal static string BuildCommand(string executablePath, string runtimeRoot, string launcherPath)
    {
        string scriptHost = Path.Combine(Environment.SystemDirectory, "wscript.exe");
        return string.Join(
            ' ',
            Quote(scriptHost),
            "//B",
            "//NoLogo",
            Quote(launcherPath),
            Quote(executablePath),
            Quote(runtimeRoot),
            BackgroundArgument);
    }

    internal static string WriteLauncherScript(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("QQBot 数据目录不能为空", nameof(directory));

        Directory.CreateDirectory(directory);
        string path = Path.Combine(Path.GetFullPath(directory), LauncherFileName);
        string content = LauncherScript.ReplaceLineEndings("\r\n");
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path, Encoding.UTF8), content, StringComparison.Ordinal))
            File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    internal static string NormalizeRuntimeRoot(string? runtimeRoot)
    {
        string trimmed = (runtimeRoot ?? "").Trim().Trim('"');
        if (trimmed.Length == 0) return "";
        return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static string ResolveRuntimeRoot(string? runtimeRoot, string dataDirectory)
    {
        string normalized = NormalizeRuntimeRoot(runtimeRoot);
        string statePath = Path.Combine(Path.GetFullPath(dataDirectory), RuntimeRootFileName);
        if (normalized.Length > 0)
        {
            Directory.CreateDirectory(dataDirectory);
            if (!File.Exists(statePath)
                || !string.Equals(File.ReadAllText(statePath, Encoding.UTF8).Trim(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(statePath, normalized, new UTF8Encoding(false));
            }
            return normalized;
        }

        try
        {
            return File.Exists(statePath)
                ? NormalizeRuntimeRoot(File.ReadAllText(statePath, Encoding.UTF8))
                : "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string Quote(string value) => $"\"{value}\"";

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
