using PackingProof.QQBot.Bootstrap;

namespace PackingProof.QQBot.Tests;

public sealed class BootstrapRuntimeLocatorTests
{
    [Fact]
    public void Resolve_UsesInjectedDotNetRoot()
    {
        using var scratch = new TempDirectory();
        string runtimeRoot = CreateRuntimeLayout(scratch.Path);

        string resolved = RuntimeLocator.Resolve(runtimeRoot, scratch.Path, scratch.Path);

        Assert.Equal(runtimeRoot, resolved);
    }

    [Fact]
    public void Resolve_UsesRememberedRuntimeWhenEnvironmentValueIsMissing()
    {
        using var scratch = new TempDirectory();
        string runtimeRoot = CreateRuntimeLayout(scratch.Path);
        RuntimeLocator.Remember(runtimeRoot, scratch.Path);

        string resolved = RuntimeLocator.Resolve(null, scratch.Path, scratch.Path);

        Assert.Equal(runtimeRoot, resolved);
    }

    [Fact]
    public void ResolveDataDirectory_UsesInjectedOrCanonicalLocation()
    {
        string localAppData = Path.Combine(Path.GetTempPath(), "local-app-data");
        string injected = Path.Combine(Path.GetTempPath(), "injected-data");

        Assert.Equal(Path.GetFullPath(injected), RuntimeLocator.ResolveDataDirectory(injected, localAppData));
        Assert.Equal(
            Path.Combine(localAppData, "ExpressPackingMonitoring", "extension-data", "packingproof.qqbot"),
            RuntimeLocator.ResolveDataDirectory(null, localAppData));
    }

    [Fact]
    public void IsUsable_RequiresSharedDesktopRuntimeLayout()
    {
        using var scratch = new TempDirectory();
        string runtimeRoot = CreateRuntimeLayout(scratch.Path);

        Assert.True(RuntimeLocator.IsUsable(runtimeRoot));
        Assert.False(RuntimeLocator.IsUsable(Path.Combine(scratch.Path, "missing")));
    }

    [Fact]
    public void CreateStartInfo_StartsManagedAppWithRuntimeAndArguments()
    {
        string directory = Path.Combine(Path.GetTempPath(), "qqbot-bootstrap");
        string runtimeRoot = Path.Combine(directory, "runtime");

        var startInfo = PackingProof.QQBot.Bootstrap.Program.CreateStartInfo(
            directory,
            runtimeRoot,
            ["--background"]);

        Assert.Equal(Path.Combine(directory, "PackingProof.QQBot.Managed.exe"), startInfo.FileName);
        Assert.Equal(directory, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(["--background"], startInfo.ArgumentList);
        Assert.Equal(runtimeRoot, startInfo.Environment["DOTNET_ROOT"]);
    }

    private static string CreateRuntimeLayout(string root)
    {
        string runtimeRoot = Path.Combine(root, "install", "runtime");
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "host", "fxr", "10.0.0"));
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "shared", "Microsoft.NETCore.App", "10.0.0"));
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "shared", "Microsoft.WindowsDesktop.App", "10.0.0"));
        return runtimeRoot;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "packingproof-qqbot-bootstrap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
