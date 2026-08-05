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

    // ── Appearance (issue #63) ───────────────────────────────────────────────

    [Fact]
    public void ThemeMode_DefaultsToLight()
    {
        // Deliberately not "follow the OS": two named options, and an operator
        // whose desktop is dark presses Theme once.
        Assert.Equal(AppTheme.Light, new AppSettings().ThemeMode);
    }

    [Fact]
    public void ThemeMode_RoundTrips()
    {
        Assert.Equal(AppTheme.Dark, RoundTrip(new AppSettings { ThemeMode = AppTheme.Dark }).ThemeMode);
    }

    [Fact]
    public void ThemeMode_PersistsByNameNotOrdinal()
    {
        // Same reasoning as BandState.Mode: the store has no global string-enum
        // converter, and an ordinal would silently remap if AppTheme were ever
        // reordered or gained a member.
        var json = JsonSerializer.Serialize(new AppSettings { ThemeMode = AppTheme.Dark });

        Assert.Contains("\"ThemeMode\":\"Dark\"", json);
    }

    [Fact]
    public void ThemeMode_MissingFromJson_LoadsAsLight()
    {
        // Settings files written before issue #63 have no ThemeMode; they must
        // load as the look those releases actually shipped.
        Assert.Equal(AppTheme.Light, JsonSerializer.Deserialize<AppSettings>("{}")!.ThemeMode);
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
            SmartDeckAlwaysOnTop = true
        });

        Assert.Equal(120, restored.SmartDeckX);
        Assert.Equal(340, restored.SmartDeckY);
        Assert.Equal(420, restored.SmartDeckWidth);
        Assert.True(restored.SmartDeckAlwaysOnTop);
    }

    [Fact]
    public void SmartDeckHeight_FromAnOlderSettingsFile_IsIgnoredRatherThanMigrated()
    {
        // The deck sizes itself to its content now (issue #63), so height is no
        // longer a setting. System.Text.Json ignores unknown properties on read,
        // which is the whole migration: an existing file keeps the dead key and
        // the deck simply stops honouring it.
        var restored = JsonSerializer.Deserialize<AppSettings>(
            """{"SmartDeckWidth":420,"SmartDeckHeight":362}""")!;

        Assert.Equal(420, restored.SmartDeckWidth);
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
