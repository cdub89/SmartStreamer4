using System;
using System.IO;
using SDRIQStreamer.CWSkimmer;

namespace SmartSDRIQStreamer.CWSkimmer.Tests;

public sealed class RuntimePathResolverTests : IDisposable
{
    private readonly string? _savedOverride;
    private readonly string _isolatedDir;

    public RuntimePathResolverTests()
    {
        _savedOverride = RuntimePathResolver.AppDataRootOverride;
        // Use a temp dir that doesn't contain SmartSDRIQStreamer.csproj
        // so the resolver's repo-root detection falls through to the appdata
        // branch (the path we're actually testing).
        _isolatedDir = Path.Combine(Path.GetTempPath(), "RuntimePathResolverTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_isolatedDir);
    }

    public void Dispose()
    {
        RuntimePathResolver.AppDataRootOverride = _savedOverride;
        try
        {
            if (Directory.Exists(_isolatedDir))
                Directory.Delete(_isolatedDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void DeployedPath_RespectsAppDataRootOverride()
    {
        var fakeAppData = Path.Combine(_isolatedDir, "FakeAppDataRoot");
        RuntimePathResolver.AppDataRootOverride = fakeAppData;

        var artifacts = RuntimePathResolver.ResolveArtifactsRoot(_isolatedDir, _isolatedDir);

        Assert.Equal(Path.Combine(fakeAppData, "artifacts"), artifacts);
    }

    [Fact]
    public void DeployedPath_FallsBackToSmartStreamer4_WhenOverrideIsNull()
    {
        RuntimePathResolver.AppDataRootOverride = null;

        var artifacts = RuntimePathResolver.ResolveArtifactsRoot(_isolatedDir, _isolatedDir);

        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SmartStreamer4",
            "artifacts");
        Assert.Equal(expectedRoot, artifacts);
    }

    [Fact]
    public void RepoRootPath_IgnoresOverride_WhenRepoMarkerFound()
    {
        var repoRoot = Path.Combine(_isolatedDir, "fake-repo");
        Directory.CreateDirectory(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, "SmartSDRIQStreamer.csproj"), "<Project/>");

        // Override is set, but repo-root detection takes priority for the
        // dev/test scenario (running from inside a checkout).
        RuntimePathResolver.AppDataRootOverride = Path.Combine(_isolatedDir, "ShouldBeIgnored");

        var artifacts = RuntimePathResolver.ResolveArtifactsRoot(repoRoot, _isolatedDir);

        Assert.Equal(Path.Combine(repoRoot, "artifacts"), artifacts);
    }

    [Fact]
    public void TryFindRepoRootPath_ReturnsNull_OutsideACheckout()
    {
        Assert.Null(RuntimePathResolver.TryFindRepoRootPath(_isolatedDir, _isolatedDir));
    }

    [Fact]
    public void TryFindRepoRootPath_WalksUpToTheMarkerFromANestedStartDir()
    {
        var repoRoot = Path.Combine(_isolatedDir, "fake-repo");
        var nested = Path.Combine(repoRoot, "bin", "Debug");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(repoRoot, "SmartSDRIQStreamer.csproj"), "<Project/>");

        Assert.Equal(repoRoot, RuntimePathResolver.TryFindRepoRootPath(nested, _isolatedDir));
    }

    [Fact]
    public void TryFindRepoRootPath_FallsBackToCurrentDir_WhenBaseDirHasNoMarker()
    {
        var repoRoot = Path.Combine(_isolatedDir, "fake-repo");
        Directory.CreateDirectory(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, "SmartSDRIQStreamer.csproj"), "<Project/>");

        Assert.Equal(repoRoot, RuntimePathResolver.TryFindRepoRootPath(_isolatedDir, repoRoot));
    }
}
