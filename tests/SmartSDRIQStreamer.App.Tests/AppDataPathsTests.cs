using System;
using System.IO;
using SDRIQStreamer.App;

namespace SmartSDRIQStreamer.App.Tests;

public sealed class AppDataPathsTests : IDisposable
{
    private readonly string _appDataRoot;

    public AppDataPathsTests()
    {
        AppDataPaths.ResetForTests();
        _appDataRoot = Path.Combine(Path.GetTempPath(), "SmartStreamer4-AppDataPathsTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_appDataRoot);
    }

    public void Dispose()
    {
        AppDataPaths.ResetForTests();
        try
        {
            if (Directory.Exists(_appDataRoot))
                Directory.Delete(_appDataRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup; a stray temp folder is preferable to a flaky test
        }
    }

    [Fact]
    public void FreshInstall_UsesCurrentFolder_AndEmitsNoMessage()
    {
        AppDataPaths.EnsureMigrated(_appDataRoot, Directory.Move);

        Assert.Equal(Path.Combine(_appDataRoot, AppDataPaths.CurrentFolderName), AppDataPaths.Root);
        Assert.Empty(AppDataPaths.DrainMigrationMessages());
    }

    [Fact]
    public void LegacyOnly_RenamesAtomically_AndEmitsSuccessMessage()
    {
        var legacy = Path.Combine(_appDataRoot, AppDataPaths.LegacyFolderName);
        var current = Path.Combine(_appDataRoot, AppDataPaths.CurrentFolderName);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

        AppDataPaths.EnsureMigrated(_appDataRoot, Directory.Move);

        Assert.Equal(current, AppDataPaths.Root);
        Assert.False(Directory.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(current, "settings.json")));
        var messages = AppDataPaths.DrainMigrationMessages();
        Assert.Single(messages);
        Assert.Contains("renamed", messages[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlreadyMigrated_UsesCurrentFolder_AndEmitsNoMessage()
    {
        var current = Path.Combine(_appDataRoot, AppDataPaths.CurrentFolderName);
        Directory.CreateDirectory(current);

        AppDataPaths.EnsureMigrated(_appDataRoot, Directory.Move);

        Assert.Equal(current, AppDataPaths.Root);
        Assert.Empty(AppDataPaths.DrainMigrationMessages());
    }

    [Fact]
    public void BothPresent_UsesCurrentFolder_AndWarns()
    {
        var legacy = Path.Combine(_appDataRoot, AppDataPaths.LegacyFolderName);
        var current = Path.Combine(_appDataRoot, AppDataPaths.CurrentFolderName);
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(current);

        AppDataPaths.EnsureMigrated(_appDataRoot, Directory.Move);

        Assert.Equal(current, AppDataPaths.Root);
        Assert.True(Directory.Exists(legacy));
        var messages = AppDataPaths.DrainMigrationMessages();
        Assert.Single(messages);
        Assert.Contains("Both", messages[0], StringComparison.Ordinal);
    }

    [Fact]
    public void LockedLegacy_FallsBackToLegacy_AndWarns()
    {
        var legacy = Path.Combine(_appDataRoot, AppDataPaths.LegacyFolderName);
        Directory.CreateDirectory(legacy);

        AppDataPaths.EnsureMigrated(_appDataRoot, (_, _) => throw new IOException("simulated lock"));

        Assert.Equal(legacy, AppDataPaths.Root);
        Assert.True(Directory.Exists(legacy));
        var messages = AppDataPaths.DrainMigrationMessages();
        Assert.Single(messages);
        Assert.Contains("rename failed", messages[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry next launch", messages[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureMigrated_IsIdempotent_OnSecondCall()
    {
        var legacy = Path.Combine(_appDataRoot, AppDataPaths.LegacyFolderName);
        Directory.CreateDirectory(legacy);

        AppDataPaths.EnsureMigrated(_appDataRoot, Directory.Move);
        var firstRoot = AppDataPaths.Root;

        AppDataPaths.EnsureMigrated(_appDataRoot, (_, _) => throw new InvalidOperationException("must not be called again"));

        Assert.Equal(firstRoot, AppDataPaths.Root);
    }
}
