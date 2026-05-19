using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SDRIQStreamer.App;
using SDRIQStreamer.CWSkimmer;
using SDRIQStreamer.FlexRadio;

namespace SmartSDRIQStreamer.App.Tests;

public sealed class CwSkimmerWorkflowServiceTests
{
    private const string MaestroStation = "Maestro C";
    private const string OtherStation   = "WX7V-M";
    private const string FakeExePath    = @"C:\fake\CwSkimmer.exe"; // any non-empty string; launcher is stubbed.

    private static CwSkimmerWorkflowService CreateService(
        IRadioConnection conn,
        ICwSkimmerLauncher launcher,
        IDaxStationConfirmer? confirmer = null) =>
        new(conn, launcher, new AppSettings(), confirmer);

    [Fact]
    public async Task LaunchForChannelAsync_PicksOwnStationsPanAndSlice()
    {
        // No collision: only our station holds ch 1. Verifies the pan/slice
        // resolution still respects the requested station (issue #40 fix).
        var conn = new FakeConnection
        {
            Panadapters =
            [
                new PanadapterInfo(StreamId: 100, CenterFreqMHz: 7.041691, DAXIQChannel: 1, ClientStation: MaestroStation),
            ],
            Slices =
            [
                new SliceInfo("A", "CW", 7.030700, false, 0, 0, PanadapterStreamId: 100, ClientStation: MaestroStation),
            ],
        };
        var launcher  = new FakeLauncher();
        var confirmer = new FakeDaxStationConfirmer();
        var service   = CreateService(conn, launcher, confirmer);

        var stream = new DaxIQStreamInfo(1, 48_000, true, 7.041691);
        var status = new List<string>();

        await service.LaunchForChannelAsync(stream, MaestroStation, FakeExePath, status.Add);

        Assert.NotNull(launcher.LastConfig);
        Assert.Equal(7_041_691L, launcher.LastCenterFreqHz);
        Assert.Equal(7.030700, launcher.LastConfig!.InitialSliceFreqMHz);
        Assert.Equal(7_041_691L, launcher.LastConfig.InitialLoFreqHz);
        Assert.Equal(0, confirmer.CallCount); // no collision -> no dialog
    }

    [Fact]
    public async Task LaunchForChannelAsync_NoPanOnRequestedStation_FailsWithExplicitStatus()
    {
        var conn = new FakeConnection
        {
            // Only the foreign station has DAX-IQ ch 1; our station has nothing.
            // The early-exit in the #39 collision check defers to the existing
            // pan-lookup gate so the message is the actionable "no panadapter".
            Panadapters =
            [
                new PanadapterInfo(StreamId: 200, CenterFreqMHz: 7.060000, DAXIQChannel: 1, ClientStation: OtherStation),
            ],
            Slices =
            [
                new SliceInfo("A", "CW", 7.060000, false, 0, 0, PanadapterStreamId: 200, ClientStation: OtherStation),
            ],
        };
        var launcher  = new FakeLauncher();
        var confirmer = new FakeDaxStationConfirmer();
        var service   = CreateService(conn, launcher, confirmer);

        var stream = new DaxIQStreamInfo(1, 48_000, true, 7.060000);
        var status = new List<string>();

        await service.LaunchForChannelAsync(stream, MaestroStation, FakeExePath, status.Add);

        Assert.Null(launcher.LastConfig);
        Assert.Equal(0, confirmer.CallCount); // no own-station pan -> no dialog (gate below handles it)
        Assert.Contains(status, s => s.Contains($"no panadapter on '{MaestroStation}'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaunchForChannelAsync_PanOnRequestedStationButNoSlice_StillLaunchesWithZeroVfo()
    {
        var conn = new FakeConnection
        {
            Panadapters =
            [
                new PanadapterInfo(StreamId: 100, CenterFreqMHz: 7.041691, DAXIQChannel: 1, ClientStation: MaestroStation),
            ],
            // A foreign-station slice exists but should NOT leak into the launch.
            Slices =
            [
                new SliceInfo("A", "CW", 7.060000, false, 0, 0, PanadapterStreamId: 200, ClientStation: OtherStation),
            ],
        };
        var launcher = new FakeLauncher();
        var service  = CreateService(conn, launcher);

        var stream = new DaxIQStreamInfo(1, 48_000, true, 7.041691);
        var status = new List<string>();

        await service.LaunchForChannelAsync(stream, MaestroStation, FakeExePath, status.Add);

        Assert.NotNull(launcher.LastConfig);
        Assert.Equal(7_041_691L, launcher.LastConfig!.InitialLoFreqHz);
        Assert.Equal(0d, launcher.LastConfig.InitialSliceFreqMHz);
    }

    [Fact]
    public async Task LaunchForChannelAsync_EmptyClientStation_FailsBeforeReachingLauncher()
    {
        var conn     = new FakeConnection();
        var launcher = new FakeLauncher();
        var service  = CreateService(conn, launcher);

        var stream = new DaxIQStreamInfo(1, 48_000, true, 7.041691);
        var status = new List<string>();

        await service.LaunchForChannelAsync(stream, string.Empty, FakeExePath, status.Add);

        Assert.Null(launcher.LastConfig);
        Assert.Contains(status, s => s.Contains("no control station selected", StringComparison.Ordinal));
    }

    // ── Issue #39: DAX station mismatch detection ──────────────────────────────

    [Fact]
    public async Task LaunchForChannelAsync_ChannelCollision_PromptsAndCancelsBlockLaunch()
    {
        // Both stations have ch 1. Confirmer says Cancel; launch must be blocked.
        var conn = new FakeConnection
        {
            Panadapters =
            [
                new PanadapterInfo(StreamId: 100, CenterFreqMHz: 14.05, DAXIQChannel: 1, ClientStation: MaestroStation),
                new PanadapterInfo(StreamId: 200, CenterFreqMHz: 7.058, DAXIQChannel: 1, ClientStation: OtherStation),
            ],
        };
        var launcher  = new FakeLauncher();
        var confirmer = new FakeDaxStationConfirmer();
        confirmer.Responses.Enqueue(DaxStationConfirmResult.Cancel);
        var service = CreateService(conn, launcher, confirmer);

        var stream = new DaxIQStreamInfo(1, 48_000, true, 14.05);
        var status = new List<string>();

        await service.LaunchForChannelAsync(stream, MaestroStation, FakeExePath, status.Add);

        Assert.Null(launcher.LastConfig);
        Assert.Equal(1, confirmer.CallCount);
        Assert.Equal(MaestroStation, confirmer.Calls[0].OwnStation);
        Assert.Equal(OtherStation, confirmer.Calls[0].OtherStation);
        Assert.Equal(1, confirmer.Calls[0].Channel);
        Assert.Contains(status, s => s.Contains("launch cancelled", StringComparison.Ordinal));
        Assert.Contains(status, s => s.Contains($"also assigned to {OtherStation}", StringComparison.Ordinal));
        Assert.Contains(status, s => s.Contains("In the SmartSDR DAX application", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaunchForChannelAsync_ChannelCollision_StartLetsLaunchProceed()
    {
        // Both stations have ch 1. Confirmer returns Start (operator confirmed
        // they updated DAX). Launch proceeds. The collision is a soft warning
        // since FlexLib does not expose DAX-the-app's actual station binding.
        var conn = new FakeConnection
        {
            Panadapters =
            [
                new PanadapterInfo(StreamId: 100, CenterFreqMHz: 14.05, DAXIQChannel: 1, ClientStation: MaestroStation),
                new PanadapterInfo(StreamId: 200, CenterFreqMHz: 7.058, DAXIQChannel: 1, ClientStation: OtherStation),
            ],
            Slices =
            [
                new SliceInfo("A", "CW", 14.054, false, 0, 0, PanadapterStreamId: 100, ClientStation: MaestroStation),
            ],
        };
        var launcher  = new FakeLauncher();
        var confirmer = new FakeDaxStationConfirmer();
        confirmer.Responses.Enqueue(DaxStationConfirmResult.Start);
        var service = CreateService(conn, launcher, confirmer);

        var stream = new DaxIQStreamInfo(1, 48_000, true, 14.05);
        var status = new List<string>();

        await service.LaunchForChannelAsync(stream, MaestroStation, FakeExePath, status.Add);

        Assert.NotNull(launcher.LastConfig);
        Assert.Equal(1, confirmer.CallCount);
        Assert.Equal(14_050_000L, launcher.LastCenterFreqHz);
        Assert.Equal(14.054, launcher.LastConfig!.InitialSliceFreqMHz);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeDaxStationConfirmer : IDaxStationConfirmer
    {
        public Queue<DaxStationConfirmResult> Responses { get; } = new();
        public int CallCount { get; private set; }
        public List<(int Channel, string OwnStation, string OtherStation)> Calls { get; } = new();

        public Task<DaxStationConfirmResult> ConfirmAsync(int daxIqChannel, string ownStation, string otherStation)
        {
            CallCount++;
            Calls.Add((daxIqChannel, ownStation, otherStation));
            // Default to Cancel if the test didn't pre-load a response; avoids accidental infinite loops.
            return Task.FromResult(Responses.TryDequeue(out var r) ? r : DaxStationConfirmResult.Cancel);
        }
    }

    private sealed class FakeConnection : IRadioConnection
    {
        public IReadOnlyList<PanadapterInfo>  Panadapters  { get; set; } = [];
        public IReadOnlyList<SliceInfo>       Slices       { get; set; } = [];
        public IReadOnlyList<DaxIQStreamInfo> DaxIQStreams { get; set; } = [];
        public IReadOnlyList<GuiClientInfo>   GuiClients   { get; set; } = [];

        public bool IsConnected => true;
        public string? ConnectedModel  => "FLEX-6400M";
        public string? ConnectedSerial => "FAKE-SERIAL";
        public string? Versions        => null;
        public uint OwnClientHandle    => 0;
        public string OwnClientStation => MaestroStation;
        public int AvgDAXKbps          => 0;
        public NetworkStatusInfo NetworkStatus => NetworkStatusInfo.Empty;

        public Task<bool> ConnectAsync(DiscoveredRadio radio) => Task.FromResult(true);
        public void Disconnect() { }
        public Task<RequestStreamResult> RequestDaxIQStreamAsync(PanadapterInfo pan) => Task.FromResult(RequestStreamResult.Success);
        public Task<RequestStreamResult> StopDaxIQStreamAsync(PanadapterInfo pan)    => Task.FromResult(RequestStreamResult.Success);
        public Task SetSliceFrequencyAsync(SliceInfo slice, double freqMHz)          => Task.CompletedTask;
        public Task PublishSpotAsync(RadioSpotInfo spot)                             => Task.CompletedTask;
        public void ResetNetworkStatus() { }

        public event Action<bool>? ConnectionStateChanged;
        public event Action<PanadapterInfo>? PanadapterAdded;
        public event Action<PanadapterInfo>? PanadapterRemoved;
        public event Action<PanadapterInfo>? PanadapterUpdated;
        public event Action<SliceInfo>? SliceAdded;
        public event Action<SliceInfo>? SliceRemoved;
        public event Action<SliceInfo>? SliceUpdated;
        public event Action<DaxIQStreamInfo>? DaxIQStreamAdded;
        public event Action<DaxIQStreamInfo>? DaxIQStreamRemoved;
        public event Action<DaxIQStreamInfo>? DaxIQStreamUpdated;
        public event Action<int>? AvgDAXKbpsChanged;
        public event Action<NetworkStatusInfo>? NetworkStatusChanged;
        public event Action<IReadOnlyList<GuiClientInfo>>? GuiClientsChanged;
        public event Action<string>? DiagnosticEvent;

        // Suppress unused-event warnings in tests.
        private void _Touch()
        {
            ConnectionStateChanged?.Invoke(false);
            PanadapterAdded?.Invoke(default!);
            PanadapterRemoved?.Invoke(default!);
            PanadapterUpdated?.Invoke(default!);
            SliceAdded?.Invoke(default!);
            SliceRemoved?.Invoke(default!);
            SliceUpdated?.Invoke(default!);
            DaxIQStreamAdded?.Invoke(default!);
            DaxIQStreamRemoved?.Invoke(default!);
            DaxIQStreamUpdated?.Invoke(default!);
            AvgDAXKbpsChanged?.Invoke(0);
            NetworkStatusChanged?.Invoke(default!);
            GuiClientsChanged?.Invoke([]);
            DiagnosticEvent?.Invoke(string.Empty);
        }
    }

    private sealed class FakeLauncher : ICwSkimmerLauncher
    {
        public CwSkimmerConfig? LastConfig { get; private set; }
        public int LastDaxIqChannel { get; private set; }
        public long LastCenterFreqHz { get; private set; }

        public bool IsRunning              => false;
        public bool IsChannelRunning(int _) => false;
        public bool TelnetConnected        => false;
        public string LastDiagnostics      => string.Empty;

        public (string SignalDevice, int SignalIdx, string AudioDevice, int AudioIdx)?
            PreviewDevices(int daxIqChannel) => null;

        public Task<LaunchResult> LaunchAsync(int daxIqChannel, int sampleRateHz, long centerFreqHz, CwSkimmerConfig config)
        {
            LastDaxIqChannel = daxIqChannel;
            LastCenterFreqHz = centerFreqHz;
            LastConfig       = config;
            return Task.FromResult(LaunchResult.Success);
        }

        public void RequestSkimmerSync(int daxIqChannel, long? loHz = null, double? vfoMHz = null) { }
        public void Stop() { }
        public void Stop(int daxIqChannel) { }

        public event Action<bool>? RunningStateChanged;
        public event Action<int, double>? FrequencyClicked;
        public event Action<int, CwSkimmerSpotInfo>? SpotReceived;
        public event Action<string>? TelnetStatusChanged;
        public event Action<int, double, DateTime>? OutboundQsyEmitted;

        private void _Touch()
        {
            RunningStateChanged?.Invoke(false);
            FrequencyClicked?.Invoke(0, 0);
            SpotReceived?.Invoke(0, default!);
            TelnetStatusChanged?.Invoke(string.Empty);
            OutboundQsyEmitted?.Invoke(0, 0, default);
        }
    }
}
