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

    // ── SmartDeck window placement (issue #59) ───────────────────────────────

    [Fact]
    public void SmartDeckPlacement_DefaultsToNeverPositioned()
    {
        var settings = new AppSettings();

        // Null rather than 0: 0,0 is a real window position on a multi-monitor
        // desktop, so "never positioned" needs its own state.
        Assert.Null(settings.SmartDeckX);
        Assert.Null(settings.SmartDeckY);
        Assert.Null(settings.SmartDeckWidth);
        Assert.Null(settings.SmartDeckHeight);
        Assert.False(settings.SmartDeckAlwaysOnTop);
    }

    [Fact]
    public void SmartDeckPlacement_RoundTrips()
    {
        var restored = RoundTrip(new AppSettings
        {
            SmartDeckX = 120,
            SmartDeckY = 340,
            SmartDeckWidth = 420,
            SmartDeckHeight = 118,
            SmartDeckAlwaysOnTop = true
        });

        Assert.Equal(120, restored.SmartDeckX);
        Assert.Equal(340, restored.SmartDeckY);
        Assert.Equal(420, restored.SmartDeckWidth);
        Assert.Equal(118, restored.SmartDeckHeight);
        Assert.True(restored.SmartDeckAlwaysOnTop);
    }

    [Fact]
    public void SmartDeckPlacement_RoundTripsAZeroOriginAsZeroNotAbsent()
    {
        var restored = RoundTrip(new AppSettings { SmartDeckX = 0, SmartDeckY = 0 });

        Assert.Equal(0, restored.SmartDeckX);
        Assert.Equal(0, restored.SmartDeckY);
    }

    [Fact]
    public void SmartDeckBandMemory_DefaultsToEmpty()
    {
        Assert.Empty(new AppSettings().SmartDeckBandMemoryMhz);
    }

    [Fact]
    public void SmartDeckBandMemory_RoundTrips()
    {
        var settings = new AppSettings();
        settings.SmartDeckBandMemoryMhz["20m"] = 14.031_5;
        settings.SmartDeckBandMemoryMhz["40m"] = 7.118;

        var restored = RoundTrip(settings);

        Assert.Equal(14.031_5, restored.SmartDeckBandMemoryMhz["20m"]);
        Assert.Equal(7.118, restored.SmartDeckBandMemoryMhz["40m"]);
    }

    [Fact]
    public void SmartDeckBandMemory_MissingFromJson_LoadsAsEmptyNotNull()
    {
        // Settings files written before issue #59 phase 2b have no band memory;
        // BandMemory mutates this dictionary in place, so it must never be null.
        var restored = JsonSerializer.Deserialize<AppSettings>("{}")!;

        Assert.NotNull(restored.SmartDeckBandMemoryMhz);
        Assert.Empty(restored.SmartDeckBandMemoryMhz);
    }

    [Fact]
    public void SmartDeckPlacement_MissingFromJson_LoadsAsNeverPositioned()
    {
        // Settings files written before issue #59 have no SmartDeck properties.
        var restored = JsonSerializer.Deserialize<AppSettings>("{}")!;

        Assert.Null(restored.SmartDeckX);
        Assert.False(restored.SmartDeckAlwaysOnTop);
    }
}
