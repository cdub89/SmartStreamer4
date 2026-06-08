using SDRIQStreamer.Digital;

namespace SmartSDRIQStreamer.Digital.Tests;

public sealed class DigitalConfigProvisionerTests
{
    // Representative WSJT-X default config: [Configuration] with the keys we
    // override, a Qt @Variant line (must be preserved verbatim), Rig, and a
    // second section. Raw string => backslashes in @Variant are literal.
    private const string Template = """
        [Configuration]
        Rig=FlexRadio 6xxx
        CATNetworkPort=127.0.0.1:60000
        PTTMethod=@Variant(\0\0\0\x7f\0\0\0\x1eTransceiverFactory::PTTMethod\0\0\0\0\xfPTT_method_CAT\0)
        SoundInName=DAX RX 1 (FlexRadio DAX)
        SoundOutName=DAX TX (FlexRadio DAX)
        UDPServerPort=2237
        Mode=FT8

        [Common]
        MyCall=WX7V
        """;

    [Fact]
    public void ApplyOverrides_SetsPerSliceKeys_AndPreservesEverythingElse()
    {
        var result = DigitalConfigProvisioner.ApplyOverrides(Template, daxRxChannel: 2, catTcpPort: 60001, udpPort: 2238);

        Assert.Contains("SoundInName=DAX RX 2 (FlexRadio DAX)", result);
        Assert.Contains("CATNetworkPort=127.0.0.1:60001", result);
        Assert.Contains("UDPServerPort=2238", result);
        Assert.Contains("SoundOutName=DAX TX (FlexRadio DAX)", result);

        // Preserved verbatim:
        Assert.Contains(@"PTTMethod=@Variant(\0\0\0\x7f\0\0\0\x1eTransceiverFactory::PTTMethod\0\0\0\0\xfPTT_method_CAT\0)", result);
        Assert.Contains("Rig=FlexRadio 6xxx", result);
        Assert.Contains("Mode=FT8", result);
        Assert.Contains("[Common]", result);
        Assert.Contains("MyCall=WX7V", result);

        // Old values gone (replaced in place, not duplicated):
        Assert.DoesNotContain("SoundInName=DAX RX 1 (FlexRadio DAX)", result);
        Assert.DoesNotContain("CATNetworkPort=127.0.0.1:60000", result);
        Assert.DoesNotContain("UDPServerPort=2237", result);
    }

    [Fact]
    public void InstanceConfigPath_MatchesRigNameLayout()
    {
        var path = DigitalConfigProvisioner.InstanceConfigPath(DigitalEngines.WsjtX(@"C:\WSJT\wsjtx\bin\wsjtx.exe"), "Slice-B");
        Assert.EndsWith(@"WSJT-X - Slice-B\WSJT-X - Slice-B.ini", path);

        var jtdx = DigitalConfigProvisioner.InstanceConfigPath(DigitalEngines.Jtdx(@"C:\JTDX64\159\bin\jtdx.exe"), "Slice-A");
        Assert.EndsWith(@"JTDX - Slice-A\JTDX - Slice-A.ini", jtdx);
    }
}

public sealed class IniEditorTests
{
    private const string Sample = """
        [Configuration]
        Rig=FlexRadio 6xxx
        SoundInName=DAX RX 1 (FlexRadio DAX)

        [Common]
        MyCall=WX7V
        """;

    [Fact]
    public void SetKeys_ReplacesExistingKey_InTargetSectionOnly()
    {
        var result = IniEditor.SetKeys(Sample, "Configuration",
            [new("SoundInName", "DAX RX 3 (FlexRadio DAX)")]);

        Assert.Contains("SoundInName=DAX RX 3 (FlexRadio DAX)", result);
        Assert.DoesNotContain("DAX RX 1", result);
        Assert.Contains("MyCall=WX7V", result); // other section untouched
    }

    [Fact]
    public void SetKeys_AddsMissingKey_AtEndOfSection()
    {
        var result = IniEditor.SetKeys(Sample, "Configuration",
            [new("UDPServerPort", "2240")]);

        var lines = result.Replace("\r\n", "\n").Split('\n');
        var cfg = Array.IndexOf(lines, "[Configuration]");
        var common = Array.IndexOf(lines, "[Common]");
        var udp = Array.FindIndex(lines, l => l == "UDPServerPort=2240");

        Assert.InRange(udp, cfg + 1, common - 1); // landed inside [Configuration]
    }

    [Fact]
    public void SetKeys_CreatesSection_WhenAbsent()
    {
        var result = IniEditor.SetKeys("[Other]\nX=1", "Configuration",
            [new("SoundInName", "DAX RX 1 (FlexRadio DAX)")]);

        Assert.Contains("[Configuration]", result);
        Assert.Contains("SoundInName=DAX RX 1 (FlexRadio DAX)", result);
        Assert.Contains("[Other]", result);
    }
}
