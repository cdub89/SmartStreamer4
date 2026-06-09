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
    /// Fires on every instance launch and every instance exit. Unlike
    /// <see cref="RunningStateChanged"/> (aggregate), this lets consumers refresh
    /// per-instance UI even when the aggregate state is unchanged (e.g. one of
    /// several running instances exits).
    /// </summary>
    event Action? InstancesChanged;

    /// <summary>
    /// Launches <paramref name="engine"/> as <c>exe --rig-name=&lt;rigName&gt;</c>.
    /// Config provisioning (the seeded <c>.ini</c>) happens before this call
    /// (checkpoint E).
    /// </summary>
    Task<DigitalLaunchResult> LaunchAsync(DigitalEngineDefinition engine, string rigName);

    /// <summary>Requests a graceful stop of the instance for <paramref name="rigName"/>.</summary>
    void Stop(string rigName);

    /// <summary>Requests a graceful stop of all running instances.</summary>
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
    private volatile bool _disposed;

    public DigitalAppLauncher(IDigitalProcessRunner? runner = null)
        => _runner = runner ?? new ProcessDigitalProcessRunner();

    public event Action<bool>? RunningStateChanged;
    public event Action? InstancesChanged;

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

        // Review fix #5: reserve, start, and store the rig atomically under the
        // lock so two concurrent launches for the same rig cannot both pass the
        // check and spawn duplicate engines on one --rig-name config.
        IDigitalProcess started;
        lock (_sync)
        {
            if (_byRigName.TryGetValue(rigName, out var existing))
            {
                if (!existing.HasExited)
                    return Task.FromResult(DigitalLaunchResult.AlreadyRunning);

                // Re-review fix: a prior instance for this rig has exited but is
                // not yet pruned (its exit callback is delayed behind this lock,
                // or it exited before its event was observed). Remove and dispose
                // it before reusing the key so the old wrapper isn't orphaned.
                _byRigName.Remove(rigName);
                existing.Exited -= OnProcessExited;
                try { existing.Dispose(); } catch { /* best-effort */ }
            }

            try
            {
                started = _runner.Start(engine.ExePath, $"--rig-name={rigName}");
            }
            catch
            {
                return Task.FromResult(DigitalLaunchResult.ProcessStartFailed);
            }

            started.Exited += OnProcessExited;
            _byRigName[rigName] = started;
        }

        EmitRunningStateIfChanged();
        InstancesChanged?.Invoke();

        // Guard the race where the engine exited around/just before we
        // subscribed: prune it now rather than letting it linger forever.
        if (started.HasExited)
            OnProcessExited(this, EventArgs.Empty);

        return Task.FromResult(DigitalLaunchResult.Success);
    }

    public void Stop(string rigName)
    {
        IDigitalProcess? process;
        lock (_sync)
            _byRigName.TryGetValue(rigName, out process);

        // Review fix #1: request a graceful close but keep the tracked instance
        // until it actually exits. Removal + state emit happen in OnProcessExited,
        // so the instance reads as running (the row stays "Stop") until the engine
        // has saved its .ini and exited — preventing a restart racing the save.
        if (process is not null)
            SafeKill(process);
    }

    public void Stop()
    {
        List<IDigitalProcess> processes;
        lock (_sync)
            processes = _byRigName.Values.ToList();

        foreach (var process in processes)
            SafeKill(process);
    }

    public void Dispose()
    {
        // Stop notifying clients once disposed (app shutdown), but still request
        // a graceful close of any running instances. A late Exited callback must
        // not invoke torn-down subscribers (re-review #2).
        _disposed = true;
        Stop();
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        CleanupExitedInstances();
        EmitRunningStateIfChanged();
        if (!_disposed)
            InstancesChanged?.Invoke();   // per-instance: fires even if aggregate is unchanged
    }

    /// <summary>
    /// Review fix #3/#4: remove, unsubscribe, and dispose every instance that has
    /// exited (whether stopped by us or closed from its own window), so the
    /// dictionary doesn't leak handles or stale entries.
    /// </summary>
    private void CleanupExitedInstances()
    {
        List<IDigitalProcess> exited = [];
        lock (_sync)
        {
            foreach (var key in _byRigName.Where(kv => kv.Value.HasExited).Select(kv => kv.Key).ToList())
            {
                if (_byRigName.Remove(key, out var process))
                    exited.Add(process);
            }
        }

        foreach (var process in exited)
        {
            process.Exited -= OnProcessExited;
            try { process.Dispose(); } catch { /* best-effort cleanup */ }
        }
    }

    private void EmitRunningStateIfChanged()
    {
        var running = IsRunning;
        bool changed;
        lock (_sync)
        {
            changed = running != _lastEmittedRunningState;
            _lastEmittedRunningState = running;
        }

        if (changed && !_disposed)
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
