using System.IO;
using System.Text.Json;

namespace SDRIQStreamer.App;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in
/// <c>%AppData%\SmartStreamer4\settings.json</c> (legacy
/// <c>%AppData%\SDRIQStreamer\</c> path is renamed at startup by
/// <see cref="AppDataPaths"/>).
/// </summary>
public sealed class AppSettingsStore
{
    private static string FilePath => Path.Combine(AppDataPaths.Root, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch { /* corrupt or missing — return defaults */ }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            if (Path.GetDirectoryName(FilePath) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch { /* non-fatal */ }
    }
}
