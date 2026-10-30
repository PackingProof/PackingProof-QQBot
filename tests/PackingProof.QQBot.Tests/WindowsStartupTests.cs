using PackingProof.QQBot;
using System.Diagnostics;
using System.Text;

namespace PackingProof.QQBot.Tests;

public sealed class WindowsStartupTests
{
    [Fact]
    public void SetEnabled_WritesRuntimeLauncherAndBackgroundArgument()
    {
        using var scratch = new TempDirectory();
        var registry = new FakeRegistry();
        const string executable = @"C:\Program Files\PackingProof QQBot\PackingProof.QQBot.exe";
        string runtimeRoot = Path.Combine(scratch.Path, "PackingProof", "runtime");
        string launcherPath = Path.Combine(scratch.Path, WindowsStartup.LauncherFileName);

        WindowsStartup.SetEnabled(
            true,
            executable,
            registry,
            dataDirectory: scratch.Path,
            runtimeRoot: runtimeRoot);

        Assert.True(WindowsStartup.IsEnabled(registry));
        Assert.Equal(WindowsStartup.BuildCommand(executable, runtimeRoot, launcherPath), registry.Value);
        Assert.Contains("//B", registry.Value);
        Assert.Contains("//NoLogo", registry.Value);
        Assert.Contains($"\"{executable}\"", registry.Value);
        Assert.Contains($"\"{runtimeRoot}\"", registry.Value);
        Assert.EndsWith("--background", registry.Value);
        Assert.True(File.Exists(launcherPath));
        Assert.Equal(
            runtimeRoot,
            File.ReadAllText(Path.Combine(scratch.Path, WindowsStartup.RuntimeRootFileName), Encoding.UTF8));
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
        using var scratch = new TempDirectory();
        var registry = new FakeRegistry { Value = "\"D:\\old-version\\PackingProof.QQBot.exe\" --background" };
        string runtimeRoot = Path.Combine(scratch.Path, "PackingProof", "runtime");

        WindowsStartup.RefreshIfEnabled(
            true,
            @"D:\new-version\PackingProof.QQBot.exe",
            registry,
            dataDirectory: scratch.Path,
            runtimeRoot: runtimeRoot);

        Assert.Contains("\"D:\\new-version\\PackingProof.QQBot.exe\"", registry.Value);
        Assert.Contains($"\"{runtimeRoot}\"", registry.Value);
        Assert.EndsWith("--background", registry.Value);
    }

    [Fact]
    public void RefreshIfEnabled_DoesNothingWhenStartupIsDisabled()
    {
        var registry = new FakeRegistry();

        WindowsStartup.RefreshIfEnabled(false, @"D:\new-version\PackingProof.QQBot.exe", registry);

        Assert.Null(registry.Value);
    }

    [Fact]
    public void RefreshIfEnabled_ReusesLastKnownRuntimeWhenCurrentProcessHasNoEnvironmentValue()
    {
        using var scratch = new TempDirectory();
        var registry = new FakeRegistry();
        string runtimeRoot = Path.Combine(scratch.Path, "PackingProof", "runtime");
        WindowsStartup.SetEnabled(
            true,
            @"D:\old-version\PackingProof.QQBot.exe",
            registry,
            dataDirectory: scratch.Path,
            runtimeRoot: runtimeRoot);

        WindowsStartup.RefreshIfEnabled(
            true,
            @"D:\new-version\PackingProof.QQBot.exe",
            registry,
            dataDirectory: scratch.Path,
            runtimeRoot: "");

        Assert.Contains("\"D:\\new-version\\PackingProof.QQBot.exe\"", registry.Value);
        Assert.Contains($"\"{runtimeRoot}\"", registry.Value);
    }

    [Fact]
    public void GeneratedLauncher_PassesDotNetRootToTheStartedProgram()
    {
        using var scratch = new TempDirectory();
        string runtimeRoot = Path.Combine(scratch.Path, "PackingProof runtime path");
        string target = Path.Combine(scratch.Path, "write-environment.cmd");
        string output = Path.Combine(scratch.Path, "dotnet-root.txt");
        File.WriteAllText(
            target,
            "@echo off\r\n>\"%~dp0dotnet-root.txt\" echo %DOTNET_ROOT%\r\n",
            new UTF8Encoding(false));
        string launcherPath = WindowsStartup.WriteLauncherScript(scratch.Path);

        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "wscript.exe"),
            UseShellExecute = false,
            ArgumentList =
            {
                "//B",
                "//NoLogo",
                launcherPath,
                target,
                runtimeRoot,
                "--background"
            }
        }) ?? throw new InvalidOperationException("无法启动 Windows Script Host");

        Assert.True(process.WaitForExit(5000));
        for (int attempt = 0; attempt < 50 && !File.Exists(output); attempt++)
            Thread.Sleep(100);
        Assert.True(File.Exists(output), "启动脚本没有运行目标程序");
        Assert.Equal(
            runtimeRoot,
            File.ReadAllText(output, Encoding.UTF8).TrimEnd('\r', '\n'));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "packingproof-qqbot-startup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }

    private sealed class FakeRegistry : WindowsStartup.IWindowsStartupRegistry
    {
        public string? Value { get; set; }
        public string? GetValue(string valueName) => Value;
        public void SetValue(string valueName, string value) => Value = value;
        public void DeleteValue(string valueName) => Value = null;
    }
}
