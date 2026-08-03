using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        if (_settings.SmartDeckWidth is { } width && width > 0)
            Width = width;
        if (_settings.SmartDeckHeight is { } height && height > 0)
            Height = height;

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
        _settings.SmartDeckHeight = Bounds.Height;

        _viewModel.Stop();
    }

    private void OnAlwaysOnTopChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton check) return;

        var onTop = check.IsChecked == true;
        Topmost = onTop;
        _settings.SmartDeckAlwaysOnTop = onTop;
    }
}
