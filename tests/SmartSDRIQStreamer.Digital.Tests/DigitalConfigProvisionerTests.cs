using SDRIQStreamer.Digital;

namespace SmartSDRIQStreamer.Digital.Tests;

public sealed class DigitalConfigProvisionerTests
{
    // Representative template: [Configuration] with blank identity, the keys we
    // override, and a Qt @Variant line (must be preserved verbatim). Raw string
    // => backslashes in @Variant are literal.
    private const string Template = """
        [Common]
        Mode=FT8

        [Configuration]
        MyCall=
        MyGrid=
        Rig=FlexRadio 6xxx
        CATNetworkPort=127.0.0.1:60000
        PTTMethod=@Variant(\0\0\0\x7f\0\0\0\x1eTransceiverFactory::PTTMethod\0\0\0\0\xfPTT_method_CAT\0)
        SoundInName=DAX RX 1 (FlexRadio DAX)
        SoundOutName=DAX TX (FlexRadio DAX)
        UDPServerPort=2237
        """;

    [Fact]
    public void ApplyOverrides_WritesIdentityAndPerSliceKeys_AndPreservesEverythingElse()
    {
        var values = new DigitalProvisionValues(
            MyCall: "WX7V", MyGrid: "EM12ou", Rig: "FlexRadio 6xxx",
            DaxRxChannel: 2, CatTcpPort: 60001, UdpPort: 2238);

        var result = DigitalConfigProvisioner.ApplyOverrides(Template, values);

        // Identity + per-slice binding written:
        Assert.Contains("MyCall=WX7V", result);
        Assert.Contains("MyGrid=EM12ou", result);
        Assert.Contains("SoundInName=DAX RX 2 (FlexRadio DAX)", result);
        Assert.Contains("CATNetworkPort=127.0.0.1:60001", result);
        Assert.Contains("UDPServerPort=2238", result);
        Assert.Contains("SoundOutName=DAX TX (FlexRadio DAX)", result);

        // @Variant blob preserved verbatim:
        Assert.Contains(@"PTTMethod=@Variant(\0\0\0\x7f\0\0\0\x1eTransceiverFactory::PTTMethod\0\0\0\0\xfPTT_method_CAT\0)", result);
        Assert.Contains("[Common]", result);
        Assert.Contains("Mode=FT8", result);

        // Old per-slice values replaced in place, not duplicated:
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

        // WSJT-Z shares the WSJT-X config root (verified 2026-06-11), so its
        // per-instance dir lives under "WSJT-X - <rig>", not "WSJT-Z - <rig>".
        var wsjtZ = DigitalConfigProvisioner.InstanceConfigPath(DigitalEngines.WsjtZ(@"C:\WSJT\wsjtz\bin\wsjtx.exe"), "Slice-A");
        Assert.EndsWith(@"WSJT-X - Slice-A\WSJT-X - Slice-A.ini", wsjtZ);
    }

    [Fact]
    public void Provision_WritesInstanceConfig_FromBundledTemplate_WithOverrides()
    {
        // Unique rig name so we never collide with a real instance dir; clean up after.
        var engine = DigitalEngines.WsjtX(@"C:\WSJT\wsjtx\bin\wsjtx.exe");
        var rigName = $"UnitTest-{Guid.NewGuid():N}";
        var values = new DigitalProvisionValues("WX7V", "EM12ou", "FlexRadio 6xxx", 3, 60002, 2239);

        try
        {
            var result = DigitalConfigProvisioner.Provision(engine, rigName, values);

            Assert.Equal(DigitalProvisionOutcome.Success, result.Outcome);
            Assert.True(File.Exists(result.InstanceConfigPath));

            var written = File.ReadAllText(result.InstanceConfigPath);
            Assert.Contains("MyCall=WX7V", written);
            Assert.Contains("SoundInName=DAX RX 3 (FlexRadio DAX)", written);
            Assert.Contains("CATNetworkPort=127.0.0.1:60002", written);
            Assert.Contains("PTT_method_CAT", written);     // blob from bundled template
        }
        finally
        {
            var dir = DigitalConfigProvisioner.InstanceConfigDir(engine, rigName);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Provision_PreservesExistingInstanceConfig_ReapplyingOnlyBindingKeys()
    {
        // WSJT-X / JTDX rewrite the instance config on exit (window geometry,
        // last protocol, band). A second Provision must preserve those and only
        // re-apply our keys -> seed from template on first run, preserve after.
        var engine = DigitalEngines.WsjtX(@"C:\WSJT\wsjtx\bin\wsjtx.exe");
        var rigName = $"UnitTest-{Guid.NewGuid():N}";
        var values = new DigitalProvisionValues("WX7V", "EM12ou", "FlexRadio 6xxx", 1, 60_000, 2_237);

        try
        {
            // First run: seed from bundled template.
            DigitalConfigProvisioner.Provision(engine, rigName, values);
            var path = DigitalConfigProvisioner.InstanceConfigPath(engine, rigName);

            // Simulate the engine saving operator changes on exit.
            var saved = File.ReadAllText(path)
                .Replace("Mode=FT8", "Mode=FT4")
                + Environment.NewLine + "[MainWindow]" + Environment.NewLine
                + @"geometry=@ByteArray(\x1\x2\x3)";
            File.WriteAllText(path, saved);

            // Second run with a changed binding (different slice values).
            var rebind = new DigitalProvisionValues("WX7V", "EM12ou", "FlexRadio 6xxx", 2, 60_001, 2_238);
            DigitalConfigProvisioner.Provision(engine, rigName, rebind);

            var result = File.ReadAllText(path);
            Assert.Contains("Mode=FT4", result);                          // operator protocol preserved
            Assert.Contains(@"geometry=@ByteArray(\x1\x2\x3)", result);   // window geometry preserved
            Assert.Contains("SoundInName=DAX RX 2 (FlexRadio DAX)", result); // binding re-applied
            Assert.Contains("CATNetworkPort=127.0.0.1:60001", result);
        }
        finally
        {
            var dir = DigitalConfigProvisioner.InstanceConfigDir(engine, rigName);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class DigitalTemplatesTests
{
    [Theory]
    [InlineData(DigitalEngine.WsjtX)]
    [InlineData(DigitalEngine.Jtdx)]
    [InlineData(DigitalEngine.WsjtZ)]   // shares the WSJT-X template (issue #28)
    public void ForEngine_LoadsBundledTemplate_WithBlobsAndBlankIdentity(DigitalEngine engine)
    {
        var template = DigitalTemplates.ForEngine(engine);

        Assert.Contains("[Configuration]", template);
        Assert.Contains("Rig=FlexRadio 6xxx", template);
        Assert.Contains("PTT_method_CAT", template);          // @Variant blob present
        Assert.Contains("SoundOutName=DAX TX (FlexRadio DAX)", template);
        Assert.Contains("MyCall=", template);

        // No personal data shipped in the resource.
        Assert.DoesNotContain("WX7V", template);
        Assert.DoesNotContain("EM12", template);
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
