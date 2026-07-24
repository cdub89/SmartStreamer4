using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace SDRIQStreamer.App;

public partial class MainWindow : Window
{
    private readonly AppSettingsSession _settingsSession;
    private MainWindowViewModel? _subscribedVm;
    private bool _firstInstallWizardShownThisSession;

    public MainWindow()
        : this(new AppSettingsSession(new AppSettingsStore()))
    {
    }

    public MainWindow(AppSettingsSession settingsSession)
    {
        _settingsSession = settingsSession;
        InitializeComponent();
        RestoreWindowPlacement();

        DataContextChanged += OnDataContextChanged;
        Opened += OnMainWindowOpened;
    }

    // Issue #57: the Exit button only closes the window so the graceful teardown
    // stays on the single OnClosing -> ViewModel.Shutdown path.
    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedVm.AudioIndexChangesDetected -= OnAudioIndexChangesDetected;
            _subscribedVm.DaxStationConfirmRequested -= OnDaxStationConfirmRequested;
            _subscribedVm.StopRunningAppsConfirmRequested -= OnStopRunningAppsConfirmRequested;
            _subscribedVm = null;
        }

        (DataContext as MainWindowViewModel)?.Shutdown();
        SaveWindowPlacement();
        _settingsSession.Save();
        base.OnClosing(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedVm.AudioIndexChangesDetected -= OnAudioIndexChangesDetected;
            _subscribedVm.DaxStationConfirmRequested -= OnDaxStationConfirmRequested;
            _subscribedVm.StopRunningAppsConfirmRequested -= OnStopRunningAppsConfirmRequested;
        }

        _subscribedVm = DataContext as MainWindowViewModel;
        if (_subscribedVm is not null)
        {
            _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;
            _subscribedVm.AudioIndexChangesDetected += OnAudioIndexChangesDetected;
            _subscribedVm.DaxStationConfirmRequested += OnDaxStationConfirmRequested;
            _subscribedVm.StopRunningAppsConfirmRequested += OnStopRunningAppsConfirmRequested;
        }
    }

    private Task<DaxStationConfirmResult> OnDaxStationConfirmRequested(DaxStationConfirmRequest request)
        => Dispatcher.UIThread.InvokeAsync(() => ShowDaxStationConfirmDialogAsync(request));

    private Task<bool> OnStopRunningAppsConfirmRequested(string message)
        => Dispatcher.UIThread.InvokeAsync(() => ShowStopRunningConfirmDialogAsync(message));

    private async void OnAudioIndexChangesDetected(IReadOnlyList<string> summary)
    {
        // Dispatch onto the UI thread; the VM raises on whichever thread the
        // DAX-IQ event arrived on. Show the dialog after the current event
        // pump tick so the launch sequence's other UI updates settle first.
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            await ShowAudioIndexChangedDialogAsync(vm, summary);
        });
    }

    private async void OnMainWindowOpened(object? sender, EventArgs e)
    {
        await EnsureDaxRunningAsync();
        TryShowFirstInstallWizard();
    }

    private async Task EnsureDaxRunningAsync()
    {
        while (true)
        {
            if (IsDaxRunning())
            {
                _subscribedVm?.AddStreamerStatus("DAX.exe is running.");
                return;
            }

            _subscribedVm?.AddStreamerStatus("DAX.exe not found.");
            bool retry = await ShowDaxNotRunningDialogAsync();
            if (!retry)
                return;
        }
    }

    private static bool IsDaxRunning()
    {
        var procs = Process.GetProcessesByName("DAX");
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CwSkimmerExePath) ||
            e.PropertyName == nameof(MainWindowViewModel.CwSkimmerIniPath))
        {
            TryShowFirstInstallWizard();
        }
    }

    private void TryShowFirstInstallWizard()
    {
        if (_firstInstallWizardShownThisSession)
            return;

        if (DataContext is not MainWindowViewModel vm)
            return;

        var settings = _settingsSession.Settings;
        if (settings.HasShownSkimmerSetupWizard)
            return;

        var exePath = vm.CwSkimmerExePath;
        var iniPath = vm.CwSkimmerIniPath;
        if (string.IsNullOrWhiteSpace(exePath) ||
            string.IsNullOrWhiteSpace(iniPath) ||
            !File.Exists(exePath) ||
            !File.Exists(iniPath))
        {
            return;
        }

        if (vm.IsCwSkimmerRunning)
            return;

        _firstInstallWizardShownThisSession = true;
        settings.HasShownSkimmerSetupWizard = true;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await ShowResetWizardAsync(vm);
            }
            catch
            {
                // Wizard is decorative on first launch; never block startup.
            }
        });
    }

    private async void OnBrowseCwSkimmer(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title           = "Select CwSkimmer.exe",
            AllowMultiple   = false,
            FileTypeFilter  =
            [
                new FilePickerFileType("Executable") { Patterns = ["CwSkimmer.exe", "*.exe"] },
                new FilePickerFileType("All files")  { Patterns = ["*"] }
            ]
        });

        if (files.Count > 0 && DataContext is MainWindowViewModel vm)
            vm.CwSkimmerExePath = files[0].Path.LocalPath;
    }

    private async void OnBrowseCwSkimmerIni(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select cwskimmer.ini",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("INI file") { Patterns = ["*.ini"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });

        if (files.Count > 0 && DataContext is MainWindowViewModel vm)
            vm.CwSkimmerIniPath = files[0].Path.LocalPath;
    }

    private async void OnBrowseDigitalExe(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var exeName = vm.ActiveEngineExeFileName;
        var path = await BrowseForExeAsync($"Select {exeName}", exeName, vm.ActiveEngineExePath);
        if (path is not null)
            vm.ActiveEngineExePath = path;
    }

    private async Task<string?> BrowseForExeAsync(string title, string preferredExe, string currentPath)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        // Open the picker in the folder of the currently-configured exe (fixes
        // the picker defaulting to Documents when a path is already set).
        IStorageFolder? startFolder = null;
        var startDir = string.IsNullOrWhiteSpace(currentPath) ? null : Path.GetDirectoryName(currentPath);
        if (!string.IsNullOrWhiteSpace(startDir) && Directory.Exists(startDir))
            startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(startDir);

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title                  = title,
            AllowMultiple          = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter         =
            [
                new FilePickerFileType("Executable") { Patterns = [preferredExe, "*.exe"] },
                new FilePickerFileType("All files")  { Patterns = ["*"] }
            ]
        });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private void OnOpenSpotTextColorMenu(object? sender, RoutedEventArgs e)
    {
        OpenSpotColorMenu(sender as Control, isBackground: false);
    }

    private void OnOpenSpotBackgroundColorMenu(object? sender, RoutedEventArgs e)
    {
        OpenSpotColorMenu(sender as Control, isBackground: true);
    }

    private void OpenSpotColorMenu(Control? anchor, bool isBackground)
    {
        if (anchor is null || DataContext is not MainWindowViewModel vm)
            return;

        var options = isBackground ? vm.SpotBackgroundColorOptions : vm.SpotColorOptions;
        var swatchPanel = new UniformGrid
        {
            Columns = 4,
            Rows = 2,
            Margin = new Thickness(1)
        };

        foreach (var option in options)
        {
            var selectedOption = option;
            var button = new Button
            {
                Content = CreateColorSwatchHeader(selectedOption.Hex),
                Width = 22,
                Height = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(1, 1, 1, 2),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            button.Click += (_, _) =>
            {
                if (isBackground)
                    vm.SpotSelectedBackgroundColorOption = selectedOption;
                else
                    vm.SpotSelectedColorOption = selectedOption;

                if (FlyoutBase.GetAttachedFlyout(anchor) is Flyout currentFlyout)
                    currentFlyout.Hide();
            };
            swatchPanel.Children.Add(button);
        }

        var flyout = new Flyout
        {
            Content = swatchPanel,
            Placement = PlacementMode.BottomEdgeAlignedLeft
        };
        flyout.FlyoutPresenterClasses.Add("compact-swatch-flyout");
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    private static Control CreateColorSwatchHeader(string hex)
    {
        var fill = Color.TryParse(hex, out var parsed)
            ? (IBrush)new SolidColorBrush(parsed)
            : Brushes.Transparent;

        return new Border
        {
            Width = 18,
            Height = 12,
            Background = fill,
            BorderBrush = Brushes.DimGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
    }


    private void RestoreWindowPlacement()
    {
        var settings = _settingsSession.Settings;

        if (settings.MainWindowWidth is > 0 && settings.MainWindowHeight is > 0)
        {
            SizeToContent = SizeToContent.Manual;
            Width = settings.MainWindowWidth.Value;
            Height = settings.MainWindowHeight.Value;
        }

        if (settings.MainWindowX.HasValue && settings.MainWindowY.HasValue)
        {
            Position = new PixelPoint(
                (int)Math.Round(settings.MainWindowX.Value),
                (int)Math.Round(settings.MainWindowY.Value));
        }
    }

    private void SaveWindowPlacement()
    {
        if (WindowState != WindowState.Normal) return;

        var settings = _settingsSession.Settings;
        settings.MainWindowX = Position.X;
        settings.MainWindowY = Position.Y;
        settings.MainWindowWidth = Bounds.Width;
        settings.MainWindowHeight = Bounds.Height;
    }

    private void OnOpenSetupWizard(object? sender, RoutedEventArgs e)
    {
        var viewer = new SetupWizardWindow();

        if (VisualRoot is Window owner)
            viewer.Show(owner);
        else
            viewer.Show();
    }

    private void OnOpenSupport(object? sender, RoutedEventArgs e)
    {
        const string issuesUrl = "https://github.com/cdub89/SmartStreamer4/issues";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = issuesUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore browser launch failures to avoid disrupting app flow.
        }
    }

    private async void OnResetChannelConfigRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (vm.IsCwSkimmerRunning)
        {
            await ShowResetBlockedDialogAsync();
            return;
        }

        await ShowResetWizardAsync(vm);
    }

    private async Task ShowResetWizardAsync(MainWindowViewModel vm)
    {
        var wizard = new ResetSkimmerWizardWindow(vm, _settingsSession.Settings);
        await wizard.ShowDialog(this);
    }

    private async Task ShowResetBlockedDialogAsync()
    {
        var message = new TextBlock
        {
            Text = "CW Skimmer is currently running.\n\nStop all CW Skimmer instances before resetting channel INI files.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 400
        };

        var okButton = new Button
        {
            Content = "OK",
            MinWidth = 80,
            IsDefault = true
        };

        var dialog = new Window
        {
            Title = "Reset Blocked",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    message,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { okButton }
                    }
                }
            }
        };

        okButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async Task<bool> ShowDaxNotRunningDialogAsync()
    {
        var message = new TextBlock
        {
            Text = "Please start or ensure DAX is running",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 400
        };

        bool retryClicked = false;

        var retryButton = new Button
        {
            Content = "Retry",
            MinWidth = 80
        };

        var ignoreButton = new Button
        {
            Content = "Ignore",
            MinWidth = 80,
            IsDefault = true,
            IsCancel = true
        };

        var dialog = new Window
        {
            Title = "Dax.exe Not Running Error",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    message,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { retryButton, ignoreButton }
                    }
                }
            }
        };

        retryButton.Click += (_, _) =>
        {
            retryClicked = true;
            dialog.Close();
        };

        ignoreButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return retryClicked;
    }

    private async Task ShowAudioIndexChangedDialogAsync(MainWindowViewModel vm, IReadOnlyList<string> changeSummary)
    {
        var lines = new List<string>
        {
            "Audio Device Numbers may have changed:",
            string.Empty
        };
        lines.AddRange(changeSummary.Select(s => "  " + s));
        lines.AddRange(new[]
        {
            string.Empty,
            "Please rerun the CW Skimmer Config Setup Wizard.",
            string.Empty,
            "Note: WDM index changes are not auto-detected. If you've upgraded SmartSDR or DAX, re-verify WDM values in the wizard too."
        });

        var message = new TextBlock
        {
            Text = string.Join(Environment.NewLine, lines),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460
        };

        var setupWizardButton = new Button
        {
            Content = "Set Up Wizard",
            MinWidth = 120
        };

        var ignoreButton = new Button
        {
            Content = "Ignore",
            MinWidth = 80,
            IsDefault = true,
            IsCancel = true
        };

        var dialog = new Window
        {
            Title = "Audio device numbers changed",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    message,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { setupWizardButton, ignoreButton }
                    }
                }
            }
        };

        bool setupWizardClicked = false;
        setupWizardButton.Click += (_, _) =>
        {
            setupWizardClicked = true;
            dialog.Close();
        };
        ignoreButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);

        if (!setupWizardClicked)
            return;

        // Stop CW Skimmer as a convenience — the wizard refuses to run while
        // Skimmer is up (channel INIs are being held open). The operator
        // already signalled intent by clicking "Set Up Wizard"; saving them
        // a manual stop-each-channel round trip.
        vm.StopAllCwSkimmerInstances();

        await ShowResetWizardAsync(vm);
    }

    private async Task<bool> ShowStopRunningConfirmDialogAsync(string messageText)
    {
        var message = new TextBlock
        {
            Text = messageText,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 400
        };

        var switchButton = new Button { Content = "Stop & Continue", MinWidth = 120 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 80, IsDefault = true, IsCancel = true };

        var dialog = new Window
        {
            Title = "Confirm",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    message,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { switchButton, cancelButton }
                    }
                }
            }
        };

        var proceed = false;
        switchButton.Click += (_, _) => { proceed = true; dialog.Close(); };
        cancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return proceed;
    }

    private async Task<DaxStationConfirmResult> ShowDaxStationConfirmDialogAsync(DaxStationConfirmRequest request)
    {
        // Issue #39 (2026-05-18): same-radio multi-station with same DAX-IQ
        // channel produces silently-wrong audio at CW Skimmer when DAX-the-app
        // is bound to the wrong station. We cannot query DAX's binding via
        // FlexLib so we ask the operator to verify it manually before launching.
        var messageText = string.Join(Environment.NewLine, new[]
        {
            $"DAX-IQ ch {request.DaxIqChannel} also assigned to {request.OtherStation}.",
            string.Empty,
            $"In the SmartSDR DAX application, select {request.OwnStation} to launch CW Skimmer properly.",
            string.Empty,
            "Click Start once you have updated the station in the SmartSDR DAX application.",
        });

        var message = new TextBlock
        {
            Text = messageText,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            FontSize = 13
        };

        var startButton = new Button
        {
            Content = "Start",
            MinWidth = 80
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 80,
            IsDefault = true,
            IsCancel = true
        };

        var dialog = new Window
        {
            Title = "DAX-IQ Channel Conflict",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    message,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { startButton, cancelButton }
                    }
                }
            }
        };

        var result = DaxStationConfirmResult.Cancel;
        startButton.Click += (_, _) =>
        {
            result = DaxStationConfirmResult.Start;
            dialog.Close();
        };
        cancelButton.Click += (_, _) =>
        {
            result = DaxStationConfirmResult.Cancel;
            dialog.Close();
        };

        await dialog.ShowDialog(this);
        return result;
    }
}
