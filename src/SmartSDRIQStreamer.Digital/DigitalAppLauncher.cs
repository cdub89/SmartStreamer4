namespace SDRIQStreamer.Digital;

public enum DigitalLaunchResult
{
    Success,
    InvalidRigName,
    ExeNotFound,
    AlreadyRunning,
    ProcessStartFailed,
}

/// <summary>
/// Launches and tracks digital-engine instances, one per <c>--rig-name</c>
/// (issue #28). Setup-and-launch only: no telnet, sync, or spot brokering
/// (unlike CW Skimmer). Concurrency is per-instance, keyed by rig name, so
/// several slices can run at once (FLEX-6600 = up to 4).
/// </summary>
public interface IDigitalAppLauncher
{
    /// <summary>True when any instance is running.</summary>
    bool IsRunning { get; }

    /// <summary>True when the instance for <paramref name="rigName"/> is running.</summary>
    bool IsInstanceRunning(string rigName);

    /// <summary>Fires when the aggregate running state changes.</summary>
    event Action<bool>? RunningStateChanged;

    /// <summary>
    /// Launches <paramref name="engine"/> as <c>exe --rig-name=&lt;rigName&gt;</c>.
    /// Config provisioning (the seeded <c>.ini</c>) happens before this call
    /// (checkpoint E).
    /// </summary>
    Task<DigitalLaunchResult> LaunchAsync(DigitalEngineDefinition engine, string rigName);

    /// <summary>Stops the instance for <paramref name="rigName"/> if running.</summary>
    void Stop(string rigName);

    /// <summary>Stops all running instances.</summary>
    void Stop();
}

/// <inheritdoc cref="IDigitalAppLauncher"/>
public sealed class DigitalAppLauncher : IDigitalAppLauncher, IDisposable
{
    private readonly IDigitalProcessRunner _runner;
    private readonly Dictionary<string, IDigitalProcess> _byRigName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private bool _lastEmittedRunningState;

    public DigitalAppLauncher(IDigitalProcessRunner? runner = null)
        => _runner = runner ?? new ProcessDigitalProcessRunner();

    public event Action<bool>? RunningStateChanged;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _byRigName.Values.Any(p => !p.HasExited);
        }
    }

    public bool IsInstanceRunning(string rigName)
    {
        lock (_sync)
            return _byRigName.TryGetValue(rigName, out var p) && !p.HasExited;
    }

    public Task<DigitalLaunchResult> LaunchAsync(DigitalEngineDefinition engine, string rigName)
    {
        ArgumentNullException.ThrowIfNull(engine);

        if (string.IsNullOrWhiteSpace(rigName))
            return Task.FromResult(DigitalLaunchResult.InvalidRigName);

        if (string.IsNullOrWhiteSpace(engine.ExePath) || !File.Exists(engine.ExePath))
            return Task.FromResult(DigitalLaunchResult.ExeNotFound);

        lock (_sync)
        {
            if (_byRigName.TryGetValue(rigName, out var existing) && !existing.HasExited)
                return Task.FromResult(DigitalLaunchResult.AlreadyRunning);
        }

        IDigitalProcess process;
        try
        {
            process = _runner.Start(engine.ExePath, $"--rig-name={rigName}");
        }
        catch
        {
            return Task.FromResult(DigitalLaunchResult.ProcessStartFailed);
        }

        process.Exited += OnProcessExited;
        lock (_sync)
        {
            _byRigName[rigName] = process;
        }

        EmitRunningStateIfChanged();
        return Task.FromResult(DigitalLaunchResult.Success);
    }

    public void Stop(string rigName)
    {
        IDigitalProcess? process;
        lock (_sync)
        {
            if (!_byRigName.Remove(rigName, out process))
                return;
        }

        SafeKill(process);
        EmitRunningStateIfChanged();
    }

    public void Stop()
    {
        List<IDigitalProcess> processes;
        lock (_sync)
        {
            processes = _byRigName.Values.ToList();
            _byRigName.Clear();
        }

        foreach (var process in processes)
            SafeKill(process);

        EmitRunningStateIfChanged();
    }

    public void Dispose() => Stop();

    private void OnProcessExited(object? sender, EventArgs e) => EmitRunningStateIfChanged();

    private void EmitRunningStateIfChanged()
    {
        var running = IsRunning;
        bool changed;
        lock (_sync)
        {
            changed = running != _lastEmittedRunningState;
            _lastEmittedRunningState = running;
        }

        if (changed)
            RunningStateChanged?.Invoke(running);
    }

    private static void SafeKill(IDigitalProcess process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch
        {
            // Best-effort stop.
        }
    }
}
