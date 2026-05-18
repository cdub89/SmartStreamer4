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

    [Fact]
    public async Task LaunchForChannelAsync_PicksPanAndSliceForRequestedStation_NotForeignStation()
    {
        var conn = new FakeConnection
        {
            Panadapters =
            [
                new PanadapterInfo(StreamId: 100, CenterFreqMHz: 7.041691, DAXIQChannel: 1, ClientStation: MaestroStation),
                new PanadapterInfo(StreamId: 200, CenterFreqMHz: 7.060000, DAXIQChannel: 1, ClientStation: OtherStation),
            ],
            Slices =
            [
                new SliceInfo("A", "CW", 7.030700, false, 0, 0, PanadapterStreamId: 100, ClientStation: MaestroStation),
                new SliceInfo("A", "CW", 7.060000, false, 0, 0, PanadapterStreamId: 200, ClientStation: OtherStation),
            ],
        };
        var launcher = new FakeLauncher();
        var service  = new CwSkimmerWorkflowService(conn, launcher, new AppSettings());

        var stream = new DaxIQStreamInfo(1, 48_000, true, 7.041691);
        var status = new List<string>();

        await service.LaunchForChannelAsync(stream, MaestroStation, FakeExePath, status.Add);

        // Launch must have happened with Maestro's pan center (LO) and Maestro's slice (initial VFO).
        Assert.NotNull(launcher.LastConfig);
        Assert.Equal(7_041_691L, launcher.LastCenterFreqHz);
        Assert.Equal(7.030700, launcher.LastConfig!.InitialSliceFreqMHz);
        Assert.Equal(7_041_691L, launcher.LastConfig.InitialLoFreqHz);
    }

    [Fact]
    public async Task LaunchForChannelAsync_NoPanOnRequestedStation_FailsWithExplicitStatus()
    {
        var conn = new FakeConnection
        {
            // Only the foreign station has DAX-IQ ch 1; our station has nothing.
            Panadapters =
            [
                new PanadapterInfo(StreamId: 200, CenterFreqMHz: 7.060000, DAXIQChannel: 1, ClientStation: OtherStation),
            ],
            Slices =
            [
                new SliceInfo("A", "CW", 7.060000, false, 0, 0, PanadapterStreamId: 200, ClientStation: OtherStation),
            ],
        };
        var launcher = new FakeLauncher();
        var service  = new CwSkimmerWorkflowService(conn, launcher, new AppSettings());

        var stream = new DaxIQStreamInfo(1, 48_000, true, 7.060000);
        var status = new List<string>();

        await service.LaunchForChannelAsync(stream, MaestroStation, FakeExePath, status.Add);

        Assert.Null(launcher.LastConfig); // launcher was not called
        Assert.Contains(status, s => s.Contains($"no panadapter on '{MaestroStation}'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaunchForChannelAsync_PanOnRequestedStationButNoSlice_StillLaunchesWithZeroVfo()
    {
        var conn = new FakeConnection
        {
            // Our station has the pan but no slice opened on it yet.
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
        var service  = new CwSkimmerWorkflowService(conn, launcher, new AppSettings());

        var stream = new DaxIQStreamInfo(1, 48_000, true, 7.041691);
        var status = new List<string>();

        await service.LaunchForChannelAsync(stream, MaestroStation, FakeExePath, status.Add);

        Assert.NotNull(launcher.LastConfig);
        Assert.Equal(7_041_691L, launcher.LastConfig!.InitialLoFreqHz);
        Assert.Equal(0d, launcher.LastConfig.InitialSliceFreqMHz); // no slice → 0, not the foreign 7.060
    }

    [Fact]
    public async Task LaunchForChannelAsync_EmptyClientStation_FailsBeforeReachingLauncher()
    {
        var conn     = new FakeConnection();
        var launcher = new FakeLauncher();
        var service  = new CwSkimmerWorkflowService(conn, launcher, new AppSettings());

        var stream = new DaxIQStreamInfo(1, 48_000, true, 7.041691);
        var status = new List<string>();

        await service.LaunchForChannelAsync(stream, string.Empty, FakeExePath, status.Add);

        Assert.Null(launcher.LastConfig);
        Assert.Contains(status, s => s.Contains("no control station selected", StringComparison.Ordinal));
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

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
