namespace SDRIQStreamer.FlexRadio;

/// <summary>
/// One coalesced reading of the radio's operating telemetry, in the units the
/// operator sees. Every value is nullable because absent is a distinct state
/// from zero here: forward power reports a real <c>0 W</c> on receive and SWR
/// reports a real <c>1.0</c> floor, so neither zero nor one can be overloaded
/// to mean "no reading yet" (issue #59 gating spike, 2026-08-02).
/// </summary>
/// <param name="PowerWatts">Forward RF power, watts. Converted from the dBm the radio reports.</param>
/// <param name="ReflectedPowerWatts">
/// Reflected RF power, watts, converted from the dBm the radio reports
/// (issue #69). Measured by the radio's own REFPWR meter rather than derived
/// from forward power and SWR: the radio has the meter, and a derived figure
/// would inherit the SWR meter's 1.0 floor and read exactly 0 W on every good
/// match.
/// </param>
/// <param name="Swr">Standing wave ratio, as a ratio. Floors at 1.0.</param>
/// <param name="PaTempCelsius">PA temperature, degrees C.</param>
/// <param name="VoltsDc">Supply voltage at the PA, volts.</param>
/// <param name="UpdatedUtc">When this snapshot was taken. <c>default</c> when never populated.</param>
public sealed record RadioTelemetryInfo(
    double? PowerWatts,
    double? ReflectedPowerWatts,
    double? Swr,
    double? PaTempCelsius,
    double? VoltsDc,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>No telemetry received yet. Every value absent, so the display shows dashes.</summary>
    public static RadioTelemetryInfo Empty { get; } = new(null, null, null, null, null, default);
}

/// <summary>Which meter stream a sample came from.</summary>
public enum TelemetryChannel
{
    /// <summary>Forward RF power, in the dBm the radio reports. Converted on the way out.</summary>
    ForwardPowerDbm,

    /// <summary>Reflected RF power, in the dBm the radio reports. Converted on the way out.</summary>
    ReflectedPowerDbm,
    Swr,
    PaTempCelsius,
    VoltsDc
}

/// <summary>Unit conversions between what the radio reports and what the operator reads.</summary>
public static class RadioTelemetryMath
{
    // dBm is referenced to 1 mW and dBW to 1 W, so the two scales sit 30 dB
    // apart; 10 dB is one power decade.
    private const double DbmToDbwOffset = 30.0;
    private const double DecibelsPerDecade = 10.0;

    /// <summary>
    /// Converts the radio's forward-power reading from dBm to watts.
    /// FlexLib documents <c>Radio.ForwardPowerDataReady</c> as dBm
    /// (<c>Radio.cs:7063-7066</c>); the gating spike measured 49.69 dBm on a
    /// 100 W radio, which is 93.1 W.
    /// </summary>
    public static double DbmToWatts(double dbm) =>
        Math.Pow(10, (dbm - DbmToDbwOffset) / DecibelsPerDecade);
}

/// <summary>
/// Coalesces the five independent meter event streams into one snapshot for
/// display. Meter events arrive off the UI thread at two very different rates
/// (forward power, reflected power and SWR at ~13.4 Hz, PA temperature and
/// volts at ~0.4 Hz,
/// measured by the issue #59 spike), so every field is guarded.
/// </summary>
/// <remarks>
/// This type holds no clock and starts no timer: the caller decides when to
/// take a snapshot, which is what makes the coalescing logic unit-testable
/// without a time abstraction. The display cadence is the caller's periodic
/// timer.
/// </remarks>
public sealed class TelemetrySnapshotAccumulator
{
    private readonly object _sync = new();

    // Peak within the current window for the fast pair, reset on every take.
    private double? _windowPeakPowerDbm;
    private double? _windowPeakReflectedDbm;
    private double? _windowPeakSwr;

    // Latest known value for each channel, carried across windows so a snapshot
    // always describes the whole radio rather than only what arrived since the
    // last take. Without this, a window with no sample for a channel would
    // report it absent and the display would flash dashes.
    private double? _powerWatts;
    private double? _reflectedPowerWatts;
    private double? _swr;
    private double? _paTempCelsius;
    private double? _voltsDc;

    private bool _hasPending;

    /// <summary>
    /// Records one meter sample. Safe to call concurrently from the FlexLib
    /// event threads.
    /// </summary>
    public void Add(TelemetryChannel channel, double value)
    {
        lock (_sync)
        {
            switch (channel)
            {
                // Peak-hold across the window rather than last-sample: at
                // ~13.4 Hz a 250 ms display window holds roughly 3.4 samples of
                // a value that swings hard under CW keying, so last-sample
                // would show whichever sample landed on the window boundary and
                // the reading would jitter. Peak-hold is also how an analog
                // power meter behaves.
                case TelemetryChannel.ForwardPowerDbm:
                    _windowPeakPowerDbm = _windowPeakPowerDbm is { } peak ? Math.Max(peak, value) : value;
                    break;

                // Reflected power rides with forward power rather than with the
                // slow pair: it is the same meter family at the same ~13.4 Hz,
                // and under CW keying it swings just as hard, so last-sample
                // would jitter for the same reason.
                case TelemetryChannel.ReflectedPowerDbm:
                    _windowPeakReflectedDbm = _windowPeakReflectedDbm is { } refPeak ? Math.Max(refPeak, value) : value;
                    break;
                case TelemetryChannel.Swr:
                    _windowPeakSwr = _windowPeakSwr is { } peakSwr ? Math.Max(peakSwr, value) : value;
                    break;

                // Last-sample: at ~0.4 Hz a window holds at most one sample, so
                // peak-hold would only make a falling temperature or a sagging
                // supply voltage read stale.
                case TelemetryChannel.PaTempCelsius:
                    _paTempCelsius = value;
                    break;
                case TelemetryChannel.VoltsDc:
                    _voltsDc = value;
                    break;
            }

            _hasPending = true;
        }
    }

    /// <summary>
    /// Takes a snapshot if any sample arrived since the last take, and starts a
    /// new peak-hold window.
    /// </summary>
    /// <returns>
    /// <c>false</c> when nothing arrived since the last take, so the caller can
    /// skip raising a change event rather than republishing an identical
    /// snapshot at the timer rate.
    /// </returns>
    public bool TryTakeSnapshot(DateTimeOffset timestampUtc, out RadioTelemetryInfo snapshot)
    {
        lock (_sync)
        {
            if (!_hasPending)
            {
                snapshot = RadioTelemetryInfo.Empty;
                return false;
            }

            if (_windowPeakPowerDbm is { } peakDbm)
                _powerWatts = RadioTelemetryMath.DbmToWatts(peakDbm);
            if (_windowPeakReflectedDbm is { } peakReflectedDbm)
                _reflectedPowerWatts = RadioTelemetryMath.DbmToWatts(peakReflectedDbm);
            if (_windowPeakSwr is { } peakSwr)
                _swr = peakSwr;

            _windowPeakPowerDbm = null;
            _windowPeakReflectedDbm = null;
            _windowPeakSwr = null;
            _hasPending = false;

            snapshot = new RadioTelemetryInfo(_powerWatts, _reflectedPowerWatts, _swr, _paTempCelsius, _voltsDc, timestampUtc);
            return true;
        }
    }

    /// <summary>Drops all state, so a reconnect starts from dashes rather than stale readings.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _windowPeakPowerDbm = null;
            _windowPeakReflectedDbm = null;
            _windowPeakSwr = null;
            _powerWatts = null;
            _reflectedPowerWatts = null;
            _swr = null;
            _paTempCelsius = null;
            _voltsDc = null;
            _hasPending = false;
        }
    }
}
