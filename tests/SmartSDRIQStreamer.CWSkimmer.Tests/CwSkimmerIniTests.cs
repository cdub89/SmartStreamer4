using SDRIQStreamer.CWSkimmer;

namespace SDRIQStreamer.CWSkimmer.Tests;

/// <summary>
/// Fake IAudioDeviceFinder for unit tests — no real audio hardware required.
/// Uses Contains matching so tests can register full device names and query by prefix.
/// </summary>
internal sealed class FakeAudioDeviceFinder : IAudioDeviceFinder
{
    private readonly Dictionary<string, int> _signalDevices;
    private readonly Dictionary<string, int> _audioDevices;

    public FakeAudioDeviceFinder(
        Dictionary<string, int> signalDevices,
        Dictionary<string, int>? audioDevices = null)
    {
        _signalDevices = signalDevices;
        _audioDevices  = audioDevices ?? signalDevices;
    }

    public int FindSignalDeviceIndex(string nameFragment)
    {
        foreach (var (key, idx) in _signalDevices)
            if (key.StartsWith(nameFragment, StringComparison.OrdinalIgnoreCase))
                return idx;
        return -1;
    }

    public int FindAudioDeviceIndex(string nameFragment)
    {
        foreach (var (key, idx) in _audioDevices)
            if (key.StartsWith(nameFragment, StringComparison.OrdinalIgnoreCase))
                return idx;
        return -1;
    }

    public int FindDaxIqSignalDeviceIndex(int channel)
    {
        int idx = FindSignalDeviceIndex($"DAX IQ {channel}");
        if (idx >= 0) return idx;
        return FindSignalDeviceIndex($"DAX IQ RX {channel}");
    }

    public IReadOnlyList<(int CwSkimmerIndex, string Name)> ListAllSignalDevices()
        => _signalDevices.Select(kv => (kv.Value, kv.Key)).ToList();

    public IReadOnlyList<(int CwSkimmerIndex, string Name)> ListAllAudioDevices()
        => _audioDevices.Select(kv => (kv.Value, kv.Key)).ToList();
}

public sealed class CwSkimmerIniWriterTests
{
    private static CwSkimmerConfig DefaultConfig() => new()
    {
        Callsign   = "WX7V",
        TelnetPort = 7310,
    };

    private static CwSkimmerIniModel MakeModel(
        int  wdmSignal  = 8,
        int  wdmAudio   = 14,
        int  mmeSignal  = 0,
        int  mmeAudio   = 0,
        bool useWdm     = true,
        int  sampleRate = 48000,
        long centerFreq = 14_048_441L,
        CwSkimmerConfig? cfg = null)
        => new(
            WdmSignalDevIndex:          wdmSignal,
            WdmAudioDevIndex:           wdmAudio,
            MmeSignalDevIndex:          mmeSignal,
            MmeAudioDevIndex:           mmeAudio,
            UseWdm:                     useWdm,
            CalibrationBaseSignalIndex: wdmSignal,
            CalibrationBaseAudioIndex:  wdmAudio,
            SampleRateHz:               sampleRate,
            CenterFreqHz:               centerFreq,
            Config:                     cfg ?? DefaultConfig());

    // ── [Audio] section ───────────────────────────────────────────────────────

    [Fact]
    public void Write_DoesNotInjectWindowsSection_WhenMissing()
    {
        var text = WriteToString(MakeModel());

        Assert.DoesNotContain("[Windows]", text);
        Assert.DoesNotContain("Colors=1", text);
    }

    [Fact]
    public void Write_AudioSection_ContainsExpectedDeviceIndices()
    {
        var text = WriteToString(MakeModel(wdmSignal: 8, wdmAudio: 14));

        Assert.Contains("[Audio]",          text);
        Assert.Contains("WdmSignalDev=8",   text);
        Assert.Contains("WdmAudioDev=14",   text);
        Assert.Contains("MmeSignalDev=0",   text);
        Assert.Contains("MmeAudioDev=0",    text);
        Assert.Contains("UseWdm=1",         text);
    }

    // [Radio], [sdrSR], and [Recorder] are CW Skimmer-owned and intentionally preserved.

    // ── [Telnet] section ──────────────────────────────────────────────────────

    [Fact]
    public void Write_TelnetSection_DefaultsMatchReference()
    {
        var text = WriteToString(MakeModel());

        Assert.Contains("[Telnet]",            text);
        Assert.Contains("Port=7310",           text);
        Assert.Contains("PasswordRequired=0",  text);
        Assert.Contains("Password=",           text);
        Assert.Contains("AnnUserOnly=0",       text);
        Assert.Contains("AnnUser=",            text);
        Assert.Contains("TelnetSrvEnabled=1",  text);
        Assert.Contains("UdpEnabled=0",        text);
    }

    [Fact]
    public void Write_TelnetSection_NoPassword_WhenDisabled()
    {
        var cfg  = DefaultConfig() with { TelnetPasswordRequired = false };
        var text = WriteToString(MakeModel(cfg: cfg));

        Assert.Contains("PasswordRequired=0", text);
        Assert.Contains("AnnUserOnly=0",       text);
    }

    // ── Section ordering ──────────────────────────────────────────────────────

    [Fact]
    public void Write_SectionOrder_AudioBeforeTelnet()
    {
        var text   = WriteToString(MakeModel());
        int audio  = text.IndexOf("[Audio]",  StringComparison.Ordinal);
        int telnet = text.IndexOf("[Telnet]", StringComparison.Ordinal);

        Assert.True(audio < telnet, "[Audio] should precede [Telnet]");
    }

    [Fact]
    public void Write_PreservesWindowsAndRecorderSections_WhenAlreadyPresent()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
[Windows]
Left=418
Top=601
Width=546
Height=416
Colors=1

[Recorder]
WavCall=OLDCALL
WavOper=Old Name

[Audio]
WdmSignalDev=1
WdmAudioDev=2
""");

            var writer = new CwSkimmerIniWriter();
            writer.Write(MakeModel(), path);
            var text = File.ReadAllText(path);

            Assert.Contains("[Windows]",      text);
            Assert.Contains("Left=418",       text);
            Assert.Contains("Top=601",        text);
            Assert.Contains("Width=546",      text);
            Assert.Contains("Height=416",     text);

            Assert.Contains("[Recorder]",     text);
            Assert.Contains("WavCall=OLDCALL",  text);
            Assert.Contains("WavOper=Old Name", text);

            Assert.Contains("WdmSignalDev=8",  text);
            Assert.Contains("WdmAudioDev=14",  text);
            Assert.DoesNotContain("[sdrSR]",   text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static string WriteToString(CwSkimmerIniModel model)
    {
        var path   = Path.GetTempFileName();
        var writer = new CwSkimmerIniWriter();
        try
        {
            writer.Write(model, path);
            return File.ReadAllText(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// Tests for CwSkimmerTelnetClient's click-event parser.
/// No network connection required.
/// </summary>
public sealed class CwSkimmerTelnetClientTests
{
    [Theory]
    [InlineData(@"To ALL de SKIMMER 1234 : Clicked on ""W1AW"" at 14012.5",  14012.5)]
    [InlineData(@"To ALL de SKIMMER 9999 : Clicked on ""VK2GR"" at 7025.0",   7025.0)]
    [InlineData(@"To ALL de SKIMMER 0001 : Clicked on ""K1DBO"" at 3512.75",  3512.75)]
    public void ParseClickedOn_ValidLine_ReturnsFreqKhz(string line, double expectedKhz)
    {
        var result = CwSkimmerTelnetClient.ParseClickedOn(line);
        Assert.NotNull(result);
        Assert.Equal(expectedKhz, result!.Value, precision: 3);
    }

    [Theory]
    [InlineData("To ALL de SKIMMER 1234 : DX de W1AW 14012.5")]   // no "Clicked on"
    [InlineData("")]
    [InlineData("SKIMMER/LO_FREQ 7060000")]
    public void ParseClickedOn_NonClickLine_ReturnsNull(string line)
    {
        Assert.Null(CwSkimmerTelnetClient.ParseClickedOn(line));
    }

    [Fact]
    public void ParseDxSpot_ValidLine_ReturnsStructuredSpot()
    {
        const string line = "DX de K1ABC-#: 14015.3 9A3B 19 dB 25 WPM CQ 1534Z";
        var spot = CwSkimmerTelnetClient.ParseDxSpot(line);

        Assert.NotNull(spot);
        Assert.Equal(14015.3, spot!.FrequencyKhz, 3);
        Assert.Equal("9A3B",   spot.Callsign);
        Assert.Equal("K1ABC-#", spot.Spotter);
        Assert.Equal(19,  spot.SignalDb);
        Assert.Equal(25,  spot.SpeedWpm);
        Assert.Equal("19 dB 25 WPM CQ 1534Z", spot.Comment);
    }

    [Fact]
    public void ParseDxSpot_LeadingWhitespace_ReturnsStructuredSpot()
    {
        const string line = "   DX de K1ABC-#: 7054.95 W7XYZ 22 dB 31 WPM TEST 0910Z";
        var spot = CwSkimmerTelnetClient.ParseDxSpot(line);

        Assert.NotNull(spot);
        Assert.Equal(7054.95, spot!.FrequencyKhz, 3);
        Assert.Equal("W7XYZ", spot.Callsign);
        Assert.Equal(22, spot.SignalDb);
        Assert.Equal(31, spot.SpeedWpm);
    }

    [Fact]
    public void ParseDxSpot_TabDelimitedLine_ReturnsStructuredSpot()
    {
        const string line = "DX de N0CALL-#:\t14015.3\t9A3B\t19 dB\t25 WPM\tCQ\t1534Z";
        var spot = CwSkimmerTelnetClient.ParseDxSpot(line);

        Assert.NotNull(spot);
        Assert.Equal(14015.3,  spot!.FrequencyKhz, 3);
        Assert.Equal("9A3B",   spot.Callsign);
        Assert.Equal("N0CALL-#", spot.Spotter);
        Assert.Equal(19, spot.SignalDb);
        Assert.Equal(25, spot.SpeedWpm);
    }

    [Theory]
    [InlineData("DX de K1ABC-#: BADFREQ 9A3B 19 dB")]
    [InlineData("DX de K1ABC-#: 14015.3")]
    [InlineData("To ALL de SKIMMER 1234 : Clicked on \"W1AW\" at 14012.5")]
    public void ParseDxSpot_InvalidOrNonSpotLine_ReturnsNull(string line)
    {
        Assert.Null(CwSkimmerTelnetClient.ParseDxSpot(line));
    }
}

public sealed class CwSkimmerIniModelFactoryTests
{
    // DAX v2 device set: UI indices (1-based) are non-sequential due to interleaved
    // DAX RX and non-DAX devices — exactly the scenario the new design must handle.
    // IQ 1 → UI 13, IQ 2 → UI 16, IQ 3 → UI 19, IQ 4 → UI 22 (gaps of 3).
    private static FakeAudioDeviceFinder DaxV2Finder() => new(new Dictionary<string, int>
    {
        { "DAX IQ 1 (FlexRadio DAX)", 13 },
        { "DAX IQ 2 (FlexRadio DAX)", 16 },
        { "DAX IQ 3 (FlexRadio DAX)", 19 },
        { "DAX IQ 4 (FlexRadio DAX)", 22 },
    });

    // DAX v1 device set: "RX" in name, different vendor suffix.
    // IQ 1 → UI 8, IQ 2 → UI 11, IQ 3 → UI 14, IQ 4 → UI 17 (gaps of 3).
    private static FakeAudioDeviceFinder DaxV1Finder() => new(new Dictionary<string, int>
    {
        { "DAX IQ RX 1 (FlexRadio Systems DAX IQ)", 8  },
        { "DAX IQ RX 2 (FlexRadio Systems DAX IQ)", 11 },
        { "DAX IQ RX 3 (FlexRadio Systems DAX IQ)", 14 },
        { "DAX IQ RX 4 (FlexRadio Systems DAX IQ)", 17 },
    });

    // Empty finder — simulates WinMM enumeration finding nothing.
    private static FakeAudioDeviceFinder EmptyFinder() => new(new Dictionary<string, int>());

    private static string WriteIni(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }

    // ── MME-only auto-derivation: direct per-channel WinMM name lookup ───────

    [Fact]
    public void Build_Channel1_DaxV2_UsesRuntimeWinMMLookup()
    {
        var path = WriteIni("""
[Audio]
WdmSignalDev=19
WdmAudioDev=14
MmeSignalDev=12
MmeAudioDev=0
UseWdm=1
""");
        try
        {
            var factory = new CwSkimmerIniModelFactory(DaxV2Finder());
            var model   = factory.Build(1, 48000, 14_048_441L, new CwSkimmerConfig { SkimmerIniPath = path });

            // UI 13 → INI 12
            Assert.Equal(12, model.MmeSignalDevIndex);
            Assert.Equal(0,  model.MmeAudioDevIndex);   // copied verbatim from master
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Build_Channel2_DaxV2_LooksUpEachChannelIndependently()
    {
        var path = WriteIni("""
[Audio]
WdmSignalDev=19
WdmAudioDev=14
MmeSignalDev=12
MmeAudioDev=0
UseWdm=1
""");
        try
        {
            var factory = new CwSkimmerIniModelFactory(DaxV2Finder());
            var model   = factory.Build(2, 48000, 7_040_000L, new CwSkimmerConfig { SkimmerIniPath = path });

            // UI 16 → INI 15. Lookup is by name, not by offset from IQ1.
            Assert.Equal(15, model.MmeSignalDevIndex);
            Assert.Equal(0,  model.MmeAudioDevIndex);   // copied verbatim — no per-channel offset
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Build_Channel4_DaxV2_LooksUpFourthChannelByName()
    {
        var path = WriteIni("""
[Audio]
WdmSignalDev=19
WdmAudioDev=14
MmeSignalDev=12
MmeAudioDev=0
UseWdm=1
""");
        try
        {
            var factory = new CwSkimmerIniModelFactory(DaxV2Finder());
            var model   = factory.Build(4, 48000, 3_540_000L, new CwSkimmerConfig { SkimmerIniPath = path });

            // UI 22 → INI 21
            Assert.Equal(21, model.MmeSignalDevIndex);
            Assert.Equal(0,  model.MmeAudioDevIndex);
        }
        finally { File.Delete(path); }
    }

    // ── DAX v1 backwards compatibility ────────────────────────────────────────

    [Fact]
    public void Build_Channel1_DaxV1_ResolvesRxPattern()
    {
        // v1 device names contain "RX" — v2 probe finds nothing, v1 probe succeeds.
        var path = WriteIni("""
[Audio]
WdmSignalDev=7
WdmAudioDev=14
MmeSignalDev=7
MmeAudioDev=0
UseWdm=1
""");
        try
        {
            var factory = new CwSkimmerIniModelFactory(DaxV1Finder());
            var model   = factory.Build(1, 48000, 14_048_441L, new CwSkimmerConfig { SkimmerIniPath = path });

            // UI 8 → INI 7
            Assert.Equal(7, model.MmeSignalDevIndex);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Build_Channel3_DaxV1_LooksUpRxPatternByChannel()
    {
        var path = WriteIni("""
[Audio]
WdmSignalDev=7
WdmAudioDev=14
MmeSignalDev=7
MmeAudioDev=0
UseWdm=1
""");
        try
        {
            var factory = new CwSkimmerIniModelFactory(DaxV1Finder());
            var model   = factory.Build(3, 48000, 7_040_000L, new CwSkimmerConfig { SkimmerIniPath = path });

            // UI 14 → INI 13
            Assert.Equal(13, model.MmeSignalDevIndex);
        }
        finally { File.Delete(path); }
    }

    // ── Driver family: MME default, WDM on operator opt-in ────────────────────

    [Fact]
    public void Build_DefaultsToMmeMode_WhenNoOperatorWdmIndex()
    {
        // Master has UseWdm=1 but the config supplies no OperatorWdmSignalDevIndex.
        // The factory defaults to MME — WDM is opt-in via the wizard, not
        // inherited from the master INI's UseWdm setting.
        var path = WriteIni("""
[Audio]
WdmSignalDev=3
WdmAudioDev=15
MmeSignalDev=8
MmeAudioDev=3
UseWdm=1
""");
        try
        {
            var factory = new CwSkimmerIniModelFactory(DaxV2Finder());
            var model   = factory.Build(2, 48000, 7_040_000L, new CwSkimmerConfig { SkimmerIniPath = path });

            Assert.False(model.UseWdm);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Build_EmitsWdmMode_WhenOperatorWdmIndexSupplied()
    {
        // When the wizard supplies an OperatorWdmSignalDevIndex (1-based UI index
        // as the operator sees it in CW Skimmer), the factory emits a WDM-mode
        // INI with the index converted to 0-based for the INI file.
        var path = WriteIni("""
[Audio]
WdmSignalDev=3
WdmAudioDev=15
MmeSignalDev=8
MmeAudioDev=3
UseWdm=0
""");
        try
        {
            var factory = new CwSkimmerIniModelFactory(DaxV2Finder());
            var model   = factory.Build(
                2, 48000, 7_040_000L,
                new CwSkimmerConfig
                {
                    SkimmerIniPath           = path,
                    OperatorWdmSignalDevIndex = 19,   // wizard supplies 1-based UI index
                });

            Assert.True(model.UseWdm);
            Assert.Equal(18, model.WdmSignalDevIndex);   // 19 - 1 → 0-based for INI
            Assert.Equal(15, model.WdmAudioDevIndex);    // copied from master verbatim
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Build_PropagatesWdmFieldsVerbatimFromMaster_WithoutPerChannelOffset()
    {
        // WDM fields are inert when UseWdm=false, but we propagate them so the
        // operator can still inspect or hand-edit them later.
        var path = WriteIni("""
[Audio]
WdmSignalDev=3
WdmAudioDev=15
MmeSignalDev=8
MmeAudioDev=3
UseWdm=1
""");
        try
        {
            var factory = new CwSkimmerIniModelFactory(DaxV2Finder());
            var model   = factory.Build(2, 48000, 7_040_000L, new CwSkimmerConfig { SkimmerIniPath = path });

            Assert.Equal(3,  model.WdmSignalDevIndex);    // verbatim — no offset
            Assert.Equal(15, model.WdmAudioDevIndex);     // verbatim — no offset
        }
        finally { File.Delete(path); }
    }

    // ── Fallback and error cases ──────────────────────────────────────────────

    [Fact]
    public void Build_ReturnsNegativeOne_WhenCalibrationIniMissing()
    {
        var factory = new CwSkimmerIniModelFactory(DaxV2Finder());
        var model   = factory.Build(1, 48000, 14_000_000L,
                          new CwSkimmerConfig { SkimmerIniPath = @"C:\does-not-exist\cwskimmer.ini" });

        Assert.Equal(-1, model.WdmSignalDevIndex);
        Assert.Equal(-1, model.WdmAudioDevIndex);
        Assert.Equal(-1, model.MmeSignalDevIndex);
        Assert.False(model.UseWdm);
    }

    [Fact]
    public void Build_Channel2_FallsBackToSequentialOffset_WhenWinMMLookupFails()
    {
        // EmptyFinder returns -1 for all lookups → sequential fallback uses
        // the master's MmeSignalDev as the IQ1 anchor and offsets by (channel - 1).
        var path = WriteIni("""
[Audio]
WdmSignalDev=7
WdmAudioDev=14
MmeSignalDev=7
MmeAudioDev=0
UseWdm=1
""");
        try
        {
            var factory = new CwSkimmerIniModelFactory(EmptyFinder());
            var model   = factory.Build(2, 48000, 7_040_000L, new CwSkimmerConfig { SkimmerIniPath = path });

            Assert.Equal(8,  model.MmeSignalDevIndex);   // 7 + (2-1) sequential fallback
            Assert.Equal(7,  model.WdmSignalDevIndex);   // verbatim from master
            Assert.Equal(14, model.WdmAudioDevIndex);    // verbatim from master
        }
        finally { File.Delete(path); }
    }
}

public sealed class FrequencyMathTests
{
    [Theory]
    [InlineData(14.055090, 100, 14.055100)]
    [InlineData(14.055010, 100, 14.055000)]
    [InlineData(14.055490, 100, 14.055500)]
    [InlineData(14.055090, 50,  14.055100)]
    [InlineData(14.055010, 50,  14.055000)]
    [InlineData(14.055025, 50,  14.055050)]
    [InlineData(7.025149,  100, 7.025100)]
    [InlineData(7.025150,  100, 7.025200)]
    public void SnapMHzToStepHz_RoundsToNearestStep(double inputMHz, int stepHz, double expectedMHz)
    {
        var result = FrequencyMath.SnapMHzToStepHz(inputMHz, stepHz);
        Assert.Equal(expectedMHz, result, precision: 6);
    }
}
