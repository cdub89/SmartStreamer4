using SDRIQStreamer.Digital;

namespace SmartSDRIQStreamer.Digital.Tests;

public sealed class WsjtxProfileHarvesterTests
{
    [Fact]
    public void Harvest_PlainFlexConfiguration_ReturnsIdentity()
    {
        const string ini = """
            [Configuration]
            Rig=FlexRadio 6xxx
            MyCall=WX7V
            MyGrid=EM12ou
            """;

        var profile = WsjtxProfileHarvester.Harvest(ini);

        Assert.NotNull(profile);
        Assert.Equal("WX7V", profile!.MyCall);
        Assert.Equal("EM12ou", profile.MyGrid);
        Assert.Equal("FlexRadio 6xxx", profile.Rig);
    }

    [Fact]
    public void Harvest_NonFlexActiveConfig_PicksFlexFromMultiSettings()
    {
        // Mirrors the real WSJT-X.ini layout: active config is non-Flex, the
        // FlexRadio profile is a saved MultiSettings config (hash-prefixed).
        const string ini = """
            [General]
            x=1

            [MultiSettings]
            CurrentName=Yaesu
            991a\Configuration\Rig=FlexRadio 6xxx
            991a\Configuration\MyCall=WX7V
            991a\Configuration\MyGrid=EM12

            [Configuration]
            Rig=Yaesu FT-991
            MyCall=SHOULD-NOT-PICK
            """;

        var profile = WsjtxProfileHarvester.Harvest(ini);

        Assert.NotNull(profile);
        Assert.Equal("WX7V", profile!.MyCall);
        Assert.Equal("EM12", profile.MyGrid);
        Assert.Equal("FlexRadio 6xxx", profile.Rig);
    }

    [Fact]
    public void Harvest_PrefersActiveFlexConfig_OverMultiSettings()
    {
        const string ini = """
            [MultiSettings]
            991a\Configuration\Rig=FlexRadio 6xxx
            991a\Configuration\MyCall=OLD

            [Configuration]
            Rig=FlexRadio 6xxx
            MyCall=ACTIVE
            MyGrid=DM79
            """;

        var profile = WsjtxProfileHarvester.Harvest(ini);

        Assert.Equal("ACTIVE", profile!.MyCall);
        Assert.Equal("DM79", profile.MyGrid);
    }

    [Fact]
    public void Harvest_NoFlexProfile_ReturnsNull()
    {
        const string ini = """
            [Configuration]
            Rig=Yaesu FT-991
            MyCall=WX7V
            """;

        Assert.Null(WsjtxProfileHarvester.Harvest(ini));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not even an ini file")]
    public void Harvest_BlankOrGarbage_ReturnsNull(string input)
    {
        Assert.Null(WsjtxProfileHarvester.Harvest(input));
    }
}
