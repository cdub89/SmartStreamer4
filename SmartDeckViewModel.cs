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

    /// <summary>Reflected power, watts (issue #69).</summary>
    [ObservableProperty]
    private string _refPowerText = Absent;

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
    [NotifyPropertyChangedFor(nameof(RitText))]
    [NotifyPropertyChangedFor(nameof(XitText))]
    [NotifyPropertyChangedFor(nameof(IsRitEnabled))]
    [NotifyPropertyChangedFor(nameof(IsXitEnabled))]
    [NotifyPropertyChangedFor(nameof(IsApfOn))]
    [NotifyPropertyChangedFor(nameof(IsNrOn))]
    [NotifyPropertyChangedFor(nameof(IsNbOn))]
    [NotifyPropertyChangedFor(nameof(IsDiversityOn))]
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

    // ── RIT and XIT (issue #73) ───────────────────────────────────

    // The control this feature exists for: a Maestro and a FlexControl have a
    // RIT knob and SmartStreamer had no equivalent anywhere, so an operator
    // running headless or on a remote seat simply could not set it.
    //
    // Both are shown at once rather than one behind a toggle, so XIT engaged
    // from the Maestro can never be hidden while the operator looks at RIT.
    // Click a readout to engage or release it; wheel over it to move the offset.
    // The wheel deliberately does not engage as a side effect: one gesture each.

    /// <summary>Hz per wheel detent, matching the Maestro and FlexControl.</summary>
    /// <remarks>
    /// Deliberately not the slice's own TuneStepHz, which the frequency wheel
    /// uses: a slice sitting on a 500 Hz SSB step would move RIT 500 Hz a click.
    /// Fixed, and not configurable, because matching the hardware is the point.
    /// </remarks>
    internal const int RitXitStepHz = 10;

    public string RitText => FormatOffset(SelectedSlice?.RitOffsetHz);
    public string XitText => FormatOffset(SelectedSlice?.XitOffsetHz);

    /// <summary>True while RIT is engaged on the selected slice, which lights the readout.</summary>
    public bool IsRitEnabled => SelectedSlice?.RitEnabled ?? false;

    /// <summary>True while XIT is engaged on the selected slice.</summary>
    public bool IsXitEnabled => SelectedSlice?.XitEnabled ?? false;

    /// <summary>
    /// A signed offset in whole Hz, or dashes with no slice selected. Signed
    /// always, including at zero, so the readout reads as an offset rather than
    /// as a frequency.
    /// </summary>
    internal static string FormatOffset(double? offsetHz) =>
        offsetHz is { } hz
            ? ((int)Math.Round(hz)).ToString("+0;-0;0", CultureInfo.InvariantCulture)
            : Absent;

    [RelayCommand]
    private async Task ToggleRitAsync()
    {
        if (SelectedSlice is not { } slice) return;
        await _connection.SetSliceRitEnabledAsync(slice, !IsRitEnabled);
    }

    [RelayCommand]
    private async Task ToggleXitAsync()
    {
        if (SelectedSlice is not { } slice) return;
        await _connection.SetSliceXitEnabledAsync(slice, !IsXitEnabled);
    }

    // ── Receive-chain toggles (issue #76) ────────────────────────────────────
    //
    // Four states and no levels. FlexLib carries APFLevel, NRLevel and NBLevel,
    // but SmartSDR removed those sliders in 4.1/4.2 and the radio adapts them
    // itself (operator, 2026-09-19), so the deck offers on/off only and the
    // wheel does nothing here. Do not add level steppers without checking that
    // SmartSDR has started exposing them again.

    /// <summary>True while the audio peaking filter is on, which lights the button.</summary>
    public bool IsApfOn => SelectedSlice?.ApfOn ?? false;

    /// <summary>True while noise reduction is on.</summary>
    public bool IsNrOn => SelectedSlice?.NrOn ?? false;

    /// <summary>True while the noise blanker is on.</summary>
    public bool IsNbOn => SelectedSlice?.NbOn ?? false;

    /// <summary>True while the slice is running diversity reception.</summary>
    public bool IsDiversityOn => SelectedSlice?.DiversityOn ?? false;

    /// <summary>
    /// Whether to offer the DIV button at all. False on the 6300, 6400, 6400M
    /// and 6500, where the button would sit dead: the radio reports its own
    /// capability, so a model list here would only go stale.
    /// </summary>
    public bool IsDiversityAvailable => _connection.DiversityIsAllowed;

    [RelayCommand]
    private async Task ToggleApfAsync()
    {
        if (SelectedSlice is not { } slice) return;
        await _connection.SetSliceApfEnabledAsync(slice, !IsApfOn);
    }

    [RelayCommand]
    private async Task ToggleNrAsync()
    {
        if (SelectedSlice is not { } slice) return;
        await _connection.SetSliceNrEnabledAsync(slice, !IsNrOn);
    }

    [RelayCommand]
    private async Task ToggleNbAsync()
    {
        if (SelectedSlice is not { } slice) return;
        await _connection.SetSliceNbEnabledAsync(slice, !IsNbOn);
    }

    [RelayCommand]
    private async Task ToggleDiversityAsync()
    {
        if (SelectedSlice is not { } slice) return;
        await _connection.SetSliceDiversityEnabledAsync(slice, !IsDiversityOn);
    }

    /// <summary>
    /// Steps the selected slice's RIT offset by <paramref name="notches"/>
    /// detents. Does not engage RIT: see the note above.
    /// </summary>
    public void NudgeRit(int notches) => NudgeOffset(notches, xit: false);

    /// <summary>Steps the selected slice's XIT offset. See <see cref="NudgeRit"/>.</summary>
    public void NudgeXit(int notches) => NudgeOffset(notches, xit: true);

    private void NudgeOffset(int notches, bool xit)
    {
        if (!_started || notches == 0 || SelectedSlice is not { } slice) return;

        var pending = xit ? _wheelTargetXitHz : _wheelTargetRitHz;
        var from = _wheelTargetOffsetSliceLetter == slice.Letter && pending is { } held
            ? held
            : (int)Math.Round(xit ? slice.XitOffsetHz : slice.RitOffsetHz);

        var target = RitXitRange.Clamp(from + (notches * RitXitStepHz));
        if (target == from) return;   // already against the rail

        if (xit) _wheelTargetXitHz = target; else _wheelTargetRitHz = target;
        _wheelTargetOffsetSliceLetter = slice.Letter;

        // Echoed locally so the readout moves with the wheel; the radio's own
        // report re-applies the same number a moment later.
        OnPropertyChanged(xit ? nameof(XitText) : nameof(RitText));
        ScheduleOffsetWrite(xit);
    }

    private void ScheduleOffsetWrite(bool xit)
    {
        if (xit)
        {
            if (_xitWriteScheduled) return;
            _xitWriteScheduled = true;
        }
        else
        {
            if (_ritWriteScheduled) return;
            _ritWriteScheduled = true;
        }
        _ = FlushOffsetAsync(xit);
    }

    // Coalesced behind the same settle window as frequency rather than written
    // per detent. RIT is folded into the effective RX frequency the CW Skimmer
    // sync tracker publishes (MainWindowViewModel.GetEffectiveRxFrequencyMHz),
    // so an unthrottled spin would put a SKIMMER/QSY burst into Skimmer for
    // every notch, exactly as an unthrottled frequency spin would.
    private async Task FlushOffsetAsync(bool xit)
    {
        await _settle(WheelWriteWindow);
        if (xit) _xitWriteScheduled = false; else _ritWriteScheduled = false;

        var target = xit ? _wheelTargetXitHz : _wheelTargetRitHz;
        if (target is not { } offset) return;

        // Dropped if the gesture belonged to a slice that is gone or no longer
        // selected, for the same reason the frequency wheel drops it.
        if (SelectedSlice is not { } slice || slice.Letter != _wheelTargetOffsetSliceLetter)
        {
            _wheelTargetRitHz = null;
            _wheelTargetXitHz = null;
            _wheelTargetOffsetSliceLetter = null;
            return;
        }

        if (xit)
            await _connection.SetSliceXitOffsetAsync(slice, offset);
        else
            await _connection.SetSliceRitOffsetAsync(slice, offset);

        if (xit && _wheelTargetXitHz == offset) _wheelTargetXitHz = null;
        if (!xit && _wheelTargetRitHz == offset) _wheelTargetRitHz = null;
    }

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
        {
            option.IsCurrent = string.Equals(option.Label, SelectedSlice?.Letter, StringComparison.OrdinalIgnoreCase);

            // Issue #69: red needs both halves. IsTransmitting is radio-scoped
            // (the radio is keyed), IsTransmitSlice is slice-scoped (this is the
            // one it transmits on). Either alone would redden the wrong chip, or
            // every chip.
            option.IsTransmitting = IsTransmitting && IsTransmitSliceLetter(option.Label);
        }
    }

    private bool IsTransmitSliceLetter(string letter) =>
        Slices.Any(slice => slice.IsTransmitSlice
                            && string.Equals(slice.Letter, letter, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True while the radio is keyed (issue #69). Radio-scoped; the chips pair
    /// it with each slice's own transmit flag.
    /// </summary>
    /// <remarks>
    /// Note this is "keyed", not "RF is going out". The SWR readout deliberately
    /// stays gated on forward power instead: between CW elements, or on SSB with
    /// no audio, the radio is keyed while forward power is nil, and the SWR
    /// meter's 1.0 floor would read as a real 1:1 match. See FormatSwr.
    /// </remarks>
    [ObservableProperty]
    private bool _isTransmitting;

    // TransmitStateChanged can fire on a FlexLib event thread.
    private void OnTransmitStateChanged(bool transmitting) =>
        _postToUi(() => ApplyTransmitState(transmitting));

    private void ApplyTransmitState(bool transmitting)
    {
        IsTransmitting = transmitting;
        ApplySliceButtonState();
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

    /// <summary>
    /// The power the QRP preset sets, and the top of the QRP range: 5 W or less
    /// is QRP by convention, so the button reads QRP anywhere at or below this,
    /// not only at exactly this value (operator, 2026-08-25).
    /// </summary>
    private const int QrpWatts = 5;

    /// <summary>
    /// Lowest power that counts as operating QRP. Below it the radio is not
    /// meaningfully transmitting, so the button still reads QRP but does not
    /// light: lit marks a power being run, not merely a number below 5.
    /// </summary>
    private const int QrpMinWatts = 1;

    /// <summary>
    /// The power the QRO preset returns to, in watts: the radio's full rated
    /// output (issue #70, corrected for issue #77).
    /// </summary>
    /// <remarks>
    /// Was a hard-coded 100, which made QRO mean one fifth of full power on a
    /// 500 W Aurora. The radio reports its own rating, so read it rather than
    /// assume it. Falls back to 100 only when the radio has not reported yet,
    /// and in that state every power control is already disabled.
    /// </remarks>
    private int QroWatts => _connection.MaxRfPowerWatts ?? FallbackMaxWatts;

    /// <summary>Rated output assumed before the radio reports one. Every power control is disabled here.</summary>
    private const int FallbackMaxWatts = 100;

    /// <summary>
    /// The power the deck last saw the radio at, or last wrote to it. Decides
    /// which preset the button offers next, and is updated optimistically on a
    /// press so the label flips with the readout instead of waiting for the
    /// radio echo.
    /// </summary>
    private int? _lastKnownWatts;

    /// <summary>The radio's transmit power setting, or dashes until it reports one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TxButtonText))]
    private string _txPowerText = Absent;

    /// <summary>
    /// The button label, which is always the preset the next press will set.
    /// </summary>
    /// <remarks>
    /// Issue #70, second pass (operator, 2026-08-25). The first build saved the
    /// power in use and restored it on release, so the button had four
    /// positions (QRP, saved, QRO, saved) and could sit reading "QRP" while the
    /// radio was at the saved power, which is incoherent. The operator cut the
    /// save/restore entirely: two presets, and any other power is set by hand.
    ///
    /// What that subtraction bought, beyond the simpler button: there is no
    /// cached power left to go stale, so the "radio wins" stand-down and every
    /// bug it existed to prevent are gone with it, including a saved power
    /// leaking across a reconnect (Codex deep audit, 2026-08-04). The label is
    /// now a pure function of the radio's own power, so it cannot disagree with
    /// the radio at all. Do not reintroduce a remembered power here.
    /// </remarks>
    /// <remarks>
    /// A state readout, not a promise about the press. Only ever displayed
    /// while the radio sits on a preset (see <see cref="TxButtonText"/>), so
    /// it names the preset in force: QRP at or below the QRP ceiling, QRO at
    /// 100 W. Pressing toggles to the other one.
    /// An earlier pass made the label name what the press would do, which put
    /// "QRP" on a button while the radio sat at the operating power. That is
    /// the thing to avoid: the label must never claim a state the radio is not
    /// in.
    /// </remarks>
    public string TxPresetText => IsQrpPower ? "QRP" : "QRO";

    /// <summary>
    /// What the single power button shows: the preset name while the radio
    /// sits on a preset, the actual wattage at any other level.
    /// </summary>
    /// <remarks>
    /// Rewritten 2026-09-20 after the v0.3.3-preview1 live test. The first
    /// pass showed the watts only while the wheel was turning and then reverted
    /// to the preset label, so the button read "QRO" while the radio sat at
    /// 54 W. "QRO" was defensible as a two-state readout, but on a button that
    /// can show the real number it is strictly worse: the number is the whole
    /// answer and never has to be interpreted. Lit still means a preset is in
    /// force, so lit and unlit now carry the label/number distinction too.
    ///
    /// This is the only place the power <em>setting</em> appears. It is not the
    /// same number as the "Fwd" telemetry below, which is measured forward
    /// power and reads zero on receive.
    ///
    /// The reverting behaviour needed a linger timer and a generation counter
    /// to keep a stale timer from clearing a fresh reading. Both are gone with
    /// it: an always-correct display has nothing to time out. Do not reintroduce
    /// a timer here.
    /// </remarks>
    public string TxButtonText => IsPresetActive ? TxPresetText : TxPowerText;

    /// <summary>
    /// True when the radio is running QRP, which is a range and not a single
    /// value: anything at or below <see cref="QrpWatts"/> counts.
    /// </summary>
    private bool IsQrpPower => _lastKnownWatts is int watts && watts <= QrpWatts;

    /// <summary>
    /// True while the radio sits on one of the two presets, which lights the
    /// button (issue #70).
    /// </summary>
    /// <remarks>
    /// Restored 2026-08-25 after the operator live-tested its absence. The
    /// first pass dropped the lit state on the grounds that the readout beside
    /// the button already shows the power, but every other control on the deck
    /// lights for the value the radio holds, and this one stopped. The real
    /// fault was the label: it named the <em>next</em> press, so lighting it
    /// would have claimed QRO was current while the radio sat at 5 W. Naming
    /// the preset the radio is on fixes both at once, and restores the deck's
    /// one meaning of lit: this is the value in force.
    /// </remarks>
    public bool IsPresetActive => _lastKnownWatts is int watts
        && (watts is >= QrpMinWatts and <= QrpWatts || watts == QroWatts);

    private void SetLastKnownWatts(int? watts)
    {
        if (_lastKnownWatts == watts) return;
        _lastKnownWatts = watts;
        OnPropertyChanged(nameof(TxPresetText));
        OnPropertyChanged(nameof(TxButtonText));
        OnPropertyChanged(nameof(IsPresetActive));
    }

    /// <summary>False until the radio has reported a power, so the button cannot aim at an unknown one.</summary>
    [ObservableProperty]
    private bool _canToggleQrp;

    [RelayCommand]
    private async Task ToggleQrpAsync()
    {
        if (!CanToggleQrp)
        {
            _logStatus("Power preset press ignored: the radio has not reported a power.");
            return;
        }

        // The button toggles between QRP and not-QRP, so anywhere in the QRP
        // range goes up to QRO and anywhere above it drops to QRP. The first
        // press from an ordinary operating power therefore drops to QRP, which
        // is the habit the original QRP button taught (operator, 2026-08-25).
        var target = IsQrpPower ? QroWatts : QrpWatts;

        _logStatus($"{(target == QrpWatts ? "QRP" : "QRO")} selected: setting {target} W.");
        SetLastKnownWatts(target);

        // Shown immediately rather than waiting for the radio's echo, so a
        // button press does not feel laggy; the echo re-applies the same value.
        TxPowerText = FormatTxPower(target);
        await _connection.SetRfPowerAsync(target);
    }

    // RfPowerChanged can fire on a FlexLib event thread.
    private void OnRfPowerChanged(int? watts) => _postToUi(() => ApplyRfPower(watts));

    private void ApplyRfPower(int? watts)
    {
        CanToggleQrp = watts is not null;
        TxPowerText = watts is { } value ? FormatTxPower(value) : Absent;

        // The radio wins, and now that is the whole of it: with no saved power
        // there is nothing to invalidate, so a power change from anywhere (the
        // deck wheel, SmartSDR, another client) simply re-aims the button.
        SetLastKnownWatts(watts);
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

    // The radio's power setting is a 0-100 percentage of its rated output
    // (issue #77), so both the ceiling and the step size scale with the radio:
    // 100 W in 1 W steps on a FLEX-6000, 500 W in 5 W steps on an Aurora.
    // Finer than one percent is not expressible through this API at all, so a
    // 1 W step on a 500 W PA would silently quantise back to 5 W and the wheel
    // would look stuck for four notches out of five.
    private const int TxPowerLow = 0;
    private int TxPowerHigh => QroWatts;
    private int TxPowerStep => Math.Max(1, QroWatts / 100);

    // The value the wheel is steering towards, held locally while a write is in
    // flight. Every control needs one: the radio's echo of notch N has not
    // landed when notch N+1 arrives, so computing from the radio-reported value
    // would make consecutive notches all compute the same target and a fast
    // spin would move one step (Codex deep audit, 2026-08-05, which caught this
    // on RF gain and AGC-T after they were first written without a target).
    // Null between gestures, so the next notch re-seeds from what the radio
    // actually holds.
    private double? _wheelTargetFreqMHz;
    private int? _wheelTargetRitHz;
    private int? _wheelTargetXitHz;
    private string? _wheelTargetOffsetSliceLetter;
    private bool _ritWriteScheduled;
    private bool _xitWriteScheduled;
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

        // Issue #69: explicitly, not as a side effect of the selection
        // changing. Chip state used to be repainted only from
        // OnSelectedSliceChanged, so a slice refresh that left the selection
        // alone never repainted, and moving the TX slice mid-transmission left
        // the red on the old chip. Selection is sticky by design, so that is
        // the common case rather than a corner.
        ApplySliceButtonState();
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
        _connection.TransmitStateChanged += OnTransmitStateChanged;
        _connection.StartTelemetry();

        RefreshSlices();

        // Adopt whatever the connection already holds, so a reopened window
        // shows values immediately instead of dashes until the next event.
        Apply(_connection.Telemetry);
        ApplyRfPower(_connection.RfPowerWatts);
        ApplyTransmitState(_connection.IsTransmitting);
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
        _connection.TransmitStateChanged -= OnTransmitStateChanged;
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
        PowerText    = FormatPower(telemetry.PowerWatts);
        RefPowerText = FormatPower(telemetry.ReflectedPowerWatts);
        SwrText      = FormatSwr(telemetry.Swr, telemetry.PowerWatts);
        TempText     = Format(telemetry.PaTempCelsius, "0");
        VoltsText    = Format(telemetry.VoltsDc, "0.0");
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
    /// <remarks>
    /// Shared by forward and reflected power (issue #69), and deliberately not
    /// gated on transmit the way <see cref="FormatSwr"/> is. SWR needs the gate
    /// because its meter floors at 1.0, so at rest it formats an idle floor
    /// into what reads as a real 1:1 match. Reflected power has no such floor:
    /// 0 W on receive is the true reading, and the same TX-idle signal the
    /// forward readout already carries. Gating it would make two adjacent power
    /// readouts behave differently for no gain.
    /// </remarks>
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
