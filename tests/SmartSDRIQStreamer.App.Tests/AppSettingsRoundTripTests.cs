using System.Text.Json;
using SDRIQStreamer.App;
using SDRIQStreamer.FlexRadio;

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
        Assert.Empty(new AppSettings().SmartDeckBandMemory);
    }

    [Fact]
    public void SmartDeckBandMemory_RoundTrips()
    {
        var settings = new AppSettings();
        settings.SmartDeckBandMemory["20m"] = new(14.031_5, SliceMode.Cw, "ANT2", "ANT1", 65);
        settings.SmartDeckBandMemory["40m"] = new(7.118, SliceMode.Lsb, "ANT1", "ANT1", 30);

        var restored = RoundTrip(settings);

        Assert.Equal(new BandState(14.031_5, SliceMode.Cw, "ANT2", "ANT1", 65), restored.SmartDeckBandMemory["20m"]);
        Assert.Equal(new BandState(7.118, SliceMode.Lsb, "ANT1", "ANT1", 30), restored.SmartDeckBandMemory["40m"]);
    }

    [Fact]
    public void SmartDeckBandMemory_KeepsAbsentFieldsAbsentAcrossTheWire()
    {
        // Absent is not the same as any real value: a band that has only ever
        // been tuned, never left, must come back with nothing but a frequency
        // so the restore leaves mode and antennas alone.
        var settings = new AppSettings();
        settings.SmartDeckBandMemory["30m"] = new(10.120);

        var restored = RoundTrip(settings).SmartDeckBandMemory["30m"];

        Assert.Equal(10.120, restored.FreqMhz);
        Assert.Null(restored.Mode);
        Assert.Null(restored.RxAntenna);
        Assert.Null(restored.TxAntenna);
        Assert.Null(restored.AgcThreshold);
    }

    [Fact]
    public void SmartDeckBandMemory_PersistsModeByNameNotOrdinal()
    {
        // The settings store has no global string-enum converter, so BandState
        // carries its own. Persisting an ordinal would silently remap every
        // saved band if SliceMode were ever reordered. The persisted form is
        // the member name (Cw), not the radio wire value (CW): what matters is
        // that it is a stable name rather than a position.
        var settings = new AppSettings();
        settings.SmartDeckBandMemory["20m"] = new(14.031_5, SliceMode.Cw);

        var json = JsonSerializer.Serialize(settings);

        Assert.Contains("\"Cw\"", json);
        Assert.DoesNotContain("\"Mode\":0", json);
    }

    [Fact]
    public void SmartDeckBandMemory_MissingFromJson_LoadsAsEmptyNotNull()
    {
        // Settings files written before issue #59 phase 2b have no band memory,
        // and those written before 2026-08-03 carry the superseded
        // SmartDeckBandMemoryMhz key instead, which is deliberately left unread.
        // BandMemory mutates this dictionary in place, so it must never be null.
        var restored = JsonSerializer.Deserialize<AppSettings>(
            """{"SmartDeckBandMemoryMhz":{"20m":14.0315}}""")!;

        Assert.NotNull(restored.SmartDeckBandMemory);
        Assert.Empty(restored.SmartDeckBandMemory);
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
