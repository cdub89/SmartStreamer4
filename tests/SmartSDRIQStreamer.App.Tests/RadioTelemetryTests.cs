using SDRIQStreamer.FlexRadio;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Covers the issue #59 SmartDeck telemetry core: the dBm-to-watts conversion
/// and the snapshot accumulator's coalescing rules. Both are pure logic with no
/// FlexLib types, so they are unit-testable; the meter subscription itself is
/// FlexLib-facing and covered by the live-radio smoke gate.
/// </summary>
public class RadioTelemetryTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 2, 20, 39, 0, TimeSpan.Zero);

    // ── dBm to watts ─────────────────────────────────────────────────────────

    [Theory]
    // The gating spike's own reading: 49.69 dBm measured on a 100 W radio.
    [InlineData(49.69, 93.1)]
    [InlineData(50.0, 100.0)]
    [InlineData(40.0, 10.0)]
    [InlineData(30.0, 1.0)]
    public void DbmToWatts_converts_known_points(double dbm, double expectedWatts)
    {
        Assert.Equal(expectedWatts, RadioTelemetryMath.DbmToWatts(dbm), precision: 1);
    }

    [Fact]
    public void DbmToWatts_treats_zero_dbm_as_one_milliwatt_not_zero_watts()
    {
        // 0 dBm is the radio's receive-time reading. It is a real measurement of
        // 1 mW, not an absent value, which is why the record uses double? for
        // absent rather than overloading zero.
        Assert.Equal(0.001, RadioTelemetryMath.DbmToWatts(0), precision: 6);
    }

    // ── Absent vs zero ───────────────────────────────────────────────────────

    [Fact]
    public void Empty_snapshot_has_every_value_absent()
    {
        var empty = RadioTelemetryInfo.Empty;

        Assert.Null(empty.PowerWatts);
        Assert.Null(empty.ReflectedPowerWatts);
        Assert.Null(empty.Swr);
        Assert.Null(empty.PaTempCelsius);
        Assert.Null(empty.VoltsDc);
    }

    [Fact]
    public void Zero_readings_stay_distinguishable_from_absent()
    {
        var accumulator = new TelemetrySnapshotAccumulator();
        accumulator.Add(TelemetryChannel.ForwardPowerDbm, 0);

        Assert.True(accumulator.TryTakeSnapshot(At, out var snapshot));

        // Power reported, and reported as a real (tiny) number rather than null.
        Assert.NotNull(snapshot.PowerWatts);
        // Channels that never reported stay absent in the same snapshot.
        Assert.Null(snapshot.PaTempCelsius);
        Assert.Null(snapshot.VoltsDc);
    }

    // ── Coalescing rules ─────────────────────────────────────────────────────

    [Fact]
    public void Power_and_swr_peak_hold_across_the_window()
    {
        var accumulator = new TelemetrySnapshotAccumulator();

        // A ramp that ends below its peak: last-sample would report the trough.
        accumulator.Add(TelemetryChannel.ForwardPowerDbm, 40.0);
        accumulator.Add(TelemetryChannel.ForwardPowerDbm, 50.0);
        accumulator.Add(TelemetryChannel.ForwardPowerDbm, 30.0);
        accumulator.Add(TelemetryChannel.Swr, 1.1);
        accumulator.Add(TelemetryChannel.Swr, 1.8);
        accumulator.Add(TelemetryChannel.Swr, 1.2);

        // Reflected power is a fast meter too (issue #69), so it peak-holds
        // with the other two rather than taking the closing sample.
        accumulator.Add(TelemetryChannel.ReflectedPowerDbm, 20.0);
        accumulator.Add(TelemetryChannel.ReflectedPowerDbm, 33.0);
        accumulator.Add(TelemetryChannel.ReflectedPowerDbm, 10.0);

        Assert.True(accumulator.TryTakeSnapshot(At, out var snapshot));

        Assert.Equal(100.0, snapshot.PowerWatts.GetValueOrDefault(), precision: 1);
        Assert.Equal(1.8, snapshot.Swr.GetValueOrDefault(), precision: 2);
        // 33 dBm is ~2 W: the window peak, not the 10 dBm it closed on.
        Assert.Equal(2.0, snapshot.ReflectedPowerWatts.GetValueOrDefault(), precision: 1);
    }

    // ── Reflected power (issue #69) ──────────────────────────────────────────

    [Fact]
    public void Reflected_power_stays_absent_until_its_own_meter_reports()
    {
        var accumulator = new TelemetrySnapshotAccumulator();

        // Forward power alone must not conjure a reflected reading: the two are
        // separate meters, and deriving one from the other is exactly what this
        // change avoids.
        accumulator.Add(TelemetryChannel.ForwardPowerDbm, 50.0);

        Assert.True(accumulator.TryTakeSnapshot(At, out var snapshot));

        Assert.NotNull(snapshot.PowerWatts);
        Assert.Null(snapshot.ReflectedPowerWatts);
    }

    [Fact]
    public void Reflected_power_carries_across_a_window_with_no_new_sample()
    {
        var accumulator = new TelemetrySnapshotAccumulator();

        accumulator.Add(TelemetryChannel.ReflectedPowerDbm, 33.0);
        Assert.True(accumulator.TryTakeSnapshot(At, out var first));
        Assert.Equal(2.0, first.ReflectedPowerWatts.GetValueOrDefault(), precision: 1);

        // A later window with only forward power must not blank the reflected
        // readout back to dashes; the snapshot describes the whole radio.
        accumulator.Add(TelemetryChannel.ForwardPowerDbm, 50.0);
        Assert.True(accumulator.TryTakeSnapshot(At, out var second));

        Assert.Equal(2.0, second.ReflectedPowerWatts.GetValueOrDefault(), precision: 1);
    }

    [Fact]
    public void Reset_clears_reflected_power_so_a_reconnect_starts_from_dashes()
    {
        var accumulator = new TelemetrySnapshotAccumulator();
        accumulator.Add(TelemetryChannel.ReflectedPowerDbm, 33.0);
        Assert.True(accumulator.TryTakeSnapshot(At, out _));

        accumulator.Reset();

        accumulator.Add(TelemetryChannel.ForwardPowerDbm, 50.0);
        Assert.True(accumulator.TryTakeSnapshot(At, out var snapshot));
        Assert.Null(snapshot.ReflectedPowerWatts);
    }

    [Fact]
    public void Temp_and_volts_take_the_last_sample_not_the_peak()
    {
        var accumulator = new TelemetrySnapshotAccumulator();

        // Both fall during the window. Peak-hold would freeze the earlier,
        // higher reading and hide a sagging supply.
        accumulator.Add(TelemetryChannel.PaTempCelsius, 35.5);
        accumulator.Add(TelemetryChannel.PaTempCelsius, 31.2);
        accumulator.Add(TelemetryChannel.VoltsDc, 14.09);
        accumulator.Add(TelemetryChannel.VoltsDc, 13.64);

        Assert.True(accumulator.TryTakeSnapshot(At, out var snapshot));

        Assert.Equal(31.2, snapshot.PaTempCelsius.GetValueOrDefault(), precision: 2);
        Assert.Equal(13.64, snapshot.VoltsDc.GetValueOrDefault(), precision: 2);
    }

    [Fact]
    public void Peak_hold_window_resets_after_each_snapshot()
    {
        var accumulator = new TelemetrySnapshotAccumulator();

        accumulator.Add(TelemetryChannel.ForwardPowerDbm, 50.0);
        Assert.True(accumulator.TryTakeSnapshot(At, out _));

        // A quieter second window must not inherit the first window's peak.
        accumulator.Add(TelemetryChannel.ForwardPowerDbm, 40.0);
        Assert.True(accumulator.TryTakeSnapshot(At, out var second));

        Assert.Equal(10.0, second.PowerWatts.GetValueOrDefault(), precision: 1);
    }

    [Fact]
    public void Values_carry_across_windows_that_receive_no_sample_for_that_channel()
    {
        var accumulator = new TelemetrySnapshotAccumulator();

        // Volts arrives at ~0.4 Hz, so most 250 ms windows contain no volts
        // sample at all. The snapshot must keep reporting the last known value
        // instead of flashing dashes between updates.
        accumulator.Add(TelemetryChannel.VoltsDc, 13.8);
        Assert.True(accumulator.TryTakeSnapshot(At, out _));

        accumulator.Add(TelemetryChannel.ForwardPowerDbm, 40.0);
        Assert.True(accumulator.TryTakeSnapshot(At, out var second));

        Assert.Equal(13.8, second.VoltsDc.GetValueOrDefault(), precision: 2);
    }

    // ── Take semantics ───────────────────────────────────────────────────────

    [Fact]
    public void TryTakeSnapshot_reports_nothing_pending_before_any_sample()
    {
        var accumulator = new TelemetrySnapshotAccumulator();

        Assert.False(accumulator.TryTakeSnapshot(At, out _));
    }

    [Fact]
    public void TryTakeSnapshot_reports_nothing_pending_on_a_second_take_with_no_new_samples()
    {
        var accumulator = new TelemetrySnapshotAccumulator();
        accumulator.Add(TelemetryChannel.VoltsDc, 13.8);

        Assert.True(accumulator.TryTakeSnapshot(At, out _));
        // Nothing new arrived, so the caller should skip raising a change event
        // rather than republish an identical snapshot at the timer rate.
        Assert.False(accumulator.TryTakeSnapshot(At, out _));
    }

    [Fact]
    public void Snapshot_carries_the_timestamp_it_was_taken_with()
    {
        var accumulator = new TelemetrySnapshotAccumulator();
        accumulator.Add(TelemetryChannel.VoltsDc, 13.8);

        Assert.True(accumulator.TryTakeSnapshot(At, out var snapshot));

        Assert.Equal(At, snapshot.UpdatedUtc);
    }

    [Fact]
    public void Reset_drops_carried_values_so_a_reconnect_starts_from_dashes()
    {
        var accumulator = new TelemetrySnapshotAccumulator();
        accumulator.Add(TelemetryChannel.VoltsDc, 13.8);
        Assert.True(accumulator.TryTakeSnapshot(At, out _));

        accumulator.Reset();

        Assert.False(accumulator.TryTakeSnapshot(At, out _));

        accumulator.Add(TelemetryChannel.ForwardPowerDbm, 40.0);
        Assert.True(accumulator.TryTakeSnapshot(At, out var afterReset));
        Assert.Null(afterReset.VoltsDc);
    }
}
