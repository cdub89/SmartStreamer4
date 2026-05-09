namespace SDRIQStreamer.CWSkimmer;

/// <summary>
/// Per-channel declarative sync between app state and CW Skimmer.
///
/// Any source (panadapter event, slice event, RIT change, startup) calls
/// <see cref="RequestSync"/> with the new desired LO and/or VFO values. A
/// single background loop coalesces updates and sends only what changed —
/// no command is emitted while desired state matches last-sent.
///
/// Replaces the previous mix of event-push, debounce queues, and
/// telnet-layer duplicate suppression. After a successful LO emit the
/// VFO last-sent value is invalidated so a stale QSY (sent against the
/// old LO context before a panadapter recenter event arrived) is
/// re-asserted on the next iteration.
/// </summary>
public sealed class CwSkimmerSyncTracker : IAsyncDisposable
{
    private static readonly TimeSpan CoalesceWindow  = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WakeupPollPeriod = TimeSpan.FromSeconds(5);

    private readonly ICwSkimmerTelnetClient    _telnet;
    private readonly Action<string>?           _onStatus;
    private readonly Action<double, DateTime>? _onQsyEmitted;
    private readonly CancellationTokenSource   _cts = new();
    private readonly SemaphoreSlim             _wakeup = new(0, int.MaxValue);
    private readonly object                    _gate = new();

    private long?    _desiredLoHz;
    private double?  _desiredVfoMHz;
    private long?    _lastSentLoHz;
    private double?  _lastSentVfoMHz;
    private Task?    _runTask;

    public CwSkimmerSyncTracker(
        ICwSkimmerTelnetClient telnet,
        Action<string>? onStatus = null,
        Action<double, DateTime>? onQsyEmitted = null)
    {
        _telnet       = telnet;
        _onStatus     = onStatus;
        _onQsyEmitted = onQsyEmitted;
    }

    public void Start() => _runTask ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>
    /// Update desired skimmer state. Either parameter may be null to leave
    /// it unchanged. Idempotent — safe to call repeatedly with the same
    /// values; the tracker only emits when desired differs from last-sent.
    /// </summary>
    public void RequestSync(long? loHz = null, double? vfoMHz = null)
    {
        if (!loHz.HasValue && !vfoMHz.HasValue)
            return;

        lock (_gate)
        {
            if (loHz.HasValue)   _desiredLoHz   = loHz.Value;
            if (vfoMHz.HasValue) _desiredVfoMHz = vfoMHz.Value;
        }

        try { _wakeup.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException)  { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _wakeup.WaitAsync(WakeupPollPeriod, ct);
            }
            catch (OperationCanceledException) { return; }

            try { await Task.Delay(CoalesceWindow, ct); }
            catch (OperationCanceledException) { return; }

            // Drain extra wakeups queued during the coalesce window so the next
            // wait blocks rather than firing immediately.
            while (_wakeup.Wait(0)) { }

            long?   desiredLo;
            double? desiredVfo;
            lock (_gate)
            {
                desiredLo  = _desiredLoHz;
                desiredVfo = _desiredVfoMHz;
            }

            bool loEmitted = false;
            if (desiredLo.HasValue && desiredLo != _lastSentLoHz)
            {
                try
                {
                    await _telnet.SendLoFreqAsync(desiredLo.Value, ct);
                    _lastSentLoHz = desiredLo;
                    loEmitted = true;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _onStatus?.Invoke($"LO sync failed: {ex.Message}"); }
            }

            // Re-assert VFO/QSY after a successful LO change. FlexLib's slice
            // event can arrive before the matching panadapter-center event;
            // invalidating last-sent here forces a fresh QSY in the new LO
            // context once the LO catches up.
            if (loEmitted)
                _lastSentVfoMHz = null;

            if (desiredVfo.HasValue && desiredVfo != _lastSentVfoMHz)
            {
                try
                {
                    await _telnet.SendQsyAsync(desiredVfo.Value * 1000.0, ct);
                    _lastSentVfoMHz = desiredVfo;
                    _onQsyEmitted?.Invoke(desiredVfo.Value, DateTime.UtcNow);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _onStatus?.Invoke($"QSY sync failed: {ex.Message}"); }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        if (_runTask is not null)
        {
            try { await _runTask.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
        _wakeup.Dispose();
    }
}
