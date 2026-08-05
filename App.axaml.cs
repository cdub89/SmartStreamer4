using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace SDRIQStreamer.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();

            // Before the first window is built, so the app never flashes the
            // wrong ground on launch (issue #63).
            ApplyTheme(services.SettingsSession.Settings.ThemeMode);

            var viewModel = services.CreateMainWindowViewModel();
            desktop.MainWindow = new MainWindow(services.SettingsSession) { DataContext = viewModel };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Applies the app-wide appearance variant (issue #63). Every window
    /// follows it, SmartDeck included: a coherent app-wide setting beats one
    /// window's private exception, even though the deck's on-screen neighbour
    /// is always-dark SmartSDR.
    /// </summary>
    public static void ApplyTheme(AppTheme theme)
    {
        if (Current is { } app)
            app.RequestedThemeVariant = theme == AppTheme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}
