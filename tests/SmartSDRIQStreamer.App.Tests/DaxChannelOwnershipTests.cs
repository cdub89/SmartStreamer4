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

    // Pins the LO-sync chokepoint gate. Reported 2026-05-19 by WX7V: two
    // stations on a shared DAX-IQ channel could each push their own pan
    // center as our LO because the prior gate only asked "any pan on this
    // channel ours?" The fix passes the source ClientHandle and requires
    // it to match a pan owned by SelectedControlStation.
    private const uint MaestroHandle = 0x1000;
    private const uint Wx7vHandle    = 0x2000;

    [Fact]
    public void IsSourceOwnedByStation_OwnPanOwnHandle_ReturnsTrue()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 7.041, DAXIQChannel: 1,
                ClientStation: MaestroStation, ClientHandle: MaestroHandle),
        };

        Assert.True(DaxChannelOwnership.IsSourceOwnedByStation(
            pans, daxIqChannel: 1, sourceClientHandle: MaestroHandle, ownStation: MaestroStation));
    }

    [Fact]
    public void IsSourceOwnedByStation_ForeignPanForeignHandle_ReturnsFalse()
    {
        // Original c908c58 repro: Maestro on 80m, SUPERWIN10's pan-update
        // event arrives with the foreign handle and the foreign center.
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 3.555, DAXIQChannel: 1,
                ClientStation: MaestroStation, ClientHandle: MaestroHandle),
            new(StreamId: 200, CenterFreqMHz: 7.029, DAXIQChannel: 1,
                ClientStation: Wx7vStation,    ClientHandle: Wx7vHandle),
        };

        Assert.False(DaxChannelOwnership.IsSourceOwnedByStation(
            pans, daxIqChannel: 1, sourceClientHandle: Wx7vHandle, ownStation: MaestroStation));
    }

    [Fact]
    public void IsSourceOwnedByStation_OwnStationHasChannelButSourceIsForeign_ReturnsFalse()
    {
        // The leak path the chokepoint fix closes: own station has a pan
        // on the channel (so the old "any pan ours?" check passed) but the
        // source firing this update is the foreign station's.
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 3.555, DAXIQChannel: 1,
                ClientStation: MaestroStation, ClientHandle: MaestroHandle),
            new(StreamId: 200, CenterFreqMHz: 7.029, DAXIQChannel: 1,
                ClientStation: Wx7vStation,    ClientHandle: Wx7vHandle),
        };

        Assert.False(DaxChannelOwnership.IsSourceOwnedByStation(
            pans, daxIqChannel: 1, sourceClientHandle: Wx7vHandle, ownStation: MaestroStation));
    }

    [Fact]
    public void IsSourceOwnedByStation_HandleZero_ReturnsFalse()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 7.041, DAXIQChannel: 1,
                ClientStation: MaestroStation, ClientHandle: MaestroHandle),
        };

        Assert.False(DaxChannelOwnership.IsSourceOwnedByStation(
            pans, daxIqChannel: 1, sourceClientHandle: 0, ownStation: MaestroStation));
    }

    [Fact]
    public void IsSourceOwnedByStation_BlankOwnStation_ReturnsFalse()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 7.041, DAXIQChannel: 1,
                ClientStation: MaestroStation, ClientHandle: MaestroHandle),
        };

        Assert.False(DaxChannelOwnership.IsSourceOwnedByStation(
            pans, daxIqChannel: 1, sourceClientHandle: MaestroHandle, ownStation: ""));
    }

    [Fact]
    public void IsSourceOwnedByStation_StationCaseInsensitive_ReturnsTrue()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 7.041, DAXIQChannel: 1,
                ClientStation: MaestroStation, ClientHandle: MaestroHandle),
        };

        Assert.True(DaxChannelOwnership.IsSourceOwnedByStation(
            pans, daxIqChannel: 1, sourceClientHandle: MaestroHandle, ownStation: "maestro c"));
    }

    [Fact]
    public void IsSourceOwnedByStation_ChannelMismatch_ReturnsFalse()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 7.041, DAXIQChannel: 2,
                ClientStation: MaestroStation, ClientHandle: MaestroHandle),
        };

        Assert.False(DaxChannelOwnership.IsSourceOwnedByStation(
            pans, daxIqChannel: 1, sourceClientHandle: MaestroHandle, ownStation: MaestroStation));
    }
}
