using SDRIQStreamer.Digital;

namespace SmartSDRIQStreamer.Digital.Tests;

public sealed class CatSettingsReaderTests
{
    // Mirrors the real CAT.settings structure: outer <Settings> whose <PortList>
    // is an XML-escaped inner ArrayOfPortSettings. Contains two CAT/TCP ports
    // (slices A and B), one CAT/Serial port (must be excluded), and one
    // non-CAT/TCP port (must be excluded).
    private const string Sample = """
        <?xml version="1.0" encoding="utf-16"?>
        <Settings>
          <Minimized>True</Minimized>
          <PortList>&lt;?xml version="1.0" encoding="utf-16"?&gt;
        &lt;ArrayOfPortSettings xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema"&gt;
          &lt;PortSettings&gt;
            &lt;Protocol&gt;CAT&lt;/Protocol&gt;
            &lt;PortCommType&gt;Serial&lt;/PortCommType&gt;
            &lt;TCPPortNumber&gt;0&lt;/TCPPortNumber&gt;
            &lt;SliceIndex&gt;A&lt;/SliceIndex&gt;
            &lt;Name&gt;Log4OM&lt;/Name&gt;
          &lt;/PortSettings&gt;
          &lt;PortSettings&gt;
            &lt;Protocol&gt;CAT&lt;/Protocol&gt;
            &lt;PortCommType&gt;TCP&lt;/PortCommType&gt;
            &lt;TCPPortNumber&gt;60000&lt;/TCPPortNumber&gt;
            &lt;SliceIndex&gt;A&lt;/SliceIndex&gt;
            &lt;Name /&gt;
          &lt;/PortSettings&gt;
          &lt;PortSettings&gt;
            &lt;Protocol&gt;CAT&lt;/Protocol&gt;
            &lt;PortCommType&gt;TCP&lt;/PortCommType&gt;
            &lt;TCPPortNumber&gt;60001&lt;/TCPPortNumber&gt;
            &lt;SliceIndex&gt;B&lt;/SliceIndex&gt;
            &lt;Name&gt;WSJT-X B&lt;/Name&gt;
          &lt;/PortSettings&gt;
          &lt;PortSettings&gt;
            &lt;Protocol&gt;PTT&lt;/Protocol&gt;
            &lt;PortCommType&gt;TCP&lt;/PortCommType&gt;
            &lt;TCPPortNumber&gt;7000&lt;/TCPPortNumber&gt;
            &lt;SliceIndex&gt;A&lt;/SliceIndex&gt;
            &lt;Name /&gt;
          &lt;/PortSettings&gt;
        &lt;/ArrayOfPortSettings&gt;</PortList>
        </Settings>
        """;

    [Fact]
    public void ParseCatTcpPorts_ReturnsOnlyCatTcpPorts()
    {
        var ports = CatSettingsReader.ParseCatTcpPorts(Sample);

        Assert.Equal(2, ports.Count); // CAT/Serial and PTT/TCP excluded
        Assert.Collection(ports,
            p => { Assert.Equal("A", p.SliceIndex); Assert.Equal(60000, p.TcpPort); Assert.Equal("", p.Name); },
            p => { Assert.Equal("B", p.SliceIndex); Assert.Equal(60001, p.TcpPort); Assert.Equal("WSJT-X B", p.Name); });
    }

    [Fact]
    public void FindPortForSlice_MatchesByLetterCaseInsensitive()
    {
        var ports = CatSettingsReader.ParseCatTcpPorts(Sample);

        Assert.Equal(60001, CatSettingsReader.FindPortForSlice(ports, "b")?.TcpPort);
        Assert.Equal(60000, CatSettingsReader.FindPortForSlice(ports, "A")?.TcpPort);
        Assert.Null(CatSettingsReader.FindPortForSlice(ports, "Z"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml")]
    [InlineData("<Settings></Settings>")]
    [InlineData("<Settings><PortList>not inner xml</PortList></Settings>")]
    public void ParseCatTcpPorts_BadInput_ReturnsEmptyNeverThrows(string input)
    {
        Assert.Empty(CatSettingsReader.ParseCatTcpPorts(input));
    }
}
