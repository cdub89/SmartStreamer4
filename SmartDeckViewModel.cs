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

    // Writes a [STREAMER] line to streamer-status.log. Injected rather than
    // reached through IRadioConnection: what a band button restored is an
    // app-level fact, not something the radio told us.
    private readonly Action<string> _logStatus;

    // Injected so tests run a band restore without real time passing. Production
    // uses Task.Delay; see BandWriteSettle for why the delay exists at all.
    private readonly Func<TimeSpan, Task> _settle;

    private bool _started;

    // Set while pushing radio state into the bound properties, so the setters
    // can tell an operator edit from an echo of the radio's own value and skip
    // writing it straight back.
    private bool _applyingSliceState;

    public SmartDeckViewModel(
        IRadioConnection connection,
        string controlStation,
        Action<Action>? postToUi = null,
        BandMemory? bandMemory = null,
        Action<string>? logStatus = null,
        Func<TimeSpan, Task>? settle = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _controlStation = controlStation ?? string.Empty;
        _postToUi = postToUi ?? (action => Dispatcher.UIThread.Post(action));
        _bandMemory = bandMemory ?? new BandMemory();
        _logStatus = logStatus ?? (_ => { });
        _settle = settle ?? Task.Delay;
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
    [NotifyPropertyChangedFor(nameof(ModeText))]
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

    /// <summary>
    /// The selected slice's mode, named as the radio names it. A slice sitting
    /// in a mode SmartDeck does not offer (DIGU under WSJT-X, RTTY) falls back
    /// to the radio's own string rather than reading blank: the header readout
    /// replaced the mode buttons, so a blank here would leave the operator with
    /// no way to see the mode and nothing to click to change it.
    /// </summary>
    public string ModeText => CurrentMode is { } mode
        ? mode.ToRadioValue()
        : SelectedSlice?.Mode.Trim() ?? string.Empty;

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

    /// <summary>
    /// The modes SmartDeck offers, in the order the header readout cycles them
    /// (operator-specified, issue #59 live feedback). Four buttons and their
    /// group heading collapsed into this one clickable readout to give the
    /// window back a row, so the order here is the whole mode surface.
    /// </summary>
    private static readonly SliceMode[] CycleOrder =
        [SliceMode.Cw, SliceMode.Lsb, SliceMode.Usb, SliceMode.Am];

    /// <summary>RX antenna buttons for the selected slice, from the radio's own list.</summary>
    public ObservableCollection<DeckOption> RxAntennaButtons { get; } = [];

    /// <summary>TX antenna buttons for the selected slice, from the radio's own list.</summary>
    public ObservableCollection<DeckOption> TxAntennaButtons { get; } = [];

    /// <summary>
    /// The mode one step along the cycle from <paramref name="current"/>,
    /// wrapping past the last entry back to the first.
    /// </summary>
    internal static SliceMode NextMode(SliceMode? current)
    {
        // A mode SmartDeck does not offer indexes as -1, and -1 + 1 lands on
        // the first entry: a slice sitting in DIGU enters the cycle at CW
        // rather than being a dead end the readout cannot move off.
        var index = current is { } mode ? Array.IndexOf(CycleOrder, mode) : -1;
        return CycleOrder[(index + 1) % CycleOrder.Length];
    }

    [RelayCommand]
    private async Task CycleModeAsync()
    {
        if (SelectedSlice is not { } slice) return;
        var next = NextMode(CurrentMode);
        await _connection.SetSliceModeAsync(slice, next);
        CurrentMode = next;
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

    /// <summary>
    /// Prefix of the transverter ports the radio offers on every slice. Covers
    /// every spelling in play (<c>XVTA</c> and <c>XVTB</c> per
    /// <c>APD.cs:29-31</c>, and the older <c>XVTR</c>) deliberately: the rule is
    /// "no transverter ports", not a list of port names to keep in step with
    /// FlexLib.
    /// </summary>
    private const string TransverterPortPrefix = "XVT";

    /// <summary>
    /// Drops transverter ports from a radio-reported antenna list. The radio
    /// offers XVTA and XVTB on every slice and the operator base does not run
    /// transverters, so they cost a button each in a window whose height is the
    /// scarce resource (issue #64, operator-reported).
    /// </summary>
    /// <remarks>
    /// A port the radio currently holds survives the filter: hiding the
    /// selected antenna would leave the group with no lit button and no way to
    /// move off the transverter from here. The list is re-derived on every
    /// slice update, so the port disappears again once the radio moves off it.
    /// </remarks>
    private static IReadOnlyList<string> WithoutUnusedTransverterPorts(
        IReadOnlyList<string> options,
        string? selected) =>
        options.Where(option =>
                !option.StartsWith(TransverterPortPrefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option, selected, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private void ApplyAntennaButtonState()
    {
        foreach (var button in RxAntennaButtons)
            button.IsCurrent = string.Equals(button.Label, SelectedRxAntenna, StringComparison.OrdinalIgnoreCase);
        foreach (var button in TxAntennaButtons)
            button.IsCurrent = string.Equals(button.Label, SelectedTxAntenna, StringComparison.OrdinalIgnoreCase);
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

    // Bug fix 2026-08-03 (operator-reported: RX/TX antenna buttons bounced
    // between values on some band changes before settling correct). Symptom was
    // cosmetic but the cause was not. The five writes used to be issued inside
    // one millisecond, far faster than the radio's ~175-250 ms status round
    // trip. The radio answers each command with a correct snapshot of itself at
    // that instant, so the frequency command's reply legitimately still carried
    // the old antennas; arriving after we had optimistically cached the new
    // ones, it overwrote them until the later replies caught up. A live capture
    // showed the same race silently corrupting band memory: a status reset the
    // cached AGC-T to a pre-write value, and the next band press captured that
    // stale number as the departing state, persisting a value the radio never
    // held. Spacing the writes so each reply lands before the next command
    // makes the status stream monotonic and fixes both. Chosen over suppressing
    // the repaint, which would have hidden the corruption rather than fixed it.
    private static readonly TimeSpan BandWriteSettle = TimeSpan.FromMilliseconds(250);

    /// <remarks>
    /// Writes are ordered frequency, mode, antennas, AGC-T. Frequency leads so
    /// the band change lands immediately and the operator is on the new band
    /// while the rest settles behind it, and the antennas sit late because the
    /// radio refuses them while transmitting: a refusal there should not strand
    /// the rest of the restore. Each field is written only when the band
    /// actually remembers one, so a band's first visit tunes it and leaves
    /// everything else exactly as the radio has it.
    /// </remarks>
    [RelayCommand]
    private async Task SelectBandAsync(string band)
    {
        if (SelectedSlice is not { } slice) return;

        var departing = new BandState(
            slice.FreqMHz,
            slice.OfferedMode,
            string.IsNullOrEmpty(slice.RxAntenna) ? null : slice.RxAntenna,
            string.IsNullOrEmpty(slice.TxAntenna) ? null : slice.TxAntenna,
            slice.AgcThreshold);

        if (_bandMemory.SwitchTo(band, departing) is not { } target) return;

        await _connection.SetSliceFrequencyAsync(slice, target.FreqMhz);

        if (target.Mode is { } mode)
        {
            await _settle(BandWriteSettle);
            await _connection.SetSliceModeAsync(slice, mode);
        }

        if (target.RxAntenna is { } rxAntenna)
        {
            await _settle(BandWriteSettle);
            await _connection.SetSliceRxAntennaAsync(slice, rxAntenna);
        }

        if (target.TxAntenna is { } txAntenna)
        {
            await _settle(BandWriteSettle);
            await _connection.SetSliceTxAntennaAsync(slice, txAntenna);
        }

        if (target.AgcThreshold is { } agcThreshold)
        {
            await _settle(BandWriteSettle);
            await _connection.SetSliceAgcThresholdAsync(slice, agcThreshold);
        }

        // One line naming what the press did, rather than a line per write.
        // The frequency and mode writes are not logged individually on purpose:
        // SetSliceFrequencyAsync is also the CW Skimmer spot-click path, which
        // fires on every spot and would swamp the log (issue #58). Listing only
        // what was actually restored means a band's first visit reads as bare
        // frequency, which is itself the useful signal.
        List<string> restored = [$"{FormatFrequency(target.FreqMhz)} MHz"];
        if (target.Mode is { } restoredMode)
            restored.Add($"mode {restoredMode.ToRadioValue()}");
        if (target.RxAntenna is { } restoredRx)
            restored.Add($"RX {restoredRx}");
        if (target.TxAntenna is { } restoredTx)
            restored.Add($"TX {restoredTx}");
        if (target.AgcThreshold is { } restoredAgc)
            restored.Add($"AGC-T {restoredAgc}");

        _logStatus($"Band {band}: {string.Join(", ", restored)}");

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
    private Task RfGainUpAsync() => StepRfGainAsync(steps: 1);

    [RelayCommand]
    private Task RfGainDownAsync() => StepRfGainAsync(steps: -1);

    /// <summary>
    /// Wheel accelerator for the buttons above (issue #65). Written per notch
    /// rather than gathered like frequency and TX power: the radio-reported
    /// range is about ten steps end to end, so a spin clamps almost immediately
    /// and there is nothing downstream of the write.
    /// </summary>
    public void NudgeRfGain(int notches)
    {
        if (!_started || notches == 0 || !CanAdjustRfGain) return;
        _ = StepRfGainAsync(notches);
    }

    // Steps, not a direction: one wheel event can carry several notches, and
    // SteppedRange multiplies the step by whatever it is given, clamping the
    // result the same way either way.
    //
    // The buttons and the wheel share this one path deliberately. They were
    // briefly separate, the buttons stepping from the radio's last echo and the
    // wheel from its local target, which meant a click straight after a spin
    // stepped backwards from where the readout already was.
    private Task StepRfGainAsync(int steps)
    {
        if (SelectedPanadapter is not { } pan) return Task.CompletedTask;

        var from = _wheelTargetRfGain ?? pan.RfGain;
        if (SteppedRange.Next(from, steps, pan.RfGainLow, pan.RfGainHigh, pan.RfGainStep) is not { } gain)
            return Task.CompletedTask;

        // Held locally until the radio catches up, so the next step computes
        // from where this one left off rather than from an echo still in
        // flight. Shown immediately for the same reason a button press is: the
        // number is what the operator is steering by.
        _wheelTargetRfGain = gain;
        RfGainText = FormatRfGain(gain);
        return _connection.SetPanadapterRfGainAsync(pan, gain);
    }

    private void ApplyRfGainState()
    {
        var pan = SelectedPanadapter;
        CanAdjustRfGain = pan?.HasRfGainRange ?? false;

        if (pan is not { HasRfGainRange: true } ready)
        {
            _wheelTargetRfGain = null;
            RfGainText = Absent;
            return;
        }

        // The wheel's local target stands down once the radio has caught up to
        // it, so the next gesture starts from the radio again rather than from
        // a number carried over from the last one. Until it does, the readout
        // shows where the wheel is steering rather than the radio's last echo,
        // which mid-spin is always a notch or more behind and would otherwise
        // make the number jump backwards under the operator's finger.
        if (_wheelTargetRfGain == ready.RfGain)
            _wheelTargetRfGain = null;

        RfGainText = FormatRfGain(_wheelTargetRfGain ?? ready.RfGain);
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
    private Task AgcThresholdUpAsync() => StepAgcThresholdAsync(steps: 1);

    [RelayCommand]
    private Task AgcThresholdDownAsync() => StepAgcThresholdAsync(steps: -1);

    /// <summary>Wheel accelerator for the buttons above; see <see cref="NudgeRfGain"/>.</summary>
    public void NudgeAgcThreshold(int notches)
    {
        if (!_started || notches == 0) return;
        _ = StepAgcThresholdAsync(notches);
    }

    // Shared by the buttons and the wheel; see StepRfGainAsync for why.
    private Task StepAgcThresholdAsync(int steps)
    {
        if (SelectedSlice is not { } slice) return Task.CompletedTask;

        var from = _wheelTargetAgcThreshold ?? slice.AgcThreshold;
        if (SteppedRange.Next(from, steps, AgcThresholdLow, AgcThresholdHigh, AgcThresholdStep)
            is not { } threshold)
        {
            return Task.CompletedTask;
        }

        _wheelTargetAgcThreshold = threshold;
        AgcThresholdText = FormatAgcThreshold(threshold);
        return _connection.SetSliceAgcThresholdAsync(slice, threshold);
    }

    internal static string FormatAgcThreshold(int threshold) => threshold.ToString(CultureInfo.InvariantCulture);

    // ── TX power and the QRP toggle (issue #64) ──────────────────────────────

    // The operator runs at whatever power the band and the amplifier want, then
    // drops to QRP for a contact that qualifies and comes back up afterwards.
    // Doing that in SmartSDR means finding the slider and remembering the number
    // to return to, which is the whole reason this button exists.

    /// <summary>The power a QRP contact runs at, in watts.</summary>
    private const int QrpWatts = 5;

    /// <summary>
    /// The power the radio held when QRP was engaged, restored when it is
    /// released. Held in memory only, and deliberately not persisted: across a
    /// restart the radio's own power is the only truth, and a saved number
    /// would be a guess about a value another client may have changed since.
    /// </summary>
    private int? _powerBeforeQrp;

    /// <summary>The radio's transmit power setting, or dashes until it reports one.</summary>
    [ObservableProperty]
    private string _txPowerText = Absent;

    /// <summary>
    /// True while the deck is holding a power to return to, which is also the
    /// only state in which pressing the button restores anything. The radio is
    /// necessarily at <see cref="QrpWatts"/> whenever this is true, because
    /// <see cref="ApplyRfPower"/> stands the toggle down the moment the radio
    /// reports anything else.
    /// </summary>
    [ObservableProperty]
    private bool _isQrp;

    /// <summary>False until the radio has reported a power, so the toggle cannot save an unknown one.</summary>
    [ObservableProperty]
    private bool _canToggleQrp;

    [RelayCommand]
    private async Task ToggleQrpAsync()
    {
        if (_powerBeforeQrp is { } restore)
        {
            // Cleared before the write, so the radio's echo of the restored
            // power is not read as the operator changing power elsewhere.
            _powerBeforeQrp = null;
            IsQrp = false;
            _logStatus($"QRP released: restoring {restore} W.");
            await _connection.SetRfPowerAsync(restore);
            TxPowerText = FormatTxPower(restore);
            return;
        }

        if (_connection.RfPowerWatts is not { } current)
        {
            _logStatus("QRP press ignored: the radio has not reported a power.");
            return;
        }

        _powerBeforeQrp = current;
        IsQrp = true;
        _logStatus($"QRP engaged: saved {current} W, setting {QrpWatts} W.");
        await _connection.SetRfPowerAsync(QrpWatts);

        // Shown immediately rather than waiting for the radio's echo, so a
        // button press does not feel laggy; the echo re-applies the same value.
        TxPowerText = FormatTxPower(QrpWatts);
    }

    // RfPowerChanged can fire on a FlexLib event thread.
    private void OnRfPowerChanged(int? watts) => _postToUi(() => ApplyRfPower(watts));

    private void ApplyRfPower(int? watts)
    {
        CanToggleQrp = watts is not null;
        TxPowerText = watts is { } value ? FormatTxPower(value) : Absent;

        // The radio wins. Anything other than QRP while we are holding a power
        // to return to means the operator changed power somewhere else, in
        // SmartSDR or on another client, so the saved value is stale. Dropping
        // it costs one press to re-engage; keeping it would silently overwrite
        // their choice the next time the button was released.
        //
        // This deliberately includes SmartDeck's own TX power wheel (issue #65).
        // Wheeling off 5 W is a manual power change like any other and loses the
        // cached pre-QRP power, confirmed by the operator 2026-08-05 when the
        // wheel was added. Do not special-case the wheel to preserve it.
        //
        // Accepted limitation (Codex deep audit, 2026-08-04): another client
        // deliberately setting 5 W while QRP is engaged is indistinguishable
        // from the echo of our own write, so the toggle keeps its saved power
        // and releasing it climbs back out. That is the same thing the operator
        // gets from a QRP contact either way, and telling the two apart would
        // mean tracking write provenance for no change in outcome.
        if (IsQrp && watts != QrpWatts)
        {
            // Worth a line: the button going dark on its own is otherwise
            // unexplained from the operator's side.
            _logStatus($"QRP stood down: radio reported {watts?.ToString() ?? "(absent)"} W, "
                       + $"discarding the saved {_powerBeforeQrp?.ToString() ?? "(none)"} W.");
            _powerBeforeQrp = null;
            IsQrp = false;
        }
    }

    internal static string FormatTxPower(int watts) => $"{watts} W";

    // ── Mouse wheel over the readouts (issue #65) ────────────────────────────

    // The wheel is an accelerator for controls that already exist, with one
    // exception: TX power had a readout and the QRP button but no stepper, so
    // the wheel is its only fine adjustment. That is deliberate (operator
    // request, 2026-08-05): QRP operators work 5 W down to 1 W and wanted single
    // watts without spending a row on a stepper the rest of the time.
    //
    // Frequency and TX power gather their notches before writing; RF gain and
    // AGC-T do not. The split is about range and blast radius, not consistency:
    // RF gain reaches its rails in about ten notches and AGC-T in twenty, and
    // neither write goes anywhere but the radio. A frequency write is answered
    // by CwSkimmerSyncTracker with SKIMMER/LO_FREQ plus SKIMMER/QSY, so an
    // unthrottled spin would put dozens of telnet lines into Skimmer in a
    // second, and TX power spans 100 single-watt notches end to end.

    /// <summary>How long wheel notches are gathered before the radio write.</summary>
    private static readonly TimeSpan WheelWriteWindow = TimeSpan.FromMilliseconds(75);

    /// <summary>
    /// Tune step used when the radio has not reported one for the slice.
    /// <see cref="SliceInfo.TuneStepHz"/> is resolved reflectively and lands at
    /// zero if this FlexLib build exposes neither property, the same case
    /// MainWindowViewModel.ResolveClickSnapStepHz covers with the same 50 Hz.
    /// </summary>
    private const int FallbackTuneStepHz = 50;

    // FlexLib clamps RF power to 0-100 in its own setter (Radio.cs:8377-8379),
    // and on a 100 W radio one unit is one watt. Sub-watt output is not
    // expressible through this API at all: below 1 W the only value is 0.
    private const int TxPowerLow = 0;
    private const int TxPowerHigh = 100;
    private const int TxPowerStep = 1;

    // The value the wheel is steering towards, held locally while a write is in
    // flight. Every control needs one: the radio's echo of notch N has not
    // landed when notch N+1 arrives, so computing from the radio-reported value
    // would make consecutive notches all compute the same target and a fast
    // spin would move one step (Codex deep audit, 2026-08-05, which caught this
    // on RF gain and AGC-T after they were first written without a target).
    // Null between gestures, so the next notch re-seeds from what the radio
    // actually holds.
    private double? _wheelTargetFreqMHz;
    private int? _wheelTargetWatts;
    private int? _wheelTargetRfGain;
    private int? _wheelTargetAgcThreshold;

    // Which slice the pending frequency target belongs to. Without it, wheeling
    // slice A and then selecting slice B inside the gather window writes A's
    // target frequency to B (Codex deep audit, 2026-08-05).
    private string? _wheelTargetSliceLetter;

    private bool _freqWriteScheduled;
    private bool _powerWriteScheduled;

    /// <summary>
    /// Steps the selected slice by <paramref name="notches"/> of the radio's own
    /// tune step. Called from the window's wheel handler; hover is enough, so
    /// this can arrive with no click having selected anything.
    /// </summary>
    public void NudgeFrequency(int notches)
    {
        if (!_started || notches == 0 || SelectedSlice is not { } slice) return;

        // A target belonging to a different slice is another slice's gesture,
        // not this one's starting point.
        var from = _wheelTargetSliceLetter == slice.Letter && _wheelTargetFreqMHz is { } pending
            ? pending
            : slice.FreqMHz;

        var stepHz = slice.TuneStepHz > 0 ? slice.TuneStepHz : FallbackTuneStepHz;
        if (NextFrequencyMHz(from, notches, stepHz) is not { } target) return;

        _wheelTargetFreqMHz = target;
        _wheelTargetSliceLetter = slice.Letter;
        ScheduleFrequencyWrite();
    }

    private void ScheduleFrequencyWrite()
    {
        if (_freqWriteScheduled) return;
        _freqWriteScheduled = true;
        _ = FlushFrequencyAsync();
    }

    private async Task FlushFrequencyAsync()
    {
        await _settle(WheelWriteWindow);
        _freqWriteScheduled = false;

        if (_wheelTargetFreqMHz is not { } target) return;

        // Dropped rather than written if the slice the gesture belonged to is
        // gone or is no longer the selected one. Left set it would seed the next
        // gesture from another slice's frequency; written blindly it would
        // retune whichever slice happens to be selected now to a frequency the
        // operator dialled for a different one.
        if (SelectedSlice is not { } slice || slice.Letter != _wheelTargetSliceLetter)
        {
            _wheelTargetFreqMHz = null;
            _wheelTargetSliceLetter = null;
            return;
        }

        await _connection.SetSliceFrequencyAsync(slice, target);

        // Cleared only if no further notch arrived while the write was in
        // flight; if one did, it already scheduled the next flush and owns the
        // target. Unlike the other readouts nothing is echoed locally here:
        // FrequencyText is computed from SelectedSlice, so the header follows
        // the radio's own report, exactly as a click-tune does today.
        if (_wheelTargetFreqMHz == target)
        {
            _wheelTargetFreqMHz = null;
            _wheelTargetSliceLetter = null;
        }
    }

    /// <summary>
    /// Steps transmit power by <paramref name="notches"/> watts, clamped to the
    /// radio's 0-100 range.
    /// </summary>
    public void NudgeTxPower(int notches)
    {
        if (!_started || notches == 0 || !CanToggleQrp) return;

        var from = _wheelTargetWatts ?? _connection.RfPowerWatts;
        if (from is not { } current) return;
        if (SteppedRange.Next(current, notches, TxPowerLow, TxPowerHigh, TxPowerStep) is not { } target) return;

        _wheelTargetWatts = target;

        // Shown immediately rather than waiting for the radio's echo, matching
        // every other control on the deck.
        TxPowerText = FormatTxPower(target);
        ScheduleTxPowerWrite();
    }

    private void ScheduleTxPowerWrite()
    {
        if (_powerWriteScheduled) return;
        _powerWriteScheduled = true;
        _ = FlushTxPowerAsync();
    }

    private async Task FlushTxPowerAsync()
    {
        await _settle(WheelWriteWindow);
        _powerWriteScheduled = false;

        if (_wheelTargetWatts is not { } target) return;

        await _connection.SetRfPowerAsync(target);

        if (_wheelTargetWatts == target)
            _wheelTargetWatts = null;
    }

    /// <summary>
    /// The frequency <paramref name="notches"/> tune steps from
    /// <paramref name="currentMHz"/>, or <c>null</c> when the step is unusable
    /// or the result would leave the spectrum.
    /// </summary>
    /// <remarks>
    /// Arithmetic runs in whole Hz rather than MHz: a double accumulating
    /// fractional MHz drifts off the tune grid over a long spin, and the header
    /// groups down to single Hz, so the drift would be visible.
    /// </remarks>
    internal static double? NextFrequencyMHz(double currentMHz, int notches, int stepHz)
    {
        if (stepHz <= 0 || notches == 0) return null;

        var currentHz = (long)Math.Round(currentMHz * 1_000_000d);
        var nextHz = currentHz + ((long)notches * stepHz);

        // No radio-reported tuning range to clamp against, so the only guard is
        // against wheeling off the bottom; the radio refuses anything else it
        // cannot tune.
        if (nextHz <= 0) return null;

        return nextHz / 1_000_000d;
    }

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
            SyncOptions(RxAntennaButtons, WithoutUnusedTransverterPorts(slice?.RxAntennaOptions ?? [], slice?.RxAntenna));
            SyncOptions(TxAntennaButtons, WithoutUnusedTransverterPorts(slice?.TxAntennaOptions ?? [], slice?.TxAntenna));

            SelectedRxAntenna = string.IsNullOrEmpty(slice?.RxAntenna) ? null : slice.RxAntenna;
            SelectedTxAntenna = string.IsNullOrEmpty(slice?.TxAntenna) ? null : slice.TxAntenna;
            CurrentMode = slice?.OfferedMode;
            CurrentBand = slice is null ? string.Empty : HamBands.Label(slice.FreqMHz);
            // Same wheel-target rule as RF gain: the local target stands down
            // once the radio has caught up, and until then the readout shows
            // where the wheel is steering rather than an echo a notch behind.
            if (slice is null || _wheelTargetAgcThreshold == slice.AgcThreshold)
                _wheelTargetAgcThreshold = null;

            AgcThresholdText = slice is null
                ? Absent
                : FormatAgcThreshold(_wheelTargetAgcThreshold ?? slice.AgcThreshold);

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
        _connection.RfPowerChanged += OnRfPowerChanged;
        _connection.StartTelemetry();

        RefreshSlices();

        // Adopt whatever the connection already holds, so a reopened window
        // shows values immediately instead of dashes until the next event.
        Apply(_connection.Telemetry);
        ApplyRfPower(_connection.RfPowerWatts);
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
        _connection.RfPowerChanged -= OnRfPowerChanged;
        _connection.StopTelemetry();
        Apply(RadioTelemetryInfo.Empty);

        // Closing the window drops the power to return to along with everything
        // else. The radio keeps whatever power it holds; nothing is restored
        // behind the operator's back on reopen.
        ApplyRfPower(null);

        // Same for anything the wheel was still steering towards. A flush
        // already in flight finds these null and writes nothing, and a reopened
        // window re-seeds from whatever the radio actually holds by then.
        _wheelTargetFreqMHz = null;
        _wheelTargetSliceLetter = null;
        _wheelTargetWatts = null;
        _wheelTargetRfGain = null;
        _wheelTargetAgcThreshold = null;
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
        // Bug fix 2026-08-04 (found by the Codex deep audit of the QRP toggle
        // before it shipped): with SmartDeck left open across a radio-side drop,
        // QRP stayed lit holding a power from the previous session, and a press
        // after the reconnect wrote that stale power to the new one. Root cause
        // is that only the operator Disconnect() path published a power change;
        // a FlexLib-side drop raises ConnectionStateChanged alone. Re-deriving
        // power from the connection here, the way slices already are, means the
        // deck cannot be left holding state the connection no longer backs.
        if (!connected)
        {
            _postToUi(() =>
            {
                RefreshSlices();
                ApplyRfPower(_connection.RfPowerWatts);
            });
            return;
        }

        _postToUi(() => ApplyRfPower(_connection.RfPowerWatts));
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
