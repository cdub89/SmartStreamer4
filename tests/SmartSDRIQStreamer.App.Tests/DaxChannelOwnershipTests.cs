using System.Collections.Generic;
using SDRIQStreamer.FlexRadio;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Pins the channel-collision detection contract for issue #39: multi-station
/// DAX-IQ channel ambiguity should surface as &gt;1 owners.
/// </summary>
public sealed class DaxChannelOwnershipTests
{
    private const string MaestroStation = "Maestro C";
    private const string Wx7vStation    = "WX7V-M";
    private const string ThirdStation   = "Lake-Op";

    [Fact]
    public void NoStations_ReturnsEmpty()
    {
        var owners = DaxChannelOwnership.GetOwners([], daxIqChannel: 1);
        Assert.Empty(owners);
    }

    [Fact]
    public void NonPositiveChannel_ReturnsEmpty()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 7.041, DAXIQChannel: 1, ClientStation: MaestroStation),
        };
        Assert.Empty(DaxChannelOwnership.GetOwners(pans, 0));
        Assert.Empty(DaxChannelOwnership.GetOwners(pans, -1));
    }

    [Fact]
    public void SingleStationHoldsChannel_ReturnsThatStation()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 7.041, DAXIQChannel: 1, ClientStation: MaestroStation),
            new(StreamId: 200, CenterFreqMHz: 14.05, DAXIQChannel: 2, ClientStation: Wx7vStation),
        };

        var owners = DaxChannelOwnership.GetOwners(pans, daxIqChannel: 1);

        Assert.Single(owners);
        Assert.Equal(MaestroStation, owners[0]);
    }

    [Fact]
    public void TwoStationsHoldSameChannel_ReturnsBothOrdered()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 7.058, DAXIQChannel: 1, ClientStation: Wx7vStation),
            new(StreamId: 200, CenterFreqMHz: 14.05, DAXIQChannel: 1, ClientStation: MaestroStation),
        };

        var owners = DaxChannelOwnership.GetOwners(pans, daxIqChannel: 1);

        Assert.Equal(2, owners.Count);
        Assert.Equal(MaestroStation, owners[0]); // Maestro C sorts before WX7V-M
        Assert.Equal(Wx7vStation, owners[1]);
    }

    [Fact]
    public void StationHoldsChannelOnMultiplePans_DedupesToOne()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 7.041, DAXIQChannel: 1, ClientStation: MaestroStation),
            new(StreamId: 101, CenterFreqMHz: 7.060, DAXIQChannel: 1, ClientStation: MaestroStation),
        };

        var owners = DaxChannelOwnership.GetOwners(pans, daxIqChannel: 1);

        Assert.Single(owners);
        Assert.Equal(MaestroStation, owners[0]);
    }

    [Fact]
    public void EmptyClientStationIsIgnored()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 7.041, DAXIQChannel: 1, ClientStation: ""),
            new(StreamId: 200, CenterFreqMHz: 7.060, DAXIQChannel: 1, ClientStation: MaestroStation),
        };

        var owners = DaxChannelOwnership.GetOwners(pans, daxIqChannel: 1);

        Assert.Single(owners);
        Assert.Equal(MaestroStation, owners[0]);
    }

    [Fact]
    public void GetForeignOwners_OnlyOwnStation_ReturnsEmpty()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 7.041, DAXIQChannel: 1, ClientStation: MaestroStation),
        };

        var foreign = DaxChannelOwnership.GetForeignOwners(pans, 1, MaestroStation);

        Assert.Empty(foreign);
    }

    [Fact]
    public void GetForeignOwners_OwnPlusOther_ReturnsTheOther()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 14.05, DAXIQChannel: 1, ClientStation: MaestroStation),
            new(StreamId: 200, CenterFreqMHz: 7.058, DAXIQChannel: 1, ClientStation: Wx7vStation),
        };

        var foreign = DaxChannelOwnership.GetForeignOwners(pans, 1, MaestroStation);

        Assert.Single(foreign);
        Assert.Equal(Wx7vStation, foreign[0]);
    }

    [Fact]
    public void GetForeignOwners_ThreeStations_ReturnsTwoOrdered()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 14.05, DAXIQChannel: 1, ClientStation: MaestroStation),
            new(StreamId: 200, CenterFreqMHz: 7.058, DAXIQChannel: 1, ClientStation: Wx7vStation),
            new(StreamId: 300, CenterFreqMHz: 21.04, DAXIQChannel: 1, ClientStation: ThirdStation),
        };

        var foreign = DaxChannelOwnership.GetForeignOwners(pans, 1, MaestroStation);

        Assert.Equal(2, foreign.Count);
        Assert.Equal(ThirdStation, foreign[0]);  // Lake-Op
        Assert.Equal(Wx7vStation, foreign[1]);    // WX7V-M
    }

    [Fact]
    public void GetForeignOwners_OwnStationEmpty_ReturnsAllOwners()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 14.05, DAXIQChannel: 1, ClientStation: MaestroStation),
        };

        var foreign = DaxChannelOwnership.GetForeignOwners(pans, 1, "");

        Assert.Single(foreign);
        Assert.Equal(MaestroStation, foreign[0]);
    }
}
