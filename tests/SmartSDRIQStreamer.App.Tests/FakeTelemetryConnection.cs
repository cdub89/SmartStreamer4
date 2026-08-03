using SDRIQStreamer.FlexRadio;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Minimal <see cref="IRadioConnection"/> stand-in for SmartDeck ViewModel
/// tests. Only the telemetry and connection-state members carry behaviour;
/// everything else is inert, since the ViewModel under test touches nothing
/// else.
/// </summary>
internal sealed class FakeTelemetryConnection : IRadioConnection
{
    public int StartTelemetryCalls { get; private set; }
    public int StopTelemetryCalls { get; private set; }

    public RadioTelemetryInfo Telemetry { get; private set; } = RadioTelemetryInfo.Empty;

    public void StartTelemetry() => StartTelemetryCalls++;
    public void StopTelemetry() => StopTelemetryCalls++;

    /// <summary>Publishes a snapshot as the pump would.</summary>
    public void PublishTelemetry(RadioTelemetryInfo telemetry)
    {
        Telemetry = telemetry;
        TelemetryChanged?.Invoke(telemetry);
    }

    /// <summary>Raises a connection-state transition as FlexLib would.</summary>
    public void RaiseConnectionStateChanged(bool connected) =>
        ConnectionStateChanged?.Invoke(connected);

    public event Action<RadioTelemetryInfo>? TelemetryChanged;
    public event Action<bool>? ConnectionStateChanged;

    // ── Inert remainder of the interface ─────────────────────────────────────

    public bool IsConnected => true;
    public string? ConnectedModel => "FLEX-6400M";
    public string? ConnectedSerial => "FAKE-SERIAL";
    public string? Versions => null;
    public uint OwnClientHandle => 0;
    public string OwnClientStation => "FAKE";
    public int AvgDAXKbps => 0;
    public NetworkStatusInfo NetworkStatus => NetworkStatusInfo.Empty;
    public bool VerboseDiagnostics { get; set; }

    public IReadOnlyList<PanadapterInfo> Panadapters => _panadapters;
    public IReadOnlyList<SliceInfo> Slices => _slices;
    public IReadOnlyList<DaxIQStreamInfo> DaxIQStreams => [];
    public IReadOnlyList<GuiClientInfo> GuiClients => [];

    public Task<bool> ConnectAsync(DiscoveredRadio radio) => Task.FromResult(true);
    public void Disconnect() { }
    public Task<RequestStreamResult> RequestDaxIQStreamAsync(PanadapterInfo pan) => Task.FromResult(RequestStreamResult.Success);
    public Task<RequestStreamResult> StopDaxIQStreamAsync(PanadapterInfo pan) => Task.FromResult(RequestStreamResult.Success);
    public List<(SliceInfo Slice, double FreqMHz)> FrequencyWrites { get; } = [];

    public Task SetSliceFrequencyAsync(SliceInfo slice, double freqMHz)
    {
        FrequencyWrites.Add((slice, freqMHz));
        return Task.CompletedTask;
    }

    public Task PublishSpotAsync(RadioSpotInfo spot) => Task.CompletedTask;
    public void ResetNetworkStatus() { }

    // ── Slice control surface, recorded for assertions ───────────────────────

    public List<(SliceInfo Slice, SliceMode Mode)> ModeWrites { get; } = [];
    public List<(SliceInfo Slice, string Antenna)> RxAntennaWrites { get; } = [];
    public List<(SliceInfo Slice, string Antenna)> TxAntennaWrites { get; } = [];

    public Task SetSliceModeAsync(SliceInfo slice, SliceMode mode)
    {
        ModeWrites.Add((slice, mode));
        return Task.CompletedTask;
    }

    public Task SetSliceRxAntennaAsync(SliceInfo slice, string antenna)
    {
        RxAntennaWrites.Add((slice, antenna));
        return Task.CompletedTask;
    }

    public Task SetSliceTxAntennaAsync(SliceInfo slice, string antenna)
    {
        TxAntennaWrites.Add((slice, antenna));
        return Task.CompletedTask;
    }

    /// <summary>Sets the slice list the ViewModel reads on refresh.</summary>
    public void SetSlices(params SliceInfo[] slices) => _slices = slices;

    public List<(SliceInfo Slice, int Threshold)> AgcThresholdWrites { get; } = [];

    public Task SetSliceAgcThresholdAsync(SliceInfo slice, int threshold)
    {
        AgcThresholdWrites.Add((slice, threshold));
        return Task.CompletedTask;
    }

    public List<(PanadapterInfo Panadapter, int RfGain)> RfGainWrites { get; } = [];

    public Task SetPanadapterRfGainAsync(PanadapterInfo panadapter, int rfGain)
    {
        RfGainWrites.Add((panadapter, rfGain));
        return Task.CompletedTask;
    }

    /// <summary>Sets the panadapter list the ViewModel reads for RF gain.</summary>
    public void SetPanadapters(params PanadapterInfo[] panadapters) => _panadapters = panadapters;

    public void RaisePanadapterUpdated(PanadapterInfo panadapter) => PanadapterUpdated?.Invoke(panadapter);

    private PanadapterInfo[] _panadapters = [];

    public void RaiseSliceUpdated(SliceInfo slice) => SliceUpdated?.Invoke(slice);
    public void RaiseSliceRemoved(SliceInfo slice) => SliceRemoved?.Invoke(slice);
    public void RaiseSliceAdded(SliceInfo slice) => SliceAdded?.Invoke(slice);

    private SliceInfo[] _slices = [];

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

    // Suppress unused-event warnings for the inert members above.
    private void Touch()
    {
        PanadapterAdded?.Invoke(default!);
        PanadapterRemoved?.Invoke(default!);
        DaxIQStreamAdded?.Invoke(default!);
        DaxIQStreamRemoved?.Invoke(default!);
        DaxIQStreamUpdated?.Invoke(default!);
        AvgDAXKbpsChanged?.Invoke(0);
        NetworkStatusChanged?.Invoke(default!);
        GuiClientsChanged?.Invoke([]);
        DiagnosticEvent?.Invoke(string.Empty);
    }
}
