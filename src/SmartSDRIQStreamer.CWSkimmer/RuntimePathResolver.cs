namespace SDRIQStreamer.CWSkimmer;

internal static class RuntimePathResolver
{
    public static string ResolveCwSkimmerIniDir()
        => Path.Combine(ResolveArtifactsRoot(), "cwskimmer", "ini");

    public static string ResolveLogsDir()
        => Path.Combine(ResolveArtifactsRoot(), "logs");

    private static string ResolveArtifactsRoot()
    {
        var repoRoot = TryFindRepoRoot(new DirectoryInfo(AppContext.BaseDirectory))
            ?? TryFindRepoRoot(new DirectoryInfo(Environment.CurrentDirectory));

        if (repoRoot is not null)
            return Path.Combine(repoRoot.FullName, "artifacts");

        // Folder name kept in sync with AppDataPaths.CurrentFolderName in the
        // root project (issue #34 rename). The CWSkimmer module is a library
        // and can't reference the root project, so the name is duplicated; the
        // root project performs the legacy-to-current rename at startup before
        // any code here runs, so this resolver always sees the current folder.
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SmartStreamer4");
        return Path.Combine(appDataRoot, "artifacts");
    }

    private static DirectoryInfo? TryFindRepoRoot(DirectoryInfo? start)
    {
        var current = start;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SmartSDRIQStreamer.csproj")) ||
                File.Exists(Path.Combine(current.FullName, "SmartSDRIQStreamer.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }
}
