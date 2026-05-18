using System.Collections.Generic;
using System.Linq;
using SDRIQStreamer.FlexRadio;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Pins the (DAXIQChannel, ClientHandle) join contract that prevents
/// a foreign station's DAX-IQ stream from being attributed to our
/// station's panadapter in multi-station setups. Reported 2026-05-18
/// on a Dallas FLEX-6400 with WX7V and Maestro both holding ch 1.
/// </summary>
public sealed class DaxIQStreamPanAttributionTests
{
    private const uint   MaestroHandle  = 0x1000;
    private const uint   Wx7vHandle     = 0x2000;
    private const string MaestroStation = "Maestro C";
    private const string Wx7vStation    = "WX7V-M";

    [Fact]
    public void TwoStationsOnSameChannel_StreamPanLookup_PicksOwningStationsPan()
    {
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 7.041691, DAXIQChannel: 1,
                ClientStation: MaestroStation, ClientHandle: MaestroHandle),
            new(StreamId: 200, CenterFreqMHz: 7.060000, DAXIQChannel: 1,
                ClientStation: Wx7vStation,    ClientHandle: Wx7vHandle),
        };

        var maestroStream = new DaxIQStreamInfo(
            DAXIQChannel: 1, SampleRate: 48_000, IsActive: true,
            CenterFreqMHz: 0, ClientHandle: MaestroHandle);
        var wx7vStream = new DaxIQStreamInfo(
            DAXIQChannel: 1, SampleRate: 48_000, IsActive: true,
            CenterFreqMHz: 0, ClientHandle: Wx7vHandle);

        // Production lookup pattern: FlexLibRadioConnection.ToDaxIQStreamInfo
        // and MainWindowViewModel.NormalizeStreamForPan both join on
        // (channel, ClientHandle), not channel alone.
        var maestroPan = pans.FirstOrDefault(p =>
            p.DAXIQChannel == maestroStream.DAXIQChannel &&
            p.ClientHandle == maestroStream.ClientHandle);
        var wx7vPan = pans.FirstOrDefault(p =>
            p.DAXIQChannel == wx7vStream.DAXIQChannel &&
            p.ClientHandle == wx7vStream.ClientHandle);

        Assert.NotNull(maestroPan);
        Assert.Equal(7.041691, maestroPan!.CenterFreqMHz);
        Assert.Equal(MaestroStation, maestroPan.ClientStation);

        Assert.NotNull(wx7vPan);
        Assert.Equal(7.060000, wx7vPan!.CenterFreqMHz);
        Assert.Equal(Wx7vStation, wx7vPan.ClientStation);
    }

    [Fact]
    public void OwnStationLosesChannelAssignment_StreamPanLookup_ReturnsNull()
    {
        // WX7V has no pan with ch 1 (operator removed it); Maestro still does.
        // Our station's stream lookup must NOT silently pick Maestro's pan.
        var pans = new List<PanadapterInfo>
        {
            new(StreamId: 100, CenterFreqMHz: 7.041691, DAXIQChannel: 1,
                ClientStation: MaestroStation, ClientHandle: MaestroHandle),
        };

        var wx7vStream = new DaxIQStreamInfo(
            DAXIQChannel: 1, SampleRate: 48_000, IsActive: true,
            CenterFreqMHz: 0, ClientHandle: Wx7vHandle);

        var wx7vPan = pans.FirstOrDefault(p =>
            p.DAXIQChannel == wx7vStream.DAXIQChannel &&
            p.ClientHandle == wx7vStream.ClientHandle);

        Assert.Null(wx7vPan);
    }
}
