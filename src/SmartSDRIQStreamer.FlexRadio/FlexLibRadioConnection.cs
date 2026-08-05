using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;

namespace SDRIQStreamer.FlexRadio;

/// <summary>
/// Implements <see cref="IRadioConnection"/> using FlexLib's <see cref="Radio"/>.
/// Wraps the synchronous <c>Radio.Connect()</c> on a thread-pool thread so it
/// never blocks the UI thread.
/// </summary>
public sealed class FlexLibRadioConnection : IRadioConnection
{
    private const double SliceNoOpToleranceMHz = 0.000001; // 1 Hz
    private Radio? _radio;
    private int _maxObservedNetworkPing = -1;
    private bool _disconnectInitiatedByUs;
    private readonly HashSet<uint> _fallbackLoggedHandles = new();
    private readonly object _fallbackLoggedHandlesLock = new();

    // MOX transition logging state (issue #51 diagnostics; see LogMoxTransition).
    private static readonly TimeSpan MoxLogThrottle = TimeSpan.FromSeconds(2);
    private DateTime _lastMoxLogUtc = DateTime.MinValue;
    private bool? _lastSeenMox;
    private int _suppressedMoxTransitions;

    public event Action<string>? DiagnosticEvent;

    private void EmitDiag(string line) => DiagnosticEvent?.Invoke(line);

    /// <inheritdoc />
    /// <remarks>Volatile: written on the UI thread, read on FlexLib event threads.</remarks>
    public bool VerboseDiagnostics
    {
        get => _verboseDiagnostics;
        set => _verboseDiagnostics = value;
    }

    private volatile bool _verboseDiagnostics;

    // ── Connection ───────────────────────────────────────────────────────────

    public bool IsConnected        => _radio?.Connected ?? false;
    public string? ConnectedModel  => _radio?.Model;
    public string? ConnectedSerial => _radio?.Serial;
    public string? Versions        => _radio?.Versions;

    public event Action<bool>? ConnectionStateChanged;

    public async Task<bool> ConnectAsync(DiscoveredRadio radio)
    {
        var flexRadio = API.RadioList.FirstOrDefault(r => r.Serial == radio.Serial);
        if (flexRadio is null) return false;

        // Found by the Codex deep audit of the issue #64 binding change: a
        // radio-side drop leaves these handlers attached (only the operator
        // Disconnect path unwires), so reconnecting to the same Radio object
        // subscribed a second time and every event arrived twice. Unsubscribing
        // first is a no-op when nothing is attached, so one line covers both
        // paths rather than duplicating teardown into the drop handler.
        UnwireRadioEvents(flexRadio);

        flexRadio.PropertyChanged    += OnRadioPropertyChanged;
        flexRadio.PanadapterAdded    += OnPanadapterAdded;
        flexRadio.PanadapterRemoved  += OnPanadapterRemoved;
        flexRadio.SliceAdded         += OnSliceAdded;
        flexRadio.SliceRemoved       += OnSliceRemoved;
        flexRadio.DAXIQStreamAdded   += OnDAXIQStreamAdded;
        flexRadio.DAXIQStreamRemoved += OnDAXIQStreamRemoved;
        flexRadio.GUIClientAdded     += OnGUIClientAdded;
        flexRadio.GUIClientRemoved   += OnGUIClientRemoved;
        _radio = flexRadio;

        EmitDiag($"Dialing {flexRadio.Model} at {flexRadio.IP}:{flexRadio.CommandPort} (serial={flexRadio.Serial}).");

        bool connected = await System.Threading.Tasks.Task.Run(() => flexRadio.Connect());

        if (!connected)
        {
            EmitDiag($"Dial failed: {flexRadio.Model} at {flexRadio.IP}.");
            UnwireRadioEvents(flexRadio);
            _radio = null;
            return false;
        }

        // Versions is populated async by FlexLib's UpdateVersions reply handler
        // (Radio.cs:7127); the [FLEX] log line lives in OnRadioPropertyChanged's
        // "Versions" case so the value actually surfaces after the radio replies.
        EmitDiag($"Radio GuiClientIPs={flexRadio.GuiClientIPs ?? "(none)"}, GuiClientHosts={flexRadio.GuiClientHosts ?? "(none)"}.");

        // Snapshot GUI clients already present at connect time. The GUIClientAdded
        // event fires only for clients that arrive *after* we subscribe, so
        // discovery-populated entries would be silent without this loop.
        lock (flexRadio.GuiClientsLockObj)
        {
            foreach (var existing in flexRadio.GuiClients)
                OnGUIClientAdded(existing);
        }

        // Snapshot lists already populated by FlexLib during connect
        foreach (var pan in flexRadio.PanadapterList)
            TrackPanadapter(pan);

        foreach (var slc in flexRadio.SliceList)
            TrackSlice(slc);

        foreach (var iq in flexRadio.DAXIQStreamList)
        {
            iq.PropertyChanged += OnDAXIQStreamPropertyChanged;
            _flexDaxIQStreams[iq.DAXIQChannel] = iq;
            _daxIQStreams[iq.DAXIQChannel] = ToDaxIQStreamInfo(iq);
        }

        _maxObservedNetworkPing = -1;
        // Cleared here as well as on Disconnect, since a radio-side drop leaves
        // the old radio object in place and this is the only path a reconnect
        // after one takes.
        _rfPowerReported = false;
        _boundToStation = false;
        PublishNetworkStatus();
        RefreshGuiClients();

        return true;
    }

    public void Disconnect()
    {
        if (_radio is null) return;
        StopTelemetry();
        var radio = _radio;
        _radio = null;
        _disconnectInitiatedByUs = true;
        EmitDiag("Disconnect: trigger=operator.");
        UnwireRadioEvents(radio);

        foreach (var pan in _flexPanadapters.Values)
            pan.PropertyChanged -= OnPanadapterPropertyChanged;
        foreach (var slc in _flexSlices.Values)
            slc.PropertyChanged -= OnSlicePropertyChanged;
        foreach (var iq in _flexDaxIQStreams.Values)
            iq.PropertyChanged -= OnDAXIQStreamPropertyChanged;

        radio.Disconnect();
        _panadapters.Clear();
        _flexPanadapters.Clear();
        _slices.Clear();
        _flexSlices.Clear();
        _daxIQStreams.Clear();
        _flexDaxIQStreams.Clear();
        _maxObservedNetworkPing = -1;
        // The next radio reports its own power; until it does, absent.
        _rfPowerReported = false;
        _boundToStation = false;
        RfPowerChanged?.Invoke(null);
        NetworkStatus = NetworkStatusInfo.Empty;
        NetworkStatusChanged?.Invoke(NetworkStatus);
        _guiClients = Array.Empty<GuiClientInfo>();
        GuiClientsChanged?.Invoke(_guiClients);
        lock (_fallbackLoggedHandlesLock) _fallbackLoggedHandles.Clear();
        ConnectionStateChanged?.Invoke(false);
        _disconnectInitiatedByUs = false;
    }

    private void UnwireRadioEvents(Radio radio)
    {
        radio.PropertyChanged    -= OnRadioPropertyChanged;
        radio.PanadapterAdded    -= OnPanadapterAdded;
        radio.PanadapterRemoved  -= OnPanadapterRemoved;
        radio.SliceAdded         -= OnSliceAdded;
        radio.SliceRemoved       -= OnSliceRemoved;
        radio.DAXIQStreamAdded   -= OnDAXIQStreamAdded;
        radio.DAXIQStreamRemoved -= OnDAXIQStreamRemoved;
        radio.GUIClientAdded     -= OnGUIClientAdded;
        radio.GUIClientRemoved   -= OnGUIClientRemoved;
    }

    private void OnRadioPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case "Connected":
                {
                    var nowConnected = _radio?.Connected ?? false;
                    if (!nowConnected && !_disconnectInitiatedByUs)
                    {
                        EmitDiag("Disconnect: trigger=flexlib.");
                        // A radio-side drop leaves the meter subscription and
                        // pump running against a dead Radio; tear them down on
                        // this path too, not just the operator Disconnect path.
                        StopTelemetry();
                        // Same reasoning for transmit power: RfPowerWatts already
                        // reads absent once Connected goes false, but nothing
                        // told subscribers, so SmartDeck's QRP toggle stayed lit
                        // holding a power from the dropped session.
                        _rfPowerReported = false;
                        _boundToStation = false;
                        RfPowerChanged?.Invoke(null);
                    }
                    ConnectionStateChanged?.Invoke(nowConnected);
                }
                break;
            case "AvgDAXkbps":
                if (_radio is not null) AvgDAXKbpsChanged?.Invoke(_radio.AvgDAXkbps);
                break;
            // Fires for our own writes and for another client's alike, which is
            // what lets SmartDeck's QRP toggle stand down when the operator
            // changes power in SmartSDR instead of fighting them for it.
            case "RFPower":
                _rfPowerReported = true;
                RfPowerChanged?.Invoke(RfPowerWatts);
                break;
            case "NetworkPing":
            case "RemoteNetworkQuality":
                PublishNetworkStatus();
                break;
            case "GuiClients":
                RefreshGuiClients();
                break;
            // Bug fix 2026-05-19 (Radio versions always "(unavailable)" in
            // [FLEX] log): flexRadio.Versions is populated async by FlexLib's
            // UpdateVersions reply handler (Radio.cs:7127). Reading it
            // synchronously right after Connect() returns is too early.
            // Defer to this PropertyChanged event so the value surfaces.
            case "Versions":
                if (_radio?.Versions is { Length: > 0 } v)
                    EmitDiag($"Radio versions: {v}.");
                break;
            // Diagnostic for issue #51 (reported 2026-07-23): Skimmer's pan
            // intermittently floods with the operator's own keying while
            // SmartSDR's pan stays clean; suspected DAX-IQ fault in SmartSDR
            // 4.2.20. Log TX transitions so streamer-status.log correlates
            // fault onset with key-down. Logging only; no action on TX state.
            // FlexLib derives Mox from the interlock state (Radio.cs:7588),
            // so CW key-down surfaces here, not just the MOX button.
            case "Mox":
                if (_radio is { } moxRadio)
                    LogMoxTransition(moxRadio.Mox);
                break;
        }
    }

    /// <summary>
    /// Emits a `[FLEX]` line per MOX on/off transition, throttled to one line
    /// per <see cref="MoxLogThrottle"/>: QSK break-in can bounce the interlock
    /// state per CW element, and unthrottled lines would flood the footer and
    /// log. Bounces inside the window are counted and reported on the next
    /// emitted line, so no transition is silently dropped.
    /// </summary>
    private void LogMoxTransition(bool mox)
    {
        // Issue #58 (2026-08-02): debug-only. Even throttled, CW operation
        // logs a MOX line per keying burst, dirtying the support log during
        // every QSO. Issue #51 captures now require the operator to enable
        // Debug logging on the Logs tab first.
        if (!VerboseDiagnostics)
            return;

        if (_lastSeenMox == mox)
            return;
        _lastSeenMox = mox;

        var now = DateTime.UtcNow;
        if (now - _lastMoxLogUtc < MoxLogThrottle)
        {
            _suppressedMoxTransitions++;
            return;
        }

        var suffix = _suppressedMoxTransitions switch
        {
            0 => string.Empty,
            1 => " (1 rapid transition unlogged)",
            var n => $" ({n} rapid transitions unlogged)",
        };
        _suppressedMoxTransitions = 0;
        _lastMoxLogUtc = now;
        EmitDiag($"MOX {(mox ? "on" : "off")}{suffix}.");
    }

    // ── Panadapters ──────────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<uint, PanadapterInfo> _panadapters   = new();
    private readonly ConcurrentDictionary<uint, Panadapter>     _flexPanadapters = new();

    public IReadOnlyList<PanadapterInfo> Panadapters => _panadapters.Values.ToList();

    public event Action<PanadapterInfo>? PanadapterAdded;
    public event Action<PanadapterInfo>? PanadapterRemoved;
    public event Action<PanadapterInfo>? PanadapterUpdated;

    private void OnPanadapterAdded(Panadapter pan, Waterfall _)
    {
        var info = TrackPanadapter(pan);
        EmitDiag(
            $"Panadapter added: streamId=0x{info.StreamId:X}, ClientHandle=0x{info.ClientHandle:X}, "
            + $"station={info.ClientStation}, DAX-IQ ch={info.DAXIQChannel}, center={info.CenterFreqMHz:F6} MHz.");
        PanadapterAdded?.Invoke(info);
    }

    private void OnPanadapterRemoved(Panadapter pan)
    {
        pan.PropertyChanged -= OnPanadapterPropertyChanged;
        _flexPanadapters.TryRemove(pan.StreamID, out _);
        if (_panadapters.TryRemove(pan.StreamID, out var info))
        {
            EmitDiag($"Panadapter removed: streamId=0x{info.StreamId:X}, station={info.ClientStation}.");
            PanadapterRemoved?.Invoke(info);
        }
    }

    private PanadapterInfo TrackPanadapter(Panadapter pan)
    {
        pan.PropertyChanged += OnPanadapterPropertyChanged;
        _flexPanadapters[pan.StreamID] = pan;

        // Issue #59 phase 2c: the RF gain range is not part of a panadapter's
        // normal status; it only arrives in reply to this explicit request
        // (Panadapter.cs:39-65). Asked once per panadapter here rather than
        // lazily when SmartDeck opens: it is a single command per panadapter,
        // and the reply then flows through the usual property-changed path.
        pan.GetRFGainInfo();

        var info = ToPanadapterInfo(pan);
        _panadapters[pan.StreamID] = info;
        return info;
    }

    private void OnPanadapterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Panadapter pan) return;
        if (e.PropertyName is not ("CenterFreq" or "DAXIQChannel"
            or "RFGain" or "RFGainLow" or "RFGainHigh" or "RFGainStep")) return;

        var info = ToPanadapterInfo(pan);
        _panadapters[pan.StreamID] = info;
        PanadapterUpdated?.Invoke(info);

        // Keep any live stream's centre-freq in sync with its panadapter.
        if (info.DAXIQChannel > 0 &&
            _daxIQStreams.TryGetValue(info.DAXIQChannel, out var existingStream))
        {
            var updated = existingStream with { CenterFreqMHz = info.CenterFreqMHz };
            _daxIQStreams[info.DAXIQChannel] = updated;
            DaxIQStreamUpdated?.Invoke(updated);
        }
    }

    // ── Slices ───────────────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<string, SliceInfo> _slices     = new();
    private readonly ConcurrentDictionary<string, Slice>     _flexSlices = new();

    public IReadOnlyList<SliceInfo> Slices => _slices.Values.ToList();

    public event Action<SliceInfo>? SliceAdded;
    public event Action<SliceInfo>? SliceRemoved;
    public event Action<SliceInfo>? SliceUpdated;

    private void OnSliceAdded(Slice slc)
    {
        var info = TrackSlice(slc);
        EmitDiag(
            $"Slice added: {info.Letter}, station={info.ClientStation}, "
            + $"pan=0x{info.PanadapterStreamId:X}, freq={info.FreqMHz:F6} MHz.");
        SliceAdded?.Invoke(info);
    }

    private void OnSliceRemoved(Slice slc)
    {
        slc.PropertyChanged -= OnSlicePropertyChanged;
        _flexSlices.TryRemove(SliceKey(slc), out _);
        if (_slices.TryRemove(SliceKey(slc), out var info))
        {
            EmitDiag($"Slice removed: {info.Letter}, station={info.ClientStation}.");
            SliceRemoved?.Invoke(info);
        }
    }

    private SliceInfo TrackSlice(Slice slc)
    {
        slc.PropertyChanged += OnSlicePropertyChanged;
        _flexSlices[SliceKey(slc)] = slc;
        var info = ToSliceInfo(slc);
        _slices[SliceKey(slc)] = info;
        return info;
    }

    private void OnSlicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Slice slc) return;
        if (!ShouldPublishSliceUpdate(e.PropertyName)) return;

        var info = ToSliceInfo(slc);
        _slices[SliceKey(slc)] = info;
        SliceUpdated?.Invoke(info);
    }

    public Task SetSliceFrequencyAsync(SliceInfo slice, double freqMHz)
    {
        var target = FindFlexSlice(slice);

        if (target is not null)
        {
            // Avoid unnecessary radio writes when frequency is unchanged (or within jitter tolerance).
            if (Math.Abs(target.Freq - freqMHz) > SliceNoOpToleranceMHz)
                target.Freq = freqMHz;
        }
        return Task.CompletedTask;
    }

    // ── Slice control surface (issue #59 phase 2a) ───────────────────────────

    public Task SetSliceAgcThresholdAsync(SliceInfo slice, int threshold)
    {
        if (FindFlexSlice(slice) is { } target && target.AGCThreshold != threshold)
        {
            // FlexLib clamps to 0-100 in the setter itself, so no clamp here.
            target.AGCThreshold = threshold;
            EmitDiag($"Slice {slice.Letter}: AGC-T set to {threshold}.");
        }
        return Task.CompletedTask;
    }

    public Task SetPanadapterRfGainAsync(PanadapterInfo panadapter, int rfGain)
    {
        if (_flexPanadapters.TryGetValue(panadapter.StreamId, out var target) && target.RFGain != rfGain)
        {
            target.RFGain = rfGain;
            EmitDiag($"Panadapter 0x{panadapter.StreamId:X}: RF gain set to {rfGain} dB.");
        }
        return Task.CompletedTask;
    }

    public Task SetSliceModeAsync(SliceInfo slice, SliceMode mode)
    {
        if (FindFlexSlice(slice) is { } target)
            target.DemodMode = mode.ToRadioValue();
        return Task.CompletedTask;
    }

    // ── Transmit power (issue #64) ───────────────────────────────────────────

    // FlexLib initialises Radio.RFPower to 0, which is also a power the operator
    // can select, so the value is only trustworthy once the radio has actually
    // reported one. ParseTransmitStatus raises RFPower unconditionally on every
    // transmit status (Radio.cs:10183-10184), so this flag flips during the
    // connect-time status burst and stays set for the session.
    private volatile bool _rfPowerReported;

    // Written from the binding path, read on FlexLib event threads.
    private volatile bool _boundToStation;

    // Absent until the radio has reported a power *in the station's context*.
    // The bind requirement is the whole lesson of issue #64: an unbound client
    // is told a fictional 100 W, and SmartDeck saving that as the power to
    // return to would write it over the operator's real setting.
    public int? RfPowerWatts =>
        _boundToStation && _rfPowerReported && _radio is { Connected: true } radio ? radio.RFPower : null;

    public event Action<int?>? RfPowerChanged;

    public Task SetRfPowerAsync(int watts)
    {
        // No MOX guard, and none is needed: this sets what a later transmission
        // will do rather than keying the radio. FlexLib clamps to 0-100 in the
        // setter (Radio.cs:8377-8379).
        // Not logged here: SmartDeck writes one [STREAMER] line per QRP press
        // naming what it saved or restored, which says more than a bare write
        // would, and a slider drag would otherwise put a line on the log per
        // step (same reasoning as the band-restore summary line).
        if (_radio is { Connected: true } radio && radio.RFPower != watts)
            radio.RFPower = watts;
        return Task.CompletedTask;
    }

    // No MOX guard on either antenna setter: the radio itself refuses antenna
    // changes while transmitting, so a guard here would be app-side code
    // duplicating a hardware interlock.
    public Task SetSliceRxAntennaAsync(SliceInfo slice, string antenna)
    {
        if (string.IsNullOrWhiteSpace(antenna)) return Task.CompletedTask;

        if (FindFlexSlice(slice) is { } target && !string.Equals(target.RXAnt, antenna, StringComparison.Ordinal))
        {
            target.RXAnt = antenna;
            EmitDiag($"Slice {slice.Letter}: RX antenna set to {antenna}.");
        }
        return Task.CompletedTask;
    }

    public Task SetSliceTxAntennaAsync(SliceInfo slice, string antenna)
    {
        if (string.IsNullOrWhiteSpace(antenna)) return Task.CompletedTask;

        if (FindFlexSlice(slice) is { } target && !string.Equals(target.TXAnt, antenna, StringComparison.Ordinal))
        {
            target.TXAnt = antenna;
            EmitDiag($"Slice {slice.Letter}: TX antenna set to {antenna}.");
        }
        return Task.CompletedTask;
    }

    private Slice? FindFlexSlice(SliceInfo slice) =>
        _flexSlices.Values.FirstOrDefault(s =>
            string.Equals(s.Letter, slice.Letter, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ResolveStation(s.ClientHandle), slice.ClientStation, StringComparison.OrdinalIgnoreCase));

    public Task PublishSpotAsync(RadioSpotInfo spot)
    {
        var radio = _radio;
        if (radio is null || !radio.Connected)
            return Task.CompletedTask;

        if (string.IsNullOrWhiteSpace(spot.Callsign) || spot.RxFrequencyMHz <= 0)
            return Task.CompletedTask;

        var flexSpot = new Spot
        {
            Callsign = spot.Callsign.Trim(),
            RXFrequency = spot.RxFrequencyMHz,
            Source = NormalizeSource(spot.Source),
            SpotterCallsign = string.IsNullOrWhiteSpace(spot.SpotterCallsign) ? null : spot.SpotterCallsign.Trim(),
            Comment = string.IsNullOrWhiteSpace(spot.Comment) ? null : spot.Comment,
            Mode = spot.Mode,
            Color = spot.Color,
            BackgroundColor = spot.BackgroundColor,
            LifetimeSeconds = spot.LifetimeSeconds,
            Timestamp = DateTime.UtcNow
        };

        radio.RequestSpot(flexSpot);
        return Task.CompletedTask;
    }

    // ── DAX-IQ Streams ───────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<int, DaxIQStreamInfo> _daxIQStreams = new();
    private readonly ConcurrentDictionary<int, DAXIQStream> _flexDaxIQStreams = new();

    public IReadOnlyList<DaxIQStreamInfo> DaxIQStreams => _daxIQStreams.Values.ToList();

    public event Action<DaxIQStreamInfo>? DaxIQStreamAdded;
    public event Action<DaxIQStreamInfo>? DaxIQStreamRemoved;
    public event Action<DaxIQStreamInfo>? DaxIQStreamUpdated;

    private void OnDAXIQStreamAdded(DAXIQStream iq)
    {
        iq.PropertyChanged += OnDAXIQStreamPropertyChanged;
        _flexDaxIQStreams[iq.DAXIQChannel] = iq;

        var info = ToDaxIQStreamInfo(iq);
        _daxIQStreams[iq.DAXIQChannel] = info;
        // The DAX-IQ stream's ClientHandle is the bridging session that opened
        // the stream (typically DAX-the-app), not the GUI client that owns the
        // panadapter. It's expected to be a non-GUI handle, so suppress the
        // fallback warning for this resolve.
        // Issue #58: add/remove fires for every station's DAX churn on the
        // radio and was 78% of streamer-status.log in a field capture, so it is
        // verbose-only. The pan-to-channel mapping stays visible at default
        // verbosity via the "Panadapter added ... DAX-IQ ch=" lines.
        if (VerboseDiagnostics)
        {
            EmitDiag(
                $"DAX-IQ stream added: ch={info.DAXIQChannel}, ClientHandle=0x{info.ClientHandle:X}, "
                + $"station={ResolveStation(info.ClientHandle, logFallback: false)}, sampleRate={info.SampleRate}.");
        }
        DaxIQStreamAdded?.Invoke(info);
    }

    private void OnDAXIQStreamRemoved(DAXIQStream iq)
    {
        iq.PropertyChanged -= OnDAXIQStreamPropertyChanged;
        _flexDaxIQStreams.TryRemove(iq.DAXIQChannel, out _);

        if (_daxIQStreams.TryRemove(iq.DAXIQChannel, out var info))
        {
            // Issue #58: verbose-only, see the stream-added comment above.
            if (VerboseDiagnostics)
                EmitDiag($"DAX-IQ stream removed: ch={info.DAXIQChannel}.");
            DaxIQStreamRemoved?.Invoke(info);
        }
    }

    private void OnDAXIQStreamPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DAXIQStream iq) return;
        if (e.PropertyName is not ("SampleRate" or "IsActive" or "Pan" or "DAXIQChannel")) return;

        // Handle rare channel-number changes by moving the tracked instance key.
        var oldEntry = _flexDaxIQStreams.FirstOrDefault(kvp => ReferenceEquals(kvp.Value, iq));
        if (oldEntry.Value is not null && oldEntry.Key != iq.DAXIQChannel)
            _flexDaxIQStreams.TryRemove(oldEntry.Key, out _);
        _flexDaxIQStreams[iq.DAXIQChannel] = iq;

        var updated = ToDaxIQStreamInfo(iq);
        _daxIQStreams[iq.DAXIQChannel] = updated;
        DaxIQStreamUpdated?.Invoke(updated);
    }

    public async Task<RequestStreamResult> StopDaxIQStreamAsync(PanadapterInfo pan)
    {
        if (_radio is null) return RequestStreamResult.Timeout;

        var stream = _radio.DAXIQStreamList
            .FirstOrDefault(s => s.DAXIQChannel == pan.DAXIQChannel);

        if (stream is null) return RequestStreamResult.NoChannelAssigned;

        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

        void OnRemoved(DAXIQStream iq)
        {
            if (iq.DAXIQChannel == pan.DAXIQChannel)
                tcs.TrySetResult(true);
        }

        _radio.DAXIQStreamRemoved += OnRemoved;
        try
        {
            stream.Close();

            var completed = await System.Threading.Tasks.Task.WhenAny(
                tcs.Task,
                System.Threading.Tasks.Task.Delay(5000));

            return completed == tcs.Task
                ? RequestStreamResult.Success
                : RequestStreamResult.Timeout;
        }
        finally
        {
            _radio.DAXIQStreamRemoved -= OnRemoved;
        }
    }

    public int AvgDAXKbps => _radio?.AvgDAXkbps ?? 0;
    public event Action<int>? AvgDAXKbpsChanged;
    public event Action<NetworkStatusInfo>? NetworkStatusChanged;
    public event Action<IReadOnlyList<GuiClientInfo>>? GuiClientsChanged;

    public NetworkStatusInfo NetworkStatus { get; private set; } = NetworkStatusInfo.Empty;

    // ── GUI clients (for DAX-bound-radio check) ──────────────────────────────

    private IReadOnlyList<GuiClientInfo> _guiClients = Array.Empty<GuiClientInfo>();

    public IReadOnlyList<GuiClientInfo> GuiClients => _guiClients;

    private void RefreshGuiClients()
    {
        var radio = _radio;
        if (radio is null)
        {
            _guiClients = Array.Empty<GuiClientInfo>();
            GuiClientsChanged?.Invoke(_guiClients);
            return;
        }

        List<GuiClientInfo> snapshot;
        lock (radio.GuiClientsLockObj)
        {
            // See ResolveStation for the issue #30 rationale: normalize station
            // here too so the GuiClients[] snapshot (drives dropdown rebuild and
            // LogGuiClientsSnapshot) agrees with pan/slice ClientStation values.
            snapshot = radio.GuiClients
                .Select(c => new GuiClientInfo(
                    c.ClientHandle,
                    c.Program?.Trim() ?? string.Empty,
                    c.Station?.Trim() ?? string.Empty)
                {
                    ClientID = c.ClientID?.Trim() ?? string.Empty
                })
                .ToList();
        }

        _guiClients = snapshot;
        BindToStationGuiClient(radio, snapshot);
        GuiClientsChanged?.Invoke(_guiClients);
    }

    private volatile string _controlStation = string.Empty;

    /// <inheritdoc />
    public string ControlStation
    {
        get => _controlStation;
        set
        {
            var station = value ?? string.Empty;
            if (string.Equals(_controlStation, station, StringComparison.OrdinalIgnoreCase)) return;

            _controlStation = station;
            // The station can be chosen before the GUI-client snapshot exists,
            // so bind against whatever snapshot we hold now; RefreshGuiClients
            // binds again when the snapshot arrives or changes. Deliberately
            // not a full RefreshGuiClients call: that republishes
            // GuiClientsChanged, which re-enters the app's control-station loss
            // detection mid-station-change, where the "seen" gate can still be
            // set from the station being left.
            if (_radio is { } radio) BindToStationGuiClient(radio, _guiClients);
        }
    }

    /// <summary>
    /// Puts this non-GUI connection into the station's client context.
    /// </summary>
    /// <remarks>
    /// FlexLib binds every non-GUI client at connect, to whatever
    /// <c>BoundClientID</c> holds (<c>Radio.cs:2249</c>). Left unset that is an
    /// empty id, and the radio then answers in a context belonging to no
    /// station: transmit status reported a constant 100 W while the operator's
    /// radio was at 62 W, and per-band transmit settings never arrived at all
    /// (issue #64, diagnosed 2026-08-04 from a live capture). Binding is what
    /// makes the transmit status ours to read. Slice-scoped controls never
    /// needed it, which is why the phase-2 plan correctly declined it; TX power
    /// is the first client-scoped control the deck has carried.
    /// </remarks>
    private void BindToStationGuiClient(Radio radio, IReadOnlyList<GuiClientInfo> clients)
    {
        // Bind to the station the app is already operating inside, rather than
        // to whichever GUI client happens to be alone on the radio: slices,
        // panadapters and the CW Skimmer workflow are all scoped by
        // ControlStation, so the transmit context has to agree with them or the
        // deck would read one station while controlling another.
        if (ControlStation is not { Length: > 0 } wanted) return;

        var station = clients.FirstOrDefault(c =>
            c.ClientID.Length > 0 &&
            string.Equals(c.Station, wanted, StringComparison.OrdinalIgnoreCase));

        if (station is null)
        {
            // Normal during connect: the GUI-client snapshot arrives after the
            // station is chosen. RefreshGuiClients runs again when it lands.
            if (_verboseDiagnostics)
                EmitDiag($"Not binding yet: no GUI client with a client id for station '{wanted}' among {clients.Count} reported.");
            return;
        }

        if (string.Equals(radio.BoundClientID, station.ClientID, StringComparison.Ordinal))
        {
            _boundToStation = true;
            return;
        }

        radio.BoundClientID = station.ClientID;
        _boundToStation = true;

        // Anything cached before this moment came from the unbound context that
        // reported a fictional 100 W, so discard it and wait for the radio to
        // report in the station's context. Found by the Codex deep audit: the
        // connect-time status can land before the GUI-client snapshot the bind
        // needs, and SmartDeck would otherwise offer QRP against that value.
        _rfPowerReported = false;
        RfPowerChanged?.Invoke(null);

        EmitDiag($"Bound to GUI client {station.DisplayLabel} (client_id={station.ClientID}).");
    }

    public void ResetNetworkStatus()
    {
        _maxObservedNetworkPing = -1;
        NetworkStatus = NetworkStatusInfo.Empty;
        NetworkStatusChanged?.Invoke(NetworkStatus);
    }

    // ── Telemetry (issue #59, SmartDeck) ─────────────────────────────────────

    // Display cadence. The four meter streams deliver roughly 28 events/sec
    // combined: forward power and SWR at ~13.4 Hz, PA temperature and volts at
    // ~0.4 Hz, both measured by the issue #59 gating spike against a live
    // FLEX-6400M. 250 ms cuts UI marshals to 4/sec while staying well clear of
    // the slow pair, so temperature and volts never look stalled.
    private static readonly TimeSpan TelemetryEmitInterval = TimeSpan.FromMilliseconds(250);

    private readonly TelemetrySnapshotAccumulator _telemetry = new();
    private readonly object _telemetrySync = new();
    private CancellationTokenSource? _telemetryCts;

    // The radio the meter handlers were attached to. Held separately from
    // _radio so a disconnect that clears _radio first can still detach cleanly.
    private Radio? _telemetryRadio;

    /// <inheritdoc />
    /// <remarks>
    /// Written on the pump thread, read on the UI thread. Reference assignment
    /// is atomic and the record is immutable, so a reader sees either the old
    /// or the new snapshot, never a torn one.
    /// </remarks>
    public RadioTelemetryInfo Telemetry { get; private set; } = RadioTelemetryInfo.Empty;

    public event Action<RadioTelemetryInfo>? TelemetryChanged;

    public void StartTelemetry()
    {
        lock (_telemetrySync)
        {
            if (_telemetryCts is not null) return;

            var radio = _radio;
            if (radio is null || !radio.Connected) return;

            // Radio-level meter events only. These are radio-scoped values, not
            // slice-scoped, so no GUI-client binding is involved: the spike
            // confirmed they arrive with API.IsGUI = false.
            radio.ForwardPowerDataReady += OnForwardPowerData;
            radio.SWRDataReady          += OnSwrData;
            radio.PATempDataReady       += OnPaTempData;
            radio.VoltsDataReady        += OnVoltsData;
            _telemetryRadio = radio;

            var cts = new CancellationTokenSource();
            _telemetryCts = cts;
            _ = PumpTelemetryAsync(cts.Token);
        }

        EmitDiag("Telemetry: started.");
    }

    public void StopTelemetry()
    {
        CancellationTokenSource? cts;
        Radio? radio;

        lock (_telemetrySync)
        {
            if (_telemetryCts is null) return;
            cts = _telemetryCts;
            radio = _telemetryRadio;
            _telemetryCts = null;
            _telemetryRadio = null;
        }

        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();

        if (radio is not null)
        {
            radio.ForwardPowerDataReady -= OnForwardPowerData;
            radio.SWRDataReady          -= OnSwrData;
            radio.PATempDataReady       -= OnPaTempData;
            radio.VoltsDataReady        -= OnVoltsData;
        }

        // Drop accumulated readings so a later start shows dashes rather than
        // values from the previous session.
        _telemetry.Reset();
        Telemetry = RadioTelemetryInfo.Empty;
        TelemetryChanged?.Invoke(Telemetry);
        EmitDiag("Telemetry: stopped.");
    }

    private async Task PumpTelemetryAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TelemetryEmitInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // Skips the tick entirely when no sample arrived, rather than
                // republishing an identical snapshot four times a second.
                if (!_telemetry.TryTakeSnapshot(TimeProvider.System.GetUtcNow(), out var snapshot))
                    continue;

                Telemetry = snapshot;
                TelemetryChanged?.Invoke(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on StopTelemetry.
        }
    }

    private void OnForwardPowerData(float data) => _telemetry.Add(TelemetryChannel.ForwardPowerDbm, data);
    private void OnSwrData(float data)          => _telemetry.Add(TelemetryChannel.Swr, data);
    private void OnPaTempData(float data)       => _telemetry.Add(TelemetryChannel.PaTempCelsius, data);
    private void OnVoltsData(float data)        => _telemetry.Add(TelemetryChannel.VoltsDc, data);

    // ── Own client handle ────────────────────────────────────────────────────

    public uint OwnClientHandle    => _radio?.ClientHandle ?? 0;

    // Bug fix 2026-06-04 (issue #45): our own client handle is a non-GUI handle
    // (API.IsGUI = false) so it never appears in GuiClients; the prior
    // ResolveStation call therefore always hit the hex fallback and leaked
    // "0x0"/"0x..." into the connected-station header whenever no named control
    // station was selected (e.g. connecting to a radio whose GUI client has no
    // station name). Return empty on no real station so the header and
    // EnsureSelectedControlStation fall back to "Unknown Station" / a present
    // station instead of a raw handle. Chosen over a ViewModel "starts with 0x"
    // display heuristic because that could suppress a legitimately-named
    // station. Reported by a betatester on v0.1.19b.
    public string OwnClientStation
    {
        get
        {
            var handle = OwnClientHandle;
            if (handle == 0) return string.Empty;
            // Read the already-snapshotted, trimmed _guiClients list rather than
            // the live _radio.GuiClients collection: FlexLib mutates the latter on
            // its event threads, so a lock-free enumeration here could race. The
            // snapshot reference is reassigned atomically by RefreshGuiClients.
            return _guiClients.FirstOrDefault(c => c.ClientHandle == handle)?.Station
                ?? string.Empty;
        }
    }

    // ── Request DAX-IQ stream ────────────────────────────────────────────────

    public async Task<RequestStreamResult> RequestDaxIQStreamAsync(PanadapterInfo pan)
    {
        if (_radio is null) return RequestStreamResult.Timeout;

        if (pan.DAXIQChannel <= 0)
            return RequestStreamResult.NoChannelAssigned;

        if (_radio.DAXIQStreamList.Any(s => s.DAXIQChannel == pan.DAXIQChannel))
            return RequestStreamResult.StreamAlreadyActive;

        // Use a TCS so we can await the async DAXIQStreamAdded radio event
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

        void OnAdded(DAXIQStream iq)
        {
            if (iq.DAXIQChannel == pan.DAXIQChannel)
                tcs.TrySetResult(true);
        }

        _radio.DAXIQStreamAdded += OnAdded;
        try
        {
            _radio.RequestDAXIQStream(pan.DAXIQChannel);

            var completed = await System.Threading.Tasks.Task.WhenAny(
                tcs.Task,
                System.Threading.Tasks.Task.Delay(5000));

            return completed == tcs.Task
                ? RequestStreamResult.Success
                : RequestStreamResult.Timeout;
        }
        finally
        {
            _radio.DAXIQStreamAdded -= OnAdded;
        }
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────

    private PanadapterInfo ToPanadapterInfo(Panadapter pan) =>
        new(pan.StreamID, pan.CenterFreq, pan.DAXIQChannel, ResolveStation(pan.ClientHandle), pan.ClientHandle)
        {
            // Issue #59 phase 2c. Low/High/Step stay zero until the radio
            // answers GetRFGainInfo(), which TrackPanadapter requests.
            RfGain     = pan.RFGain,
            RfGainLow  = pan.RFGainLow,
            RfGainHigh = pan.RFGainHigh,
            RfGainStep = pan.RFGainStep
        };

    private SliceInfo ToSliceInfo(Slice slc) =>
        new(slc.Letter    ?? string.Empty,
            slc.DemodMode ?? string.Empty,
            slc.Freq,
            ResolveRitEnabled(slc),
            ResolveRitOffsetHz(slc),
            ResolveTuneStepHz(slc),
            slc.PanadapterStreamID,
            ResolveStation(slc.ClientHandle),
            slc.DAXChannel)
        {
            // Issue #59 phase 2a. The antenna lists are radio-reported and can
            // be null before the radio has answered, which is why they collapse
            // to empty rather than being dereferenced.
            RxAntenna = slc.RXAnt ?? string.Empty,
            TxAntenna = slc.TXAnt ?? string.Empty,
            RxAntennaOptions = slc.RXAntList ?? [],
            TxAntennaOptions = slc.TXAntList ?? [],
            AgcThreshold = slc.AGCThreshold
        };

    private static bool ShouldPublishSliceUpdate(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return true;

        if (propertyName is "Freq" or "DemodMode" or "DAXChannel")
            return true;

        // Issue #59 phase 2a: antenna selection and the radio-reported option
        // lists drive the SmartDeck control surface, so their changes have to
        // reach the UI. Without this the selectors would never populate,
        // because the lists arrive after the slice is first tracked.
        if (propertyName is "RXAnt" or "TXAnt" or "RXAntList" or "TXAntList")
            return true;

        // AGC-T drives its own SmartDeck readout.
        if (propertyName is "AGCThreshold")
            return true;

        // FlexLib variants expose RIT state/offset and tune-step with different names.
        return propertyName.Contains("RIT", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("Step", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ResolveRitEnabled(Slice slc)
    {
        return TryGetBoolProperty(slc, "RITOn")
            ?? TryGetBoolProperty(slc, "RitOn")
            ?? TryGetBoolProperty(slc, "RitEnabled")
            ?? false;
    }

    private static double ResolveRitOffsetHz(Slice slc)
    {
        return TryGetDoubleProperty(slc, "RITFreq")
            ?? TryGetDoubleProperty(slc, "RitFreq")
            ?? TryGetDoubleProperty(slc, "RITOffset")
            ?? TryGetDoubleProperty(slc, "RitOffset")
            ?? 0d;
    }

    private static int ResolveTuneStepHz(Slice slc)
    {
        return TryGetTuneStepHz(slc, "TuneStep")
            ?? TryGetTuneStepHz(slc, "Step")
            ?? 0;
    }

    private static int? TryGetTuneStepHz(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null)
            return null;

        var value = property.GetValue(target);
        if (value is null)
            return null;

        if (value is int i && i > 0)
            return i;
        if (value is long l && l > 0 && l <= int.MaxValue)
            return (int)l;
        if (value is float f && f > 0)
            return (int)Math.Round(f);
        if (value is double d && d > 0)
            return (int)Math.Round(d);
        if (value is decimal m && m > 0)
            return (int)Math.Round(m);

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        var numericPart = new string(trimmed.Where(ch => char.IsDigit(ch) || ch is '.' or ',').ToArray());
        if (numericPart.Length == 0)
            return null;

        var normalized = numericPart.Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            return null;

        if (trimmed.Contains("khz", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(" k", StringComparison.OrdinalIgnoreCase))
        {
            parsed *= 1000d;
        }

        var hz = (int)Math.Round(parsed);
        return hz > 0 ? hz : null;
    }

    private static bool? TryGetBoolProperty(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null)
            return null;

        var value = property.GetValue(target);
        if (value is null)
            return null;

        if (value is bool boolValue)
            return boolValue;

        return bool.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static double? TryGetDoubleProperty(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null)
            return null;

        var value = property.GetValue(target);
        if (value is null)
            return null;

        if (value is double d)
            return d;
        if (value is float f)
            return f;
        if (value is decimal m)
            return (double)m;
        if (value is int i)
            return i;
        if (value is long l)
            return l;

        return double.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private DaxIQStreamInfo ToDaxIQStreamInfo(DAXIQStream iq)
    {
        // Bug fix 2026-05-18 (issue #39): pre-fix lookup was station-blind
        // (FirstOrDefault on DAXIQChannel alone), so in a multi-station setup
        // where both stations had the same channel assigned, our stream's
        // CenterFreqMHz could be attributed to the other station's pan and
        // leak that frequency into the connected station's UI section. Filter
        // by (DAXIQChannel, ClientHandle) to bind the stream to its owning
        // station's panadapter. Reported 2026-05-18 during multi-station test
        // on a Dallas FLEX-6400 with WX7V + Maestro both holding ch 1.
        var centerFreqMHz = _panadapters.Values
            .FirstOrDefault(p => p.DAXIQChannel == iq.DAXIQChannel && p.ClientHandle == iq.ClientHandle)
            ?.CenterFreqMHz ?? 0.0;

        return new(iq.DAXIQChannel, iq.SampleRate, iq.IsActive, centerFreqMHz, iq.ClientHandle);
    }

    private static string SliceKey(Slice slc)
    {
        var letter = string.IsNullOrWhiteSpace(slc.Letter)
            ? slc.GetHashCode().ToString()
            : slc.Letter!;
        return $"{slc.ClientHandle:X8}:{letter}";
    }

    /// <summary>
    /// Resolve a station name from a client handle by looking it up in
    /// <c>_radio.GuiClients</c>. If the handle is not in the list, returns
    /// the hex form (<c>0x...</c>).
    ///
    /// <paramref name="logFallback"/>: emit a diagnostic warning when the
    /// fallback path is taken. Pass <c>false</c> when calling from a context
    /// where a non-GUI handle is expected (e.g. our own client handle, or
    /// the DAX-the-app session handle that owns a DAX-IQ stream) so benign
    /// not-in-list resolutions don't spam the log. Pass <c>true</c> (default)
    /// when resolving a handle that should belong to a GUI client (pan owner,
    /// slice owner) so genuinely orphaned attribution is surfaced. Dedup is
    /// per handle per session via <c>_fallbackLoggedHandles</c>.
    /// </summary>
    private string ResolveStation(uint clientHandle, bool logFallback = true)
    {
        // Snapshot the lookup under GuiClientsLockObj: FlexLib mutates the backing
        // GuiClients list under this lock (Radio.UpdateGuiClientsList), and this
        // method runs on FlexLib pan/slice event threads, so a lock-free
        // FirstOrDefault here can race a discovery GUI-client refresh and throw
        // "collection was modified" or read a torn entry. Same lock pattern as
        // RefreshGuiClients. Found during issue #45 re-validation review (2026-06-11).
        var radio = _radio;
        GUIClient? client;
        int clientCount;
        if (radio is null)
        {
            client = null;
            clientCount = 0;
        }
        else
        {
            lock (radio.GuiClientsLockObj)
            {
                client = radio.GuiClients?.FirstOrDefault(c => c.ClientHandle == clientHandle);
                clientCount = radio.GuiClients?.Count ?? 0;
            }
        }
        // Bug fix 2026-05-19 (issue #30, AI9T repro): Trim() the station name so
        // a stray trailing space on the Maestro/SmartSDR side (operator-entered
        // in the station-name field) doesn't desync from the discovery-trimmed
        // dropdown selection. Without this, VisibleClientGroups filters out
        // every pan/slice because "Maestro " (event-time) != "Maestro" (dropdown).
        // FlexLibRadioDiscovery.ResolveStations already trims at discovery time;
        // this aligns the runtime path with that.
        var station = client?.Station?.Trim();
        if (!string.IsNullOrEmpty(station))
            return station;

        if (logFallback)
        {
            bool firstSighting;
            lock (_fallbackLoggedHandlesLock)
                firstSighting = _fallbackLoggedHandles.Add(clientHandle);

            if (firstSighting)
            {
                EmitDiag(
                    $"ResolveStation fallback: handle=0x{clientHandle:X} not in GuiClients (n={clientCount}), using hex name.");
            }
        }

        return $"0x{clientHandle:X}";
    }

    // ── GUI clients — per-event diagnostic emission ──────────────────────────

    private void OnGUIClientAdded(GUIClient client)
    {
        EmitDiag(
            $"GUI client added: handle=0x{client.ClientHandle:X}, "
            + $"program={client.Program ?? "(none)"}, station={client.Station ?? "(none)"}.");
    }

    private void OnGUIClientRemoved(GUIClient client)
    {
        EmitDiag($"GUI client removed: handle=0x{client.ClientHandle:X}, station={client.Station ?? "(none)"}.");
    }

    private static string NormalizeSource(string? source)
    {
        var effective = string.IsNullOrWhiteSpace(source) ? "CWSkimmer" : source.Trim();
        return effective.Replace(' ', '_');
    }

    private void PublishNetworkStatus()
    {
        var radio = _radio;
        if (radio is null || !radio.Connected)
        {
            NetworkStatus = NetworkStatusInfo.Empty;
            NetworkStatusChanged?.Invoke(NetworkStatus);
            return;
        }

        var currentPing = radio.NetworkPing;
        if (currentPing >= 0)
            _maxObservedNetworkPing = Math.Max(_maxObservedNetworkPing, currentPing);

        var maxPing = _maxObservedNetworkPing >= 0 ? _maxObservedNetworkPing : currentPing;
        NetworkStatus = new NetworkStatusInfo(
            MapHealth(radio.RemoteNetworkQuality),
            currentPing,
            maxPing);
        NetworkStatusChanged?.Invoke(NetworkStatus);
    }

    private static NetworkHealthLevel MapHealth(NetworkQuality quality)
    {
        return quality switch
        {
            NetworkQuality.EXCELLENT or NetworkQuality.VERYGOOD => NetworkHealthLevel.Excellent,
            NetworkQuality.GOOD or NetworkQuality.FAIR => NetworkHealthLevel.Good,
            NetworkQuality.POOR or NetworkQuality.OFF => NetworkHealthLevel.Poor,
            _ => NetworkHealthLevel.Unknown
        };
    }
}
