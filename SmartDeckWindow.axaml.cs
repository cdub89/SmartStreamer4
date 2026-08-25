using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SDRIQStreamer.App;

/// <summary>
/// SmartDeck window (issue #59, phase 1: telemetry only). Owns nothing but the
/// shell: position and size restore, the always-on-top toggle, and starting or
/// stopping the telemetry subscription with the window's lifetime. All value
/// formatting lives in <see cref="SmartDeckViewModel"/>.
/// </summary>
public partial class SmartDeckWindow : Window
{
    private readonly SmartDeckViewModel _viewModel;
    private readonly AppSettings _settings;

    // No parameterless constructor: this window is only ever created with a
    // live connection behind it. The existing wizard windows expose one for the
    // XAML previewer via `this(null!, null!)`, which the repo's no-`!` rule
    // rules out for new code, and the previewer is not worth a null-object
    // IRadioConnection implementation to satisfy.
    public SmartDeckWindow(SmartDeckViewModel viewModel, AppSettings settings)
    {
        _viewModel = viewModel;
        _settings = settings;

        InitializeComponent();
        DataContext = viewModel;

        RestorePlacement();

        Topmost = settings.SmartDeckAlwaysOnTop;
        if (this.FindControl<ToggleButton>("AlwaysOnTopCheck") is { } check)
            check.IsChecked = settings.SmartDeckAlwaysOnTop;

        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void RestorePlacement()
    {
        // Nullable rather than zero-defaulted: 0,0 is a real position on a
        // multi-monitor desktop, so "never positioned" has to be its own state.
        //
        // Width only. Height is not persisted (issue #63): the window sizes to
        // its content, so a restored height could only ever add dead space, and
        // restoring it made the issue #64 row merge invisible to anyone who had
        // already run the deck. Width still governs button sizing, so it stays.
        if (_settings.SmartDeckWidth is { } width && width > 0)
            Width = width;

        if (_settings.SmartDeckX is { } x && _settings.SmartDeckY is { } y)
        {
            Position = new Avalonia.PixelPoint((int)x, (int)y);
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
    }

    private void OnOpened(object? sender, System.EventArgs e) => _viewModel.Start();

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _settings.SmartDeckX = Position.X;
        _settings.SmartDeckY = Position.Y;
        _settings.SmartDeckWidth = Bounds.Width;

        _viewModel.Stop();
    }

    private void OnAlwaysOnTopChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton check) return;

        var onTop = check.IsChecked == true;
        Topmost = onTop;
        _settings.SmartDeckAlwaysOnTop = onTop;
    }

    // ── Mouse wheel over the readouts (issue #65) ────────────────────────────
    //
    // Hover is the whole gesture: no click, no focus, per the operator's
    // request. The window does nothing but turn a wheel event into a notch
    // count; every rule about steps, clamping and write throttling lives in the
    // ViewModel where it is testable without a radio.

    private readonly WheelNotchCounter _notches = new();

    private void OnFrequencyWheel(object? sender, PointerWheelEventArgs e) =>
        Nudge(sender, e, _viewModel.NudgeFrequency);

    private void OnRfGainWheel(object? sender, PointerWheelEventArgs e) =>
        Nudge(sender, e, _viewModel.NudgeRfGain);

    private void OnAgcThresholdWheel(object? sender, PointerWheelEventArgs e) =>
        Nudge(sender, e, _viewModel.NudgeAgcThreshold);

    private void OnTxPowerWheel(object? sender, PointerWheelEventArgs e) =>
        Nudge(sender, e, _viewModel.NudgeTxPower);

    // Issue #73. RIT and XIT are the one pair with two gestures on one target:
    // the wheel moves the offset, a click engages or releases. Kept separate on
    // purpose, so spinning the wheel never turns the function on behind the
    // operator's back.
    private void OnRitWheel(object? sender, PointerWheelEventArgs e) =>
        Nudge(sender, e, _viewModel.NudgeRit);

    private void OnXitWheel(object? sender, PointerWheelEventArgs e) =>
        Nudge(sender, e, _viewModel.NudgeXit);

    private void OnRitTapped(object? sender, TappedEventArgs e) =>
        _viewModel.ToggleRitCommand.Execute(null);

    private void OnXitTapped(object? sender, TappedEventArgs e) =>
        _viewModel.ToggleXitCommand.Execute(null);

    /// <summary>
    /// Marks the event handled whenever a step was delivered, so a wheel over a
    /// control never also scrolls something behind it.
    /// </summary>
    /// <remarks>
    /// The delta is not the step count. See <see cref="WheelNotchCounter"/>:
    /// this shipped taking Math.Round(Delta.Y) as the count, and on the
    /// operator's seat that made every control move two steps per detent.
    /// </remarks>
    private void Nudge(object? sender, PointerWheelEventArgs e, Action<int> nudge)
    {
        var steps = _notches.Add(e.Delta.Y, sender);
        if (steps == 0) return;

        nudge(steps);
        e.Handled = true;
    }
}
