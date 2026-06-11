using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SDRIQStreamer.CWSkimmer;
using SDRIQStreamer.Digital;
using SDRIQStreamer.FlexRadio;

namespace SDRIQStreamer.App;

public partial class MainWindowViewModel : ObservableObject, IDaxStationConfirmer
{
    private static readonly object s_streamerLogSync = new();
    private static readonly object s_spotPayloadLogSync = new();
    private readonly IRadioDiscovery   _discovery;
    private readonly IRadioConnection  _connection;
    private readonly ICwSkimmerLauncher _launcher;
    private readonly IDigitalAppLauncher _digitalLauncher;
    private readonly AppSettingsSession _settingsSession;
    private readonly AppSettings _settings;
    private readonly FooterStatusBuffer _footerStatusBuffer;
    private readonly CwSkimmerWorkflowService _cwSkimmerWorkflow;
    private readonly IReleaseUpdateService _releaseUpdateService;
    private readonly IAudioDeviceFinder _deviceFinder;
private static readonly (string ReleaseTag, string CommitHash, string Display, string BuildDate) s_appBuildInfo =
        ResolveAppBuildInfo();

    // ── Discovered radios ─────────────────────────────────────────────────────

    public ObservableCollection<DiscoveredRadio> Radios { get; } = new();
    public ObservableCollection<RadioConnectTarget> ConnectTargets { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor("ConnectCommand")]
    private RadioConnectTarget? _selectedConnectTarget;

    [ObservableProperty]
    private string _statusText = "Discovering…";

    // ── Connection state ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor("ConnectCommand")]
    [NotifyCanExecuteChangedFor("DisconnectCommand")]
    [NotifyCanExecuteChangedFor(nameof(ResetNetworkStatusCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private string _networkStatusLabel = "--";

    [ObservableProperty]
    private string _networkLatencyRttText = "--";

    [ObservableProperty]
    private string _networkMaxLatencyRttText = "--";

    // ── Operating mode (issue #28) ────────────────────────────────────────────
    // Mode gates which working tabs are visible. CW Mode = existing CW Skimmer
    // tabs (unchanged); Digital Mode = WSJT-X / JTDX screens. The Launch tab is
    // always visible and is selected on startup.

    // Null until the operator chooses a mode on the Launch tab. While null, only
    // the Launch and Help tabs are shown (the CW/Config/Logs and Digital tabs are
    // mode-specific and stay hidden until a mode is picked).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCwMode))]
    [NotifyPropertyChangedFor(nameof(IsDigitalMode))]
    [NotifyPropertyChangedFor(nameof(IsModeActive))]
    [NotifyPropertyChangedFor(nameof(ModeActionButtonLabel))]
    [NotifyPropertyChangedFor(nameof(ModeHeaderText))]
    [NotifyPropertyChangedFor(nameof(SelectedLaunchModeName))]
    private AppMode? _activeMode;

    public bool IsCwMode => ActiveMode == AppMode.Cw;
    public bool IsDigitalMode => ActiveMode == AppMode.Digital;

    /// <summary>True once a mode (CW or Digital) is active. Drives the Launch-tab
    /// mode control: swaps the mode list for the running status and flips the
    /// Start/Stop button (issue #28).</summary>
    public bool IsModeActive => ActiveMode is not null;

    // Launch-tab mode selector, consistent with the radio-station ListBox used to
    // connect: pick CW or Digital from a single-select list, then one Start/Stop
    // button enters/exits that mode. The list is disabled while a mode is active,
    // so (like choosing a station only while disconnected) you Stop to switch.
    private const string CwModeName = "CW";
    private const string DigitalModeName = "Digital";

    public IReadOnlyList<string> LaunchModes { get; } = [CwModeName, DigitalModeName];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLaunchModeName))]
    private AppMode _selectedLaunchMode = AppMode.Cw;

    /// <summary>
    /// The mode-list selection. While a mode is running it reflects the active
    /// mode (so the disabled list still shows what you are in); otherwise it is
    /// the pending choice the Start button will launch.
    /// </summary>
    public string SelectedLaunchModeName
    {
        get => (ActiveMode ?? SelectedLaunchMode) == AppMode.Digital ? DigitalModeName : CwModeName;
        set => SelectedLaunchMode = value == DigitalModeName ? AppMode.Digital : AppMode.Cw;
    }

    public string ModeActionButtonLabel => IsModeActive ? "Stop" : "Start";

    /// <summary>
    /// Header above the mode list: just "Mode" when idle (list shown), or the
    /// running-mode status when active (list hidden, only Stop remains).
    /// </summary>
    public string ModeHeaderText => ActiveMode switch
    {
        AppMode.Cw      => "Mode: CW Mode Running",
        AppMode.Digital => "Mode: Digital Mode Running",
        _ => "Mode",
    };

    /// <summary>
    /// The single Launch-tab mode button: when no mode is active it starts the
    /// selected mode; when a mode is active it stops/exits it. One button, like
    /// Connect/Disconnect.
    /// </summary>
    [RelayCommand]
    private async Task ModeAction()
    {
        if (IsModeActive)
        {
            await CloseModeCommand.ExecuteAsync(null);
            return;
        }

        if (SelectedLaunchMode == AppMode.Cw)
            await LaunchCwModeCommand.ExecuteAsync(null);
        else
            await LaunchDigitalModeCommand.ExecuteAsync(null);
    }

    /// <summary>Drives TabControl.SelectedIndex. 0 = Launch tab (startup).</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>
    /// Raised when an action (switch / close mode) would stop running apps. The
    /// View shows a confirm dialog with the given message; returns true to proceed.
    /// </summary>
    public event Func<string, Task<bool>>? StopRunningAppsConfirmRequested;

    // ── Digital Mode config (issue #28) ───────────────────────────────────────
    // Operator identity + engine paths + recommended defaults written into each
    // per-slice config at launch. Identity is prepopulated from an existing
    // FlexRadio WSJT-X / JTDX profile when present (see ImportDigitalProfile).

    [ObservableProperty]
    private string _digitalMyCall = string.Empty;

    [ObservableProperty]
    private string _digitalMyGrid = string.Empty;

    [ObservableProperty]
    private string _digitalRig = "FlexRadio 6xxx";

    // 0 = WSJT-X, 1 = JTDX-Improved, 2 = WSJT-Z. The single active engine;
    // switching is a config change. Drives ActiveDigitalEngine and the active
    // exe-path proxy below.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveDigitalEngine))]
    [NotifyPropertyChangedFor(nameof(ActiveEngineLabel))]
    [NotifyPropertyChangedFor(nameof(ActiveEngineExePath))]
    [NotifyPropertyChangedFor(nameof(ActiveEngineExeFileName))]
    [NotifyPropertyChangedFor(nameof(IsWsjtXSelected))]
    [NotifyPropertyChangedFor(nameof(IsJtdxSelected))]
    [NotifyPropertyChangedFor(nameof(IsWsjtZSelected))]
    private int _digitalEngineIndex;

    /// <summary>
    /// The active engine can only be changed when no digital instance is running
    /// (one engine at a time; switching is a config change). Bound to the engine
    /// radio buttons' IsEnabled so the operator cannot mix WSJT-X and JTDX
    /// against the same slice / CAT port (review #2).
    /// </summary>
    public bool CanChangeDigitalEngine => !_digitalLauncher.IsRunning;

    // Radio-button bindings for the engine selector (mutually exclusive).
    public bool IsWsjtXSelected
    {
        get => DigitalEngineIndex == 0;
        set { if (value) DigitalEngineIndex = 0; }
    }

    public bool IsJtdxSelected
    {
        get => DigitalEngineIndex == 1;
        set { if (value) DigitalEngineIndex = 1; }
    }

    public bool IsWsjtZSelected
    {
        get => DigitalEngineIndex == 2;
        set { if (value) DigitalEngineIndex = 2; }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveEngineExePath))]
    private string _wsjtXExePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveEngineExePath))]
    private string _jtdxExePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveEngineExePath))]
    private string _wsjtZExePath = string.Empty;

    public DigitalEngine ActiveDigitalEngine => DigitalEngineIndex switch
    {
        1 => DigitalEngine.Jtdx,
        2 => DigitalEngine.WsjtZ,
        _ => DigitalEngine.WsjtX,
    };

    public string ActiveEngineLabel => ActiveDigitalEngine switch
    {
        DigitalEngine.Jtdx  => "JTDX-Improved",
        DigitalEngine.WsjtZ => "WSJT-Z",
        _ => "WSJT-X",
    };

    // WSJT-Z's own executable is named wsjtx.exe, so only JTDX differs here.
    public string ActiveEngineExeFileName => ActiveDigitalEngine == DigitalEngine.Jtdx ? "jtdx.exe" : "wsjtx.exe";

    /// <summary>
    /// Exe path of the active engine. Reads / writes the matching backing
    /// property so the Config tab shows a single path for the chosen engine
    /// while every engine's path stays remembered.
    /// </summary>
    public string ActiveEngineExePath
    {
        get => ActiveDigitalEngine switch
        {
            DigitalEngine.Jtdx  => JtdxExePath,
            DigitalEngine.WsjtZ => WsjtZExePath,
            _ => WsjtXExePath,
        };
        set
        {
            switch (ActiveDigitalEngine)
            {
                case DigitalEngine.Jtdx:  JtdxExePath = value;  break;
                case DigitalEngine.WsjtZ: WsjtZExePath = value; break;
                default: WsjtXExePath = value; break;
            }
        }
    }

    /// <summary>
    /// Per-slice DAX RX channel + CAT / UDP ports, pre-set to recommended
    /// defaults but editable per slice. Edits persist to settings. TX audio is
    /// the shared DAX TX (not per slice). Consumed by the Operating screen's
    /// per-slice launch.
    /// </summary>
    public ObservableCollection<DigitalSliceConfigViewModel> DigitalSliceConfigs { get; } = new();

    /// <summary>
    /// Digital Operating screen rows: one per live slice, each paired with its
    /// per-slice binding and Start/Stop for the active engine (issue #28).
    /// </summary>
    public ObservableCollection<DigitalOperatingRowViewModel> DigitalSlices { get; } = new();

    // ── Post-connect details ──────────────────────────────────────────────────

    /// <summary>Hierarchical view: client → panadapter(s) → slice(s).</summary>
    public ObservableCollection<ClientGroup> ClientGroups { get; } = new();
    public IEnumerable<ClientGroup> VisibleClientGroups =>
        string.IsNullOrWhiteSpace(SelectedControlStation)
            ? ClientGroups
            : ClientGroups.Where(g => string.Equals(g.Station, SelectedControlStation, StringComparison.OrdinalIgnoreCase));

    public ObservableCollection<DaxIQStreamInfo> DaxIQStreams { get; } = new();

    [ObservableProperty]
    private int _avgDAXKbps;
    private int _radioAvgDaxKbps;

    [ObservableProperty]
    private string _daxStreamingSummary = "DAX Streaming : 0.0 Mbps (0 kbps)";

    /// <summary>Station name of our own connected client.</summary>
    public string OwnClientStation { get; private set; } = string.Empty;
    public string SelectedControlStation { get; private set; } = string.Empty;

    // Issue #45: tracks whether SelectedControlStation has been observed present
    // in a GUI-client snapshot this session. Disconnect detection only fires
    // after the station has been seen, so a partial/initial snapshot during
    // connect cannot raise a false "station disconnected". Reset whenever the
    // selected station changes (see SetSelectedControlStation).
    private bool _controlStationSeen;

    // ── CW Skimmer ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCwSkimmerForChannelCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchCwSkimmerForSliceCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCwSkimmerForSliceCommand))]
    private string _cwSkimmerExePath = string.Empty;

    [ObservableProperty]
    private string _cwSkimmerIniPath = string.Empty;

    [ObservableProperty]
    private bool _isCwSkimmerRunning;

    [ObservableProperty]
    private string _telnetCallsign = string.Empty;

    [ObservableProperty]
    private int _connectDelaySeconds = 5;

    [ObservableProperty]
    private int _launchDelaySeconds = 3;

    [ObservableProperty]
    private int _telnetPortBase = 7300;

    [ObservableProperty]
    private bool _telnetClusterEnabled = true;

    [ObservableProperty]
    private string _streamerIniCh1PathText = "(not generated yet)";

    [ObservableProperty]
    private string _streamerIniCh1Path = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenStreamerIniCh1FileCommand))]
    private bool _hasStreamerIniCh1File;

    [ObservableProperty]
    private string _streamerIniCh2PathText = "(not generated yet)";

    [ObservableProperty]
    private string _streamerIniCh2Path = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenStreamerIniCh2FileCommand))]
    private bool _hasStreamerIniCh2File;

    [ObservableProperty]
    private string _streamerIniFolderPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenStreamerIniFolderCommand))]
    private bool _hasStreamerIniFolder;

    [ObservableProperty]
    private string _logsFolderPathText = "(not available)";

    [ObservableProperty]
    private string _logsFolderPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenLogsFolderCommand))]
    private bool _hasLogsFolder;

    [ObservableProperty]
    private bool _spotForwardingEnabled = true;

    [ObservableProperty]
    private int _spotLifetimeSeconds = 300;

    [ObservableProperty]
    private string _spotColor = "#FF00FFFF";

    [ObservableProperty]
    private string _spotBackgroundColor = "#00000000";

    [ObservableProperty]
    private SpotColorOption? _spotSelectedColorOption;
    public IReadOnlyList<SpotColorOption> SpotColorOptions { get; } =
    [
        new SpotColorOption("Red", "#FFFF0000"),
        new SpotColorOption("Green", "#FF008000"),
        new SpotColorOption("Blue", "#FF0000FF"),
        new SpotColorOption("Yellow", "#FFFFFF00"),
        new SpotColorOption("Orange", "#FFFFA500"),
        new SpotColorOption("Purple", "#FF800080"),
        new SpotColorOption("Cyan", "#FF00FFFF"),
        new SpotColorOption("White", "#FFFFFFFF"),
    ];

    [ObservableProperty]
    private SpotColorOption? _spotSelectedBackgroundColorOption;
    public IReadOnlyList<SpotColorOption> SpotBackgroundColorOptions { get; } =
    [
        new SpotColorOption("Transparent", "#00000000"),
        new SpotColorOption("Red", "#66FF0000"),
        new SpotColorOption("Green", "#6600FF00"),
        new SpotColorOption("Blue", "#660000FF"),
        new SpotColorOption("Yellow", "#66FFFF00"),
        new SpotColorOption("Orange", "#66FFA500"),
        new SpotColorOption("Purple", "#66800080"),
        new SpotColorOption("Cyan", "#6600FFFF"),
    ];

    public ObservableCollection<string> FooterStatusLines { get; } = new();
    private readonly Dictionary<int, string> _lastCwDevicePreviewByChannel = new();
    private readonly Dictionary<int, CancellationTokenSource> _streamRemovedDebounceCtsByChannel = new();
    private readonly CancellationTokenSource _updateCheckLoopCts = new();
    private readonly SemaphoreSlim _updateCheckLock = new(1, 1);
    private Task? _updateCheckLoopTask;
    private string _lastAnnouncedUpdateTag = string.Empty;
    private readonly object _syncDampenGate = new();
    private readonly ThrottledStatusEmitter _ritStatusEmitter;
    private readonly Dictionary<string, (bool RitEnabled, double RitOffsetHz)> _lastRitStateBySlice = new();
    private readonly Dictionary<int, (double FreqMHz, DateTime Utc)> _lastOutboundQsyByChannel = new();
    private readonly Dictionary<int, (double FreqMHz, DateTime Utc)> _lastInboundClickByChannel = new();
    private readonly Dictionary<int, (long LoHz, double? RxMHz)> _lastLoggedPanSyncByChannel = new();
    private bool _isApplyingStartupSettings;

    private const double EchoSuppressToleranceMHz = 0.000010; // 10 Hz
    private static readonly TimeSpan EchoSuppressWindow = TimeSpan.FromMilliseconds(700);
    private const double DuplicateClickToleranceMHz = 0.000005; // 5 Hz
    private static readonly TimeSpan DuplicateClickWindow = TimeSpan.FromMilliseconds(250);
    private const int FallbackClickSnapStepHz = 50;
    private static readonly TimeSpan RitStatusMinInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DaxStreamRemovedGracePeriod = TimeSpan.FromMilliseconds(1500);
    [ObservableProperty]
    private string _footerStatusText = string.Empty;

    [ObservableProperty]
    private string _latestFooterEvent = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenLatestReleaseCommand))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenLatestReleaseCommand))]
    private string _latestReleaseUrl = string.Empty;

    [ObservableProperty]
    private string _latestAvailableTag = string.Empty;

    [ObservableProperty]
    private string _updateStatusText = "Not checked yet.";

    public string AppReleaseTag => s_appBuildInfo.ReleaseTag;
    public string AppReleaseDisplay => FormatReleaseForHelp(AppReleaseTag);
    public string AppCommitHash => s_appBuildInfo.CommitHash;
    public string AppBuildDisplay => s_appBuildInfo.Display;
    public string AppBuildDate => s_appBuildInfo.BuildDate;
    private string AppReleaseTagForUpdateChecks => ResolveReleaseTagForUpdateChecks();
    public string WindowTitle => $"SmartStreamer4 {AppBuildDisplay}";
    public string ConnectTargetHeaderColor => IsConnected ? "Green" : "Gray";
    public string ConnectTargetHeaderText
    {
        get
        {
            if (!IsConnected)
                return "Available Radios";

            var radioName = ResolveConnectedRadioDisplayName();

            var stationName = !string.IsNullOrWhiteSpace(SelectedControlStation)
                ? SelectedControlStation
                : OwnClientStation;
            if (string.IsNullOrWhiteSpace(stationName))
                stationName = "Unknown Station";

            return $"Connected: {radioName} - {stationName}";
        }
    }

    public string AboutDevelopedBy => "Developed by Chris L White, WX7V and Cursor.AI Premium Agent v31.14";
    public string AboutLicenseReference => "Licensed under the MIT License. See LICENSE for full terms.";

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainWindowViewModel(IRadioDiscovery discovery, IRadioConnection connection,
                               ICwSkimmerLauncher launcher, IDigitalAppLauncher digitalLauncher,
                               AppSettingsSession settingsSession,
                               IReleaseUpdateService releaseUpdateService,
                               IAudioDeviceFinder deviceFinder)
    {
        _discovery     = discovery;
        _connection    = connection;
        _launcher      = launcher;
        _digitalLauncher = digitalLauncher;
        _settingsSession = settingsSession;
        _settings = _settingsSession.Settings;
        // No mode is active until the operator chooses one on the Launch tab, so
        // only Launch + Help show initially. LastMode is still persisted on
        // selection (reserved for a future "default mode" preference) but is not
        // auto-activated here.
        _activeMode = null;
        _releaseUpdateService = releaseUpdateService;
        _deviceFinder = deviceFinder;
        _footerStatusBuffer = new FooterStatusBuffer(FooterStatusLines);
        _ritStatusEmitter = new ThrottledStatusEmitter(RitStatusMinInterval, UIPost, AddTelnetStatus);
        _cwSkimmerWorkflow = new CwSkimmerWorkflowService(_connection, _launcher, _settings, this);

        _discovery.RadioAdded   += OnRadioAdded;
        _discovery.RadioRemoved += OnRadioRemoved;

        _connection.ConnectionStateChanged += OnConnectionStateChanged;
        _connection.PanadapterAdded        += p => UIPost(() => AddPan(p));
        _connection.PanadapterRemoved      += p => UIPost(() => RemovePan(p));
        _connection.PanadapterUpdated      += p => UIPost(() => UpdatePan(p));
        _connection.SliceAdded             += s => UIPost(() => AddSlice(s));
        _connection.SliceRemoved           += s => UIPost(() => RemoveSlice(s));
        _connection.SliceUpdated           += s => UIPost(() => UpdateSlice(s));
        _connection.DaxIQStreamAdded       += d => UIPost(() => OnDaxIQStreamAdded(d));
        _connection.DaxIQStreamRemoved     += d => UIPost(() => OnDaxIQStreamRemoved(d));
        _connection.DaxIQStreamUpdated     += d => UIPost(() => OnDaxIQStreamUpdated(d));
        _connection.AvgDAXKbpsChanged      += kbps => UIPost(() =>
        {
            _radioAvgDaxKbps = kbps;
            UpdateDisplayedDaxKbps();
        });
        _connection.NetworkStatusChanged   += status => UIPost(() => ApplyNetworkStatus(status));
        _connection.GuiClientsChanged      += clients => UIPost(() => OnGuiClientsChanged(clients));
        _connection.DiagnosticEvent        += line => UIPost(() => AddDiagnosticStatus(line));

        // Re-evaluate Launch command whenever the stream list changes
        DaxIQStreams.CollectionChanged += (_, _) =>
            UIPost(() =>
            {
                LaunchCwSkimmerForChannelCommand.NotifyCanExecuteChanged();
                StopCwSkimmerForChannelCommand.NotifyCanExecuteChanged();
                LaunchCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
                StopCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
                RefreshSliceSkimmerStates();
            });

        // Track CW Skimmer process state
        _launcher.RunningStateChanged += running =>
            UIPost(() =>
            {
                IsCwSkimmerRunning = running;
                LaunchCwSkimmerForChannelCommand.NotifyCanExecuteChanged();
                StopCwSkimmerForChannelCommand.NotifyCanExecuteChanged();
                LaunchCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
                StopCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
                RefreshDaxStreamPanBindings();
                RefreshSliceSkimmerStates();
                RefreshAllPanStreamSummaries();
                if (!running)
                    AddSkimmerStatus("CW Skimmer stopped.");
            });

        // Click→tune: when user clicks a signal in CW Skimmer, tune the associated slice
        _launcher.FrequencyClicked += (daxIqChannel, freqKhz) =>
        {
            var slice = GetPreferredSliceForTune(daxIqChannel);
            if (slice is null) return;
            var rawFreqMHz = freqKhz / 1000.0;
            var clickSnapStepHz = ResolveClickSnapStepHz(slice);
            var snappedFreqMHz = FrequencyMath.SnapMHzToStepHz(rawFreqMHz, clickSnapStepHz);
            if (ShouldSuppressInboundClick(daxIqChannel, snappedFreqMHz, clickSnapStepHz))
                return;

            _ = _connection.SetSliceFrequencyAsync(slice, snappedFreqMHz);
            UIPost(() =>
            {
                if (Math.Abs(snappedFreqMHz - rawFreqMHz) >= 0.0000005)
                {
                    AddTelnetStatus(
                        $"ch {daxIqChannel}: Click snap (Skimmer): {rawFreqMHz:F6} MHz -> {snappedFreqMHz:F6} MHz (step {clickSnapStepHz} Hz)");
                }

                AddTelnetStatus(
                    $"ch {daxIqChannel}: Click tune (Skimmer): {snappedFreqMHz:F6} MHz -> Slice {slice.Letter} ({slice.ClientStation})");
            });
        };

        // Digital engine instances: on every launch/exit, refresh per-slice
        // running state and the engine selector's enabled state. InstancesChanged
        // (per-instance) fires even when one of several instances exits, unlike
        // the aggregate RunningStateChanged (issue #28).
        _digitalLauncher.InstancesChanged += () => UIPost(() =>
        {
            RefreshDigitalRunningStates();
            OnPropertyChanged(nameof(CanChangeDigitalEngine));
        });

        _launcher.TelnetStatusChanged += message =>
            UIPost(() => AddTelnetStatus(message));
        _launcher.SpotReceived += (daxIqChannel, spot) =>
        {
            _ = PublishSkimmerSpotAsync(spot);
        };

        // Mirror sync-tracker outbound QSYs into echo-suppression state so
        // CW Skimmer's click feedback for our own commands is recognized and
        // doesn't loop back as a tune request.
        _launcher.OutboundQsyEmitted += (daxIqChannel, freqMHz, _) =>
            RecordOutboundQsy(daxIqChannel, freqMHz);

        // Load persisted settings without emitting user-facing change notices.
        _isApplyingStartupSettings = true;
        try
        {
            CwSkimmerExePath = _settings.CwSkimmerExePath;
            CwSkimmerIniPath = _settings.CwSkimmerIniPath;
            TelnetCallsign = _settings.Callsign;
            ConnectDelaySeconds = _settings.ConnectDelaySeconds;
            LaunchDelaySeconds = _settings.LaunchDelaySeconds;
            TelnetPortBase = _settings.TelnetPortBase;
            TelnetClusterEnabled = _settings.TelnetClusterEnabled;
            UpdateTelnetIniSummary();
            SpotForwardingEnabled = _settings.SpotForwardingEnabled;
            SpotLifetimeSeconds = _settings.SpotLifetimeSeconds;
            SpotColor = _settings.SpotColor;
            SpotBackgroundColor = _settings.SpotBackgroundColor;
            UpdateSpotColorSelection(SpotColor);
            UpdateSpotBackgroundColorSelection(SpotBackgroundColor);

            DigitalMyCall = _settings.DigitalMyCall;
            DigitalMyGrid = _settings.DigitalMyGrid;
            DigitalRig = _settings.DigitalRig;
            WsjtXExePath = _settings.WsjtXExePath;
            JtdxExePath = _settings.JtdxExePath;
            WsjtZExePath = _settings.WsjtZExePath;
            DigitalEngineIndex = _settings.DigitalActiveEngine switch
            {
                var s when string.Equals(s, "Jtdx", StringComparison.OrdinalIgnoreCase) => 1,
                var s when string.Equals(s, "WsjtZ", StringComparison.OrdinalIgnoreCase) => 2,
                _ => 0,
            };
        }
        finally
        {
            _isApplyingStartupSettings = false;
        }

        LoadDigitalSliceConfigs();

        // Prepopulate call/grid from an existing FlexRadio profile for operators
        // who already run WSJT-X / JTDX (issue #28). Only fills blanks, so it
        // never clobbers values the operator has already entered.
        TryPrepopulateDigitalIdentity();

        UpdateLogsFolderSummary();
        AddStreamerStatus($"Release: {AppReleaseTag} | Commit: {AppCommitHash}");
        foreach (var line in AppDataPaths.DrainMigrationMessages())
            AddStreamerStatus(line);
        StartUpdateChecks();
        _discovery.Start();
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    private void OnRadioAdded(DiscoveredRadio radio) => UIPost(() =>
    {
        var existing = Radios.FirstOrDefault(r => r.Serial == radio.Serial);
        if (existing is null)
            Radios.Add(radio);
        else
            Radios[Radios.IndexOf(existing)] = radio;

        RebuildConnectTargets();
        StatusText = Radios.Count == 0 ? "No radios found" : string.Empty;
    });

    private void OnRadioRemoved(DiscoveredRadio radio) => UIPost(() =>
    {
        var m = Radios.FirstOrDefault(r => r.Serial == radio.Serial);
        if (m is not null) Radios.Remove(m);
        RebuildConnectTargets();
        StatusText = Radios.Count == 0 ? "No radios found" : string.Empty;
    });

    // ── Connection ────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (SelectedConnectTarget is null) return;

        SetSelectedControlStation(SelectedConnectTarget.Station);
        bool ok = await _connection.ConnectAsync(SelectedConnectTarget.Radio);
        if (!ok) AddStreamerStatus("Connection failed.");
    }

    private bool CanConnect() => SelectedConnectTarget is not null && !IsConnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect() => _connection.Disconnect();

    private bool CanDisconnect() => IsConnected;

    // ── Mode switching (issue #28) ────────────────────────────────────────────
    // Tab order in MainWindow.axaml (visibility, not position, is gated by mode):
    //   0 Launch | 1 CW | 2 CW Config | 3 Digital | 4 Digital Config | 5 Logs | 6 Help
    // Launch / Logs / Help are always shown; activating a mode reveals that mode's
    // operating + config tabs. Entering a mode navigates to its operating tab.
    private const int CwHomeTabIndex      = 1; // CW operating tab
    private const int DigitalHomeTabIndex = 3; // Digital operating tab

    [RelayCommand]
    private Task LaunchCwMode() => SwitchModeAsync(AppMode.Cw);

    [RelayCommand]
    private Task LaunchDigitalMode() => SwitchModeAsync(AppMode.Digital);

    /// <summary>
    /// Close the active mode back to mode selection (only Launch + Help show).
    /// The radio stays connected. `LastMode` is left unchanged so a future
    /// "Auto Start Last Mode" can restore it.
    /// </summary>
    [RelayCommand]
    private async Task CloseMode()
    {
        if (ActiveMode is not { } current)
            return;

        if (HasRunningAppForCurrentMode() && !await ConfirmStopRunningAsync(
                $"Closing {ModeName(current)} Mode will stop its running application(s).\n\nContinue?"))
        {
            return;
        }

        StopCurrentModeApps();
        ActiveMode = null;       // back to "no mode"; LastMode intentionally preserved
        SelectedTabIndex = 0;    // Launch tab
    }

    private async Task SwitchModeAsync(AppMode target)
    {
        if (ActiveMode == target)
        {
            NavigateToModeHome(target);
            return;
        }

        // Hard modes: leaving a mode with a running app stops it. Confirm first.
        if (ActiveMode is { } current && HasRunningAppForCurrentMode() && !await ConfirmStopRunningAsync(
                $"Switching to {ModeName(target)} Mode will stop {ModeName(current)} Mode's running application(s).\n\nContinue?"))
        {
            return;
        }

        StopCurrentModeApps();
        ActiveMode = target;                 // flips tab visibility first
        _settings.LastMode = target.ToString();
        _settingsSession.Save();
        NavigateToModeHome(target);          // then select the now-visible tab
    }

    private async Task<bool> ConfirmStopRunningAsync(string message)
    {
        var handler = StopRunningAppsConfirmRequested;
        return handler is null || await handler(message);
    }

    private static string ModeName(AppMode mode) => mode == AppMode.Cw ? "CW" : "Digital";

    private bool HasRunningAppForCurrentMode() => ActiveMode switch
    {
        AppMode.Cw      => _launcher.IsRunning,
        AppMode.Digital => _digitalLauncher.IsRunning,
        _               => false,
    };

    private void StopCurrentModeApps()
    {
        switch (ActiveMode)
        {
            case AppMode.Cw:
                StopAllCwSkimmerInstances();
                break;
            case AppMode.Digital:
                _digitalLauncher.Stop();
                break;
        }
    }

    private void NavigateToModeHome(AppMode mode) =>
        SelectedTabIndex = mode == AppMode.Cw ? CwHomeTabIndex : DigitalHomeTabIndex;

    // ── Digital Operating (issue #28) ─────────────────────────────────────────

    /// <summary>
    /// Adds or updates the Digital Operating row for <paramref name="slice"/>.
    /// Scoped to the controlled station (like the CW VisibleClientGroups): a row
    /// is keyed by slice letter, which is unique only WITHIN a station, so a
    /// foreign station's slice must not feed in. Refreshes live mode/freq and
    /// re-reads the per-slice binding so Config edits are reflected.
    /// </summary>
    private void SyncDigitalSliceRow(SliceInfo slice)
    {
        // Bug (reported 2026-06-11): with two stations (SUPERWIN + MaestroC) each
        // has its own Slice A. Rows were keyed by letter only with no station
        // filter, so the non-controlled station's Slice A (MaestroC, CW) appeared
        // on the Digital Operating page when controlling SUPERWIN (DIGU). Fix:
        // only sync slices owned by SelectedControlStation. Chosen over keying by
        // station+letter because Digital Mode operates one station at a time, so
        // the rows should mirror exactly the controlled station's slices.
        if (!IsOwnStationSlice(slice))
            return;

        var (daxRx, catPort, udpPort) = GetBindingForLetter(slice.Letter);

        var row = DigitalSlices.FirstOrDefault(r =>
            string.Equals(r.SliceLetter, slice.Letter, StringComparison.OrdinalIgnoreCase));

        if (row is null)
        {
            row = new DigitalOperatingRowViewModel(slice.Letter, slice.ClientStation);
            InsertDigitalSliceRowSorted(row);
        }

        row.Mode = slice.Mode;
        row.FreqMHz = slice.FreqMHz;
        row.DaxRxChannel = daxRx;
        row.CatPort = catPort;
        row.UdpPort = udpPort;
        row.IsRunning = _digitalLauncher.IsInstanceRunning(row.RigName);
    }

    private void InsertDigitalSliceRowSorted(DigitalOperatingRowViewModel row)
    {
        // Keep rows ordered by slice letter for a stable display.
        var index = 0;
        while (index < DigitalSlices.Count &&
               string.CompareOrdinal(DigitalSlices[index].SliceLetter, row.SliceLetter) < 0)
            index++;
        DigitalSlices.Insert(index, row);
    }

    private void RemoveDigitalSliceRow(SliceInfo slice)
    {
        // Match station + letter so removing another station's Slice A cannot drop
        // the controlled station's row (see SyncDigitalSliceRow, 2026-06-11).
        var row = DigitalSlices.FirstOrDefault(r =>
            string.Equals(r.SliceLetter, slice.Letter, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Station, slice.ClientStation, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
            DigitalSlices.Remove(row);
    }

    /// <summary>
    /// Rebuilds the Digital Operating rows for the current
    /// <see cref="SelectedControlStation"/> (issue #28). Called when the
    /// controlled station changes (initial connect, or an issue #45 re-pin) so
    /// the rows always reflect the controlled station's slices and never a
    /// previously-shown station's.
    /// </summary>
    private void RebuildDigitalSlices()
    {
        DigitalSlices.Clear();
        foreach (var slice in _connection.Slices)
            SyncDigitalSliceRow(slice);   // guard keeps only the controlled station
    }

    /// <summary>
    /// The DAX RX / CAT / UDP for a slice letter: the operator's Config binding
    /// when present, else recommended defaults derived from the letter ordinal
    /// (e.g. slices beyond A-D on a FLEX-6700).
    /// </summary>
    private (int DaxRx, int CatPort, int UdpPort) GetBindingForLetter(string letter)
    {
        var binding = DigitalSliceConfigs.FirstOrDefault(c =>
            string.Equals(c.SliceLetter, letter, StringComparison.OrdinalIgnoreCase));
        if (binding is not null)
            return (binding.DaxRxChannel, binding.CatPort, binding.UdpPort);

        var ordinal = letter.Length == 1 ? Math.Max(0, char.ToUpperInvariant(letter[0]) - 'A') : 0;
        return (ordinal + 1, 60_000 + ordinal, 2_237 + ordinal);
    }

    private void RefreshDigitalRunningStates()
    {
        foreach (var row in DigitalSlices)
            row.IsRunning = _digitalLauncher.IsInstanceRunning(row.RigName);
    }

    [RelayCommand]
    private async Task StartDigitalForSlice(DigitalOperatingRowViewModel? row)
    {
        if (row is null)
            return;

        var engine = GetEngineDefinition(ActiveDigitalEngine);
        var values = new DigitalProvisionValues(
            DigitalMyCall, DigitalMyGrid, DigitalRig, row.DaxRxChannel, row.CatPort, row.UdpPort);

        var provision = DigitalConfigProvisioner.Provision(engine, row.RigName, values);
        if (provision.Outcome != DigitalProvisionOutcome.Success)
        {
            row.StatusText = "Could not write the engine config file.";
            return;
        }

        var result = await _digitalLauncher.LaunchAsync(engine, row.RigName);
        row.StatusText = result switch
        {
            DigitalLaunchResult.Success => string.Empty,
            DigitalLaunchResult.AlreadyRunning => "Already running.",
            DigitalLaunchResult.ExeNotFound => $"{ActiveEngineLabel} not found. Set the Path on the Config tab.",
            DigitalLaunchResult.InvalidRigName => "Invalid rig name.",
            _ => "Failed to start the engine.",
        };

        RefreshDigitalRunningStates();
    }

    [RelayCommand]
    private void StopDigitalForSlice(DigitalOperatingRowViewModel? row)
    {
        if (row is null)
            return;

        _digitalLauncher.Stop(row.RigName);
        row.StatusText = string.Empty;
        RefreshDigitalRunningStates();
    }

    private void OnConnectionStateChanged(bool connected)
    {
        UIPost(() =>
        {
            IsConnected = connected;
            OnPropertyChanged(nameof(ConnectTargetHeaderColor));
            OnPropertyChanged(nameof(ConnectTargetHeaderText));
            if (connected)
            {
                OwnClientStation = _connection.OwnClientStation;
                OnPropertyChanged(nameof(OwnClientStation));
                EnsureSelectedControlStation();

                foreach (var p in _connection.Panadapters)
                    AddPan(p);
                foreach (var s in _connection.Slices)
                    AddSlice(s);
                foreach (var d in _connection.DaxIQStreams)
                    OnDaxIQStreamAdded(d);

                _radioAvgDaxKbps = _connection.AvgDAXKbps;
                UpdateDisplayedDaxKbps();
                ApplyNetworkStatus(_connection.NetworkStatus);
                AddStreamerStatus($"Connected to {_connection.ConnectedModel}.");
                AddStreamerStatus($"Control station: {SelectedControlStation}");
                LogGuiClientsSnapshot(_connection.GuiClients);
            }
            else
            {
                // Stop every external app on disconnect: CW Skimmer and the
                // digital engines (WSJT-X / JTDX). Stop() is a no-op when the
                // family isn't running, so calling both is safe (issue #28).
                _launcher.Stop();
                _digitalLauncher.Stop();
                OwnClientStation = string.Empty;
                OnPropertyChanged(nameof(OwnClientStation));
                SetSelectedControlStation(string.Empty);
                ClientGroups.Clear();
                DigitalSlices.Clear();
                DaxIQStreams.Clear();
                _radioAvgDaxKbps = 0;
                AvgDAXKbps = 0;
                DaxStreamingSummary = "DAX Streaming : 0.0 Mbps (0 kbps)";
                ResetDisplayedNetworkStatus();
                _lastCwDevicePreviewByChannel.Clear();
                _lastRitStateBySlice.Clear();
                _lastLoggedPanSyncByChannel.Clear();
                lock (_syncDampenGate)
                {
                    _lastOutboundQsyByChannel.Clear();
                    _lastInboundClickByChannel.Clear();
                    foreach (var cts in _streamRemovedDebounceCtsByChannel.Values)
                    {
                        try { cts.Cancel(); } catch { }
                        cts.Dispose();
                    }
                    _streamRemovedDebounceCtsByChannel.Clear();
                }
                _ritStatusEmitter.Clear();
                _lastLoggedGuiClientsKey = string.Empty;

                // Losing the radio drops out of the active mode back to mode
                // selection (only Launch + Help show), since neither mode can
                // operate without a radio. LastMode is preserved (issue #28).
                if (ActiveMode is not null)
                {
                    ActiveMode = null;
                    SelectedTabIndex = 0;   // Launch tab
                }

                AddStreamerStatus("Disconnected.");
            }
        });
    }

    // ── Audio-index change detection (issue #38) ──────────────────────────────

    /// <summary>
    /// Channels we've already run change-detection for in this session. Detection
    /// fires once per channel per session; subsequent stream events for the same
    /// channel skip the probe so the operator doesn't see duplicate notices.
    /// </summary>
    private readonly HashSet<int> _audioIndexChangeDetectionDoneByChannel = new();

    /// <summary>
    /// Set when at least one channel reported a change this session, used to
    /// gate the one-shot "rerun the wizard" dialog so it fires only once even
    /// if multiple channels shift.
    /// </summary>
    private bool _audioIndexChangeDialogShownThisSession;

    /// <summary>
    /// Raised by the ViewModel when one or more channels' audio index baselines
    /// have shifted since the previous session. Carries summary lines suitable
    /// for the dialog body. MainWindow subscribes and shows the dialog with an
    /// "Open Set Up Wizard now" affordance.
    /// </summary>
    public event Action<IReadOnlyList<string>>? AudioIndexChangesDetected;

    private void RunAudioIndexChangeDetection(int daxIqChannel)
    {
        if (daxIqChannel <= 0 || daxIqChannel > 4) return;
        if (!_audioIndexChangeDetectionDoneByChannel.Add(daxIqChannel)) return;

        var result = AudioIndexChangeDetection.Probe(daxIqChannel, _deviceFinder, _settings);

        // Skip the channel entirely when the probe failed (DAX not exposing
        // this channel yet); we'll retry the next session.
        if (result.ProbeUnavailable)
        {
            _audioIndexChangeDetectionDoneByChannel.Remove(daxIqChannel);
            return;
        }

        if (result.WasFirstRun)
        {
            AudioIndexChangeDetection.CommitBaseline(_settings, result);
            AddStreamerStatus(
                $"Audio index baseline recorded for ch {daxIqChannel}: MME={result.CurrentMme}.");
            return;
        }

        if (!result.AnyChanged)
            return;

        var changeLine = $"MME was {result.PriorMme}, now {result.CurrentMme}";

        AddStreamerStatus(
            $"ch {daxIqChannel}: Audio Device Numbers may have changed: {changeLine}. "
            + "Please rerun CW Skimmer Config Setup Wizard.");

        AudioIndexChangeDetection.CommitBaseline(_settings, result);

        if (!_audioIndexChangeDialogShownThisSession)
        {
            _audioIndexChangeDialogShownThisSession = true;
            var summary = new List<string> { $"ch {daxIqChannel}: {changeLine}" };
            AudioIndexChangesDetected?.Invoke(summary);
        }
    }

    /// <summary>
    /// Stops every running CW Skimmer instance. Used as a convenience hook by
    /// the audio-index-change dialog so the operator can click straight through
    /// to the Set Up Wizard (which refuses to run while Skimmer is up) without
    /// having to stop channels manually first.
    /// </summary>
    public void StopAllCwSkimmerInstances()
    {
        if (IsCwSkimmerRunning)
            _launcher.Stop();
    }

    // ── GUI client snapshot logging (DAX-bound-radio check) ───────────────────

    private string _lastLoggedGuiClientsKey = string.Empty;

    private void OnGuiClientsChanged(IReadOnlyList<GuiClientInfo> clients)
    {
        if (!IsConnected) return;
        LogGuiClientsSnapshot(clients);

        // Bug fix 2026-06-04 (issue #45): when the GUI client we were controlling
        // (Maestro/SmartSDR station) disconnects while the radio TCP session stays
        // up, FlexLib correctly keeps Radio.Connected = true, so the old code left
        // the header pinned to the now-absent station ("connected to the radio with
        // no client"). RadioRemoved cannot fix this (the radio is still present), so
        // detect the loss of our control station here. Reported by a betatester on
        // v0.1.19b.
        //
        // The "(unknown)" placeholder and an empty selection are never real GUI
        // stations, so they are not tracked. Disconnect is only declared after the
        // station has been seen present this session (_controlStationSeen): a
        // partial or initial snapshot during connect must not raise a false loss.
        if (string.IsNullOrWhiteSpace(SelectedControlStation) ||
            string.Equals(SelectedControlStation, RadioConnectTarget.UnknownStation, StringComparison.OrdinalIgnoreCase))
            return;

        if (clients.Any(c => string.Equals(c.Station, SelectedControlStation, StringComparison.OrdinalIgnoreCase)))
        {
            _controlStationSeen = true;
            return;
        }

        if (!_controlStationSeen)
            return;

        AddStreamerStatus($"Control station '{SelectedControlStation}' disconnected.");
        // Re-resolve only from stations still present in THIS snapshot, not from
        // _connection.Slices: a GUI-client removal can arrive before the matching
        // pan/slice removals, so the slice list may still hold the just-departed
        // station and EnsureSelectedControlStation would re-pin it. Empty -> the
        // header falls back to "Unknown Station".
        var replacement = clients
            .Select(c => c.Station)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;
        SetSelectedControlStation(replacement);
    }

    private void LogGuiClientsSnapshot(IReadOnlyList<GuiClientInfo> clients)
    {
        var key = string.Join("|",
            clients.OrderBy(c => c.ClientHandle).Select(c => $"{c.ClientHandle:X}:{c.Program}:{c.Station}"));
        if (key == _lastLoggedGuiClientsKey) return;
        _lastLoggedGuiClientsKey = key;

        var list = clients.Count == 0
            ? "(none reported)"
            : string.Join(", ", clients.Select(c => c.DisplayLabel));
        AddStreamerStatus($"Clients on {_connection.ConnectedModel ?? "radio"}: {list}");
    }

    // ── Issue #39: DAX-IQ channel collision detection ─────────────────────────

    /// <summary>
    /// Raised when the workflow service needs the operator to confirm DAX-the-app's
    /// station selection (multi-station ch collision). The handler must show a
    /// modal dialog and complete the TaskCompletionSource with the operator's choice.
    /// </summary>
    public event Func<DaxStationConfirmRequest, Task<DaxStationConfirmResult>>? DaxStationConfirmRequested;

    Task<DaxStationConfirmResult> IDaxStationConfirmer.ConfirmAsync(int daxIqChannel, string ownStation, string otherStation)
    {
        var handler = DaxStationConfirmRequested;
        if (handler is null)
            return Task.FromResult(DaxStationConfirmResult.Cancel);

        return handler(new DaxStationConfirmRequest(daxIqChannel, ownStation, otherStation));
    }

    private readonly Dictionary<int, string> _lastLoggedDaxCollisionByChannel = new();

    private void EvaluateDaxChannelCollisions()
    {
        if (!IsConnected) return;
        var ownStation = _connection.OwnClientStation;
        if (string.IsNullOrWhiteSpace(ownStation)) return;

        var ourChannels = _connection.Panadapters
            .Where(p => p.DAXIQChannel > 0 &&
                        string.Equals(p.ClientStation, ownStation, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.DAXIQChannel)
            .Distinct();

        foreach (var channel in ourChannels)
        {
            var foreign = DaxChannelOwnership.GetForeignOwners(_connection.Panadapters, channel, ownStation);
            var key = foreign.Count == 0 ? string.Empty : string.Join(",", foreign);

            _lastLoggedDaxCollisionByChannel.TryGetValue(channel, out var lastKey);
            if (key == lastKey) continue;

            _lastLoggedDaxCollisionByChannel[channel] = key;
            if (foreign.Count == 0) continue;

            AddStreamerStatus(
                $"DAX-IQ ch {channel} also assigned to {foreign[0]}. In the SmartSDR DAX application, select {ownStation} to launch CW Skimmer properly.");
        }
    }

    // ── DAX-IQ stream tracking ────────────────────────────────────────────────

    private void OnDaxIQStreamAdded(DaxIQStreamInfo stream)
    {
        // SmartSDR 4.2.x may transiently remove then re-add the stream during reconfiguration.
        // Cancel any pending debounced stop so the skimmer keeps running.
        CancelStreamRemovedDebounce(stream.DAXIQChannel);

        var normalized = NormalizeStreamForPan(stream);
        if (DaxIQStreams.All(x => x.DAXIQChannel != normalized.DAXIQChannel))
            DaxIQStreams.Add(normalized);
        else
            ReplaceDaxStream(normalized);

        UpdateDisplayedDaxKbps();
        RefreshAllPanStreamSummaries();
        RefreshSliceSkimmerStates();
        RefreshCwSkimmerDeviceInfo(normalized.DAXIQChannel);
        RunAudioIndexChangeDetection(normalized.DAXIQChannel);
    }

    private void OnDaxIQStreamRemoved(DaxIQStreamInfo stream)
    {
        // Debounce the stop: SmartSDR 4.2.x can transiently remove/re-add the IQ stream
        // (e.g., during panadapter moves or after QSY).  If it reappears within the grace
        // period, OnDaxIQStreamAdded cancels the pending stop.
        var channel = stream.DAXIQChannel;
        var cts = ReplaceStreamRemovedDebounceCts(channel);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DaxStreamRemovedGracePeriod, cts.Token);
                UIPost(() => StopSkimmerIfRunningForDisabledChannel(channel, "DAX-IQ stream removed"));
            }
            catch (OperationCanceledException) { }
        });

        var m = DaxIQStreams.FirstOrDefault(x => x.DAXIQChannel == stream.DAXIQChannel);
        if (m is not null) DaxIQStreams.Remove(m);

        UpdateDisplayedDaxKbps();
        RefreshAllPanStreamSummaries();
        RefreshSliceSkimmerStates();
    }

    private void OnDaxIQStreamUpdated(DaxIQStreamInfo stream)
    {
        // IsActive=false means another DAX client (CW Skimmer via WDM) is the active
        // consumer — this is the expected state while the skimmer is running.  Do not stop.

        var normalized = NormalizeStreamForPan(stream);
        ReplaceDaxStream(normalized);
        UpdateDisplayedDaxKbps();
        RefreshAllPanStreamSummaries();
        RefreshSliceSkimmerStates();
        RefreshCwSkimmerDeviceInfo(normalized.DAXIQChannel);
        RunAudioIndexChangeDetection(normalized.DAXIQChannel);
        TrySyncSkimmerForPanChange(
            normalized.DAXIQChannel,
            normalized.ClientHandle,
            normalized.CenterFreqMHz,
            normalized.SampleRate,
            "DAX stream update");
    }

    private void RefreshCwSkimmerDeviceInfo(int daxIqChannel)
    {
        var preview = _launcher.PreviewDevices(daxIqChannel);
        var status = preview is null
            ? $"CW device map ch {daxIqChannel}: DAX audio devices not found in WinMM yet."
            : $"CW device map ch {daxIqChannel}: Signal {preview.Value.SignalDevice} [idx {preview.Value.SignalIdx}]  |  Audio {preview.Value.AudioDevice} [idx {preview.Value.AudioIdx}]";

        if (_lastCwDevicePreviewByChannel.TryGetValue(daxIqChannel, out var existing) && existing == status)
            return;

        _lastCwDevicePreviewByChannel[daxIqChannel] = status;
        AddSkimmerStatus(status);
    }

    // ── Client / pan / slice grouping ─────────────────────────────────────────

    private void AddPan(PanadapterInfo pan)
    {
        var group = GetOrCreateClientGroup(pan.ClientStation);
        if (group.Panadapters.All(p => p.Pan.StreamId != pan.StreamId))
        {
            var panGroup = new PanSliceGroup(pan);
            UpdatePanStreamSummary(panGroup);
            group.Panadapters.Add(panGroup);
        }
        RefreshDaxStreamPanBindings();
        LaunchCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
        StopCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(VisibleClientGroups));
        EvaluateDaxChannelCollisions();
    }

    private void RemovePan(PanadapterInfo pan)
    {
        var removedChannel = pan.DAXIQChannel;

        var group = ClientGroups.FirstOrDefault(g => g.Station == pan.ClientStation);
        if (group is null) return;
        var entry = group.Panadapters.FirstOrDefault(p => p.Pan.StreamId == pan.StreamId);
        if (entry is not null) group.Panadapters.Remove(entry);
        if (group.Panadapters.Count == 0) ClientGroups.Remove(group);

        if (removedChannel > 0 &&
            !_connection.Panadapters.Any(p => p.DAXIQChannel == removedChannel && p.StreamId != pan.StreamId))
        {
            StopSkimmerIfRunningForDisabledChannel(removedChannel, "panadapter closed");
        }

        RefreshDaxStreamPanBindings();
        LaunchCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
        StopCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(VisibleClientGroups));
        EvaluateDaxChannelCollisions();
    }

    private void UpdatePan(PanadapterInfo pan)
    {
        var group = ClientGroups.FirstOrDefault(g => g.Station == pan.ClientStation);
        if (group is null) return;
        var entry = group.Panadapters.FirstOrDefault(p => p.Pan.StreamId == pan.StreamId);
        if (entry is not null)
        {
            var oldChannel = entry.Pan.DAXIQChannel;
            entry.Pan = pan;
            UpdatePanStreamSummary(entry);
            if (oldChannel != pan.DAXIQChannel)
            {
                if (oldChannel > 0)
                    StopSkimmerIfRunningForDisabledChannel(oldChannel, $"pan reassigned from ch {oldChannel} to ch {pan.DAXIQChannel}");

                var running = _launcher.IsChannelRunning(pan.DAXIQChannel);
                foreach (var sliceVm in entry.Slices)
                {
                    sliceVm.UpdateDaxIqChannel(pan.DAXIQChannel);
                    sliceVm.IsSkimmerRunning = running;
                }
            }
        }
        RefreshDaxStreamPanBindings();
        LaunchCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
        StopCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(VisibleClientGroups));

        var sampleRate = DaxIQStreams.FirstOrDefault(s => s.DAXIQChannel == pan.DAXIQChannel)?.SampleRate ?? 48_000;
        TrySyncSkimmerForPanChange(
            pan.DAXIQChannel,
            pan.ClientHandle,
            pan.CenterFreqMHz,
            sampleRate,
            "Panadapter update");
        EvaluateDaxChannelCollisions();
    }

    private void AddSlice(SliceInfo slice)
    {
        // Digital rows are slice-audio-centric and pan-independent, so sync
        // before the CW pan-grouping (which early-returns if no pan yet).
        SyncDigitalSliceRow(slice);

        var group = GetOrCreateClientGroup(slice.ClientStation);
        var panEntry = group.Panadapters.FirstOrDefault(p => p.Pan.StreamId == slice.PanadapterStreamId);
        if (panEntry is null) return;
        if (panEntry.Slices.All(s => s.Slice.Letter != slice.Letter))
        {
            var channel = panEntry.Pan.DAXIQChannel;
            var vm = new SliceViewModel(slice, channel)
            {
                IsSkimmerRunning = _launcher.IsChannelRunning(channel)
            };
            panEntry.Slices.Add(vm);
        }

        LaunchCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
        StopCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(VisibleClientGroups));
    }

    private void RemoveSlice(SliceInfo slice)
    {
        RemoveDigitalSliceRow(slice);

        var group = ClientGroups.FirstOrDefault(g => g.Station == slice.ClientStation);
        if (group is null) return;
        var removed = false;
        foreach (var panEntry in group.Panadapters)
        {
            var match = panEntry.Slices.FirstOrDefault(s => s.Slice.Letter == slice.Letter);
            if (match is null) continue;
            panEntry.Slices.Remove(match);
            removed = true;
            break;
        }
        if (removed)
        {
            LaunchCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
            StopCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(VisibleClientGroups));
        }
    }

    private void UpdateSlice(SliceInfo slice)
    {
        SyncDigitalSliceRow(slice);

        var group = ClientGroups.FirstOrDefault(g => g.Station == slice.ClientStation);
        if (group is null) return;
        foreach (var panEntry in group.Panadapters)
        {
            var vm = panEntry.Slices.FirstOrDefault(s => s.Slice.Letter == slice.Letter);
            if (vm is not null) { vm.Update(slice); break; }
        }

        TrySyncSliceToSkimmer(slice);
    }

    private ClientGroup GetOrCreateClientGroup(string station)
    {
        var existing = ClientGroups.FirstOrDefault(g => g.Station == station);
        if (existing is not null) return existing;
        var newGroup = new ClientGroup(station);
        ClientGroups.Add(newGroup);
        OnPropertyChanged(nameof(VisibleClientGroups));
        return newGroup;
    }

    private void RefreshAllPanStreamSummaries()
    {
        foreach (var group in ClientGroups)
        foreach (var panGroup in group.Panadapters)
            UpdatePanStreamSummary(panGroup);
    }

    private void UpdatePanStreamSummary(PanSliceGroup panGroup)
    {
        int ch = panGroup.Pan.DAXIQChannel;
        if (ch <= 0)
        {
            panGroup.StreamSummary = "No DAX-IQ channel assigned.";
            return;
        }

        // Prefer the stream owned by this pan's station (issue #39, multi-station
        // same-channel), but fall back to any stream on the channel for the
        // state readback: the DAX-IQ stream is usually owned by a non-GUI client
        // (the DAX app or our own connection), so the strict handle match misses
        // and the row otherwise showed a stale "Off" regardless of streaming
        // (reported 2026-06-11). Display only; the sync path attributes by the
        // controlled-station pan/slice, not this lookup, and is unaffected.
        var stream = DaxIQStreams.FirstOrDefault(s =>
                s.DAXIQChannel == ch && s.ClientHandle == panGroup.Pan.ClientHandle)
            ?? DaxIQStreams.FirstOrDefault(s => s.DAXIQChannel == ch);

        var rateKHz = (stream?.SampleRate ?? 48_000) / 1000;

        // Match SmartSDR's DAX terminology: "Streaming" when the stream is the
        // active consumer, else "Ready" (no stream, or present but not active).
        var state = stream is { IsActive: true } ? "Streaming" : "Ready";

        // "DAX-IQ <ch>" identifies the channel; then center frequency (from the
        // pan) and sample rate with no labels, then the DAX state.
        panGroup.StreamSummary = $"DAX-IQ {ch}  {panGroup.Pan.CenterFreqMHz:F6} MHz  {rateKHz} kHz  {state}";
    }

    private void RefreshDaxStreamPanBindings()
    {
        for (int i = 0; i < DaxIQStreams.Count; i++)
            DaxIQStreams[i] = NormalizeStreamForPan(DaxIQStreams[i]);
    }

    private DaxIQStreamInfo NormalizeStreamForPan(DaxIQStreamInfo stream)
    {
        // Bug fix 2026-05-18 (issue #39): join pan by (channel, ClientHandle)
        // so a stream owned by station A is never rebound to station B's pan
        // when both stations have the same channel assigned.
        var pan = _connection.Panadapters.FirstOrDefault(p =>
            p.DAXIQChannel == stream.DAXIQChannel && p.ClientHandle == stream.ClientHandle);
        return pan is null
            ? stream with
            {
                CenterFreqMHz = 0,
                IsSkimmerRunning = _launcher.IsChannelRunning(stream.DAXIQChannel)
            }
            : stream with
            {
                CenterFreqMHz = pan.CenterFreqMHz,
                IsSkimmerRunning = _launcher.IsChannelRunning(stream.DAXIQChannel)
            };
    }

    private void ReplaceDaxStream(DaxIQStreamInfo stream)
    {
        var existing = DaxIQStreams.FirstOrDefault(x => x.DAXIQChannel == stream.DAXIQChannel);
        if (existing is null)
        {
            DaxIQStreams.Add(stream);
            return;
        }

        var idx = DaxIQStreams.IndexOf(existing);
        DaxIQStreams[idx] = stream;
    }

    private void UpdateDisplayedDaxKbps()
    {
        if (_radioAvgDaxKbps > 0)
        {
            AvgDAXKbps = _radioAvgDaxKbps;
        }
        else
        {
            // Fallback estimate for IQ-only workflows when radio aggregate remains zero.
            // SmartSDR's displayed IQ stream rate includes transport overhead, so this
            // is slightly above the raw 64 bits/sample payload rate.
            const double iqBitsPerSampleWithOverhead = 64.6666667;
            AvgDAXKbps = DaxIQStreams
                .Where(s => s.IsActive)
                .Sum(s => (int)Math.Round((s.SampleRate * iqBitsPerSampleWithOverhead) / 1000.0));
        }

        DaxStreamingSummary = $"DAX Streaming : {AvgDAXKbps / 1000.0:F1} Mbps ({AvgDAXKbps} kbps)";
    }

    // ── CW Skimmer ────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanLaunchCwSkimmerForChannel))]
    private async Task LaunchCwSkimmerForChannelAsync(DaxIQStreamInfo? stream)
    {
        await _cwSkimmerWorkflow.LaunchForChannelAsync(stream, SelectedControlStation, CwSkimmerExePath, AddSkimmerStatus);
        UpdateTelnetIniSummary();
        RefreshDaxStreamPanBindings();
        RefreshSliceSkimmerStates();
    }

    private bool CanLaunchCwSkimmerForChannel(DaxIQStreamInfo? stream) =>
        _cwSkimmerWorkflow.CanLaunch(stream, CwSkimmerExePath);

    [RelayCommand(CanExecute = nameof(CanLaunchCwSkimmerForSlice))]
    private async Task LaunchCwSkimmerForSliceAsync(SliceViewModel? sliceVm)
    {
        var stream = ResolveSliceLaunchStream(sliceVm);
        if (stream is null)
        {
            AddSkimmerStatus("Unable to launch: no DAX-IQ stream for this slice.");
            return;
        }

        // Slice-launch path: use the slice's own ClientStation so the pan/slice
        // lookup in LaunchForChannelAsync stays consistent with what the operator
        // clicked, even if SelectedControlStation has been overridden elsewhere.
        var station = sliceVm?.Slice.ClientStation ?? SelectedControlStation;
        await _cwSkimmerWorkflow.LaunchForChannelAsync(stream, station, CwSkimmerExePath, AddSkimmerStatus);
        UpdateTelnetIniSummary();
        RefreshDaxStreamPanBindings();
        RefreshSliceSkimmerStates();
    }

    private bool CanLaunchCwSkimmerForSlice(SliceViewModel? sliceVm)
    {
        var stream = ResolveSliceLaunchStream(sliceVm);
        return _cwSkimmerWorkflow.CanLaunch(stream, CwSkimmerExePath);
    }

    [RelayCommand(CanExecute = nameof(CanStopCwSkimmerForChannel))]
    private void StopCwSkimmerForChannel(DaxIQStreamInfo? stream)
    {
        _cwSkimmerWorkflow.StopForChannel(stream, AddSkimmerStatus);
        UpdateTelnetIniSummary();
        LaunchCwSkimmerForChannelCommand.NotifyCanExecuteChanged();
        StopCwSkimmerForChannelCommand.NotifyCanExecuteChanged();
        RefreshDaxStreamPanBindings();
        RefreshSliceSkimmerStates();
    }

    private bool CanStopCwSkimmerForChannel(DaxIQStreamInfo? stream) =>
        _cwSkimmerWorkflow.CanStop(stream);

    [RelayCommand(CanExecute = nameof(CanStopCwSkimmerForSlice))]
    private void StopCwSkimmerForSlice(SliceViewModel? sliceVm)
    {
        var stream = ResolveSliceLaunchStream(sliceVm);
        if (stream is null)
        {
            AddSkimmerStatus("Unable to stop: no DAX-IQ stream for this slice.");
            return;
        }

        StopCwSkimmerForChannel(stream);
    }

    private bool CanStopCwSkimmerForSlice(SliceViewModel? sliceVm)
    {
        var stream = ResolveSliceLaunchStream(sliceVm);
        return _cwSkimmerWorkflow.CanStop(stream);
    }

    [RelayCommand(CanExecute = nameof(CanResetNetworkStatus))]
    private void ResetNetworkStatus()
    {
        _connection.ResetNetworkStatus();
        ApplyNetworkStatus(_connection.NetworkStatus);
        AddStreamerStatus("Network status reset.");
    }

    private bool CanResetNetworkStatus() => IsConnected;

    [RelayCommand]
    private void ResetCwSkimmerChannelConfig()
    {
        if (_launcher.IsRunning)
        {
            AddStreamerStatus("Stop CW Skimmer before resetting channel INI files.");
            return;
        }

        var iniDir = ResolveCwSkimmerIniDir();
        var deleted = 0;

        for (var channel = 1; channel <= 4; channel++)
        {
            var path = Path.Combine(iniDir, $"CwSkimmer-ch{channel}.ini");
            if (!File.Exists(path))
                continue;

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex)
            {
                AddStreamerStatus($"Failed to delete channel INI ch {channel}: {ex.Message}");
            }
        }

        UpdateTelnetIniSummary();
        AddStreamerStatus(
            deleted > 0
                ? $"Reset CW Skimmer channel INI files ({deleted} removed). Next launch will seed from manual IQ1 baseline."
                : "No channel INI files found to reset.");
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        await CheckForUpdatesCoreAsync("manual", _updateCheckLoopCts.Token);
    }

    [RelayCommand(CanExecute = nameof(CanOpenLatestRelease))]
    private void OpenLatestRelease()
    {
        if (!CanOpenLatestRelease())
            return;

        TryOpenPath(LatestReleaseUrl, "open latest release page");
    }

    private bool CanOpenLatestRelease() =>
        IsUpdateAvailable && !string.IsNullOrWhiteSpace(LatestReleaseUrl);

    private DaxIQStreamInfo? ResolveSliceLaunchStream(SliceViewModel? sliceVm)
    {
        if (sliceVm is null)
            return null;

        var pan = _connection.Panadapters.FirstOrDefault(p => p.StreamId == sliceVm.Slice.PanadapterStreamId);
        if (pan is null || pan.DAXIQChannel <= 0)
            return null;

        var channel = pan.DAXIQChannel;
        // Bug fix 2026-05-18 (issue #39): filter by (channel, ClientHandle) so
        // a foreign station's stream on the same channel cannot satisfy a
        // launch resolution for our slice.
        var stream = DaxIQStreams.FirstOrDefault(s =>
                s.DAXIQChannel == channel && s.ClientHandle == pan.ClientHandle)
            ?? _connection.DaxIQStreams.FirstOrDefault(s =>
                s.DAXIQChannel == channel && s.ClientHandle == pan.ClientHandle);

        if (stream is not null)
        {
            var centerMHz = stream.CenterFreqMHz > 0 ? stream.CenterFreqMHz : pan.CenterFreqMHz;
            return stream with
            {
                CenterFreqMHz = centerMHz,
                IsSkimmerRunning = _launcher.IsChannelRunning(channel)
            };
        }

        // Fallback for command enable/launch when pan has a valid DAX channel
        // but stream metadata has not populated yet.
        return new DaxIQStreamInfo(
            DAXIQChannel: channel,
            SampleRate: 48000,
            IsActive: false,
            CenterFreqMHz: pan.CenterFreqMHz,
            ClientHandle: pan.ClientHandle)
        {
            IsSkimmerRunning = _launcher.IsChannelRunning(channel)
        };
    }

    private void RefreshSliceSkimmerStates()
    {
        foreach (var group in ClientGroups)
        {
            foreach (var pan in group.Panadapters)
            {
                var channel = pan.Pan.DAXIQChannel;
                var running = _launcher.IsChannelRunning(channel);
                foreach (var slice in pan.Slices)
                {
                    slice.UpdateDaxIqChannel(channel);
                    slice.IsSkimmerRunning = running;
                }
            }
        }

        LaunchCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
        StopCwSkimmerForSliceCommand.NotifyCanExecuteChanged();
    }

    private void StopSkimmerIfRunningForDisabledChannel(int daxIqChannel, string reason)
    {
        if (daxIqChannel <= 0 || !_launcher.IsChannelRunning(daxIqChannel))
            return;

        _launcher.Stop(daxIqChannel);
        AddSkimmerStatus($"Stopped CW Skimmer on channel {daxIqChannel}: {reason}.");
    }

    [RelayCommand(CanExecute = nameof(CanOpenStreamerIniCh1File))]
    private void OpenStreamerIniCh1File()
    {
        if (!CanOpenStreamerIniCh1File())
        {
            UpdateTelnetIniSummary();
            AddStreamerStatus("Streamer INI file for ch 1 not found yet.");
            return;
        }

        TryOpenPath(StreamerIniCh1Path, "open streamer INI file for ch 1");
    }

    private bool CanOpenStreamerIniCh1File() =>
        HasStreamerIniCh1File &&
        !string.IsNullOrWhiteSpace(StreamerIniCh1Path);

    [RelayCommand(CanExecute = nameof(CanOpenStreamerIniCh2File))]
    private void OpenStreamerIniCh2File()
    {
        if (!CanOpenStreamerIniCh2File())
        {
            UpdateTelnetIniSummary();
            AddStreamerStatus("Streamer INI file for ch 2 not found yet.");
            return;
        }

        TryOpenPath(StreamerIniCh2Path, "open streamer INI file for ch 2");
    }

    private bool CanOpenStreamerIniCh2File() =>
        HasStreamerIniCh2File &&
        !string.IsNullOrWhiteSpace(StreamerIniCh2Path);

    [RelayCommand(CanExecute = nameof(CanOpenStreamerIniFolder))]
    private void OpenStreamerIniFolder()
    {
        if (!CanOpenStreamerIniFolder())
        {
            UpdateTelnetIniSummary();
            AddStreamerStatus("Streamer INI folder not found yet.");
            return;
        }

        TryOpenPath(StreamerIniFolderPath, "open streamer INI folder");
    }

    private bool CanOpenStreamerIniFolder() =>
        HasStreamerIniFolder &&
        !string.IsNullOrWhiteSpace(StreamerIniFolderPath);

    [RelayCommand(CanExecute = nameof(CanOpenLogsFolder))]
    private void OpenLogsFolder()
    {
        if (!CanOpenLogsFolder())
        {
            UpdateLogsFolderSummary();
            AddStreamerStatus("Logs folder not found yet.");
            return;
        }

        TryOpenPath(LogsFolderPath, "open logs folder");
    }

    private bool CanOpenLogsFolder() =>
        HasLogsFolder &&
        !string.IsNullOrWhiteSpace(LogsFolderPath);

    private async Task PublishSkimmerSpotAsync(CwSkimmerSpotInfo spot)
    {
        if (!IsConnected || !SpotForwardingEnabled || spot.FrequencyKhz <= 0 || string.IsNullOrWhiteSpace(spot.Callsign))
            return;

        var commentParts = new List<string>();
        if (spot.SignalDb.HasValue)
            commentParts.Add($"{spot.SignalDb.Value} dB");
        if (spot.SpeedWpm.HasValue)
            commentParts.Add($"{spot.SpeedWpm.Value} WPM");
        if (!string.IsNullOrWhiteSpace(spot.Comment))
            commentParts.Add(spot.Comment);

        var comment = commentParts.Count == 0 ? null : string.Join(" | ", commentParts);
        var sourceBaseCall = ResolveSourceBaseCall(spot.Spotter);
        var sourceIdentity = ResolveSpotSourceIdentity(sourceBaseCall);

        var radioSpot = new RadioSpotInfo(
            Callsign: spot.Callsign,
            RxFrequencyMHz: spot.FrequencyKhz / 1000.0,
            Source: sourceIdentity,
            SpotterCallsign: sourceBaseCall,
            Comment: comment,
            Mode: "CW",
            Color: NormalizeSpotColor(SpotColor),
            BackgroundColor: NormalizeSpotBackgroundColor(SpotBackgroundColor),
            LifetimeSeconds: Math.Max(30, SpotLifetimeSeconds));

        var payloadSummary =
            $"call={radioSpot.Callsign}, freq_mhz={radioSpot.RxFrequencyMHz:F6}, source={radioSpot.Source}, " +
            $"spotter_callsign={radioSpot.SpotterCallsign}, lifetime_seconds={radioSpot.LifetimeSeconds}";

        try
        {
            AppendSpotPayloadLog($"publish-attempt {payloadSummary}");
            await _connection.PublishSpotAsync(radioSpot);
            AppendSpotPayloadLog($"publish-success {payloadSummary}");
            UIPost(() => AddSkimmerStatus(
                $"Spot sent: {spot.Callsign} @ {(spot.FrequencyKhz / 1000.0):F6} MHz"));
        }
        catch (Exception ex)
        {
            AppendSpotPayloadLog($"publish-failed {payloadSummary}, error={ex.Message}");
            UIPost(() => AddSkimmerStatus($"Spot publish failed: {ex.Message}"));
        }
    }

    private void StartUpdateChecks()
    {
        _ = Task.Run(() => CheckForUpdatesCoreAsync("startup", _updateCheckLoopCts.Token));

        if (!_settings.UpdateAutoCheckEnabled)
            return;

        _updateCheckLoopTask = Task.Run(async () =>
        {
            try
            {
                var intervalMinutes = Math.Max(5, _settings.UpdateCheckIntervalMinutes);
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

                while (await timer.WaitForNextTickAsync(_updateCheckLoopCts.Token))
                    await CheckForUpdatesCoreAsync("automatic", _updateCheckLoopCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown path.
            }
        });
    }

    private async Task CheckForUpdatesCoreAsync(string source, CancellationToken ct)
    {
        if (!await _updateCheckLock.WaitAsync(0, ct))
            return;

        try
        {
            var result = await _releaseUpdateService.CheckForUpdateAsync(AppReleaseTagForUpdateChecks, ct);
            _settings.UpdateLastCheckedUtc = DateTime.UtcNow;

            UIPost(() =>
            {
                UpdateStatusText = result.StatusMessage;

                if (!result.Succeeded)
                {
                    if (string.Equals(source, "manual", StringComparison.OrdinalIgnoreCase))
                        AddStreamerStatus($"Update check failed: {result.StatusMessage}");
                    return;
                }

                IsUpdateAvailable = result.IsUpdateAvailable;
                LatestAvailableTag = result.LatestTag;
                LatestReleaseUrl = result.LatestReleaseUrl;

                if (result.IsUpdateAvailable &&
                    !string.Equals(_lastAnnouncedUpdateTag, result.LatestTag, StringComparison.OrdinalIgnoreCase))
                {
                    _lastAnnouncedUpdateTag = result.LatestTag;
                    AddStreamerStatus($"Update available: {result.LatestTag}. Open Help tab for details.");
                }
                else if (!result.IsUpdateAvailable &&
                         string.Equals(source, "manual", StringComparison.OrdinalIgnoreCase))
                {
                    AddStreamerStatus($"No update found. {result.StatusMessage}");
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation path.
        }
        finally
        {
            _updateCheckLock.Release();
        }
    }

    // ── Shutdown ──────────────────────────────────────────────────────────────

    public void Shutdown()
    {
        _updateCheckLoopCts.Cancel();
        try { _updateCheckLoopTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }

        if (IsCwSkimmerRunning) _launcher.Stop();
        if (IsConnected) _connection.Disconnect();
        _ritStatusEmitter.Dispose();
        _updateCheckLock.Dispose();
        _updateCheckLoopCts.Dispose();
        _discovery.Stop();
    }

    partial void OnCwSkimmerExePathChanged(string value)
    {
        _settings.CwSkimmerExePath = value;
    }

    // ── Digital config persistence (issue #28) ────────────────────────────────

    partial void OnDigitalMyCallChanged(string value) => _settings.DigitalMyCall = value.Trim();
    partial void OnDigitalMyGridChanged(string value) => _settings.DigitalMyGrid = value.Trim();
    partial void OnDigitalRigChanged(string value) => _settings.DigitalRig = value.Trim();
    partial void OnWsjtXExePathChanged(string value) => _settings.WsjtXExePath = value;
    partial void OnJtdxExePathChanged(string value) => _settings.JtdxExePath = value;
    partial void OnWsjtZExePathChanged(string value) => _settings.WsjtZExePath = value;
    partial void OnDigitalEngineIndexChanged(int value) =>
        _settings.DigitalActiveEngine = value switch { 1 => "Jtdx", 2 => "WsjtZ", _ => "WsjtX" };
    /// <summary>
    /// Default slices to expose editable bindings for. FLEX-6600 has 4 slices
    /// (A-D); the Operating screen drives the actual slices.
    /// </summary>
    private const int DigitalSliceConfigCount = 4;

    private void LoadDigitalSliceConfigs()
    {
        DigitalSliceConfigs.Clear();

        for (var ordinal = 0; ordinal < DigitalSliceConfigCount; ordinal++)
        {
            var letter = ((char)('A' + ordinal)).ToString();
            // Use the persisted binding when present, else a recommended default
            // (Slice A = DAX RX 1 / CAT 60000 / UDP 2237, B = 2/60001/2238, ...).
            var saved = _settings.DigitalSliceBindings.Find(b =>
                string.Equals(b.SliceLetter, letter, StringComparison.OrdinalIgnoreCase));

            var row = saved is not null
                ? new DigitalSliceConfigViewModel(letter, saved.DaxRxChannel, saved.CatPort, saved.UdpPort)
                : new DigitalSliceConfigViewModel(letter, ordinal + 1, 60_000 + ordinal, 2_237 + ordinal);

            row.PropertyChanged += (_, _) => PersistDigitalSliceConfigs();
            DigitalSliceConfigs.Add(row);
        }

        // Persist the seeded defaults on first run so the saved list mirrors the UI.
        PersistDigitalSliceConfigs();
    }

    private void PersistDigitalSliceConfigs() =>
        _settings.DigitalSliceBindings = DigitalSliceConfigs.Select(r => r.ToBinding()).ToList();

    /// <summary>
    /// Builds the engine definition for <paramref name="engine"/> using the
    /// operator-configured exe path. The config root (for the harvester and
    /// provisioner) is fixed per engine; only the exe path is configurable.
    /// </summary>
    private DigitalEngineDefinition GetEngineDefinition(DigitalEngine engine) => engine switch
    {
        DigitalEngine.WsjtX => DigitalEngines.WsjtX(WsjtXExePath),
        DigitalEngine.Jtdx  => DigitalEngines.Jtdx(JtdxExePath),
        DigitalEngine.WsjtZ => DigitalEngines.WsjtZ(WsjtZExePath),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown digital engine."),
    };

    private void TryPrepopulateDigitalIdentity()
    {
        // Already have identity (entered or previously imported) — leave it.
        if (!string.IsNullOrWhiteSpace(DigitalMyCall))
            return;

        // Prefer the active engine's profile, then the other as a fallback.
        var other = ActiveDigitalEngine == DigitalEngine.Jtdx ? DigitalEngine.WsjtX : DigitalEngine.Jtdx;
        var profile = WsjtxProfileHarvester.HarvestFromDefault(GetEngineDefinition(ActiveDigitalEngine))
                   ?? WsjtxProfileHarvester.HarvestFromDefault(GetEngineDefinition(other));
        if (profile is null)
            return;

        if (!string.IsNullOrWhiteSpace(profile.MyCall)) DigitalMyCall = profile.MyCall;
        if (!string.IsNullOrWhiteSpace(profile.MyGrid)) DigitalMyGrid = profile.MyGrid;
        if (!string.IsNullOrWhiteSpace(profile.Rig)) DigitalRig = profile.Rig;
    }

    partial void OnCwSkimmerIniPathChanged(string value)
    {
        _settings.CwSkimmerIniPath = value;
    }

    partial void OnTelnetCallsignChanged(string value)
    {
        _settings.Callsign = value;
        UpdateTelnetIniSummary();
    }

    partial void OnConnectDelaySecondsChanged(int value)
    {
        _settings.ConnectDelaySeconds = Math.Max(0, value);
    }

    partial void OnLaunchDelaySecondsChanged(int value)
    {
        _settings.LaunchDelaySeconds = Math.Max(0, value);
    }

    partial void OnTelnetPortBaseChanged(int value)
    {
        _settings.TelnetPortBase = Math.Max(1, value);
        UpdateTelnetIniSummary();
    }

    partial void OnTelnetClusterEnabledChanged(bool value)
    {
        _settings.TelnetClusterEnabled = value;
        UpdateTelnetIniSummary();
    }

    partial void OnSpotForwardingEnabledChanged(bool value)
    {
        _settings.SpotForwardingEnabled = value;
    }

    partial void OnSpotLifetimeSecondsChanged(int value)
    {
        var normalized = Math.Max(30, value);
        if (normalized != value)
        {
            SpotLifetimeSeconds = normalized;
            return;
        }

        _settings.SpotLifetimeSeconds = normalized;
        if (!_isApplyingStartupSettings)
            AddStreamerStatus($"Spot persistence set to {normalized} seconds for next spots.");
    }

    partial void OnSpotColorChanged(string value)
    {
        var normalized = NormalizeSpotColor(value);
        _settings.SpotColor = normalized;
        UpdateSpotColorSelection(normalized);
    }

    partial void OnSpotBackgroundColorChanged(string value)
    {
        var normalized = NormalizeSpotBackgroundColor(value);
        _settings.SpotBackgroundColor = normalized;
        UpdateSpotBackgroundColorSelection(normalized);
    }

    partial void OnSpotSelectedColorOptionChanged(SpotColorOption? value)
    {
        if (value is null)
            return;

        if (!string.Equals(SpotColor, value.Hex, StringComparison.OrdinalIgnoreCase))
            SpotColor = value.Hex;
    }

    partial void OnSpotSelectedBackgroundColorOptionChanged(SpotColorOption? value)
    {
        if (value is null)
            return;

        if (!string.Equals(SpotBackgroundColor, value.Hex, StringComparison.OrdinalIgnoreCase))
            SpotBackgroundColor = value.Hex;
    }

    private void AddFooterStatus(string message)
    {
        LatestFooterEvent = message;
        FooterStatusText = _footerStatusBuffer.Add(message);
    }

    internal void AddStreamerStatus(string message)
    {
        AddFooterStatus($"[STREAMER] {message}");
        AppendStreamerLog(message, "[STREAMER]");
    }

    /// <summary>
    /// Emits a `[FLEX]`-prefixed diagnostic line into both the footer and the
    /// streamer log. Used for connect-path / object-lifecycle traces sourced
    /// from the FlexRadio layer (see <c>IRadioConnection.DiagnosticEvent</c>).
    /// Volume is bounded: fires per discrete event (connect, pan/slice/IQ
    /// add or remove, GUI client add or remove, disconnect trigger), not in
    /// steady state.
    /// </summary>
    internal void AddDiagnosticStatus(string message)
    {
        AddFooterStatus($"[FLEX] {message}");
        AppendStreamerLog(message, "[FLEX]");
    }

    private void AddSkimmerStatus(string message) => AddFooterStatus($"[SKIMMER] {message}");
    private void AddTelnetStatus(string message) => AddFooterStatus($"[TELNET] {message}");

    // Issue #28 (WSJT-X / JTDX setup-and-launch): dumps every Windows audio
    // endpoint (full DirectSound names + WinMM cross-reference) to
    // streamer-status.log so we can characterize how SmartSDR DAXv2 exposes
    // the DAX Audio RX/TX devices WSJT-X must select. Read-only: it does not
    // touch the radio, so it is safe to run during a live session.
    [RelayCommand]
    private void DumpAudioDevices()
    {
        var report = AudioEndpointDiagnostics.BuildReport(_deviceFinder);
        AppendStreamerLog(Environment.NewLine + report, "[AUDIO]");
        AddStreamerStatus("Audio endpoint diagnostic written to streamer-status.log (open the Logs folder to view or share it).");
    }

    private static void AppendStreamerLog(string message, string tag)
    {
        try
        {
            var logPath = ResolveStreamerLogPath();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {tag} {message}{Environment.NewLine}";

            lock (s_streamerLogSync)
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(logPath, line);
            }
        }
        catch
        {
            // Logging must not impact runtime behavior.
        }
    }

    private static void AppendSpotPayloadLog(string message)
    {
        try
        {
            var logPath = ResolveSpotPayloadLogPath();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [SPOT] {message}{Environment.NewLine}";

            lock (s_spotPayloadLogSync)
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(logPath, line);
            }
        }
        catch
        {
            // Logging must not impact runtime behavior.
        }
    }

    private static string ResolveStreamerLogPath()
    {
        var repoRoot = TryFindRepoRoot(new DirectoryInfo(AppContext.BaseDirectory))
            ?? TryFindRepoRoot(new DirectoryInfo(Environment.CurrentDirectory));

        if (repoRoot is not null)
            return Path.Combine(repoRoot.FullName, "artifacts", "logs", "streamer-status.log");

        return Path.Combine(AppDataPaths.Root, "artifacts", "logs", "streamer-status.log");
    }

    private static string ResolveSpotPayloadLogPath()
    {
        var repoRoot = TryFindRepoRoot(new DirectoryInfo(AppContext.BaseDirectory))
            ?? TryFindRepoRoot(new DirectoryInfo(Environment.CurrentDirectory));

        if (repoRoot is not null)
            return Path.Combine(repoRoot.FullName, "artifacts", "logs", "spot-publish.log");

        return Path.Combine(AppDataPaths.Root, "artifacts", "logs", "spot-publish.log");
    }

    private static string ResolveLogsFolderPath()
    {
        var streamerLogPath = ResolveStreamerLogPath();
        return Path.GetDirectoryName(streamerLogPath) ?? string.Empty;
    }

    private static string ResolveCwSkimmerIniDir()
    {
        var repoRoot = TryFindRepoRoot(new DirectoryInfo(AppContext.BaseDirectory))
            ?? TryFindRepoRoot(new DirectoryInfo(Environment.CurrentDirectory));

        if (repoRoot is not null)
            return Path.Combine(repoRoot.FullName, "artifacts", "cwskimmer", "ini");

        return Path.Combine(AppDataPaths.Root, "artifacts", "cwskimmer", "ini");
    }

    private static DirectoryInfo? TryFindRepoRoot(DirectoryInfo? start)
    {
        var current = start;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SmartSDRIQStreamer.csproj")) ||
                File.Exists(Path.Combine(current.FullName, "SmartSDRIQStreamer.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string NormalizeSpotColor(string color)
    {
        var trimmed = string.IsNullOrWhiteSpace(color)
            ? string.Empty
            : color.Trim().ToUpperInvariant();

        if (trimmed.Length == 9 && trimmed.StartsWith("#") &&
            trimmed.Skip(1).All(Uri.IsHexDigit))
        {
            return trimmed;
        }

        return "#FF00FFFF";
    }

    private static string NormalizeSpotBackgroundColor(string color)
    {
        var trimmed = string.IsNullOrWhiteSpace(color)
            ? string.Empty
            : color.Trim().ToUpperInvariant();

        if (trimmed.Length == 9 && trimmed.StartsWith("#") &&
            trimmed.Skip(1).All(Uri.IsHexDigit))
        {
            return trimmed;
        }

        return "#00000000";
    }

    private void UpdateSpotColorSelection(string colorHex)
    {
        var normalized = NormalizeSpotColor(colorHex);
        var match = SpotColorOptions.FirstOrDefault(c =>
            string.Equals(c.Hex, normalized, StringComparison.OrdinalIgnoreCase));

        match ??= SpotColorOptions.FirstOrDefault(c => c.Name == "Cyan");
        if (match is not null && !Equals(SpotSelectedColorOption, match))
            SpotSelectedColorOption = match;
    }

    private void UpdateSpotBackgroundColorSelection(string colorHex)
    {
        var normalized = NormalizeSpotBackgroundColor(colorHex);
        var match = SpotBackgroundColorOptions.FirstOrDefault(c =>
            string.Equals(c.Hex, normalized, StringComparison.OrdinalIgnoreCase));

        match ??= SpotBackgroundColorOptions.FirstOrDefault(c => c.Name == "Transparent");
        if (match is not null && !Equals(SpotSelectedBackgroundColorOption, match))
            SpotSelectedBackgroundColorOption = match;
    }

    private void UpdateTelnetIniSummary()
    {
        var iniDir = ResolveCwSkimmerIniDirPath();
        StreamerIniFolderPath = iniDir;
        HasStreamerIniFolder = Directory.Exists(iniDir);

        if (TryResolveStreamerIniFiles(out var iniFiles) && iniFiles.Count > 0)
        {
            var byChannel = iniFiles
                .Select(f => new { File = f, Channel = ExtractChannelFromIniFileName(f.Name) })
                .Where(x => x.Channel.HasValue)
                .GroupBy(x => x.Channel!.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(x => x.File.LastWriteTimeUtc).First().File);

            if (byChannel.TryGetValue(1, out var ch1))
            {
                StreamerIniCh1Path = ch1.FullName;
                StreamerIniCh1PathText = ch1.FullName;
                HasStreamerIniCh1File = true;
            }
            else
            {
                StreamerIniCh1Path = string.Empty;
                StreamerIniCh1PathText = "(not generated yet)";
                HasStreamerIniCh1File = false;
            }

            if (byChannel.TryGetValue(2, out var ch2))
            {
                StreamerIniCh2Path = ch2.FullName;
                StreamerIniCh2PathText = ch2.FullName;
                HasStreamerIniCh2File = true;
            }
            else
            {
                StreamerIniCh2Path = string.Empty;
                StreamerIniCh2PathText = "(not generated yet)";
                HasStreamerIniCh2File = false;
            }
            return;
        }

        StreamerIniCh1Path = string.Empty;
        HasStreamerIniCh1File = false;
        StreamerIniCh1PathText = "(not generated yet)";
        StreamerIniCh2Path = string.Empty;
        HasStreamerIniCh2File = false;
        StreamerIniCh2PathText = "(not generated yet)";
    }

    private void UpdateLogsFolderSummary()
    {
        var logsDir = ResolveLogsFolderPath();
        LogsFolderPath = logsDir;
        LogsFolderPathText = string.IsNullOrWhiteSpace(logsDir) ? "(not available)" : logsDir;
        HasLogsFolder = !string.IsNullOrWhiteSpace(logsDir) && Directory.Exists(logsDir);
    }

    private static bool TryResolveStreamerIniFiles(out IReadOnlyList<FileInfo> iniFiles)
    {
        iniFiles = [];

        try
        {
            var iniDir = ResolveCwSkimmerIniDirPath();
            if (!Directory.Exists(iniDir))
                return false;

            var files = new DirectoryInfo(iniDir)
                .GetFiles("CwSkimmer-ch*.ini", SearchOption.TopDirectoryOnly)
                .ToArray();

            if (files.Length == 0)
                return false;

            iniFiles = files;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int? ExtractChannelFromIniFileName(string fileName)
    {
        var match = Regex.Match(fileName, @"CwSkimmer-ch(?<ch>\d+)\.ini", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        return int.TryParse(match.Groups["ch"].Value, out var ch) ? ch : null;
    }

    private void TryOpenPath(string path, string targetDescription)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AddStreamerStatus($"Unable to {targetDescription}: {ex.Message}");
        }
    }

    private static string ResolveCwSkimmerIniDirPath()
    {
        var repoRoot = TryFindRepoRoot(new DirectoryInfo(AppContext.BaseDirectory))
            ?? TryFindRepoRoot(new DirectoryInfo(Environment.CurrentDirectory));

        if (repoRoot is not null)
            return Path.Combine(repoRoot.FullName, "artifacts", "cwskimmer", "ini");

        return Path.Combine(AppDataPaths.Root, "artifacts", "cwskimmer", "ini");
    }

    private static (string ReleaseTag, string CommitHash, string Display, string BuildDate) ResolveAppBuildInfo()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var releaseTag = ResolveReleaseTag(assembly, informationalVersion) ?? "dev";

        var commit = ExtractCommitHash(informationalVersion)
            ?? ExtractCommitHash(
                assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(x =>
                        string.Equals(x.Key, "SourceRevisionId", StringComparison.OrdinalIgnoreCase))
                    ?.Value)
            ?? "unknown";

        var displayTag = NormalizeVersionDisplayTag(releaseTag);
        var display = commit == "unknown"
            ? displayTag
            : $"{displayTag} ({commit})";
        var buildDate = ResolveBuildDate(assembly);
        return (releaseTag, commit, display, buildDate);
    }

    private static string NormalizeVersionDisplayTag(string releaseTag)
    {
        var trimmed = string.IsNullOrWhiteSpace(releaseTag) ? "dev" : releaseTag.Trim();
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"v{trimmed}";
    }

    private static string FormatReleaseForHelp(string releaseTag)
    {
        var trimmed = string.IsNullOrWhiteSpace(releaseTag) ? "dev" : releaseTag.Trim();
        if (trimmed.StartsWith("v.", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            return $"v.{trimmed[1..]}";
        return $"v.{trimmed}";
    }

    private static string? ResolveReleaseTag(Assembly assembly, string? informationalVersion)
    {
        // Prefer an explicit tag on HEAD when present (shipped builds: publish-release.ps1
        // packages from a tagged HEAD).
        var exactTag = TryGetGitTag(pointsAtHead: true);
        if (!string.IsNullOrWhiteSpace(exactTag))
            return exactTag;

        // Bug fix 2026-05-17: dotnet run was showing "v0.1.0" because the csproj
        // <Version> fallback ran before git describe. csproj <Version> is permanently
        // 0.1.0 (clean numeric default; MSBuild's condition evaluator OOMs on the
        // trailing 'b' of our release labels), so trusting it produced a meaningless
        // version for any dev build past the last tag. Try git describe first and tag
        // the result with -dev so it's obvious it isn't a shipped build.
        var latestTag = TryGetGitTag(pointsAtHead: false);
        if (!string.IsNullOrWhiteSpace(latestTag))
            return $"{latestTag}-dev";

        // Final fallback for non-git environments (shipped exe extracted from zip):
        // pull the version out of InformationalVersion, which publish-release.ps1
        // injects as <label>+<sha>.
        var assemblyVersion = ExtractVersion(informationalVersion)
            ?? assembly.GetName().Version?.ToString(3);
        if (!string.IsNullOrWhiteSpace(assemblyVersion))
            return assemblyVersion;

        return null;
    }

    private static string? ExtractVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return null;

        var plusIndex = informationalVersion.IndexOf('+');
        var version = plusIndex >= 0
            ? informationalVersion[..plusIndex]
            : informationalVersion;

        version = version.Trim();
        return version.Length == 0 ? null : version;
    }

    private static string? ExtractCommitHash(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        var match = Regex.Match(source, "[0-9a-fA-F]{7,40}");
        if (!match.Success)
            return null;

        var hash = match.Value.ToLowerInvariant();
        return hash.Length <= 8 ? hash : hash[..8];
    }

    private static string? TryGetGitTag(bool pointsAtHead)
    {
        try
        {
            var repoRoot = TryFindRepoRoot(new DirectoryInfo(AppContext.BaseDirectory))
                ?? TryFindRepoRoot(new DirectoryInfo(Environment.CurrentDirectory));
            var workingDir = repoRoot?.FullName ?? Environment.CurrentDirectory;

            var args = pointsAtHead
                ? "tag --points-at HEAD --sort=-v:refname"
                : "describe --tags --abbrev=0";

            var result = RunGit(args, workingDir);
            if (string.IsNullOrWhiteSpace(result))
                return null;

            if (!pointsAtHead)
                return result;

            // If multiple tags point at HEAD, prefer the first sorted highest version.
            var firstLine = result
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? RunGit(string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
            return null;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(2000);
        if (process.ExitCode != 0)
            return null;

        return output.Trim();
    }

    private static string ResolveBuildDate(Assembly assembly)
    {
        var metadataDate = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(x => string.Equals(x.Key, "BuildDate", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (!string.IsNullOrWhiteSpace(metadataDate))
        {
            if (DateTime.TryParse(metadataDate, out var parsedMetadataDate))
                return parsedMetadataDate.ToString("M/d/yyyy");
            return metadataDate.Trim();
        }

        try
        {
            // Environment.ProcessPath: the running executable's path. Works for
            // both single-file published builds and standalone exes. Assembly.Location
            // returns empty in single-file mode (IL3000), so shipped releases were
            // falling through to "--" instead of showing a real file-mtime date.
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
                return File.GetLastWriteTime(processPath).ToString("M/d/yyyy");
        }
        catch
        {
            // Fallback handled below.
        }

        return "--";
    }

    private string ResolveReleaseTagForUpdateChecks()
    {
        // AppReleaseTag is the source of truth post the 2026-05-17 dotnet-run fix:
        // either an exact-HEAD tag, "{latest-tag}-dev" for dev builds past the last
        // tag, or the InformationalVersion-derived label for shipped builds. Strip
        // the -dev suffix so update checks compare against the base tag rather than
        // a nonexistent "v0.1.18b-dev" release.
        var tag = AppReleaseTag;
        if (tag.EndsWith("-dev", StringComparison.OrdinalIgnoreCase))
            tag = tag[..^"-dev".Length];
        return NormalizeVersionDisplayTag(tag);
    }

    private string ResolveConnectedRadioDisplayName()
    {
        var connectedSerial = _connection.ConnectedSerial;
        if (!string.IsNullOrWhiteSpace(connectedSerial))
        {
            var discovered = Radios.FirstOrDefault(r => string.Equals(r.Serial, connectedSerial, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(discovered?.Nickname))
                return discovered.Nickname;
        }

        return !string.IsNullOrWhiteSpace(_connection.ConnectedModel)
            ? _connection.ConnectedModel
            : "Connected Radio";
    }

    private int ResolveSliceDaxIqChannel(SliceInfo slice)
    {
        return _connection.Panadapters
            .FirstOrDefault(p => p.StreamId == slice.PanadapterStreamId)
            ?.DAXIQChannel ?? 0;
    }

    private CancellationTokenSource ReplaceStreamRemovedDebounceCts(int daxIqChannel)
    {
        var next = new CancellationTokenSource();
        CancellationTokenSource? previous;

        lock (_syncDampenGate)
        {
            _streamRemovedDebounceCtsByChannel.TryGetValue(daxIqChannel, out previous);
            _streamRemovedDebounceCtsByChannel[daxIqChannel] = next;
        }

        if (previous is not null)
        {
            try { previous.Cancel(); } catch { }
            previous.Dispose();
        }

        return next;
    }

    private void CancelStreamRemovedDebounce(int daxIqChannel)
    {
        CancellationTokenSource? toDispose = null;
        lock (_syncDampenGate)
        {
            if (_streamRemovedDebounceCtsByChannel.TryGetValue(daxIqChannel, out toDispose))
                _streamRemovedDebounceCtsByChannel.Remove(daxIqChannel);
        }
        if (toDispose is null) return;
        try { toDispose.Cancel(); } catch { }
        toDispose.Dispose();
    }

    private void RecordOutboundQsy(int daxIqChannel, double freqMHz)
    {
        lock (_syncDampenGate)
        {
            _lastOutboundQsyByChannel[daxIqChannel] = (freqMHz, DateTime.UtcNow);
        }
    }

    private bool ShouldSuppressInboundClick(int daxIqChannel, double freqMHz, int clickSnapStepHz)
    {
        var now = DateTime.UtcNow;
        var snapAwareEchoToleranceMHz = Math.Max(
            EchoSuppressToleranceMHz,
            Math.Max(1, clickSnapStepHz / 2) / 1_000_000d);

        lock (_syncDampenGate)
        {
            // Suppress immediate echo-back from our own outbound QSY commands.
            if (_lastOutboundQsyByChannel.TryGetValue(daxIqChannel, out var outbound) &&
                (now - outbound.Utc) <= EchoSuppressWindow &&
                Math.Abs(freqMHz - outbound.FreqMHz) <= snapAwareEchoToleranceMHz)
            {
                return true;
            }

            // Suppress fast duplicate click notifications at effectively same frequency.
            if (_lastInboundClickByChannel.TryGetValue(daxIqChannel, out var inbound) &&
                (now - inbound.Utc) <= DuplicateClickWindow &&
                Math.Abs(freqMHz - inbound.FreqMHz) <= DuplicateClickToleranceMHz)
            {
                return true;
            }

            _lastInboundClickByChannel[daxIqChannel] = (freqMHz, now);
            return false;
        }
    }

    private SliceInfo? GetPreferredSliceForTune(int daxIqChannel)
    {
        if (daxIqChannel > 0)
        {
            var panForChannel = _connection.Panadapters
                .FirstOrDefault(p => p.DAXIQChannel == daxIqChannel &&
                                     string.Equals(p.ClientStation, SelectedControlStation, StringComparison.OrdinalIgnoreCase))
                ?? _connection.Panadapters.FirstOrDefault(p => p.DAXIQChannel == daxIqChannel);

            if (panForChannel is not null)
            {
                var ownSlice = _connection.Slices.FirstOrDefault(s =>
                    s.PanadapterStreamId == panForChannel.StreamId &&
                    IsOwnStationSlice(s));
                if (ownSlice is not null)
                    return ownSlice;

                var anySlice = _connection.Slices.FirstOrDefault(s =>
                    s.PanadapterStreamId == panForChannel.StreamId);
                if (anySlice is not null)
                    return anySlice;
            }
        }

        return GetPreferredSliceForTune();
    }

    private SliceInfo? GetPreferredSliceForTune()
    {
        var ownSlice = _connection.Slices.FirstOrDefault(IsOwnStationSlice);
        return ownSlice ?? _connection.Slices.FirstOrDefault();
    }

    private static int ResolveClickSnapStepHz(SliceInfo slice)
    {
        if (slice.TuneStepHz > 0)
            return slice.TuneStepHz;

        return FallbackClickSnapStepHz;
    }

    private string ResolveSpotSourceIdentity(string baseCall)
    {
        var sliceLetter = GetPreferredSliceForTune()?.Letter?.Trim();
        if (string.IsNullOrWhiteSpace(sliceLetter))
            return baseCall;

        return $"{baseCall}/{sliceLetter.ToUpperInvariant()}";
    }

    private string ResolveSourceBaseCall(string? spotterFromTelnet)
    {
        if (IsLikelySourceCallsign(_settings.Callsign))
            return _settings.Callsign.Trim().ToUpperInvariant();

        var normalizedSpotter = NormalizeTelnetSpotterCallsign(spotterFromTelnet);
        if (IsLikelySourceCallsign(normalizedSpotter))
            return normalizedSpotter;

        return "CWSKIMMER";
    }

    private static string NormalizeTelnetSpotterCallsign(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim().ToUpperInvariant();
        var hyphenIdx = trimmed.LastIndexOf('-');
        if (hyphenIdx <= 0 || hyphenIdx >= trimmed.Length - 1)
            return trimmed;

        var suffix = trimmed[(hyphenIdx + 1)..];
        if (suffix.All(ch => ch == '#' || char.IsDigit(ch)))
            return trimmed[..hyphenIdx];

        return trimmed;
    }

    private static bool IsLikelySourceCallsign(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Length < 3 || trimmed.Length > 16)
            return false;

        bool hasLetter = false;
        bool hasDigit = false;
        foreach (var ch in trimmed)
        {
            if (char.IsLetter(ch)) hasLetter = true;
            else if (char.IsDigit(ch)) hasDigit = true;
            else if (ch != '-' && ch != '/')
                return false;
        }

        return hasLetter && hasDigit;
    }

    private void TrySyncSliceToSkimmer(SliceInfo slice)
    {
        var effectiveRxFreqMHz = GetEffectiveRxFrequencyMHz(slice);
        PublishRitSyncStatusIfChanged(slice, effectiveRxFreqMHz);

        // Keep CW Skimmer's main window VFO in sync with effective RX frequency.
        if (!TelnetClusterEnabled ||
            !IsCwSkimmerRunning ||
            effectiveRxFreqMHz <= 0 ||
            !IsOwnStationSlice(slice))
        {
            return;
        }

        var daxIqChannel = ResolveSliceDaxIqChannel(slice);
        if (daxIqChannel <= 0)
            return;

        // Pass current panadapter center as loHz on every slice-frequency change.
        // FlexLib may fire the slice event before propagating a cross-band pan
        // recenter; including loHz here lets the tracker sequence LO_FREQ + QSY
        // correctly whenever the pan model is already up to date.
        var pan = _connection.Panadapters.FirstOrDefault(p => p.StreamId == slice.PanadapterStreamId);
        var loHz = pan is not null && pan.CenterFreqMHz > 0
            ? (long)Math.Round(pan.CenterFreqMHz * 1_000_000d)
            : (long?)null;

        _launcher.RequestSkimmerSync(daxIqChannel, loHz: loHz, vfoMHz: effectiveRxFreqMHz);
    }

    private bool IsOwnStationSlice(SliceInfo slice)
    {
        if (string.IsNullOrWhiteSpace(SelectedControlStation))
            return false;

        return string.Equals(slice.ClientStation, SelectedControlStation, StringComparison.OrdinalIgnoreCase);
    }

    // Bug fix 2026-05-19 (multi-station LO leak, WX7V repro). With two
    // stations on a shared DAX-IQ channel the prior channel-only gate
    // returned true for any source ("any pan on ch=N ours?") and pushed
    // the foreign center as our LO. Match the source ClientHandle to a
    // pan owned by SelectedControlStation so only own-station pan AND
    // stream updates drive the sync. Extends c908c58's UpdatePan-only
    // fix to OnDaxIQStreamUpdated.
    private bool IsOwnStationPanSource(int daxIqChannel, uint sourceClientHandle) =>
        DaxChannelOwnership.IsSourceOwnedByStation(
            _connection.Panadapters,
            daxIqChannel,
            sourceClientHandle,
            SelectedControlStation);

    private static double GetEffectiveRxFrequencyMHz(SliceInfo slice)
    {
        var ritOffsetHz = slice.RitEnabled ? slice.RitOffsetHz : 0d;
        return slice.FreqMHz + (ritOffsetHz / 1_000_000d);
    }

    private void PublishRitSyncStatusIfChanged(SliceInfo slice, double effectiveRxFreqMHz)
    {
        var key = BuildSliceRitKey(slice);
        var roundedOffsetHz = Math.Round(slice.RitOffsetHz, 3);
        var current = (slice.RitEnabled, roundedOffsetHz);

        if (_lastRitStateBySlice.TryGetValue(key, out var previous) && previous.Equals(current))
            return;

        _lastRitStateBySlice[key] = current;
        if (!slice.RitEnabled || Math.Abs(roundedOffsetHz) < 0.5)
            return;

        if (!TelnetClusterEnabled || !IsCwSkimmerRunning || !IsOwnStationSlice(slice))
            return;

        _ritStatusEmitter.Enqueue($"RIT sync: {roundedOffsetHz:+0;-0} Hz -> {effectiveRxFreqMHz:F6} MHz");
    }

    private static string BuildSliceRitKey(SliceInfo slice)
        => $"{slice.ClientStation}|{slice.Letter}";

    private long? ResolveEffectiveRxFrequencyHzForChannel(int daxIqChannel)
    {
        if (daxIqChannel <= 0)
            return null;

        // Bug fix 2026-05-18 (issue #40): pre-fix code had cross-station ??
        // fallbacks (Panadapters.FirstOrDefault any pan, Slices.FirstOrDefault
        // any slice on that pan). Didn't bite in the same live test that
        // exposed the launcher bug, but it's a latent leak: when our station
        // has the pan but no slice yet, the slice fallback would pull a
        // foreign-station slice's freq and emit it as our QSY. Filter strictly
        // by SelectedControlStation now; return null when no match, which the
        // caller (TrySyncSkimmerForPanChange) already treats as "slice freq
        // unavailable" and just logs without emitting a wrong QSY.
        var panForChannel = _connection.Panadapters.FirstOrDefault(p =>
            p.DAXIQChannel == daxIqChannel &&
            string.Equals(p.ClientStation, SelectedControlStation, StringComparison.OrdinalIgnoreCase));

        if (panForChannel is null)
            return null;

        var slice = _connection.Slices.FirstOrDefault(s =>
            s.PanadapterStreamId == panForChannel.StreamId &&
            IsOwnStationSlice(s));

        if (slice is null)
            return null;

        var effectiveRxMHz = GetEffectiveRxFrequencyMHz(slice);
        return (long)Math.Round(effectiveRxMHz * 1_000_000d);
    }

    private void TrySyncSkimmerForPanChange(
        int daxIqChannel,
        uint sourceClientHandle,
        double centerFreqMHz,
        int sampleRateHz,
        string reason)
    {
        if (!TelnetClusterEnabled ||
            !IsCwSkimmerRunning ||
            daxIqChannel <= 0 ||
            centerFreqMHz <= 0 ||
            sampleRateHz <= 0 ||
            !IsOwnStationPanSource(daxIqChannel, sourceClientHandle))
        {
            return;
        }

        var centerHz = (long)Math.Round(centerFreqMHz * 1_000_000d);
        var effectiveRxFreqHz = ResolveEffectiveRxFrequencyHzForChannel(daxIqChannel);
        var effectiveRxFreqMHz = effectiveRxFreqHz.HasValue
            ? effectiveRxFreqHz.Value / 1_000_000d
            : (double?)null;

        _launcher.RequestSkimmerSync(daxIqChannel, loHz: centerHz, vfoMHz: effectiveRxFreqMHz);

        // FlexLib emits multiple panadapter and DAX-stream property change events
        // for a single band change (zoom, bandwidth, sample rate, ...). Dedup by
        // (LO, RX?) so the [TELNET] log gets one line per real (LO,RX) state
        // transition per channel instead of one per FlexLib event. RX may be
        // transiently null mid-band-change while the slice/pan mapping is in flux;
        // the same dict tracks that state so transitions in/out of it log once.
        if (effectiveRxFreqMHz.HasValue)
        {
            var current = (centerHz, (double?)Math.Round(effectiveRxFreqMHz.Value, 6));
            if (!_lastLoggedPanSyncByChannel.TryGetValue(daxIqChannel, out var previous) || previous != current)
            {
                _lastLoggedPanSyncByChannel[daxIqChannel] = current;
                AddTelnetStatus(
                    $"LO/QSY sync ({reason}): ch {daxIqChannel}, LO {centerHz} Hz, RX {effectiveRxFreqMHz.Value:F6} MHz.");
            }
        }
        else
        {
            var current = (centerHz, (double?)null);
            if (!_lastLoggedPanSyncByChannel.TryGetValue(daxIqChannel, out var previous) || previous != current)
            {
                _lastLoggedPanSyncByChannel[daxIqChannel] = current;
                AddTelnetStatus(
                    $"LO sync ({reason}): ch {daxIqChannel} -> {centerHz} Hz; slice frequency unavailable.");
            }
        }
    }

    private void EnsureSelectedControlStation()
    {
        if (string.Equals(SelectedControlStation, RadioConnectTarget.UnknownStation, StringComparison.OrdinalIgnoreCase))
            SetSelectedControlStation(string.Empty);

        if (!string.IsNullOrWhiteSpace(SelectedControlStation))
            return;

        if (!string.IsNullOrWhiteSpace(OwnClientStation))
        {
            SetSelectedControlStation(OwnClientStation);
            return;
        }

        SetSelectedControlStation(_connection.Slices
            .Select(s => s.ClientStation)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
            ?? string.Empty);
    }

    private void SetSelectedControlStation(string station)
    {
        if (string.Equals(SelectedControlStation, station, StringComparison.OrdinalIgnoreCase))
            return;

        SelectedControlStation = station;
        // Issue #45: the new station has not yet been confirmed present in a
        // GUI-client snapshot, so re-arm the seen gate before disconnect detection.
        _controlStationSeen = false;
        OnPropertyChanged(nameof(SelectedControlStation));
        OnPropertyChanged(nameof(VisibleClientGroups));
        OnPropertyChanged(nameof(ConnectTargetHeaderText));

        // Re-scope the Digital Operating rows to the new control station so a
        // station change (or re-pin) never leaves another station's slices shown
        // (issue #28 multi-station bug, 2026-06-11).
        RebuildDigitalSlices();
    }

    private void RebuildConnectTargets()
    {
        var selected = SelectedConnectTarget;
        ConnectTargets.Clear();

        foreach (var radio in Radios.OrderBy(r => r.Model).ThenBy(r => r.Nickname).ThenBy(r => r.Serial))
        {
            var stations = radio.Stations
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (stations.Length == 0)
            {
                ConnectTargets.Add(new RadioConnectTarget(radio, RadioConnectTarget.UnknownStation));
                continue;
            }

            foreach (var station in stations)
                ConnectTargets.Add(new RadioConnectTarget(radio, station));
        }

        if (selected is null)
        {
            if (ConnectTargets.Count > 0)
                SelectedConnectTarget = ConnectTargets[0];
            return;
        }

        SelectedConnectTarget = ConnectTargets.FirstOrDefault(t =>
            t.Radio.Serial == selected.Radio.Serial &&
            string.Equals(t.Station, selected.Station, StringComparison.OrdinalIgnoreCase))
            ?? ConnectTargets.FirstOrDefault();
    }

    private void ApplyNetworkStatus(NetworkStatusInfo status)
    {
        var isDisconnected = status.CurrentRttMs < 0 && status.MaxRttMs < 0;
        if (isDisconnected)
        {
            NetworkStatusLabel = "--";
            NetworkLatencyRttText = "--";
            NetworkMaxLatencyRttText = "--";
            return;
        }

        NetworkStatusLabel = status.Health switch
        {
            NetworkHealthLevel.Excellent => "Excellent",
            NetworkHealthLevel.Good => "Good",
            NetworkHealthLevel.Poor => "Poor",
            _ => "Poor"
        };
        NetworkLatencyRttText = status.CurrentRttMs >= 0 ? $"{status.CurrentRttMs} ms" : "--";
        NetworkMaxLatencyRttText = status.MaxRttMs >= 0 ? $"{status.MaxRttMs} ms" : "--";
    }

    private void ResetDisplayedNetworkStatus()
    {
        NetworkStatusLabel = "--";
        NetworkLatencyRttText = "--";
        NetworkMaxLatencyRttText = "--";
    }

    private static void UIPost(System.Action action) => Dispatcher.UIThread.Post(action);
}

public sealed record SpotColorOption(string Name, string Hex);
