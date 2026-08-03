using System.Text.Json;
using SDRIQStreamer.App;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// JSON round-trip coverage for AppSettings fields (the CLAUDE.md-required
/// load/save test for new settings). Serialization-level rather than through
/// AppSettingsStore because the store's path is fixed to the real AppData
/// folder; the store adds no mapping logic beyond System.Text.Json.
/// </summary>
public sealed class AppSettingsRoundTripTests
{
    private static AppSettings RoundTrip(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

    [Fact]
    public void DebugLoggingEnabled_DefaultsToFalse()
    {
        Assert.False(new AppSettings().DebugLoggingEnabled);
    }

    [Fact]
    public void DebugLoggingEnabled_RoundTripsWhenEnabled()
    {
        var restored = RoundTrip(new AppSettings { DebugLoggingEnabled = true });

        Assert.True(restored.DebugLoggingEnabled);
    }

    [Fact]
    public void DebugLoggingEnabled_MissingFromJson_LoadsAsFalse()
    {
        // Settings files written by releases predating issue #58 have no
        // DebugLoggingEnabled property; they must load with the gate off.
        var restored = JsonSerializer.Deserialize<AppSettings>("{}")!;

        Assert.False(restored.DebugLoggingEnabled);
    }
}
