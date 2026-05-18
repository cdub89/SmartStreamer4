using Avalonia;
using System;
using SDRIQStreamer.CWSkimmer;

namespace SDRIQStreamer.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before any AppSettingsStore load or log file open so the
        // legacy %APPDATA%\SDRIQStreamer\ folder is renamed to
        // %APPDATA%\SmartStreamer4\ atomically (issue #34) while no file
        // handles are held by this process.
        AppDataPaths.EnsureMigrated();

        // Hand the resolved root to the CWSkimmer module so its INIs and logs
        // land in the same folder the root app uses. Matters in the locked-
        // fallback path where AppDataPaths.Root is the legacy folder for the
        // session — without this, the CWSkimmer module would write to the new
        // (empty) folder and split-brain against the root app (issue #34).
        // Must come before any code touches CwSkimmerLauncher, whose static
        // IniDir field initializer calls RuntimePathResolver.
        RuntimePathResolver.AppDataRootOverride = AppDataPaths.Root;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
