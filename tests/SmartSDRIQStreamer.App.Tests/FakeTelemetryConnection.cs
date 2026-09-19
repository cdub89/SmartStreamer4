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

    /// <summary>
    /// Raises a connection-state transition as FlexLib would. A drop makes the
    /// power absent, matching FlexLibRadioConnection, whose RfPowerWatts reads
    /// null the moment Radio.Connected goes false.
    /// </summary>
    /// <remarks>
    /// Deliberately does not raise <see cref="RfPowerChanged"/>: the real
    /// connection does, but leaving it out here is what makes the disconnect
    /// test exercise the ViewModel's own re-derive rather than the event path.
    /// </remarks>
    public void RaiseConnectionStateChanged(bool connected)
    {
        if (!connected) RfPowerWatts = null;
        ConnectionStateChanged?.Invoke(connected);
    }

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

    // ── RIT and XIT (issue #73) ──────────────────────────────────────────────

    public List<(string Letter, bool Enabled)> RitEnableWrites { get; } = [];
    public List<(string Letter, int OffsetHz)> RitOffsetWrites { get; } = [];
    public List<(string Letter, bool Enabled)> XitEnableWrites { get; } = [];
    public List<(string Letter, int OffsetHz)> XitOffsetWrites { get; } = [];

    public Task SetSliceRitEnabledAsync(SliceInfo slice, bool enabled)
    {
        RitEnableWrites.Add((slice.Letter, enabled));
        return Task.CompletedTask;
    }

    public Task SetSliceRitOffsetAsync(SliceInfo slice, int offsetHz)
    {
        // Clamped as the real connection does, so a test driving the wheel past
        // the rail sees the same value the radio would have been sent.
        RitOffsetWrites.Add((slice.Letter, RitXitRange.Clamp(offsetHz)));
        return Task.CompletedTask;
    }

    public Task SetSliceXitEnabledAsync(SliceInfo slice, bool enabled)
    {
        XitEnableWrites.Add((slice.Letter, enabled));
        return Task.CompletedTask;
    }

    public Task SetSliceXitOffsetAsync(SliceInfo slice, int offsetHz)
    {
        XitOffsetWrites.Add((slice.Letter, RitXitRange.Clamp(offsetHz)));
        return Task.CompletedTask;
    }

    public List<(SliceInfo Slice, int Threshold)> AgcThresholdWrites { get; } = [];

    public Task SetSliceAgcThresholdAsync(SliceInfo slice, int threshold)
    {
        AgcThresholdWrites.Add((slice, threshold));
        return Task.CompletedTask;
    }

    public string ControlStation { get; set; } = string.Empty;

    // ── Transmit power (issue #64) ───────────────────────────────────────────

    public List<int> RfPowerWrites { get; } = [];

    public int? RfPowerWatts { get; private set; }

    /// <summary>
    /// Rated PA output (issue #77). Defaults to 100 so existing tests read the
    /// same numbers they always did; set it to 500 to stand in for an Aurora.
    /// </summary>
    public int? MaxRfPowerWatts { get; set; } = 100;

    public event Action<int?>? RfPowerChanged;

    /// <summary>
    /// Records the write and echoes it as the radio does, since FlexLib raises
    /// RFPower for our own writes as well as another client's
    /// (<c>Radio.cs:8381-8390</c>).
    /// </summary>
    public Task SetRfPowerAsync(int watts)
    {
        RfPowerWrites.Add(watts);
        ReportRfPower(watts);
        return Task.CompletedTask;
    }

    /// <summary>Reports a power as the radio would, whoever changed it.</summary>
    public void ReportRfPower(int? watts)
    {
        RfPowerWatts = watts;
        RfPowerChanged?.Invoke(watts);
    }

    public bool IsTransmitting { get; private set; }

    public event Action<bool>? TransmitStateChanged;

    /// <summary>
    /// Keys or unkeys as the radio would (issue #69). Raises only on a real
    /// change, matching FlexLibRadioConnection, so a test can call it
    /// repeatedly without inventing transitions the radio would not report.
    /// </summary>
    public void ReportTransmitting(bool transmitting)
    {
        if (IsTransmitting == transmitting) return;
        IsTransmitting = transmitting;
        TransmitStateChanged?.Invoke(transmitting);
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
