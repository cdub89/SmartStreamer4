using System.Net;
using System.Net.Sockets;
using SDRIQStreamer.Digital;

namespace SmartSDRIQStreamer.Digital.Tests;

/// <summary>
/// The CAT reachability gate that stands in front of Digital Start (issue #66).
/// </summary>
public sealed class CatPortProbeTests
{
    [Fact]
    public async Task A_listening_loopback_port_probes_as_reachable()
    {
        // Binding port 0 lets the OS pick a free one, so the test cannot collide
        // with a real SmartSDR CAT port on the developer machine.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.True(await new TcpCatPortProbe().IsListeningAsync(port));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task A_closed_port_probes_as_unreachable()
    {
        // Same trick in reverse: take a port the OS just confirmed was free,
        // then stop listening, so nothing is on it when the probe runs.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.False(await new TcpCatPortProbe().IsListeningAsync(port));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70_000)]
    public async Task An_out_of_range_port_reports_unreachable_rather_than_throwing(int port)
    {
        // A bad port in settings must surface as the same operator-facing hint,
        // not as an unhandled ArgumentOutOfRangeException out of Start.
        Assert.False(await new TcpCatPortProbe().IsListeningAsync(port));
    }

    [Fact]
    public void With_no_CAT_ports_configured_the_hint_says_to_add_one()
    {
        var message = CatPortHint.ForUnreachablePort(60_000, []);

        Assert.Contains("no TCP ports configured", message);
        Assert.Contains("ADD", message);
        Assert.Contains("60000", message);
    }

    [Fact]
    public void A_configured_but_dead_port_says_to_start_SmartSDR_CAT()
    {
        // Different cause, different fix: the port exists, so telling the
        // operator to add it would send them round in circles.
        var configured = new[] { new CatTcpPort("0", 60_000, "Slice A") };

        var message = CatPortHint.ForUnreachablePort(60_000, configured);

        Assert.Contains("nothing is listening", message);
        Assert.Contains("Start the SmartSDR CAT application", message);
        Assert.DoesNotContain("ADD", message);
    }

    [Fact]
    public void A_mismatched_port_lists_the_ones_SmartSDR_CAT_actually_has()
    {
        // The most useful case: CAT is set up, the slice is just pointed at the
        // wrong number, and the hint can name the right ones.
        var configured = new[]
        {
            new CatTcpPort("1", 60_002, "Slice B"),
            new CatTcpPort("0", 60_001, "Slice A"),
        };

        var message = CatPortHint.ForUnreachablePort(60_000, configured);

        Assert.Contains("60001 (slice 0)", message);
        Assert.Contains("60002 (slice 1)", message);
        Assert.True(message.IndexOf("60001", StringComparison.Ordinal)
                    < message.IndexOf("60002", StringComparison.Ordinal),
                    "configured ports should be listed in ascending order");
    }
}
