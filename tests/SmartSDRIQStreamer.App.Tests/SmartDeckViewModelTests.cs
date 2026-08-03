using SDRIQStreamer.App;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Display formatting for the SmartDeck telemetry footer (issue #59, phase 1).
/// Covers the dash-vs-zero rule, which is the whole reason the telemetry record
/// uses nullable values. The window shell, keyboard handling, and always-on-top
/// toggle are UI-only wiring covered by the live-radio smoke gate.
/// </summary>
public class SmartDeckViewModelTests
{
    // ── Absent renders as dashes ─────────────────────────────────────────────

    [Fact]
    public void Absent_values_render_as_dashes()
    {
        Assert.Equal("---", SmartDeckViewModel.FormatPower(null));
        Assert.Equal("---", SmartDeckViewModel.Format(null, "0.0"));
        Assert.Equal("---", SmartDeckViewModel.Format(null, "0"));
    }

    [Fact]
    public void Zero_renders_as_zero_not_dashes()
    {
        // Forward power reports a real 0 W on receive. That is a useful TX-idle
        // signal and must not be confused with "no telemetry".
        Assert.Equal("0.0", SmartDeckViewModel.FormatPower(0));
        Assert.Equal("0.0", SmartDeckViewModel.Format(0, "0.0"));
    }

    // ── Power: watts, per the operator's spec ────────────────────────────────

    [Theory]
    // The gating spike's own peak reading, 49.69 dBm, converts to 93.1 W.
    [InlineData(93.1, "93")]
    [InlineData(100.0, "100")]
    [InlineData(10.0, "10")]
    public void Power_shows_whole_watts_at_ten_and_above(double watts, string expected)
    {
        Assert.Equal(expected, SmartDeckViewModel.FormatPower(watts));
    }

    [Theory]
    [InlineData(5.0, "5.0")]
    [InlineData(0.5, "0.5")]
    public void Power_shows_a_decimal_below_ten_watts_for_qrp(double watts, string expected)
    {
        // A 93 W reading does not need a tenth of a watt, but an operator
        // running 5 W does.
        Assert.Equal(expected, SmartDeckViewModel.FormatPower(watts));
    }

    // ── SWR, temp, volts ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(1.44, "1.4")]
    [InlineData(2.05, "2.1")]
    public void Swr_shows_one_decimal_as_a_ratio_while_transmitting(double swr, string expected)
    {
        Assert.Equal(expected, SmartDeckViewModel.FormatSwr(swr, powerWatts: 93.1));
    }

    [Fact]
    public void Swr_shows_dashes_at_rest_rather_than_its_meter_floor()
    {
        // Operator-reported 2026-08-02: the meter floors at 1.0 on receive, and
        // showing that reads as a real 1:1 match. Dashes rather than 0, since
        // SWR is undefined below 1.0.
        Assert.Equal("---", SmartDeckViewModel.FormatSwr(1.0, powerWatts: 0.001));
    }

    [Fact]
    public void Swr_shows_dashes_when_power_is_absent()
    {
        // No power reading means transmit state is unknown, so SWR cannot be
        // asserted as meaningful.
        Assert.Equal("---", SmartDeckViewModel.FormatSwr(1.5, powerWatts: null));
    }

    [Theory]
    // The lowest real transmit power on these radios is 1 W; the receive floor
    // is 0.001 W. The threshold has clearance on both sides.
    [InlineData(1.0, "1.5")]
    [InlineData(0.5, "1.5")]
    [InlineData(0.001, "---")]
    [InlineData(0.0, "---")]
    public void Swr_visibility_follows_forward_power(double watts, string expected)
    {
        Assert.Equal(expected, SmartDeckViewModel.FormatSwr(1.5, watts));
    }

    [Theory]
    [InlineData(35.52, "36")]
    [InlineData(31.17, "31")]
    public void Temp_shows_whole_degrees(double celsius, string expected)
    {
        Assert.Equal(expected, SmartDeckViewModel.Format(celsius, "0"));
    }

    [Theory]
    [InlineData(13.64, "13.6")]
    [InlineData(14.09, "14.1")]
    public void Volts_shows_one_decimal(double volts, string expected)
    {
        Assert.Equal(expected, SmartDeckViewModel.Format(volts, "0.0"));
    }

    // ── Subscription lifetime ────────────────────────────────────────────────

    [Fact]
    public void Start_and_stop_are_idempotent()
    {
        var connection = new FakeTelemetryConnection();
        var viewModel = new SmartDeckViewModel(connection);

        viewModel.Start();
        viewModel.Start();
        viewModel.Stop();
        viewModel.Stop();

        Assert.Equal(1, connection.StartTelemetryCalls);
        Assert.Equal(1, connection.StopTelemetryCalls);
    }

    [Fact]
    public void Reconnecting_with_the_window_open_rearms_telemetry()
    {
        // Found by the Codex deep audit 2026-08-02. A radio-side drop makes the
        // connection call its own StopTelemetry(), which the ViewModel cannot
        // observe, so without re-arming the footer stayed on dashes until the
        // window was closed and reopened.
        var connection = new FakeTelemetryConnection();
        var viewModel = new SmartDeckViewModel(connection);

        viewModel.Start();
        connection.RaiseConnectionStateChanged(false);
        connection.RaiseConnectionStateChanged(true);

        Assert.Equal(2, connection.StartTelemetryCalls);
    }

    [Fact]
    public void Reconnecting_after_the_window_closes_does_not_restart_telemetry()
    {
        // The window is gone, so nothing should re-subscribe behind it.
        var connection = new FakeTelemetryConnection();
        var viewModel = new SmartDeckViewModel(connection);

        viewModel.Start();
        viewModel.Stop();
        connection.RaiseConnectionStateChanged(true);

        Assert.Equal(1, connection.StartTelemetryCalls);
    }

    [Fact]
    public void Losing_the_connection_alone_does_not_restart_telemetry()
    {
        var connection = new FakeTelemetryConnection();
        var viewModel = new SmartDeckViewModel(connection);

        viewModel.Start();
        connection.RaiseConnectionStateChanged(false);

        Assert.Equal(1, connection.StartTelemetryCalls);
    }
}
