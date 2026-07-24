namespace SDRIQStreamer.CWSkimmer;

public static class RuntimePathResolver
{
    /// <summary>
    /// Per-user appdata folder the root app resolved at startup (typically
    /// <c>%APPDATA%\SmartStreamer4</c>, or <c>%APPDATA%\SDRIQStreamer</c> in the
    /// locked-fallback case when the issue #34 rename couldn't run). The root
    /// app sets this in <c>Program.Main</c> after <c>AppDataPaths.EnsureMigrated</c>
    /// so this module writes INIs and logs to the same folder the root app uses.
    /// When null (tests, standalone consumers), the deployed-path branch falls
    /// back to a hardcoded <c>%APPDATA%\SmartStreamer4</c> path.
    /// </summary>
    public static string? AppDataRootOverride { get; set; }

    public static string ResolveCwSkimmerIniDir()
        => Path.Combine(ResolveArtifactsRoot(), "cwskimmer", "ini");

    public static string ResolveLogsDir()
        => Path.Combine(ResolveArtifactsRoot(), "logs");

    private static string ResolveArtifactsRoot()
        => ResolveArtifactsRoot(AppContext.BaseDirectory, Environment.CurrentDirectory);

    internal static string ResolveArtifactsRoot(string baseDir, string currentDir)
    {
        var repoRoot = TryFindRepoRoot(new DirectoryInfo(baseDir))
            ?? TryFindRepoRoot(new DirectoryInfo(currentDir));

        if (repoRoot is not null)
            return Path.Combine(repoRoot.FullName, "artifacts");

        var appDataRoot = AppDataRootOverride
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SmartStreamer4");
        return Path.Combine(appDataRoot, "artifacts");
    }

    private static DirectoryInfo? TryFindRepoRoot(DirectoryInfo? start)
    {
        var current = start;
        while (current is not null)
        {
            // Repo-root marker is the app csproj. (A second marker,
            // SmartSDRIQStreamer.slnx, was removed 2026-07-23 when the .slnx
            // was replaced by SmartStreamer4.sln; see issue #50.)
            if (File.Exists(Path.Combine(current.FullName, "SmartSDRIQStreamer.csproj")))
                return current;

            current = current.Parent;
        }

        return null;
    }
}
