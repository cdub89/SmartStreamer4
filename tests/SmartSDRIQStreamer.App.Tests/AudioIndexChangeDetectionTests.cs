using System.Collections.Generic;
using SDRIQStreamer.App;
using SDRIQStreamer.CWSkimmer;

namespace SmartSDRIQStreamer.App.Tests;

public sealed class AudioIndexChangeDetectionTests
{
    // Live-test fixtures captured 2026-05-18.
    // Dallas PC, SmartSDR 4.2.18 + local dev build.
    private static readonly int[] DallasMme = [13, 8, 9, 7];

    // Lake PC, SmartSDR 4.2.18 + v0.1.18b release (after upgrading from 4.1.5).
    private static readonly int[] LakeMme = [15, 4, 14, 5];

    [Fact]
    public void Probe_FirstRunOnDallas_RecordsBaselineAndReportsFirstRun()
    {
        var finder = new FakeFinder(DallasMme);
        var settings = new AppSettings();

        var result = AudioIndexChangeDetection.Probe(channel: 1, finder, settings);

        Assert.True(result.WasFirstRun);
        Assert.False(result.AnyChanged);
        Assert.Equal(13, result.CurrentMme);
        Assert.Null(result.PriorMme);

        AudioIndexChangeDetection.CommitBaseline(settings, result);
        Assert.Equal(13, settings.LastSeenMmeDeviceIndexCh1);
    }

    [Fact]
    public void Probe_BaselinedDallasUnchanged_NoChangeReported()
    {
        var finder = new FakeFinder(DallasMme);
        var settings = SeedBaseline(DallasMme);

        for (int ch = 1; ch <= 4; ch++)
        {
            var result = AudioIndexChangeDetection.Probe(ch, finder, settings);
            Assert.False(result.WasFirstRun);
            Assert.False(result.MmeChanged);
            Assert.False(result.AnyChanged);
        }
    }

    [Fact]
    public void Probe_DallasBaseline_LakeCurrent_AllChannelsReportChange()
    {
        // Simulates a SmartSDR major-version upgrade reshuffling the device
        // ordering; same operator, same hardware shift seen on Lake when
        // upgrading from 4.1.5 to 4.2.18.
        var finder = new FakeFinder(LakeMme);
        var settings = SeedBaseline(DallasMme);

        for (int ch = 1; ch <= 4; ch++)
        {
            var result = AudioIndexChangeDetection.Probe(ch, finder, settings);
            Assert.False(result.WasFirstRun);
            Assert.True(result.MmeChanged);
            Assert.True(result.AnyChanged);
            Assert.Equal(DallasMme[ch - 1], result.PriorMme);
            Assert.Equal(LakeMme[ch - 1], result.CurrentMme);
        }
    }

    [Fact]
    public void Probe_FinderReturnsMinusOne_ProbeUnavailable()
    {
        // DAX not running, channel not yet exposed.
        var finder = new FakeFinder([-1, -1, -1, -1]);
        var settings = SeedBaseline(DallasMme);

        var result = AudioIndexChangeDetection.Probe(1, finder, settings);

        Assert.True(result.ProbeUnavailable);
    }

    [Fact]
    public void CommitBaseline_NegativeProbeValue_DoesNotPoisonBaseline()
    {
        var settings = SeedBaseline(DallasMme);
        var result = new AudioIndexChangeDetection.Result(
            Channel: 1,
            WasFirstRun: false,
            CurrentMme: -1,
            PriorMme: 13);

        AudioIndexChangeDetection.CommitBaseline(settings, result);

        Assert.Equal(13, settings.LastSeenMmeDeviceIndexCh1); // unchanged
    }

    [Fact]
    public void GetLastSeenMme_OutOfRangeChannel_ReturnsNull()
    {
        var settings = SeedBaseline(DallasMme);
        Assert.Null(AudioIndexChangeDetection.GetLastSeenMme(settings, 5));
        Assert.Null(AudioIndexChangeDetection.GetLastSeenMme(settings, 0));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AppSettings SeedBaseline(int[] mmeByChannel) => new()
    {
        LastSeenMmeDeviceIndexCh1 = mmeByChannel[0],
        LastSeenMmeDeviceIndexCh2 = mmeByChannel[1],
        LastSeenMmeDeviceIndexCh3 = mmeByChannel[2],
        LastSeenMmeDeviceIndexCh4 = mmeByChannel[3],
    };

    private sealed class FakeFinder : IAudioDeviceFinder
    {
        private readonly int[] _mmeByChannel;
        public FakeFinder(int[] mmeByChannel) => _mmeByChannel = mmeByChannel;

        public int FindDaxIqSignalDeviceIndex(int channel) =>
            channel is >= 1 and <= 4 ? _mmeByChannel[channel - 1] : -1;

        public int FindSignalDeviceIndex(string nameFragment) => -1;
        public int FindAudioDeviceIndex(string nameFragment) => -1;
        public IReadOnlyList<(int CwSkimmerIndex, string Name)> ListAllSignalDevices() => [];
        public IReadOnlyList<(int CwSkimmerIndex, string Name)> ListAllAudioDevices() => [];
    }
}
