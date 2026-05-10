using SDRIQStreamer.CWSkimmer;

namespace SDRIQStreamer.CWSkimmer.Tests;

/// <summary>
/// Fake ICwSkimmerTelnetClient that records every SendLoFreqAsync /
/// SendQsyAsync call and supports failure injection via LoThrow / QsyThrow.
/// </summary>
internal sealed class FakeTelnetClient : ICwSkimmerTelnetClient
{
    private readonly object _gate = new();
    private readonly List<long>   _loCalls  = new();
    private readonly List<double> _qsyCalls = new();

    public IReadOnlyList<long> LoCalls
    {
        get { lock (_gate) return _loCalls.ToList(); }
    }

    public IReadOnlyList<double> QsyCalls
    {
        get { lock (_gate) return _qsyCalls.ToList(); }
    }

    public Func<long,   Exception?>? LoThrow  { get; set; }
    public Func<double, Exception?>? QsyThrow { get; set; }

    public bool IsConnected => true;

#pragma warning disable CS0067 // events declared by interface but not raised in tests
    public event Action<string>?            StatusChanged;
    public event Action<double>?            FrequencyClicked;
    public event Action<CwSkimmerSpotInfo>? SpotReceived;
#pragma warning restore CS0067

    public Task ConnectAsync(string host, int port, string callsign, string password,
                             CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DisconnectAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task SendLoFreqAsync(long freqHz, CancellationToken ct = default)
    {
        lock (_gate) _loCalls.Add(freqHz);
        var ex = LoThrow?.Invoke(freqHz);
        return ex is null ? Task.CompletedTask : Task.FromException(ex);
    }

    public Task SendQsyAsync(double freqKhz, CancellationToken ct = default)
    {
        lock (_gate) _qsyCalls.Add(freqKhz);
        var ex = QsyThrow?.Invoke(freqKhz);
        return ex is null ? Task.CompletedTask : Task.FromException(ex);
    }

    public async Task WaitForCallsAsync(int loTotal, int qsyTotal, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            int lo, qsy;
            lock (_gate) { lo = _loCalls.Count; qsy = _qsyCalls.Count; }
            if (lo >= loTotal && qsy >= qsyTotal) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(
            $"Expected lo>={loTotal}, qsy>={qsyTotal}; got lo={LoCalls.Count}, qsy={QsyCalls.Count}.");
    }
}

public class CwSkimmerSyncTrackerTests
{
    private static readonly TimeSpan EmitTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task LoChangeWithUnchangedVfo_ReassertsVfoOnSameIteration()
    {
        var fake = new FakeTelnetClient();
        await using var tracker = new CwSkimmerSyncTracker(fake);
        tracker.Start();

        // Establish initial last-sent state for both LO and VFO.
        tracker.RequestSync(loHz: 7_055_000, vfoMHz: 7.055);
        await fake.WaitForCallsAsync(1, 1, EmitTimeout);

        // LO-only change. Layer 1 must invalidate _lastSentVfoMHz so the
        // VFO branch re-emits the same QSY against the new LO context,
        // even though desiredVfo equals the previously-sent value.
        tracker.RequestSync(loHz: 14_200_000);
        await fake.WaitForCallsAsync(2, 2, EmitTimeout);

        Assert.Equal(2, fake.LoCalls.Count);
        Assert.Equal(2, fake.QsyCalls.Count);
        Assert.Equal(14_200_000, fake.LoCalls[1]);
        Assert.Equal(7.055 * 1000.0, fake.QsyCalls[1]);  // tracker converts MHz -> kHz
    }

    [Fact]
    public async Task RepeatedSameVfoNoLo_DoesNotResend()
    {
        var fake = new FakeTelnetClient();
        await using var tracker = new CwSkimmerSyncTracker(fake);
        tracker.Start();

        tracker.RequestSync(loHz: 7_055_000, vfoMHz: 7.055);
        await fake.WaitForCallsAsync(1, 1, EmitTimeout);

        // Repeated identical RequestSyncs must not produce any extra
        // telnet traffic — idempotence is the entire point of the tracker.
        tracker.RequestSync(vfoMHz: 7.055);
        tracker.RequestSync(vfoMHz: 7.055);
        await Task.Delay(250);

        Assert.Single(fake.LoCalls);
        Assert.Single(fake.QsyCalls);
    }

    [Fact]
    public async Task LoChange_TelnetThrowsOnLo_DoesNotInvalidateVfoState()
    {
        var fake = new FakeTelnetClient();
        await using var tracker = new CwSkimmerSyncTracker(fake);
        tracker.Start();

        tracker.RequestSync(loHz: 7_055_000, vfoMHz: 7.055);
        await fake.WaitForCallsAsync(1, 1, EmitTimeout);

        fake.LoThrow = _ => new IOException("simulated telnet failure");

        // LO emit throws → loEmitted stays false → Layer 1 invalidation
        // skipped → VFO branch sees no change → no spurious QSY re-emit.
        tracker.RequestSync(loHz: 14_200_000);
        await Task.Delay(250);

        Assert.Equal(2, fake.LoCalls.Count);   // LO attempt was made (and threw)
        Assert.Single(fake.QsyCalls);          // QSY did not re-fire
    }
}
