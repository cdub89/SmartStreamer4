using Avalonia;
using System;

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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
