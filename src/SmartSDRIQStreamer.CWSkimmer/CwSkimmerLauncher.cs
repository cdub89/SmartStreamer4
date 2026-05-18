using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace SDRIQStreamer.CWSkimmer;

/// <summary>
/// Writes the CW Skimmer INI, launches CwSkimmer.exe, monitors the process,
/// and manages the telnet connection for two-way sync.
/// </summary>
public sealed class CwSkimmerLauncher : ICwSkimmerLauncher, IDisposable
{
    private static readonly string IniDir =
        RuntimePathResolver.ResolveCwSkimmerIniDir();
    private static readonly TimeSpan GracefulStopWait = TimeSpan.FromSeconds(2);

    private readonly CwSkimmerIniModelFactory _modelFactory;
    private readonly CwSkimmerIniWriter       _iniWriter;
    private readonly IAudioDeviceFinder       _deviceFinder;
    private readonly Func<ICwSkimmerTelnetClient> _telnetFactory;

    private readonly Dictionary<int, Process> _processesByChannel = new();
    private readonly Dictionary<int, ICwSkimmerTelnetClient> _telnetByChannel = new();
    private readonly Dictionary<int, CwSkimmerSyncTracker> _trackerByChannel = new();
    private readonly Dictionary<int, CancellationTokenSource> _telnetLifecycleCtsByChannel = new();
    private readonly Dictionary<int, int> _telnetPortByChannel = new();
    private readonly Dictionary<int, string> _managedIniPathByChannel = new();
    private readonly Dictionary<int, DateTime> _processStartUtcByChannel = new();
    private readonly Dictionary<int, string> _requestedStopReasonByChannel = new();
    private readonly List<Task> _backgroundTasks = new();
    private readonly object _sync = new();
    private readonly HashSet<int> _telnetDisconnectInFlightChannels = [];
    private bool? _lastEmittedRunningState;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _processesByChannel.Values.Any(p => !p.HasExited);
        }
    }
    public bool TelnetConnected
    {
        get
        {
            lock (_sync)
                return _telnetByChannel.Values.Any(t => t.IsConnected);
        }
    }
    public bool IsChannelRunning(int daxIqChannel)
    {
        lock (_sync)
            return _processesByChannel.TryGetValue(daxIqChannel, out var p) && !p.HasExited;
    }

    public event Action<bool>?   RunningStateChanged;
    public event Action<int, double>? FrequencyClicked;
    public event Action<int, CwSkimmerSpotInfo>? SpotReceived;
    public event Action<string>? TelnetStatusChanged;
    public event Action<int, double, DateTime>? OutboundQsyEmitted;

    public string LastDiagnostics { get; private set; } = string.Empty;

    public CwSkimmerLauncher(CwSkimmerIniModelFactory modelFactory,
                             CwSkimmerIniWriter       iniWriter,
                             IAudioDeviceFinder       deviceFinder,
                             Func<ICwSkimmerTelnetClient> telnetFactory)
    {
        _modelFactory = modelFactory;
        _iniWriter    = iniWriter;
        _deviceFinder = deviceFinder;
        _telnetFactory = telnetFactory;
    }

    public (string SignalDevice, int SignalIdx, string AudioDevice, int AudioIdx)?
        PreviewDevices(int daxIqChannel)
    {
        // Probe v2 ("DAX IQ {N}") then v1 ("DAX IQ RX {N}") to support both DAX versions.
        int sigIdx = _deviceFinder.FindDaxIqSignalDeviceIndex(daxIqChannel);
        if (sigIdx < 0) return null;

        var signalDevices = _deviceFinder.ListAllSignalDevices();
        var sigEntry      = signalDevices.FirstOrDefault(d => d.CwSkimmerIndex == sigIdx);
        string signalLabel = sigEntry.Name ?? $"DAX IQ {daxIqChannel}";

        // Audio I/O is the user's local speakers/headphones configured in the master INI,
        // not a DAX device — index and name are not resolvable here without the config path.
        return (signalLabel, sigIdx, string.Empty, -1);
    }

    public async Task<LaunchResult> LaunchAsync(
        int             daxIqChannel,
        int             sampleRateHz,
        long            centerFreqHz,
        CwSkimmerConfig config)
    {
        if (IsChannelRunning(daxIqChannel)) return LaunchResult.AlreadyRunning;

        string exePath = config.ExePath;
        if (!File.Exists(exePath)) return LaunchResult.ExeNotFound;

        var model      = _modelFactory.Build(daxIqChannel, sampleRateHz, centerFreqHz, config);
        var iniPath = Path.Combine(IniDir, $"CwSkimmer-ch{daxIqChannel}.ini");
        var channelIniExists = File.Exists(iniPath);

        LastDiagnostics = BuildDiagnostics(daxIqChannel, model, config.SkimmerIniPath, channelIniExists, _deviceFinder);
        WriteDiagnosticLog(LastDiagnostics);

        // Generated channel INIs always use MME — only MmeSignalDev gates launch.
        if (model.MmeSignalDevIndex < 0)
        {
            EmitLauncherStatus(daxIqChannel,
                $"Launch blocked: MME signal device for DAX IQ {daxIqChannel} not found in WinMM enumeration.");
            return LaunchResult.DeviceNotFound;
        }

        if (!PrepareManagedIniFromTemplate(iniPath, config, daxIqChannel, out _))
            return LaunchResult.TemplateIniNotFound;

        // Audio section is seeded from calibrated baseline only on first channel INI
        // creation. After that, operator edits in CW Skimmer are preserved.
        if (!channelIniExists)
            _iniWriter.Write(model, iniPath);
        lock (_sync)
        {
            _managedIniPathByChannel[daxIqChannel] = iniPath;
        }
        var resolvedTelnetPort = TryReadTelnetPortFromIni(iniPath) ?? config.TelnetPort;
        lock (_sync)
            _telnetPortByChannel[daxIqChannel] = resolvedTelnetPort;

        if (config.LaunchDelaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(config.LaunchDelaySeconds));

        var psi = new ProcessStartInfo
        {
            FileName        = exePath,
            Arguments       = $"ini=\"{iniPath}\"",
            UseShellExecute = false,
        };

        Process? process;
        try { process = Process.Start(psi); }
        catch
        {
            return LaunchResult.ProcessStartFailed;
        }

        if (process is null) return LaunchResult.ProcessStartFailed;

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(daxIqChannel, process);
        lock (_sync)
        {
            _processesByChannel[daxIqChannel] = process;
            _processStartUtcByChannel[daxIqChannel] = DateTime.UtcNow;
            _requestedStopReasonByChannel.Remove(daxIqChannel);
        }
        EmitRunningStateChangedIfNeeded(true);

        // Connect telnet in the background after CW Skimmer has started up,
        // then immediately sync the VFO frequency.
        var sliceFreqMHz = config.InitialSliceFreqMHz;
        var loFreqHz = config.InitialLoFreqHz;
        if (!IsTelnetConnected(daxIqChannel))
        {
            var telnetCts = GetOrCreateTelnetLifecycleCts(daxIqChannel);
            RunBackgroundTask(
                ConnectTelnetAsync(daxIqChannel, resolvedTelnetPort, config, sliceFreqMHz, loFreqHz, telnetCts.Token),
                $"telnet connect ch {daxIqChannel}");
        }

        return LaunchResult.Success;
    }

    private async Task ConnectTelnetAsync(
        int daxIqChannel,
        int telnetPort,
        CwSkimmerConfig config,
        double initialSliceFreqMHz,
        long initialLoFreqHz,
        CancellationToken ct)
    {
        if (config.ConnectDelaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(config.ConnectDelaySeconds), ct);

        if (!IsChannelRunning(daxIqChannel))
        {
            EmitLauncherStatus(daxIqChannel, "Skipping telnet connect because CW Skimmer process is not running.");
            return;
        }

        const int maxConnectAttempts = 3;
        var retryDelays = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(800),
            TimeSpan.FromMilliseconds(1600)
        };

        for (var attempt = 1; attempt <= maxConnectAttempts; attempt++)
        {
            try
            {
                var settleDelay = retryDelays[Math.Min(attempt - 1, retryDelays.Length - 1)];
                if (settleDelay > TimeSpan.Zero)
                    await Task.Delay(settleDelay, ct);

                var telnet = GetOrCreateTelnetClient(daxIqChannel);
                await telnet.ConnectAsync(
                    "127.0.0.1",
                    telnetPort,
                    config.Callsign,
                    config.TelnetPassword,
                    ct);

                if (config.TelnetClusterEnabled)
                {
                    // Seed the per-channel sync tracker with the initial desired
                    // LO/QSY state right after telnet connect. Tracker emits one
                    // pair of commands and stays quiet until a panadapter or
                    // slice event changes the desired values (no heartbeat —
                    // that was removed in 7e8c58c on 2026-05-02 to stop telnet
                    // log spam on idle channels).
                    var tracker = GetOrCreateSyncTracker(daxIqChannel, telnet);
                    tracker.RequestSync(
                        loHz:   initialLoFreqHz   > 0 ? initialLoFreqHz : null,
                        vfoMHz: initialSliceFreqMHz > 0 ? initialSliceFreqMHz : null);
                }

                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (attempt < maxConnectAttempts && IsRetriableStartupConnectFailure(ex))
            {
                LogNonFatal($"Telnet connect retry {attempt}/{maxConnectAttempts - 1} after startup race.", ex);
            }
            catch (Exception ex)
            {
                // Telnet is best-effort; LO/QSY sync won't work but CW Skimmer still runs.
                LogNonFatal("Background telnet connect failed.", ex);
                return;
            }
        }
    }

    public void RequestSkimmerSync(int daxIqChannel, long? loHz = null, double? vfoMHz = null)
    {
        CwSkimmerSyncTracker? tracker;
        lock (_sync)
            _trackerByChannel.TryGetValue(daxIqChannel, out tracker);

        tracker?.RequestSync(loHz: loHz, vfoMHz: vfoMHz);
    }

    public void Stop()
    {
        List<int> channels;
        List<Process> procs;
        lock (_sync)
        {
            channels = _processesByChannel.Keys.ToList();
            procs = _processesByChannel.Values.ToList();
            _processesByChannel.Clear();
            foreach (var channel in channels)
                _requestedStopReasonByChannel[channel] = "stop requested by application (all channels).";
        }

        foreach (var proc in procs)
        {
            TryStopProcessGracefully(proc);
        }

        foreach (var channel in channels)
        {
            CancelPendingTelnetWork(channel);
            BeginTelnetDisconnect(channel);
        }

        List<int> telnetOnlyChannels;
        lock (_sync)
            telnetOnlyChannels = _telnetByChannel.Keys.Except(channels).ToList();

        foreach (var channel in telnetOnlyChannels)
        {
            CancelPendingTelnetWork(channel);
            BeginTelnetDisconnect(channel);
        }
        EmitRunningStateChangedIfNeeded(IsRunning);
    }

    public void Stop(int daxIqChannel)
    {
        Process? proc = null;
        lock (_sync)
        {
            if (_processesByChannel.TryGetValue(daxIqChannel, out var p))
            {
                proc = p;
                _processesByChannel.Remove(daxIqChannel);
            }
            _requestedStopReasonByChannel[daxIqChannel] = "stop requested by application (single channel).";
        }

        if (proc is not null)
            TryStopProcessGracefully(proc);

        CancelPendingTelnetWork(daxIqChannel);
        BeginTelnetDisconnect(daxIqChannel);

        EmitRunningStateChangedIfNeeded(IsRunning);
    }

    private void OnProcessExited(int daxIqChannel, Process process)
    {
        DateTime? startUtc = null;
        string requestedStopReason = string.Empty;
        int? exitCode = null;
        try
        {
            if (process.HasExited)
                exitCode = process.ExitCode;
        }
        catch
        {
            // Ignore metadata fetch failures for exited process.
        }

        lock (_sync)
        {
            _processesByChannel.Remove(daxIqChannel);
            if (_processStartUtcByChannel.TryGetValue(daxIqChannel, out var started))
            {
                startUtc = started;
                _processStartUtcByChannel.Remove(daxIqChannel);
            }
            if (_requestedStopReasonByChannel.TryGetValue(daxIqChannel, out var reason))
            {
                requestedStopReason = reason;
                _requestedStopReasonByChannel.Remove(daxIqChannel);
            }
        }

        var uptime = startUtc.HasValue
            ? DateTime.UtcNow - startUtc.Value
            : (TimeSpan?)null;
        var reasonText = string.IsNullOrWhiteSpace(requestedStopReason)
            ? "process exited without app stop request."
            : requestedStopReason;
        var exitCodeText = exitCode.HasValue ? exitCode.Value.ToString(CultureInfo.InvariantCulture) : "unknown";
        var uptimeText = uptime.HasValue ? $"{uptime.Value.TotalSeconds:F1}s" : "unknown";
        EmitLauncherStatus(
            daxIqChannel,
            $"CW Skimmer process exited (exit_code={exitCodeText}, uptime={uptimeText}, reason={reasonText})");

        CancelPendingTelnetWork(daxIqChannel);
        BeginTelnetDisconnect(daxIqChannel);
        EmitRunningStateChangedIfNeeded(IsRunning);
    }

    private static string BuildDiagnostics(
        int daxIqChannel,
        CwSkimmerIniModel model,
        string templateIniPath,
        bool channelIniExists,
        IAudioDeviceFinder deviceFinder)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== CW Skimmer Device Diagnostic  (DAX ch {daxIqChannel}) ===");
        sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("--- Calibration source ---");
        sb.AppendLine($"  Template INI = {templateIniPath}");
        sb.AppendLine($"  Channel INI exists = {channelIniExists}");
        sb.AppendLine($"  UseWdm                = {model.UseWdm}");
        sb.AppendLine();
        sb.AppendLine("--- Selected for INI ---");
        sb.AppendLine($"  Baseline WdmSignalDev = {model.CalibrationBaseSignalIndex}");
        sb.AppendLine($"  Baseline WdmAudioDev  = {model.CalibrationBaseAudioIndex}");
        sb.AppendLine($"  WdmSignalDev          = {model.WdmSignalDevIndex}");
        sb.AppendLine($"  WdmAudioDev           = {model.WdmAudioDevIndex}");
        sb.AppendLine($"  MmeSignalDev          = {model.MmeSignalDevIndex}");
        sb.AppendLine($"  MmeAudioDev           = {model.MmeAudioDevIndex}");
        sb.AppendLine($"  SignalRate            = {model.SampleRateHz}");
        sb.AppendLine($"  CenterFreq            = {model.CenterFreqHz}");
        sb.AppendLine();
        sb.AppendLine("--- WinMM WaveIn capture devices (UI 1-based, INI = UI - 1) ---");
        try
        {
            foreach (var (idx, name) in deviceFinder.ListAllSignalDevices())
                sb.AppendLine($"  [idx {idx,3}] {name}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (enumeration failed: {ex.Message})");
        }
        sb.AppendLine();
        sb.AppendLine("--- WinMM WaveOut playback devices (UI 1-based, INI = UI - 1) ---");
        try
        {
            foreach (var (idx, name) in deviceFinder.ListAllAudioDevices())
                sb.AppendLine($"  [idx {idx,3}] {name}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (enumeration failed: {ex.Message})");
        }
        sb.AppendLine();
        sb.AppendLine("--- DAX IQ channel resolution (UI 1-based) ---");
        for (int ch = 1; ch <= 4; ch++)
        {
            var idx = deviceFinder.FindDaxIqSignalDeviceIndex(ch);
            sb.AppendLine($"  ch {ch}: {(idx >= 0 ? $"UI idx {idx}" : "NOT FOUND")}");
        }
        sb.AppendLine();
        sb.AppendLine("--- DirectSound capture devices (NOT CW Skimmer's WDM list; see DirectSoundProbe header) ---");
        try
        {
            foreach (var dev in DirectSoundProbe.EnumerateCaptureDevices())
                sb.AppendLine($"  [idx {dev.Index,3}] {dev.Description}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (enumeration failed: {ex.Message})");
        }
        sb.AppendLine();
        sb.AppendLine("--- DirectSound output devices (NOT CW Skimmer's WDM list; see DirectSoundProbe header) ---");
        try
        {
            foreach (var dev in DirectSoundProbe.EnumerateOutputDevices())
                sb.AppendLine($"  [idx {dev.Index,3}] {dev.Description}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (enumeration failed: {ex.Message})");
        }
        return sb.ToString();
    }

    private static void WriteDiagnosticLog(string content)
    {
        try
        {
            Directory.CreateDirectory(IniDir);
            File.WriteAllText(Path.Combine(IniDir, "device-diagnostic.txt"), content);
        }
        catch (Exception ex) { LogNonFatal("Failed to write diagnostic log.", ex); }
    }

    private bool PrepareManagedIniFromTemplate(string targetIniPath, CwSkimmerConfig config, int daxIqChannel, out string templateIniPath)
    {
        // Preserve per-channel window geometry and other CW-managed sections by
        // reusing an existing managed INI when present.
        if (File.Exists(targetIniPath))
        {
            templateIniPath = targetIniPath;
            return true;
        }

        templateIniPath = ResolveTemplateIniPath(config);
        if (string.IsNullOrWhiteSpace(templateIniPath))
            return false;

        try
        {
            var dir = Path.GetDirectoryName(targetIniPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.Copy(templateIniPath, targetIniPath, overwrite: true);

            // First-creation only: tile the channel window so multiple
            // simultaneous CW Skimmer instances don't stack on top of each
            // other. After CW Skimmer saves its own geometry on close, this
            // adjustment is never re-applied.
            OffsetChannelWindowPosition(targetIniPath, daxIqChannel);

            return true;
        }
        catch (Exception ex)
        {
            LogNonFatal("Failed to seed managed INI from template.", ex);
            return false;
        }
    }

    private static void OffsetChannelWindowPosition(string iniPath, int daxIqChannel)
    {
        if (daxIqChannel <= 1)
            return;

        const int offsetPerChannel = 40;
        var deltaX = (daxIqChannel - 1) * offsetPerChannel;
        var deltaY = (daxIqChannel - 1) * offsetPerChannel;

        try
        {
            var lines = File.ReadAllLines(iniPath);
            var inWindows = false;
            var changed = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();

                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    inWindows = string.Equals(trimmed, "[Windows]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inWindows || !trimmed.Contains('='))
                    continue;

                var eq = lines[i].IndexOf('=');
                var key = lines[i][..eq].Trim();
                var rhs = lines[i][(eq + 1)..].Trim();
                if (!int.TryParse(rhs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current))
                    continue;

                if (key.Equals("Left", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"Left={current + deltaX}";
                    changed = true;
                }
                else if (key.Equals("Top", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"Top={current + deltaY}";
                    changed = true;
                }
            }

            if (changed)
                File.WriteAllLines(iniPath, lines);
        }
        catch
        {
            // Tiling is cosmetic; failure to apply must not block launch.
        }
    }

    private string ResolveTemplateIniPath(CwSkimmerConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.SkimmerIniPath) && File.Exists(config.SkimmerIniPath))
            return config.SkimmerIniPath;

        return string.Empty;
    }

    private static int? TryReadTelnetPortFromIni(string iniPath)
    {
        if (!File.Exists(iniPath))
            return null;

        var inTelnetSection = false;
        foreach (var raw in File.ReadLines(iniPath))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inTelnetSection = string.Equals(line, "[Telnet]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inTelnetSection)
                continue;

            if (!line.StartsWith("Port=", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line["Port=".Length..].Trim();
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0)
                return port;
        }

        return null;
    }

    private bool IsTelnetConnected(int daxIqChannel)
    {
        lock (_sync)
            return _telnetByChannel.TryGetValue(daxIqChannel, out var telnet) && telnet.IsConnected;
    }

    private bool TryGetTelnetClient(int daxIqChannel, out ICwSkimmerTelnetClient telnet)
    {
        lock (_sync)
            return _telnetByChannel.TryGetValue(daxIqChannel, out telnet!);
    }

    private CwSkimmerSyncTracker GetOrCreateSyncTracker(int daxIqChannel, ICwSkimmerTelnetClient telnet)
    {
        lock (_sync)
        {
            if (_trackerByChannel.TryGetValue(daxIqChannel, out var existing))
                return existing;

            var tracker = new CwSkimmerSyncTracker(
                telnet,
                onStatus:     msg => EmitLauncherStatus(daxIqChannel, msg),
                onQsyEmitted: (mhz, utc) => OutboundQsyEmitted?.Invoke(daxIqChannel, mhz, utc));
            tracker.Start();
            _trackerByChannel[daxIqChannel] = tracker;
            return tracker;
        }
    }

    private ICwSkimmerTelnetClient GetOrCreateTelnetClient(int daxIqChannel)
    {
        lock (_sync)
        {
            if (_telnetByChannel.TryGetValue(daxIqChannel, out var existing))
                return existing;

            var telnet = _telnetFactory();
            telnet.FrequencyClicked += freq => FrequencyClicked?.Invoke(daxIqChannel, freq);
            telnet.SpotReceived += spot => SpotReceived?.Invoke(daxIqChannel, spot);
            telnet.StatusChanged += message =>
            {
                if (message.Contains("Telnet connection closed by CW Skimmer.", StringComparison.OrdinalIgnoreCase))
                {
                    message = $"{message} (process_running={IsChannelRunning(daxIqChannel)})";
                }
                var portText = TryGetKnownTelnetPortText(daxIqChannel);
                TelnetStatusChanged?.Invoke($"ch {daxIqChannel}{portText}: {message}");
            };
            _telnetByChannel[daxIqChannel] = telnet;
            return telnet;
        }
    }

    private string TryGetKnownTelnetPortText(int daxIqChannel)
    {
        lock (_sync)
        {
            if (_telnetPortByChannel.TryGetValue(daxIqChannel, out var port) && port > 0)
                return $" ({port})";
        }

        return string.Empty;
    }

    private CancellationTokenSource GetOrCreateTelnetLifecycleCts(int daxIqChannel)
    {
        lock (_sync)
        {
            if (_telnetLifecycleCtsByChannel.TryGetValue(daxIqChannel, out var existing))
                return existing;

            var created = new CancellationTokenSource();
            _telnetLifecycleCtsByChannel[daxIqChannel] = created;
            return created;
        }
    }

    private void BeginTelnetDisconnect(int daxIqChannel)
    {
        lock (_sync)
        {
            if (_telnetDisconnectInFlightChannels.Contains(daxIqChannel))
                return;

            _telnetDisconnectInFlightChannels.Add(daxIqChannel);
        }

        RunBackgroundTask(DisconnectTelnetAsync(daxIqChannel), $"telnet disconnect ch {daxIqChannel}");
    }

    private void CancelPendingTelnetWork(int daxIqChannel)
    {
        CancellationTokenSource? toDispose = null;
        lock (_sync)
        {
            if (_telnetLifecycleCtsByChannel.TryGetValue(daxIqChannel, out var current))
            {
                toDispose = current;
                _telnetLifecycleCtsByChannel[daxIqChannel] = new CancellationTokenSource();
            }
        }

        if (toDispose is null)
            return;

        try { toDispose.Cancel(); }
        catch (ObjectDisposedException) { }
        finally { toDispose.Dispose(); }
    }

    private async Task DisconnectTelnetAsync(int daxIqChannel)
    {
        ICwSkimmerTelnetClient? telnet = null;
        CwSkimmerSyncTracker? tracker = null;
        CancellationTokenSource? lifecycleCts = null;
        lock (_sync)
        {
            if (_telnetByChannel.TryGetValue(daxIqChannel, out var existing))
            {
                telnet = existing;
                _telnetByChannel.Remove(daxIqChannel);
            }

            if (_trackerByChannel.TryGetValue(daxIqChannel, out var existingTracker))
            {
                tracker = existingTracker;
                _trackerByChannel.Remove(daxIqChannel);
            }

            if (_telnetLifecycleCtsByChannel.TryGetValue(daxIqChannel, out var cts))
            {
                lifecycleCts = cts;
                _telnetLifecycleCtsByChannel.Remove(daxIqChannel);
            }

            _telnetPortByChannel.Remove(daxIqChannel);
            _managedIniPathByChannel.Remove(daxIqChannel);
        }

        try
        {
            if (tracker is not null)
            {
                try { await tracker.DisposeAsync(); }
                catch (Exception ex) { LogNonFatal($"Sync tracker dispose failed (ch {daxIqChannel}).", ex); }
            }

            if (telnet is not null)
            {
                try { await telnet.DisconnectAsync(); }
                finally { await telnet.DisposeAsync(); }
            }
        }
        catch (Exception ex)
        {
            LogNonFatal($"Background telnet disconnect failed (ch {daxIqChannel}).", ex);
        }
        finally
        {
            if (lifecycleCts is not null)
            {
                try { lifecycleCts.Cancel(); } catch { }
                lifecycleCts.Dispose();
            }

            lock (_sync)
                _telnetDisconnectInFlightChannels.Remove(daxIqChannel);
        }
    }

    private void RunBackgroundTask(Task task, string operation)
    {
        lock (_sync)
            _backgroundTasks.Add(task);

        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted)
                LogNonFatal($"Background operation failed: {operation}.", t.Exception?.GetBaseException());

            lock (_sync)
                _backgroundTasks.Remove(task);
        }, TaskScheduler.Default);
    }

    private static void LogNonFatal(string message, Exception? ex)
    {
        if (ex is null)
            Debug.WriteLine($"[CwSkimmerLauncher] {message}");
        else
            Debug.WriteLine($"[CwSkimmerLauncher] {message} {ex.Message}");
    }

    private static bool IsRetriableStartupConnectFailure(Exception ex)
    {
        if (ex is SocketException socketEx && socketEx.SocketErrorCode == SocketError.ConnectionRefused)
            return true;

        return false;
    }

    private void EmitLauncherStatus(int daxIqChannel, string message)
    {
        var portText = TryGetKnownTelnetPortText(daxIqChannel);
        TelnetStatusChanged?.Invoke($"ch {daxIqChannel}{portText}: {message}");
    }

    private static void TryStopProcessGracefully(Process proc)
    {
        if (proc.HasExited)
            return;

        try
        {
            var closeRequested = proc.CloseMainWindow();
            if (closeRequested && proc.WaitForExit(GracefulStopWait))
                return;
        }
        catch (Exception ex)
        {
            LogNonFatal("Graceful CW Skimmer stop failed, falling back to kill.", ex);
        }

        if (proc.HasExited)
            return;

        try
        {
            proc.Kill();
        }
        catch (Exception ex)
        {
            LogNonFatal("Failed to kill CW Skimmer process.", ex);
        }
    }

    private void EmitRunningStateChangedIfNeeded(bool running)
    {
        lock (_sync)
        {
            if (_lastEmittedRunningState.HasValue && _lastEmittedRunningState.Value == running)
                return;

            _lastEmittedRunningState = running;
        }

        RunningStateChanged?.Invoke(running);
    }

    public void Dispose()
    {
        Stop();
        lock (_sync)
        {
            foreach (var cts in _telnetLifecycleCtsByChannel.Values)
                cts.Dispose();
            _telnetLifecycleCtsByChannel.Clear();
        }
    }
}
