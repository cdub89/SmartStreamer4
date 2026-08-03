using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SDRIQStreamer.FlexRadio;

namespace SDRIQStreamer.App;

/// <summary>
/// Backs the SmartDeck window: the radio-level telemetry footer (issue #59
/// phase 1) and the slice control surface (phase 2a). Owns the telemetry
/// subscription lifetime, starting it when the window opens and stopping it
/// when the window closes, so a session that never opens SmartDeck does no
/// coalescing work.
/// </summary>
/// <remarks>
/// The control surface targets an explicitly selected slice rather than the
/// radio's active slice. SmartStreamer runs CW Skimmer and WSJT-X per slice
/// concurrently, so there is no single slice to follow, and a target that
/// moved whenever the operator changed focus in SmartSDR would fight that.
/// </remarks>
public sealed partial class SmartDeckViewModel : ObservableObject, IDisposable
{
    /// <summary>Shown in place of a value that has never been reported.</summary>
    private const string Absent = "---";

    private readonly IRadioConnection _connection;
    private readonly string _controlStation;

    // Injected so tests can run the marshalling synchronously; production uses
    // the Avalonia dispatcher. Same shape as ThrottledStatusEmitter's postToUi.
    private readonly Action<Action> _postToUi;
    private readonly BandMemory _bandMemory;

    private bool _started;

    // Set while pushing radio state into the bound properties, so the setters
    // can tell an operator edit from an echo of the radio's own value and skip
    // writing it straight back.
    private bool _applyingSliceState;

    public SmartDeckViewModel(
        IRadioConnection connection,
        string controlStation,
        Action<Action>? postToUi = null,
        BandMemory? bandMemory = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _controlStation = controlStation ?? string.Empty;
        _postToUi = postToUi ?? (action => Dispatcher.UIThread.Post(action));
        _bandMemory = bandMemory ?? new BandMemory();
    }

    [ObservableProperty]
    private string _powerText = Absent;

    [ObservableProperty]
    private string _swrText = Absent;

    [ObservableProperty]
    private string _tempText = Absent;

    [ObservableProperty]
    private string _voltsText = Absent;

    // ── Slice control surface (phase 2a) ─────────────────────────────────────

    /// <summary>Slices belonging to the control station, in letter order.</summary>
    public ObservableCollection<SliceInfo> Slices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSlice))]
    [NotifyPropertyChangedFor(nameof(FrequencyText))]
    private SliceInfo? _selectedSlice;

    [ObservableProperty]
    private string? _selectedRxAntenna;

    [ObservableProperty]
    private string? _selectedTxAntenna;

    /// <summary>The mode SmartDeck offers that the slice is currently in, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeText))]
    private SliceMode? _currentMode;

    public bool HasSelectedSlice => SelectedSlice is not null;

    /// <summary>
    /// Selected slice frequency for the header readout, grouped the way SmartSDR
    /// groups it, or dashes when no slice is selected.
    /// </summary>
    public string FrequencyText => SelectedSlice is { } slice ? FormatFrequency(slice.FreqMHz) : Absent;

    /// <summary>The selected slice's mode, named as the radio names it.</summary>
    public string ModeText => CurrentMode is { } mode ? mode.ToRadioValue() : string.Empty;

    /// <summary>
    /// Groups a frequency as MHz.kHz.Hz, so 14.05 MHz reads "14.050.000". The
    /// header readout is scanned mid-QSO, and grouped digits are what the
    /// operator already reads off SmartSDR.
    /// </summary>
    internal static string FormatFrequency(double mhz)
    {
        var hz = (long)Math.Round(mhz * 1_000_000d);
        return string.Join(
            ".",
            (hz / 1_000_000).ToString(CultureInfo.InvariantCulture),
            (hz / 1_000 % 1_000).ToString("000", CultureInfo.InvariantCulture),
            (hz % 1_000).ToString("000", CultureInfo.InvariantCulture));
    }

    /// <summary>The modes SmartDeck offers, in button order.</summary>
    private static readonly SliceMode[] OfferedModes =
        [SliceMode.Cw, SliceMode.Usb, SliceMode.Lsb, SliceMode.Am];

    /// <summary>Mode buttons, each tracking whether it is the slice's current mode.</summary>
    public IReadOnlyList<ModeOption> ModeOptions { get; } =
        OfferedModes.Select(mode => new ModeOption(mode, mode.ToRadioValue())).ToArray();

    /// <summary>RX antenna buttons for the selected slice, from the radio's own list.</summary>
    public ObservableCollection<DeckOption> RxAntennaButtons { get; } = [];

    /// <summary>TX antenna buttons for the selected slice, from the radio's own list.</summary>
    public ObservableCollection<DeckOption> TxAntennaButtons { get; } = [];

    [RelayCommand]
    private async Task SetModeAsync(SliceMode mode)
    {
        if (SelectedSlice is not { } slice) return;
        await _connection.SetSliceModeAsync(slice, mode);
        CurrentMode = mode;
    }

    // Antenna buttons drive the same two-way properties the selectors used
    // before the layout pass, so the radio write still happens in one place:
    // the property-changed hooks below.
    [RelayCommand]
    private void SelectRxAntenna(string antenna) => SelectedRxAntenna = antenna;

    [RelayCommand]
    private void SelectTxAntenna(string antenna) => SelectedTxAntenna = antenna;

    /// <summary>Slice buttons for the control station, in letter order.</summary>
    public ObservableCollection<DeckOption> SliceOptions { get; } = [];

    // Selects by letter rather than by SliceInfo so the button carries a plain
    // string like every other group; SelectedSlice stays the single source of
    // truth, including its sticky-selection behaviour in RefreshSlices.
    [RelayCommand]
    private void SelectSlice(string letter)
    {
        if (Slices.FirstOrDefault(slice =>
                string.Equals(slice.Letter, letter, StringComparison.OrdinalIgnoreCase)) is { } found)
            SelectedSlice = found;
    }

    private void ApplySliceButtonState()
    {
        foreach (var option in SliceOptions)
            option.IsCurrent = string.Equals(option.Label, SelectedSlice?.Letter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rebuilds a button list only when the radio's options actually differ.
    /// Slice events fire on every radio update, and clearing a bound collection
    /// on each one would drop keyboard focus mid-press.
    /// </summary>
    private static void SyncOptions(ObservableCollection<DeckOption> buttons, IReadOnlyList<string> options)
    {
        if (buttons.Count == options.Count &&
            buttons.Select(button => button.Label).SequenceEqual(options, StringComparer.Ordinal))
            return;

        buttons.Clear();
        foreach (var option in options)
            buttons.Add(new DeckOption(option));
    }

    private void ApplyAntennaButtonState()
    {
        foreach (var button in RxAntennaButtons)
            button.IsCurrent = string.Equals(button.Label, SelectedRxAntenna, StringComparison.OrdinalIgnoreCase);
        foreach (var button in TxAntennaButtons)
            button.IsCurrent = string.Equals(button.Label, SelectedTxAntenna, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnCurrentModeChanged(SliceMode? value)
    {
        foreach (var option in ModeOptions)
            option.IsCurrent = option.Mode == value;
    }

    partial void OnCurrentBandChanged(string value)
    {
        foreach (var option in BandOptions)
            option.IsCurrent = string.Equals(option.Label, value, StringComparison.OrdinalIgnoreCase);
    }

    // ── Band buttons (phase 2b) ──────────────────────────────────────────────

    /// <summary>Band buttons in grid order, each tracking whether it is the current band.</summary>
    public IReadOnlyList<DeckOption> BandOptions { get; } =
        BandMemory.Bands.Select(band => new DeckOption(band)).ToArray();

    /// <summary>The band the selected slice is currently sitting in, if any.</summary>
    [ObservableProperty]
    private string _currentBand = string.Empty;

    [RelayCommand]
    private async Task SelectBandAsync(string band)
    {
        if (SelectedSlice is not { } slice) return;
        if (_bandMemory.SwitchTo(band, slice.FreqMHz) is not { } targetMhz) return;

        await _connection.SetSliceFrequencyAsync(slice, targetMhz);
        CurrentBand = band;
    }

    // ── RF gain (phase 2c) ───────────────────────────────────────────────────

    /// <summary>
    /// The panadapter behind the selected slice. RF gain is a panadapter
    /// property, not a slice one, so every gain read and write hops through
    /// <see cref="SliceInfo.PanadapterStreamId"/>.
    /// </summary>
    private PanadapterInfo? SelectedPanadapter =>
        SelectedSlice is { } slice
            ? _connection.Panadapters.FirstOrDefault(p => p.StreamId == slice.PanadapterStreamId)
            : null;

    [ObservableProperty]
    private string _rfGainText = Absent;

    /// <summary>
    /// False until the radio has answered with a usable RF gain range, so the
    /// buttons cannot step against a 0-to-0 range.
    /// </summary>
    [ObservableProperty]
    private bool _canAdjustRfGain;

    [RelayCommand]
    private Task RfGainUpAsync() => StepRfGainAsync(direction: 1);

    [RelayCommand]
    private Task RfGainDownAsync() => StepRfGainAsync(direction: -1);

    private async Task StepRfGainAsync(int direction)
    {
        if (SelectedPanadapter is not { } pan) return;

        var next = SteppedRange.Next(pan.RfGain, direction, pan.RfGainLow, pan.RfGainHigh, pan.RfGainStep);
        if (next is not { } gain) return;

        await _connection.SetPanadapterRfGainAsync(pan, gain);

        // Shown immediately rather than waiting for the radio's echo, so a
        // button press does not feel laggy; the echo re-applies the same value.
        RfGainText = FormatRfGain(gain);
    }

    private void ApplyRfGainState()
    {
        var pan = SelectedPanadapter;
        CanAdjustRfGain = pan?.HasRfGainRange ?? false;
        RfGainText = pan is { HasRfGainRange: true } ready ? FormatRfGain(ready.RfGain) : Absent;
    }

    internal static string FormatRfGain(int gain) => $"{gain} dB";

    // ── AGC-T (AGC threshold) ────────────────────────────────────────────────

    // AGC-T is slice-scoped and its 0-100 range is fixed by the protocol rather
    // than radio-reported, so unlike RF gain there is no range request and no
    // "not yet known" state. The step is ours to choose: 5 gives 20 presses
    // end to end, which is coarse enough to be quick and fine enough to tune by.
    private const int AgcThresholdLow = 0;
    private const int AgcThresholdHigh = 100;
    private const int AgcThresholdStep = 5;

    [ObservableProperty]
    private string _agcThresholdText = Absent;

    [RelayCommand]
    private Task AgcThresholdUpAsync() => StepAgcThresholdAsync(direction: 1);

    [RelayCommand]
    private Task AgcThresholdDownAsync() => StepAgcThresholdAsync(direction: -1);

    private async Task StepAgcThresholdAsync(int direction)
    {
        if (SelectedSlice is not { } slice) return;

        var next = SteppedRange.Next(
            slice.AgcThreshold, direction, AgcThresholdLow, AgcThresholdHigh, AgcThresholdStep);
        if (next is not { } threshold) return;

        await _connection.SetSliceAgcThresholdAsync(slice, threshold);

        // Shown immediately rather than waiting for the radio's echo, so a
        // button press does not feel laggy; the echo re-applies the same value.
        AgcThresholdText = FormatAgcThreshold(threshold);
    }

    internal static string FormatAgcThreshold(int threshold) => threshold.ToString(CultureInfo.InvariantCulture);

    private void OnPanadapterListChanged(PanadapterInfo panadapter) => _postToUi(ApplyRfGainState);

    partial void OnSelectedSliceChanged(SliceInfo? value)
    {
        ApplySliceButtonState();
        ApplySliceState(value);
    }

    // No transmit guard on either antenna change: the radio refuses them while
    // transmitting, so guarding here would duplicate a hardware interlock.
    partial void OnSelectedRxAntennaChanged(string? value)
    {
        // Lit state tracks the value however it arrived, including the radio's
        // own echo, so this runs before the guard rather than after it.
        ApplyAntennaButtonState();

        if (_applyingSliceState || value is null) return;
        if (SelectedSlice is { } slice)
            _ = _connection.SetSliceRxAntennaAsync(slice, value);
    }

    partial void OnSelectedTxAntennaChanged(string? value)
    {
        ApplyAntennaButtonState();

        if (_applyingSliceState || value is null) return;
        if (SelectedSlice is { } slice)
            _ = _connection.SetSliceTxAntennaAsync(slice, value);
    }

    private void ApplySliceState(SliceInfo? slice)
    {
        _applyingSliceState = true;
        try
        {
            // Buttons first: the antenna setters below light whichever button
            // matches, so the list has to hold this slice's options by then.
            SyncOptions(RxAntennaButtons, slice?.RxAntennaOptions ?? []);
            SyncOptions(TxAntennaButtons, slice?.TxAntennaOptions ?? []);

            SelectedRxAntenna = string.IsNullOrEmpty(slice?.RxAntenna) ? null : slice.RxAntenna;
            SelectedTxAntenna = string.IsNullOrEmpty(slice?.TxAntenna) ? null : slice.TxAntenna;
            CurrentMode = slice?.OfferedMode;
            CurrentBand = slice is null ? string.Empty : HamBands.Label(slice.FreqMHz);
            AgcThresholdText = slice is null ? Absent : FormatAgcThreshold(slice.AgcThreshold);

            // Switching slices can rebuild the buttons without changing the
            // selected antenna, and the property hooks only fire on a change.
            ApplyAntennaButtonState();
            ApplyRfGainState();
        }
        finally
        {
            _applyingSliceState = false;
        }
    }

    private void RefreshSlices()
    {
        var wanted = _connection.Slices
            .Where(BelongsToControlStation)
            .OrderBy(s => s.Letter, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Bug fix 2026-08-02 (operator-reported, phase 2b live test): with two
        // slices, selecting slice B and pressing a band button snapped the
        // selector back to slice A. Root cause is that the ComboBox writes null
        // back through the two-way SelectedItem binding the moment Slices is
        // cleared, so reading SelectedSlice after the clear saw null and the
        // sticky-selection logic fell through to the first slice. Captured
        // before the clear rather than suppressing the binding write, because
        // the null is the control behaving correctly: the item genuinely is not
        // in the list at that instant.
        var previous = SelectedSlice;

        Slices.Clear();
        foreach (var slice in wanted)
            Slices.Add(slice);

        SyncOptions(SliceOptions, wanted.Select(slice => slice.Letter).ToArray());

        // Selection is sticky: keep the operator's slice across list churn and
        // only re-resolve when it is gone. With Skimmer on one slice and WSJT-X
        // on another, a selection that moved on its own would be worse than
        // useless.
        var keep = previous is { } current
            ? wanted.FirstOrDefault(s => SameSlice(s, current))
            : null;

        SelectedSlice = keep ?? wanted.FirstOrDefault();
        if (SelectedSlice is { } refreshed)
            ApplySliceState(refreshed);
    }

    private bool BelongsToControlStation(SliceInfo slice) =>
        string.IsNullOrWhiteSpace(_controlStation) ||
        string.Equals(slice.ClientStation, _controlStation, StringComparison.OrdinalIgnoreCase);

    private static bool SameSlice(SliceInfo a, SliceInfo b) =>
        string.Equals(a.Letter, b.Letter, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.ClientStation, b.ClientStation, StringComparison.OrdinalIgnoreCase);

    private void OnSliceListChanged(SliceInfo slice) => _postToUi(RefreshSlices);

    /// <summary>Subscribes and starts the radio publishing telemetry. Idempotent.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        _connection.TelemetryChanged += OnTelemetryChanged;
        _connection.ConnectionStateChanged += OnConnectionStateChanged;
        _connection.SliceAdded += OnSliceListChanged;
        _connection.SliceRemoved += OnSliceListChanged;
        _connection.SliceUpdated += OnSliceListChanged;
        _connection.PanadapterAdded += OnPanadapterListChanged;
        _connection.PanadapterRemoved += OnPanadapterListChanged;
        _connection.PanadapterUpdated += OnPanadapterListChanged;
        _connection.StartTelemetry();

        RefreshSlices();

        // Adopt whatever the connection already holds, so a reopened window
        // shows values immediately instead of dashes until the next event.
        Apply(_connection.Telemetry);
    }

    /// <summary>Unsubscribes and stops the radio publishing telemetry. Idempotent.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;

        _connection.TelemetryChanged -= OnTelemetryChanged;
        _connection.ConnectionStateChanged -= OnConnectionStateChanged;
        _connection.SliceAdded -= OnSliceListChanged;
        _connection.SliceRemoved -= OnSliceListChanged;
        _connection.SliceUpdated -= OnSliceListChanged;
        _connection.PanadapterAdded -= OnPanadapterListChanged;
        _connection.PanadapterRemoved -= OnPanadapterListChanged;
        _connection.PanadapterUpdated -= OnPanadapterListChanged;
        _connection.StopTelemetry();
        Apply(RadioTelemetryInfo.Empty);
    }

    // TelemetryChanged fires on the pump thread, not the UI thread.
    private void OnTelemetryChanged(RadioTelemetryInfo telemetry) =>
        _postToUi(() => Apply(telemetry));

    // Bug fix 2026-08-02 (found by the Codex deep audit before this change
    // shipped): with SmartDeck left open across a radio-side drop and
    // reconnect, the footer stayed on dashes until the window was closed and
    // reopened. Root cause is that a disconnect makes FlexLibRadioConnection
    // call its own StopTelemetry(), which this ViewModel has no way to observe,
    // so _started stayed true and nothing re-armed the subscription. Re-arming
    // from the connection-state event rather than tracking a "wanted" flag
    // inside the connection keeps the desired-state logic with the window whose
    // lifetime defines it.
    private void OnConnectionStateChanged(bool connected)
    {
        // Bug fix 2026-08-03 (found by the Codex deep audit of the layout
        // pass): with SmartDeck left open across a disconnect, the deck kept
        // the last slice selected, its frequency in the header, and its band,
        // mode and antenna buttons lit and enabled, so presses landed on a
        // stale SliceInfo. Root cause is that Disconnect() clears its own slice
        // map directly and raises only ConnectionStateChanged(false), having
        // already unsubscribed the per-slice handler, so no SliceRemoved ever
        // reaches this ViewModel. Refreshing from the now-empty connection
        // rather than clearing by hand keeps one code path deciding what the
        // deck shows. The telemetry footer needs no equivalent: StopTelemetry
        // already publishes an empty snapshot, so it falls back to dashes.
        if (!connected)
        {
            _postToUi(RefreshSlices);
            return;
        }

        _connection.StartTelemetry();
    }

    private void Apply(RadioTelemetryInfo telemetry)
    {
        PowerText = FormatPower(telemetry.PowerWatts);
        SwrText   = FormatSwr(telemetry.Swr, telemetry.PowerWatts);
        TempText  = Format(telemetry.PaTempCelsius, "0");
        VoltsText = Format(telemetry.VoltsDc, "0.0");
    }

    // Above this, the radio is putting out RF. The SWR meter floors at 1.0 and
    // the forward-power meter floors at 0 dBm (0.001 W), while the lowest real
    // transmit power is 1 W (30 dBm), so this threshold sits with an order of
    // magnitude of clearance on both sides.
    private const double TransmitPowerThresholdWatts = 0.01;

    /// <summary>
    /// SWR, shown only while the radio is actually transmitting.
    /// </summary>
    /// <remarks>
    /// Bug fix 2026-08-02, reported by the operator during the phase-1 live
    /// test: at rest the footer showed SWR 1.0, which reads as a real 1:1
    /// match. Root cause is that the SWR meter floors at 1.0 rather than
    /// reporting nothing, so its idle floor was being formatted as a
    /// measurement. Gated on forward power rather than on Radio.Mox because
    /// power is already in the snapshot and "no RF going out" is the condition
    /// that actually makes SWR meaningless; MOX would need new plumbing to
    /// reach the same answer. Dashes rather than 0 because SWR is undefined
    /// below 1.0, so a displayed 0 would be a value that cannot physically
    /// occur.
    /// </remarks>
    internal static string FormatSwr(double? swr, double? powerWatts) =>
        powerWatts is { } watts && watts >= TransmitPowerThresholdWatts
            ? Format(swr, "0.0")
            : Absent;

    /// <summary>
    /// Whole watts at 10 W and above, one decimal below it. A 93 W reading does
    /// not need a tenth of a watt, but a QRP operator running 5 W does.
    /// </summary>
    internal static string FormatPower(double? watts) =>
        watts is { } value
            ? value.ToString(value >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture)
            : Absent;

    /// <summary>
    /// Absent renders as dashes; a real zero renders as zero. Forward power
    /// reports a genuine 0 W on receive, which is a useful TX-idle signal and
    /// must not look like "no telemetry".
    /// </summary>
    internal static string Format(double? value, string format) =>
        value is { } present
            ? present.ToString(format, CultureInfo.InvariantCulture)
            : Absent;

    public void Dispose() => Stop();
}
