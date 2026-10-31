using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PackingProof.QQBot.Bootstrap;

internal static class Program
{
    private const string ManagedExecutableName = "PackingProof.QQBot.Managed.exe";
    private const string RuntimeRootFileName = "startup-runtime-root.txt";

    [STAThread]
    private static int Main(string[] args)
    {
        string baseDirectory = AppContext.BaseDirectory;
        string managedExecutable = Path.Combine(baseDirectory, ManagedExecutableName);
        if (!File.Exists(managedExecutable))
        {
            ShowError($"QQBot 程序文件不完整，请重新安装。\n\n缺少：{managedExecutable}");
            return 1;
        }

        string dataDirectory = RuntimeLocator.ResolveDataDirectory(
            Environment.GetEnvironmentVariable("PACKINGPROOF_EXTENSION_DATA_DIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string runtimeRoot = RuntimeLocator.Resolve(
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            dataDirectory,
            baseDirectory);
        if (!string.IsNullOrWhiteSpace(runtimeRoot))
            RuntimeLocator.Remember(runtimeRoot, dataDirectory);

        try
        {
            using Process? process = Process.Start(CreateStartInfo(baseDirectory, runtimeRoot, args));
            if (process == null)
            {
                ShowError("无法启动 QQBot 主程序。");
                return 1;
            }
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or IOException)
        {
            ShowError($"无法启动 QQBot。\n\n{exception.Message}");
            return 1;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string baseDirectory,
        string runtimeRoot,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(baseDirectory, ManagedExecutableName),
            WorkingDirectory = baseDirectory,
            UseShellExecute = false,
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(runtimeRoot))
            startInfo.Environment["DOTNET_ROOT"] = runtimeRoot;
        return startInfo;
    }

    private static void ShowError(string message) =>
        MessageBoxW(IntPtr.Zero, message, "PackingProof QQBot", 0x00000010);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}

internal static class RuntimeLocator
{
    private static readonly string[] CommonInstallRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PackingProof"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PackingProof"),
    ];

    internal static string Resolve(string? environmentDotNetRoot, string dataDirectory, string baseDirectory)
    {
        foreach (string? candidate in new[]
        {
            Normalize(environmentDotNetRoot),
            ReadRemembered(dataDirectory),
            FindFromRunningDesktop(),
            FindFromRegistry(),
        })
        {
            string normalized = Normalize(candidate) ?? "";
            if (IsUsable(normalized)) return normalized;
        }

        foreach (string installRoot in CommonInstallRoots)
        {
            string candidate = Path.Combine(installRoot, "runtime");
            if (IsUsable(candidate)) return Path.GetFullPath(candidate);
        }

        string? sibling = FindSiblingRuntime(baseDirectory);
        return IsUsable(sibling) ? Path.GetFullPath(sibling!) : "";
    }

    internal static string ResolveDataDirectory(string? injectedDirectory, string localAppDataRoot)
    {
        string normalized = (injectedDirectory ?? "").Trim().Trim('"');
        if (normalized.Length > 0) return Path.GetFullPath(normalized);
        return Path.Combine(
            Path.GetFullPath(localAppDataRoot),
            "ExpressPackingMonitoring",
            "extension-data",
            "packingproof.qqbot");
    }

    internal static bool IsUsable(string? runtimeRoot)
    {
        string? normalized = Normalize(runtimeRoot);
        if (normalized == null) return false;
        try
        {
            return Directory.Exists(Path.Combine(normalized, "host", "fxr"))
                && Directory.Exists(Path.Combine(normalized, "shared", "Microsoft.NETCore.App"))
                && Directory.Exists(Path.Combine(normalized, "shared", "Microsoft.WindowsDesktop.App"));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static void Remember(string runtimeRoot, string dataDirectory)
    {
        try
        {
            string normalized = Path.GetFullPath(runtimeRoot);
            Directory.CreateDirectory(dataDirectory);
            string path = Path.Combine(dataDirectory, "startup-runtime-root.txt");
            if (!File.Exists(path)
                || !string.Equals(File.ReadAllText(path, Encoding.UTF8).Trim(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(path, normalized, new UTF8Encoding(false));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 记录失败不影响这次启动，下一次仍可通过环境变量、Desktop 进程或注册表找到运行时。
        }
    }

    private static string? ReadRemembered(string dataDirectory)
    {
        try
        {
            string path = Path.Combine(dataDirectory, "startup-runtime-root.txt");
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindFromRunningDesktop()
    {
        foreach (Process process in Process.GetProcessesByName("ExpressPackingMonitoring"))
        {
            using (process)
            {
                try
                {
                    string? executable = process.MainModule?.FileName;
                    string? runtime = RuntimeForExecutable(executable);
                    if (IsUsable(runtime)) return runtime;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
                {
                }
            }
        }
        return null;
    }

    private static string? FindFromRegistry()
    {
        const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using RegistryKey? uninstall = root.OpenSubKey(uninstallPath);
            if (uninstall == null) continue;
            foreach (string subKeyName in uninstall.GetSubKeyNames())
            {
                using RegistryKey? item = uninstall.OpenSubKey(subKeyName);
                string displayName = item?.GetValue("DisplayName") as string ?? "";
                if (!displayName.Contains("PackingProof", StringComparison.OrdinalIgnoreCase)
                    && !displayName.Contains("ExpressPackingMonitoring", StringComparison.OrdinalIgnoreCase)
                    && !subKeyName.Contains("99E9FCE3-C8FE-4D7A-9FA4-BC9CB9186B05", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string installLocation = item?.GetValue("InstallLocation") as string ?? "";
                if (installLocation.Length == 0) continue;
                string runtime = Path.Combine(installLocation, "runtime");
                if (IsUsable(runtime)) return runtime;
            }
        }

        using RegistryKey? appPath = Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\ExpressPackingMonitoring.exe");
        return RuntimeForExecutable(appPath?.GetValue(null) as string);
    }

    private static string? RuntimeForExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        string? directory = Path.GetDirectoryName(executablePath.Trim().Trim('"'));
        if (directory == null) return null;
        string? installRoot = string.Equals(Path.GetFileName(directory), "app", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory)
            : directory;
        return installRoot == null ? null : Path.Combine(installRoot, "runtime");
    }

    private static string? FindSiblingRuntime(string baseDirectory)
    {
        DirectoryInfo? current = new(Path.GetFullPath(baseDirectory));
        for (int depth = 0; depth < 6 && current != null; depth++, current = current.Parent)
        {
            string candidate = Path.Combine(current.FullName, "runtime");
            if (IsUsable(candidate)) return candidate;
        }
        return null;
    }

    private static string? Normalize(string? value)
    {
        string trimmed = (value ?? "").Trim().Trim('"');
        if (trimmed.Length == 0) return null;
        try { return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
