using System.Collections.Specialized;
using SDRIQStreamer.App;
using SDRIQStreamer.FlexRadio;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Display formatting for the SmartDeck telemetry footer (issue #59, phase 1).
/// Covers the dash-vs-zero rule, which is the whole reason the telemetry record
/// uses nullable values. The window shell, keyboard handling, and always-on-top
/// toggle are UI-only wiring covered by the live-radio smoke gate.
/// </summary>
public class SmartDeckViewModelTests
{
    private const string TestStation = "SUPERWIN10";
    private const string OtherStation = "WX7V-M";

    private static SliceInfo Slice(
        string letter,
        string station = TestStation,
        string mode = "CW",
        string rxAnt = "ANT1",
        string txAnt = "ANT1",
        double freqMhz = 14.050,
        int agcThreshold = 50,
        int tuneStepHz = 0,
        bool isTransmitSlice = false,
        bool ritEnabled = false,
        double ritOffsetHz = 0,
        bool xitEnabled = false,
        double xitOffsetHz = 0,
        bool apfOn = false,
        bool nrOn = false,
        bool nbOn = false,
        bool diversityOn = false) =>
        new(letter, mode, freqMhz, ritEnabled, ritOffsetHz, tuneStepHz, PanadapterStreamId: 100, ClientStation: station)
        {
            RxAntenna = rxAnt,
            TxAntenna = txAnt,
            AgcThreshold = agcThreshold,
            RxAntennaOptions = ["ANT1", "ANT2", "RX_A"],
            TxAntennaOptions = ["ANT1", "ANT2"],
            IsTransmitSlice = isTransmitSlice,
            XitEnabled = xitEnabled,
            XitOffsetHz = xitOffsetHz,
            ApfOn = apfOn,
            NrOn = nrOn,
            NbOn = nbOn,
            DiversityOn = diversityOn
        };

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
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

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
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

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
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();
        viewModel.Stop();
        connection.RaiseConnectionStateChanged(true);

        Assert.Equal(1, connection.StartTelemetryCalls);
    }

    [Fact]
    public void Losing_the_connection_alone_does_not_restart_telemetry()
    {
        var connection = new FakeTelemetryConnection();
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();
        connection.RaiseConnectionStateChanged(false);

        Assert.Equal(1, connection.StartTelemetryCalls);
    }

    // ── Mode discriminator mapping ───────────────────────────────────────────

    [Theory]
    [InlineData(SliceMode.Cw, "CW")]
    [InlineData(SliceMode.Usb, "USB")]
    [InlineData(SliceMode.Lsb, "LSB")]
    [InlineData(SliceMode.Am, "AM")]
    public void Mode_maps_to_the_radios_wire_value(SliceMode mode, string expected)
    {
        Assert.Equal(expected, mode.ToRadioValue());
    }

    [Theory]
    [InlineData("CW", SliceMode.Cw)]
    [InlineData("usb", SliceMode.Usb)]
    [InlineData(" LSB ", SliceMode.Lsb)]
    public void Mode_parses_back_from_the_radios_wire_value(string wire, SliceMode expected)
    {
        Assert.Equal(expected, SliceModes.FromRadioValue(wire));
    }

    [Theory]
    // Real modes the radio supports that SmartDeck deliberately does not offer,
    // plus the never-reported case. All are absent, not errors.
    [InlineData("DIGU")]
    [InlineData("RTTY")]
    [InlineData("")]
    [InlineData(null)]
    public void Modes_smartdeck_does_not_offer_map_to_absent(string? wire)
    {
        Assert.Null(SliceModes.FromRadioValue(wire));
    }

    // ── Slice control surface (phase 2a) ─────────────────────────────────────

    [Fact]
    public void Slice_list_is_scoped_to_the_control_station()
    {
        // Matches how slice sync and pan visibility already filter, so SmartDeck
        // cannot reach a second station's slice.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"), Slice("B", station: OtherStation), Slice("C"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Equal(["A", "C"], viewModel.Slices.Select(s => s.Letter));
    }

    [Fact]
    public void Slice_list_is_ordered_by_letter()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("C"), Slice("A"), Slice("B"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Equal(["A", "B", "C"], viewModel.Slices.Select(s => s.Letter));
    }

    [Fact]
    public void First_slice_is_selected_by_default()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"), Slice("B"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Equal("A", viewModel.SelectedSlice?.Letter);
        Assert.True(viewModel.HasSelectedSlice);
    }

    [Theory]
    [InlineData("Shack", "Maestro-C", "SmartDeck Shack : Maestro-C")]
    [InlineData("Shack", "", "SmartDeck Shack")]
    // A nickname is optional on the radio; the fake's model stands in for it.
    [InlineData("", "Maestro-C", "SmartDeck FLEX-6400M : Maestro-C")]
    public void Title_names_the_radio_and_the_control_station(string nickname, string station, string expected)
    {
        // Issue #83.
        var connection = new FakeTelemetryConnection { ConnectedNickname = nickname };
        var viewModel = new SmartDeckViewModel(connection, station, postToUi: action => action());

        Assert.Equal(expected, viewModel.WindowTitle);
    }

    [Fact]
    public void Title_follows_a_disconnect_and_a_swap_to_a_different_radio()
    {
        // The deck stays open across a disconnect, and nothing else republishes
        // the radio's identity, so the title is re-raised on both edges.
        var connection = new FakeTelemetryConnection { ConnectedNickname = "Shack" };
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();
        var raised = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SmartDeckViewModel.WindowTitle)) raised++;
        };

        connection.RaiseConnectionStateChanged(false);
        Assert.Equal("SmartDeck Disconnected", viewModel.WindowTitle);

        connection.ConnectedNickname = "Contest";
        connection.RaiseConnectionStateChanged(true);

        Assert.Equal(2, raised);
        Assert.Equal($"SmartDeck Contest : {TestStation}", viewModel.WindowTitle);
    }

    [Fact]
    public void No_slices_leaves_the_controls_without_a_target()
    {
        var connection = new FakeTelemetryConnection();
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Null(viewModel.SelectedSlice);
        Assert.False(viewModel.HasSelectedSlice);
    }

    [Fact]
    public void Selecting_a_slice_adopts_its_mode_and_antennas()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", mode: "USB", rxAnt: "ANT2", txAnt: "ANT2"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Equal(SliceMode.Usb, viewModel.CurrentMode);
        Assert.Equal("ANT2", viewModel.SelectedRxAntenna);
        Assert.Equal("ANT2", viewModel.SelectedTxAntenna);
    }

    [Fact]
    public void Adopting_a_slices_state_does_not_write_it_back_to_the_radio()
    {
        // The selectors are two-way bound, so echoing the radio's own value back
        // as a command would be a write storm on every slice update.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", rxAnt: "ANT2"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Empty(connection.RxAntennaWrites);
        Assert.Empty(connection.TxAntennaWrites);
    }

    [Fact]
    public void A_mode_in_a_mode_smartdeck_does_not_offer_leaves_no_button_lit()
    {
        // A slice sitting in DIGU is valid; SmartDeck simply offers no button
        // for it, which is absent rather than an error.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", mode: "DIGU"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Null(viewModel.CurrentMode);
    }

    [Fact]
    public async Task Cycling_the_mode_writes_to_the_selected_slice()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"), Slice("B", mode: "CW"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();
        viewModel.SelectedSlice = viewModel.Slices.Single(s => s.Letter == "B");

        await viewModel.CycleModeCommand.ExecuteAsync(null);

        var (slice, mode) = Assert.Single(connection.ModeWrites);
        Assert.Equal("B", slice.Letter);
        Assert.Equal(SliceMode.Lsb, mode);
        Assert.Equal(SliceMode.Lsb, viewModel.CurrentMode);
    }

    [Fact]
    public async Task Cycling_the_mode_with_no_slice_selected_writes_nothing()
    {
        var connection = new FakeTelemetryConnection();
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        await viewModel.CycleModeCommand.ExecuteAsync(null);

        Assert.Empty(connection.ModeWrites);
    }

    [Theory]
    [InlineData(SliceMode.Cw, SliceMode.Lsb)]
    [InlineData(SliceMode.Lsb, SliceMode.Usb)]
    [InlineData(SliceMode.Usb, SliceMode.Am)]
    // Wraps rather than sticking at the end: the readout is the whole mode
    // surface now, so a cycle that dead-ends at AM would strand the operator.
    [InlineData(SliceMode.Am, SliceMode.Cw)]
    public void The_mode_cycle_runs_in_the_operators_order_and_wraps(SliceMode current, SliceMode expected)
    {
        Assert.Equal(expected, SmartDeckViewModel.NextMode(current));
    }

    [Fact]
    public void A_mode_smartdeck_does_not_offer_enters_the_cycle_at_the_start()
    {
        // A slice sitting in DIGU under WSJT-X is valid and outside the cycle.
        // It has to be clickable anyway: the mode buttons that used to offer a
        // way back to CW are gone, so a dead end here would be a regression.
        Assert.Equal(SliceMode.Cw, SmartDeckViewModel.NextMode(null));
    }

    [Fact]
    public void Choosing_an_antenna_writes_to_the_selected_slice()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        viewModel.SelectedRxAntenna = "RX_A";
        viewModel.SelectedTxAntenna = "ANT2";

        var (rxSlice, rxAntenna) = Assert.Single(connection.RxAntennaWrites);
        Assert.Equal("A", rxSlice.Letter);
        Assert.Equal("RX_A", rxAntenna);

        var (_, txAntenna) = Assert.Single(connection.TxAntennaWrites);
        Assert.Equal("ANT2", txAntenna);
    }

    [Fact]
    public void Selection_survives_a_slice_list_refresh()
    {
        // Sticky by design: with Skimmer on one slice and WSJT-X on another, a
        // selection that moved on its own would be worse than useless.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"), Slice("B"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();
        viewModel.SelectedSlice = viewModel.Slices.Single(s => s.Letter == "B");

        // A slice update republishes the whole list.
        connection.SetSlices(Slice("A"), Slice("B", mode: "USB"));
        connection.RaiseSliceUpdated(Slice("B", mode: "USB"));

        Assert.Equal("B", viewModel.SelectedSlice?.Letter);
        Assert.Equal(SliceMode.Usb, viewModel.CurrentMode);
    }

    /// <summary>
    /// Mimics what a bound ComboBox does to the ViewModel: when the ItemsSource
    /// is cleared, the control writes null back through the two-way SelectedItem
    /// binding, because the selected item genuinely is not in the list at that
    /// instant. Tests that skip this pass against selection bugs that the real
    /// UI hits, which is exactly what happened with the phase 2b regression
    /// below.
    /// </summary>
    private static void AttachComboBoxSelectionBehaviour(SmartDeckViewModel viewModel)
    {
        viewModel.Slices.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                viewModel.SelectedSlice = null;
        };
    }

    [Fact]
    public async Task Selecting_a_second_slice_and_changing_band_keeps_that_slice_selected()
    {
        // Operator-reported 2026-08-02: with two slices, selecting slice B and
        // pressing a band button snapped the selector back to slice A. The band
        // write echoes back as SliceUpdated, which rebuilds the list, and the
        // rebuild was losing the selection.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 14.031_5), Slice("B", freqMhz: 7.118));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();
        AttachComboBoxSelectionBehaviour(viewModel);

        viewModel.SelectedSlice = viewModel.Slices.Single(s => s.Letter == "B");
        await viewModel.SelectBandCommand.ExecuteAsync("20m");

        // The radio echoes the new frequency for slice B.
        connection.SetSlices(Slice("A", freqMhz: 14.031_5), Slice("B", freqMhz: 14.050));
        connection.RaiseSliceUpdated(Slice("B", freqMhz: 14.050));

        Assert.Equal("B", viewModel.SelectedSlice?.Letter);
    }

    [Fact]
    public void Selection_survives_a_refresh_even_when_the_control_nulls_it_on_clear()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"), Slice("B"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();
        AttachComboBoxSelectionBehaviour(viewModel);

        viewModel.SelectedSlice = viewModel.Slices.Single(s => s.Letter == "B");
        connection.RaiseSliceUpdated(Slice("A"));

        Assert.Equal("B", viewModel.SelectedSlice?.Letter);
    }

    // ── RF gain (phase 2c) ───────────────────────────────────────────────────

    private static PanadapterInfo Pan(int rfGain = 0, bool withRange = true) =>
        new(StreamId: 100, CenterFreqMHz: 14.05, DAXIQChannel: 1, ClientStation: TestStation)
        {
            RfGain = rfGain,
            RfGainLow = withRange ? -8 : 0,
            RfGainHigh = withRange ? 32 : 0,
            RfGainStep = withRange ? 4 : 0
        };

    [Fact]
    public void Rf_gain_reads_from_the_panadapter_behind_the_selected_slice()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.SetPanadapters(Pan(rfGain: 12));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Equal("12 dB", viewModel.RfGainText);
        Assert.True(viewModel.CanAdjustRfGain);
    }

    [Fact]
    public void Rf_gain_is_unavailable_until_the_radio_reports_a_range()
    {
        // GetRFGainInfo() is an explicit request whose reply arrives after the
        // panadapter is first tracked, so there is a window with no range.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.SetPanadapters(Pan(withRange: false));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.False(viewModel.CanAdjustRfGain);
        Assert.Equal("---", viewModel.RfGainText);
    }

    [Fact]
    public void Rf_gain_becomes_available_when_the_range_reply_arrives()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.SetPanadapters(Pan(withRange: false));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        connection.SetPanadapters(Pan(rfGain: 8));
        connection.RaisePanadapterUpdated(Pan(rfGain: 8));

        Assert.True(viewModel.CanAdjustRfGain);
        Assert.Equal("8 dB", viewModel.RfGainText);
    }

    [Fact]
    public async Task Rf_gain_up_writes_one_step_higher()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.SetPanadapters(Pan(rfGain: 12));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        await viewModel.RfGainUpCommand.ExecuteAsync(null);

        var (pan, gain) = Assert.Single(connection.RfGainWrites);
        Assert.Equal(100u, pan.StreamId);
        Assert.Equal(16, gain);
        Assert.Equal("16 dB", viewModel.RfGainText);
    }

    [Fact]
    public async Task Rf_gain_down_writes_one_step_lower()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.SetPanadapters(Pan(rfGain: 12));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        await viewModel.RfGainDownCommand.ExecuteAsync(null);

        Assert.Equal(8, Assert.Single(connection.RfGainWrites).RfGain);
    }

    [Fact]
    public async Task Rf_gain_at_the_ceiling_writes_nothing()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.SetPanadapters(Pan(rfGain: 32));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        await viewModel.RfGainUpCommand.ExecuteAsync(null);

        Assert.Empty(connection.RfGainWrites);
    }

    [Fact]
    public async Task Rf_gain_with_no_panadapter_behind_the_slice_writes_nothing()
    {
        // A slice whose panadapter is not in the list, e.g. mid-teardown.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        await viewModel.RfGainUpCommand.ExecuteAsync(null);

        Assert.Empty(connection.RfGainWrites);
        Assert.False(viewModel.CanAdjustRfGain);
    }

    // ── AGC-T ────────────────────────────────────────────────────────────────

    [Fact]
    public void Agc_threshold_reads_from_the_selected_slice()
    {
        // Slice-scoped, unlike RF gain, and always known: the 0-100 range is
        // fixed by the protocol so there is no "not yet reported" state.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", agcThreshold: 65));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Equal("65", viewModel.AgcThresholdText);
    }

    [Fact]
    public async Task Agc_threshold_up_writes_one_step_higher()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", agcThreshold: 50));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        await viewModel.AgcThresholdUpCommand.ExecuteAsync(null);

        var (slice, threshold) = Assert.Single(connection.AgcThresholdWrites);
        Assert.Equal("A", slice.Letter);
        Assert.Equal(55, threshold);
        Assert.Equal("55", viewModel.AgcThresholdText);
    }

    [Fact]
    public async Task Agc_threshold_down_writes_one_step_lower()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", agcThreshold: 50));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        await viewModel.AgcThresholdDownCommand.ExecuteAsync(null);

        Assert.Equal(45, Assert.Single(connection.AgcThresholdWrites).Threshold);
    }

    [Theory]
    // The protocol range is 0-100 and the step is 5, so both ends are reachable
    // exactly and stepping past them writes nothing.
    [InlineData(98, 1, 100)]
    [InlineData(2, -1, 0)]
    public async Task Agc_threshold_clamps_to_the_protocol_range(int start, int direction, int expected)
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", agcThreshold: start));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        var command = direction > 0 ? viewModel.AgcThresholdUpCommand : viewModel.AgcThresholdDownCommand;
        await command.ExecuteAsync(null);

        Assert.Equal(expected, Assert.Single(connection.AgcThresholdWrites).Threshold);
    }

    [Theory]
    [InlineData(100, 1)]
    [InlineData(0, -1)]
    public async Task Agc_threshold_at_a_limit_writes_nothing(int start, int direction)
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", agcThreshold: start));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        var command = direction > 0 ? viewModel.AgcThresholdUpCommand : viewModel.AgcThresholdDownCommand;
        await command.ExecuteAsync(null);

        Assert.Empty(connection.AgcThresholdWrites);
    }

    [Fact]
    public async Task Agc_threshold_with_no_slice_selected_writes_nothing()
    {
        var connection = new FakeTelemetryConnection();
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        await viewModel.AgcThresholdUpCommand.ExecuteAsync(null);

        Assert.Empty(connection.AgcThresholdWrites);
        Assert.Equal("---", viewModel.AgcThresholdText);
    }

    // ── Band buttons (phase 2b) ──────────────────────────────────────────────

    [Fact]
    public async Task Pressing_a_band_tunes_the_selected_slice_to_that_bands_default()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 14.031_5));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        await viewModel.SelectBandCommand.ExecuteAsync("40m");

        var (slice, freq) = Assert.Single(connection.FrequencyWrites);
        Assert.Equal("A", slice.Letter);
        Assert.Equal(7.055, freq);
        Assert.Equal("40m", viewModel.CurrentBand);
    }

    [Fact]
    public async Task Pressing_a_band_you_have_used_before_returns_to_where_you_left_it()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 14.031_5));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        // Leave 20m at 14.0315, then come back to it from 40m.
        await viewModel.SelectBandCommand.ExecuteAsync("40m");
        connection.SetSlices(Slice("A", freqMhz: 7.055));
        connection.RaiseSliceUpdated(Slice("A", freqMhz: 7.055));
        await viewModel.SelectBandCommand.ExecuteAsync("20m");

        Assert.Equal(14.031_5, connection.FrequencyWrites[^1].FreqMHz);
    }

    [Fact]
    public async Task Pressing_a_band_with_no_slice_selected_writes_nothing()
    {
        var connection = new FakeTelemetryConnection();
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        await viewModel.SelectBandCommand.ExecuteAsync("40m");

        Assert.Empty(connection.FrequencyWrites);
    }

    [Fact]
    public void Current_band_follows_the_selected_slices_frequency()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 7.030_7));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Equal("40m", viewModel.CurrentBand);
    }

    [Fact]
    public void Selection_falls_back_when_the_selected_slice_disappears()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"), Slice("B"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();
        viewModel.SelectedSlice = viewModel.Slices.Single(s => s.Letter == "B");

        connection.SetSlices(Slice("A"));
        connection.RaiseSliceRemoved(Slice("B"));

        Assert.Equal("A", viewModel.SelectedSlice?.Letter);
    }

    // ── Button state for the layout pass ─────────────────────────────────────
    // Every group on the deck is a row of buttons that has to show which value
    // the radio currently holds. Before the layout pass the controls were
    // write-only, so "what am I on" could only be answered by opening a
    // dropdown. These cover the lit state itself; the XAML that renders it is
    // UI-only wiring covered by the live-radio smoke gate.

    [Theory]
    [InlineData(14.050, "14.050.000")]
    [InlineData(1.8125, "1.812.500")]
    [InlineData(28.0, "28.000.000")]
    [InlineData(7.055, "7.055.000")]
    public void Frequency_reads_grouped_the_way_smartsdr_groups_it(double mhz, string expected)
    {
        Assert.Equal(expected, SmartDeckViewModel.FormatFrequency(mhz));
    }

    [Fact]
    public void No_slice_leaves_the_header_frequency_absent()
    {
        var connection = new FakeTelemetryConnection();
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Equal("---", viewModel.FrequencyText);
        Assert.Equal(string.Empty, viewModel.ModeText);
    }

    [Fact]
    public void The_band_holding_the_slice_is_the_only_one_lit()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 7.055));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Equal("40m", Assert.Single(viewModel.BandOptions, band => band.IsCurrent).Label);
    }

    [Fact]
    public void The_header_names_the_slices_mode()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", mode: "LSB"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Equal("LSB", viewModel.ModeText);
    }

    [Fact]
    public void A_mode_smartdeck_does_not_offer_still_reads_out_as_the_radio_names_it()
    {
        // A slice sitting in DIGU is valid. The readout replaced the mode
        // buttons, so falling back to blank would leave the operator unable to
        // see the mode at all.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", mode: "DIGU"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Null(viewModel.CurrentMode);
        Assert.Equal("DIGU", viewModel.ModeText);
    }

    [Fact]
    public void Antenna_buttons_come_from_the_radios_own_options_with_the_current_one_lit()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", rxAnt: "RX_A", txAnt: "ANT2"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Equal(["ANT1", "ANT2", "RX_A"], viewModel.RxAntennaButtons.Select(button => button.Label));
        Assert.Equal("RX_A", Assert.Single(viewModel.RxAntennaButtons, button => button.IsCurrent).Label);
        Assert.Equal("ANT2", Assert.Single(viewModel.TxAntennaButtons, button => button.IsCurrent).Label);
    }

    // The radio offers XVTA/XVTB on every slice. Issue #64 hides them: they cost
    // a button each in a window whose height is the scarce resource, and the
    // operator base does not run transverters.
    // Carries both spellings at once (XVTA/XVTB per APD.cs, and the older XVTR)
    // rather than what one radio would really report, so the test pins the rule
    // "no transverter ports" rather than a list of port names.
    private static SliceInfo SliceWithTransverterPorts(string rxAnt, string txAnt) =>
        Slice("A", rxAnt: rxAnt, txAnt: txAnt) with
        {
            RxAntennaOptions = ["ANT1", "ANT2", "RX_A", "XVTA", "XVTB", "XVTR"],
            TxAntennaOptions = ["ANT1", "ANT2", "XVTA", "XVTR"]
        };

    [Fact]
    public void Transverter_ports_the_radio_is_not_using_are_hidden()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(SliceWithTransverterPorts(rxAnt: "RX_A", txAnt: "ANT2"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());

        viewModel.Start();

        Assert.Equal(["ANT1", "ANT2", "RX_A"], viewModel.RxAntennaButtons.Select(button => button.Label));
        Assert.Equal(["ANT1", "ANT2"], viewModel.TxAntennaButtons.Select(button => button.Label));
    }

    [Fact]
    public void A_transverter_port_the_radio_currently_holds_stays_visible_until_the_radio_leaves_it()
    {
        // Hiding the selected antenna would leave the group with no lit button
        // and no way to move off the transverter from the deck. The radio wins:
        // the port appears while the radio holds it and goes once it does not.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(SliceWithTransverterPorts(rxAnt: "XVTA", txAnt: "XVTA"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        Assert.Equal(["ANT1", "ANT2", "RX_A", "XVTA"], viewModel.RxAntennaButtons.Select(button => button.Label));
        Assert.Equal("XVTA", Assert.Single(viewModel.RxAntennaButtons, button => button.IsCurrent).Label);
        Assert.Equal("XVTA", Assert.Single(viewModel.TxAntennaButtons, button => button.IsCurrent).Label);

        var movedOff = SliceWithTransverterPorts(rxAnt: "ANT1", txAnt: "ANT1");
        connection.SetSlices(movedOff);
        connection.RaiseSliceUpdated(movedOff);

        Assert.Equal(["ANT1", "ANT2", "RX_A"], viewModel.RxAntennaButtons.Select(button => button.Label));
        Assert.Equal(["ANT1", "ANT2"], viewModel.TxAntennaButtons.Select(button => button.Label));
    }

    // ── TX power (the PWR cell) ──────────────────────────────────────────────

    // The QRP/QRO preset button (issues #64, #70) was removed outright on
    // 2026-09-20, and the fifteen tests that pinned its label, lit state and
    // press behaviour went with it. What is left is what the deck still does:
    // show the radio's power setting and steer it by wheel. The wheel's own
    // arithmetic is pinned further down, under "TX power wheel". Do not
    // reintroduce a preset or a remembered power.

    private static (FakeTelemetryConnection Connection, SmartDeckViewModel ViewModel) DeckAtPower(int? watts)
        => DeckAtPower(watts, maxWatts: 100);

    /// <summary>
    /// A deck on a radio rated <paramref name="maxWatts"/>. Issue #77: every
    /// test above this one runs at 100 W, where the radio's percentage setting
    /// and its wattage are the same integer. Pass 500 for an Aurora, where they
    /// are not, which is the case the old hard-coded ceiling got wrong.
    /// </summary>
    private static (FakeTelemetryConnection Connection, SmartDeckViewModel ViewModel) DeckAtPower(int? watts, int maxWatts)
    {
        var connection = new FakeTelemetryConnection { MaxRfPowerWatts = maxWatts };
        connection.SetSlices(Slice("A"));
        connection.ReportRfPower(watts);
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();
        return (connection, viewModel);
    }

    [Fact]
    public void The_wheel_steps_one_percent_of_the_radios_rating()
    {
        // A 1 W step on a 500 W PA is not expressible, so the radio would
        // quantise four notches out of five back to where they started and the
        // wheel would look stuck. Stepping 5 W keeps every notch a real move.
        var (_, viewModel) = DeckAtPower(250, maxWatts: 500);

        viewModel.NudgeTxPower(1);
        Assert.Equal("255 W", viewModel.TxPowerText);

        viewModel.NudgeTxPower(-3);
        Assert.Equal("240 W", viewModel.TxPowerText);
    }

    [Fact]
    public void The_wheel_cannot_steer_past_the_radios_rating()
    {
        var (_, viewModel) = DeckAtPower(490, maxWatts: 500);

        viewModel.NudgeTxPower(50);

        Assert.Equal("500 W", viewModel.TxPowerText);
    }

    [Fact]
    public void A_power_change_from_elsewhere_shows_in_the_PWR_cell()
    {
        // The deck holds no power of its own, so a change made in SmartSDR or on
        // the Maestro simply shows, and the next wheel notch steps from it.
        var (connection, viewModel) = DeckAtPower(75);
        Assert.Equal("75 W", viewModel.TxPowerText);

        connection.ReportRfPower(30);          // operator moved it in SmartSDR

        Assert.Equal("30 W", viewModel.TxPowerText);
        Assert.Empty(connection.RfPowerWrites);   // showing it writes nothing back
    }

    [Fact]
    public void A_disconnect_leaves_nothing_behind_to_write_into_the_next_session()
    {
        // Regression kept from the QRP button era (Codex deep audit, 2026-08-04):
        // a power held across a drop was written into the next session. The
        // button is gone, but the wheel could do the same if the deck went on
        // believing the old power, so the guard moves to the PWR cell.
        //
        // The settle is held open so a notch is genuinely in flight when the
        // radio drops, which is the only moment this can go wrong.
        var settle = new TaskCompletionSource();
        var connection = new FakeTelemetryConnection { MaxRfPowerWatts = 100 };
        connection.SetSlices(Slice("A"));
        connection.ReportRfPower(75);
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: _ => settle.Task);
        viewModel.Start();

        viewModel.NudgeTxPower(-1);               // 74 W, waiting out the settle
        Assert.Equal("74 W", viewModel.TxPowerText);

        connection.ReportRfPower(null);           // the radio drops

        Assert.False(viewModel.CanAdjustTxPower);
        Assert.Equal("---", viewModel.TxPowerText);

        viewModel.NudgeTxPower(-1);               // wheel while disconnected
        Assert.Equal("---", viewModel.TxPowerText);   // must not step on from the stale 74

        settle.SetResult();                       // the in-flight notch comes due
        Assert.Empty(connection.RfPowerWrites);   // and writes nothing

        connection.ReportRfPower(50);             // the next session's own power
        Assert.True(viewModel.CanAdjustTxPower);
        Assert.Equal("50 W", viewModel.TxPowerText);
        Assert.Empty(connection.RfPowerWrites);
    }

    [Fact]
    public void Pressing_an_antenna_button_writes_it_and_moves_the_lit_state()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", rxAnt: "ANT1"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        viewModel.SelectRxAntennaCommand.Execute("ANT2");

        Assert.Equal("ANT2", Assert.Single(connection.RxAntennaWrites).Antenna);
        Assert.Equal("ANT2", Assert.Single(viewModel.RxAntennaButtons, button => button.IsCurrent).Label);
    }

    [Fact]
    public void The_selected_slice_is_the_only_chip_lit()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"), Slice("B"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        viewModel.SelectSliceCommand.Execute("B");

        Assert.Equal("B", viewModel.SelectedSlice?.Letter);
        Assert.Equal("B", Assert.Single(viewModel.SliceOptions, slice => slice.IsCurrent).Label);
    }

    // ── Per-band state memory ────────────────────────────────────────────────
    // A band button restores frequency, mode, both antennas and AGC-T. The
    // radio cannot do this for us: a real band change on the radio tears the
    // slice down and rebuilds it from slice persistence, and SmartDeck
    // deliberately never does that, because Skimmer and WSJT-X are bound per
    // slice. RF gain is excluded: it is panadapter-scoped.

    [Fact]
    public void A_bands_first_visit_tunes_it_and_changes_nothing_else()
    {
        // Nothing is remembered yet, so guessing a mode or antenna would be
        // worse than leaving the radio as the operator set it.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 14.031_5));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), bandMemory: new BandMemory(), settle: _ => Task.CompletedTask);
        viewModel.Start();

        viewModel.SelectBandCommand.Execute("40m");

        Assert.Equal(7.055, Assert.Single(connection.FrequencyWrites).FreqMHz);
        Assert.Empty(connection.ModeWrites);
        Assert.Empty(connection.RxAntennaWrites);
        Assert.Empty(connection.TxAntennaWrites);
        Assert.Empty(connection.AgcThresholdWrites);
    }

    [Fact]
    public void Returning_to_a_band_restores_everything_it_was_left_with()
    {
        var memory = new BandMemory(new Dictionary<string, BandState>
        {
            ["40m"] = new(7.118, SliceMode.Lsb, "ANT2", "XVTR", 30),
        });
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", mode: "CW", rxAnt: "ANT1", txAnt: "ANT1", freqMhz: 14.031_5));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), bandMemory: memory, settle: _ => Task.CompletedTask);
        viewModel.Start();

        viewModel.SelectBandCommand.Execute("40m");

        Assert.Equal(7.118, Assert.Single(connection.FrequencyWrites).FreqMHz);
        Assert.Equal(SliceMode.Lsb, Assert.Single(connection.ModeWrites).Mode);
        Assert.Equal("ANT2", Assert.Single(connection.RxAntennaWrites).Antenna);
        Assert.Equal("XVTR", Assert.Single(connection.TxAntennaWrites).Antenna);
        Assert.Equal(30, Assert.Single(connection.AgcThresholdWrites).Threshold);
    }

    [Fact]
    public void Leaving_a_band_captures_its_full_state_for_next_time()
    {
        var store = new Dictionary<string, BandState>();
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(
            Slice("A", mode: "CW", rxAnt: "RX_A", txAnt: "ANT2", freqMhz: 14.031_5, agcThreshold: 65));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), bandMemory: new BandMemory(store), settle: _ => Task.CompletedTask);
        viewModel.Start();

        viewModel.SelectBandCommand.Execute("40m");

        var remembered = Assert.Contains("20m", store);
        Assert.Equal(14.031_5, remembered.FreqMhz);
        Assert.Equal(SliceMode.Cw, remembered.Mode);
        Assert.Equal("RX_A", remembered.RxAntenna);
        Assert.Equal("ANT2", remembered.TxAntenna);
        Assert.Equal(65, remembered.AgcThreshold);
    }

    [Fact]
    public void Leaving_a_band_in_a_mode_smartdeck_does_not_offer_records_no_mode()
    {
        // DIGU is WSJT-X's business. Recording it would need an untyped mode
        // write to restore, so the band remembers everything except the mode.
        var store = new Dictionary<string, BandState>();
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", mode: "DIGU", rxAnt: "ANT1", freqMhz: 14.074));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), bandMemory: new BandMemory(store), settle: _ => Task.CompletedTask);
        viewModel.Start();

        viewModel.SelectBandCommand.Execute("40m");

        var remembered = Assert.Contains("20m", store);
        Assert.Null(remembered.Mode);
        Assert.Equal("ANT1", remembered.RxAntenna);
    }

    [Fact]
    public void A_band_press_logs_one_line_naming_what_it_restored()
    {
        // One line per press, not one per write. The frequency and mode writes
        // are deliberately not logged individually: SetSliceFrequencyAsync is
        // also the CW Skimmer spot-click path and would swamp the log.
        var memory = new BandMemory(new Dictionary<string, BandState>
        {
            ["40m"] = new(7.118, SliceMode.Lsb, "ANT2", "XVTR", 30),
        });
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 14.031_5));
        List<string> log = [];
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), bandMemory: memory, logStatus: log.Add, settle: _ => Task.CompletedTask);
        viewModel.Start();

        viewModel.SelectBandCommand.Execute("40m");

        Assert.Equal("Band 40m: 7.118.000 MHz, mode LSB, RX ANT2, TX XVTR, AGC-T 30", Assert.Single(log));
    }

    [Fact]
    public void A_first_visit_logs_the_frequency_alone()
    {
        // Nothing else was restored, and the bare line says so.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 14.031_5));
        List<string> log = [];
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(),
            bandMemory: new BandMemory(), logStatus: log.Add, settle: _ => Task.CompletedTask);
        viewModel.Start();

        viewModel.SelectBandCommand.Execute("40m");

        Assert.Equal("Band 40m: 7.055.000 MHz", Assert.Single(log));
    }

    [Fact]
    public void A_band_smartdeck_does_not_offer_writes_nothing_at_all()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 14.031_5));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), bandMemory: new BandMemory(), settle: _ => Task.CompletedTask);
        viewModel.Start();

        viewModel.SelectBandCommand.Execute("6m");

        Assert.Empty(connection.FrequencyWrites);
        Assert.Empty(connection.ModeWrites);
    }

    [Fact]
    public void Disconnecting_drops_the_stale_slice_and_disables_the_controls()
    {
        // Found by the Codex deep audit 2026-08-03. Disconnect() clears the
        // connection's slice map directly and raises only
        // ConnectionStateChanged(false), so no SliceRemoved arrives and the
        // deck went on showing the old slice with its buttons lit and enabled.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 7.055));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        connection.SetSlices();
        connection.RaiseConnectionStateChanged(false);

        Assert.Null(viewModel.SelectedSlice);
        Assert.False(viewModel.HasSelectedSlice);
        Assert.Empty(viewModel.Slices);
        Assert.Empty(viewModel.SliceOptions);
        Assert.DoesNotContain(viewModel.BandOptions, band => band.IsCurrent);
        Assert.Equal("---", viewModel.FrequencyText);
        Assert.Equal(string.Empty, viewModel.ModeText);
    }

    [Fact]
    public void Reconnecting_after_a_disconnect_restores_the_slice_controls()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 7.055));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        connection.SetSlices();
        connection.RaiseConnectionStateChanged(false);
        connection.SetSlices(Slice("A", freqMhz: 14.050));
        connection.RaiseConnectionStateChanged(true);
        connection.RaiseSliceAdded(Slice("A", freqMhz: 14.050));

        Assert.Equal("A", viewModel.SelectedSlice?.Letter);
        Assert.Equal("20m", Assert.Single(viewModel.BandOptions, band => band.IsCurrent).Label);
    }

    [Fact]
    public void Antenna_buttons_survive_a_slice_update_that_does_not_change_the_options()
    {
        // Slice events fire on every radio update. Rebuilding the bound
        // collection each time would drop keyboard focus mid-press, so the
        // instances have to be reused when the radio's option list is the same.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();
        var before = viewModel.RxAntennaButtons.ToArray();

        connection.RaiseSliceUpdated(Slice("A", freqMhz: 21.050));

        Assert.Equal(before, viewModel.RxAntennaButtons);
    }

    // ── Mouse wheel over the readouts (issue #65) ────────────────────────────
    //
    // The window turns a wheel event into a notch count and nothing else, so
    // everything worth testing is here: step size, clamping, and the gathering
    // of a spin into one radio write. The handlers themselves are UI wiring
    // covered by the live-radio smoke gate.

    /// <summary>
    /// A settle the test controls, standing in for the gather window. Releasing
    /// it runs the waiting flush inline, so no test needs a real delay.
    /// </summary>
    private static (Func<TimeSpan, Task> Settle, TaskCompletionSource Gate) HeldSettle()
    {
        var gate = new TaskCompletionSource();
        return (_ => gate.Task, gate);
    }

    private static readonly Func<TimeSpan, Task> ImmediateSettle = _ => Task.CompletedTask;

    // ── Frequency stepping arithmetic ────────────────────────────────────────

    [Theory]
    [InlineData(14.050, 1, 100, 14.0501)]
    [InlineData(14.050, -1, 100, 14.0499)]
    [InlineData(14.050, 5, 1_000, 14.055)]
    [InlineData(14.050, -5, 1_000, 14.045)]
    [InlineData(7.0, 1, 10, 7.00001)]
    public void Wheel_steps_the_frequency_by_notches_of_the_tune_step(
        double from, int notches, int stepHz, double expected)
    {
        var next = SmartDeckViewModel.NextFrequencyMHz(from, notches, stepHz);

        Assert.NotNull(next);
        Assert.Equal(expected, next.Value, precision: 9);
    }

    [Fact]
    public void Wheel_stepping_does_not_drift_off_the_tune_grid()
    {
        // The reason the arithmetic runs in whole Hz. Accumulating 500 steps of
        // 10 Hz in MHz leaves a fractional-Hz residue that the header, which
        // groups down to single Hz, would show.
        var freq = 14.050;
        for (var i = 0; i < 500; i++)
            freq = SmartDeckViewModel.NextFrequencyMHz(freq, 1, 10) ?? freq;

        Assert.Equal(14.055, freq, precision: 9);
        Assert.Equal("14.055.000", SmartDeckViewModel.FormatFrequency(freq));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Wheel_stepping_refuses_an_unusable_tune_step(int stepHz)
    {
        Assert.Null(SmartDeckViewModel.NextFrequencyMHz(14.050, 1, stepHz));
    }

    [Fact]
    public void Wheel_stepping_refuses_to_leave_the_bottom_of_the_spectrum()
    {
        // No radio-reported tuning range to clamp against, so the only guard is
        // against wheeling through zero into a negative frequency.
        Assert.Null(SmartDeckViewModel.NextFrequencyMHz(0.000_050, -1, 100));
    }

    // ── Frequency wheel against the radio ────────────────────────────────────

    [Fact]
    public void Frequency_wheel_uses_the_tune_step_the_radio_reports()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 14.050, tuneStepHz: 250));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        viewModel.NudgeFrequency(1);

        var (slice, freq) = Assert.Single(connection.FrequencyWrites);
        Assert.Equal("A", slice.Letter);
        Assert.Equal(14.05025, freq, precision: 9);
    }

    [Fact]
    public void Frequency_wheel_falls_back_to_fifty_hertz_when_the_radio_reports_no_step()
    {
        // TuneStepHz is resolved reflectively and lands at zero if this FlexLib
        // build exposes neither property; the same 50 Hz the click-tune path
        // falls back to keeps the wheel usable rather than dead.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 14.050, tuneStepHz: 0));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        viewModel.NudgeFrequency(1);

        var (_, freq) = Assert.Single(connection.FrequencyWrites);
        Assert.Equal(14.05005, freq, precision: 9);
    }

    [Fact]
    public void A_frequency_spin_becomes_one_write_at_the_final_target()
    {
        // The whole reason the write is gathered: CwSkimmerSyncTracker answers
        // every slice QSY with SKIMMER/LO_FREQ plus SKIMMER/QSY, so one write
        // per notch would put dozens of telnet lines into Skimmer in a second.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", freqMhz: 14.050, tuneStepHz: 100));
        var (settle, gate) = HeldSettle();
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: settle);
        viewModel.Start();

        viewModel.NudgeFrequency(1);
        viewModel.NudgeFrequency(1);
        viewModel.NudgeFrequency(2);
        Assert.Empty(connection.FrequencyWrites);

        gate.SetResult();

        // Four notches of 100 Hz, written once. Each notch advanced a local
        // target rather than re-reading the slice, so none of the spin is lost
        // to an echo that has not arrived yet.
        var (_, freq) = Assert.Single(connection.FrequencyWrites);
        Assert.Equal(14.0504, freq, precision: 9);
    }

    [Fact]
    public void The_frequency_wheel_does_nothing_without_a_slice()
    {
        var connection = new FakeTelemetryConnection();
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        viewModel.NudgeFrequency(1);

        Assert.Empty(connection.FrequencyWrites);
    }

    [Fact]
    public void The_frequency_wheel_does_nothing_before_the_window_opens()
    {
        // Start() is the window's Opened hook. A nudge outside that lifetime
        // would write to a radio the deck is no longer watching.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", tuneStepHz: 100));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);

        viewModel.NudgeFrequency(1);

        Assert.Empty(connection.FrequencyWrites);
    }

    // ── TX power wheel ───────────────────────────────────────────────────────

    [Fact]
    public void Tx_power_wheel_steps_one_watt_a_notch()
    {
        // One watt, not five: QRP operators work 5 W down to 1 W and need the
        // single-watt granularity (operator request, issue #65).
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.ReportRfPower(5);
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        viewModel.NudgeTxPower(-1);

        Assert.Equal(4, Assert.Single(connection.RfPowerWrites));
        Assert.Equal("4 W", viewModel.TxPowerText);
    }

    [Fact]
    public void Tx_power_wheel_clamps_at_the_bottom_of_the_radios_range()
    {
        // FlexLib clamps to 0-100 in its own setter, and sub-watt output is not
        // expressible through this API: below 1 W the only value is 0.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.ReportRfPower(2);
        var (settle, gate) = HeldSettle();
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: settle);
        viewModel.Start();

        viewModel.NudgeTxPower(-10);
        gate.SetResult();

        Assert.Equal(0, Assert.Single(connection.RfPowerWrites));
    }

    [Fact]
    public void Tx_power_wheel_clamps_at_the_top_of_the_radios_range()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.ReportRfPower(95);
        var (settle, gate) = HeldSettle();
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: settle);
        viewModel.Start();

        viewModel.NudgeTxPower(20);
        gate.SetResult();

        Assert.Equal(100, Assert.Single(connection.RfPowerWrites));
    }

    [Fact]
    public void A_tx_power_spin_becomes_one_write_at_the_final_target()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.ReportRfPower(50);
        var (settle, gate) = HeldSettle();
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: settle);
        viewModel.Start();

        viewModel.NudgeTxPower(-1);
        viewModel.NudgeTxPower(-1);
        viewModel.NudgeTxPower(-3);

        // The readout follows every notch even though the write has not gone
        // out: the number is what the operator is steering by.
        Assert.Equal("45 W", viewModel.TxPowerText);
        Assert.Empty(connection.RfPowerWrites);

        gate.SetResult();

        Assert.Equal(45, Assert.Single(connection.RfPowerWrites));
    }

    [Fact]
    public void The_tx_power_wheel_does_nothing_until_the_radio_reports_a_power()
    {
        // Without a reported power there is nothing to step from, and
        // stepping from a guess would write over
        // whatever the operator actually has set.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        viewModel.NudgeTxPower(-1);

        Assert.Empty(connection.RfPowerWrites);
    }

    // ── RF gain and AGC-T wheel ──────────────────────────────────────────────

    [Fact]
    public void Rf_gain_wheel_steps_once_per_notch()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.SetPanadapters(Pan(rfGain: 12));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        // Three notches of the radio-reported 4 dB step, in one wheel event.
        viewModel.NudgeRfGain(3);

        var (_, gain) = Assert.Single(connection.RfGainWrites);
        Assert.Equal(24, gain);
        Assert.Equal("24 dB", viewModel.RfGainText);
    }

    [Fact]
    public void Rf_gain_wheel_clamps_to_the_radios_range()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.SetPanadapters(Pan(rfGain: 28));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        viewModel.NudgeRfGain(10);

        Assert.Equal(32, Assert.Single(connection.RfGainWrites).RfGain);
    }

    [Fact]
    public void The_rf_gain_wheel_does_nothing_until_the_radio_reports_a_range()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.SetPanadapters(Pan(withRange: false));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        viewModel.NudgeRfGain(1);

        Assert.Empty(connection.RfGainWrites);
    }

    [Fact]
    public void Agc_threshold_wheel_steps_once_per_notch()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", agcThreshold: 50));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        viewModel.NudgeAgcThreshold(-2);

        var (_, threshold) = Assert.Single(connection.AgcThresholdWrites);
        Assert.Equal(40, threshold);
        Assert.Equal("40", viewModel.AgcThresholdText);
    }

    [Fact]
    public void Agc_threshold_wheel_clamps_to_the_protocol_range()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", agcThreshold: 10));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        viewModel.NudgeAgcThreshold(-5);

        Assert.Equal(0, Assert.Single(connection.AgcThresholdWrites).Threshold);
    }

    // ── Regressions found by the Codex deep audit, 2026-08-05 ────────────────

    [Fact]
    public void A_pending_frequency_write_never_lands_on_a_slice_selected_since()
    {
        // Wheel slice A, switch to slice B inside the gather window. Writing
        // the pending target now would retune B to a frequency the operator
        // dialled for A, which on a live radio means a slice jumping bands
        // under someone who only clicked a chip.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(
            Slice("A", freqMhz: 14.050, tuneStepHz: 100),
            Slice("B", freqMhz: 7.030, tuneStepHz: 100));
        var (settle, gate) = HeldSettle();
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: settle);
        viewModel.Start();
        viewModel.SelectSliceCommand.Execute("A");

        viewModel.NudgeFrequency(5);
        viewModel.SelectSliceCommand.Execute("B");
        gate.SetResult();

        Assert.Empty(connection.FrequencyWrites);
    }

    [Fact]
    public void A_new_gesture_on_another_slice_starts_from_that_slices_frequency()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(
            Slice("A", freqMhz: 14.050, tuneStepHz: 100),
            Slice("B", freqMhz: 7.030, tuneStepHz: 100));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();
        viewModel.SelectSliceCommand.Execute("A");
        viewModel.NudgeFrequency(1);

        viewModel.SelectSliceCommand.Execute("B");
        viewModel.NudgeFrequency(1);

        // B steps from B's own 7.030, not from the 14.0501 A was left at.
        Assert.Equal(2, connection.FrequencyWrites.Count);
        Assert.Equal(7.0301, connection.FrequencyWrites[1].FreqMHz, precision: 9);
    }

    [Fact]
    public void Consecutive_rf_gain_notches_do_not_collapse_before_the_radio_echoes()
    {
        // A mouse delivers one event per notch. Each one used to recompute from
        // the panadapter's last radio-reported gain, so three notches arriving
        // before the first echo all wrote the same one-step target and a spin
        // moved 4 dB instead of 12.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.SetPanadapters(Pan(rfGain: 12));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        viewModel.NudgeRfGain(1);
        viewModel.NudgeRfGain(1);
        viewModel.NudgeRfGain(1);

        Assert.Equal([16, 20, 24], connection.RfGainWrites.Select(write => write.RfGain));
        Assert.Equal("24 dB", viewModel.RfGainText);
    }

    [Fact]
    public void Consecutive_agc_threshold_notches_do_not_collapse_before_the_radio_echoes()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", agcThreshold: 50));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        viewModel.NudgeAgcThreshold(1);
        viewModel.NudgeAgcThreshold(1);

        Assert.Equal([55, 60], connection.AgcThresholdWrites.Select(write => write.Threshold));
        Assert.Equal("60", viewModel.AgcThresholdText);
    }

    [Fact]
    public void An_rf_gain_echo_that_catches_up_hands_the_readout_back_to_the_radio()
    {
        // The local target is a bridge across the echo delay, not a second
        // source of truth: once the radio reports the value the wheel asked
        // for, the readout follows the radio again.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        connection.SetPanadapters(Pan(rfGain: 12));
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();
        viewModel.NudgeRfGain(1);

        connection.SetPanadapters(Pan(rfGain: 16));
        connection.RaisePanadapterUpdated(Pan(rfGain: 16));
        Assert.Equal("16 dB", viewModel.RfGainText);

        // Another client dropped it to 0 while nobody was wheeling; the deck
        // shows the radio rather than the number it last steered to.
        connection.SetPanadapters(Pan(rfGain: 0));
        connection.RaisePanadapterUpdated(Pan(rfGain: 0));

        Assert.Equal("0 dB", viewModel.RfGainText);
    }

    [Fact]
    public void A_wheel_event_carrying_no_notches_writes_nothing()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", tuneStepHz: 100));
        connection.SetPanadapters(Pan(rfGain: 12));
        connection.ReportRfPower(50);
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        viewModel.NudgeFrequency(0);
        viewModel.NudgeTxPower(0);
        viewModel.NudgeRfGain(0);
        viewModel.NudgeAgcThreshold(0);

        Assert.Empty(connection.FrequencyWrites);
        Assert.Empty(connection.RfPowerWrites);
        Assert.Empty(connection.RfGainWrites);
        Assert.Empty(connection.AgcThresholdWrites);
    }

    // ── TX indication (issue #69) ────────────────────────────────────────────

    private static (FakeTelemetryConnection Connection, SmartDeckViewModel ViewModel) DeckWithTwoSlices()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", isTransmitSlice: true), Slice("B"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();
        return (connection, viewModel);
    }

    private static DeckOption Chip(SmartDeckViewModel viewModel, string letter) =>
        viewModel.SliceOptions.Single(option => option.Label == letter);

    [Fact]
    public void Only_the_transmit_slice_reddens_and_only_while_the_radio_is_keyed()
    {
        // Red needs both halves: the radio is keyed (radio-scoped) and this is
        // the slice it transmits on (slice-scoped). Either alone would redden
        // the wrong chip, or every chip.
        var (connection, viewModel) = DeckWithTwoSlices();

        Assert.False(Chip(viewModel, "A").IsTransmitting);
        Assert.False(Chip(viewModel, "B").IsTransmitting);

        connection.ReportTransmitting(true);

        Assert.True(Chip(viewModel, "A").IsTransmitting);
        Assert.False(Chip(viewModel, "B").IsTransmitting);

        connection.ReportTransmitting(false);

        Assert.False(Chip(viewModel, "A").IsTransmitting);
        Assert.False(Chip(viewModel, "B").IsTransmitting);
    }

    [Fact]
    public void Keying_with_no_transmit_slice_reddens_nothing()
    {
        // A station whose slices are all receive-only must not light up just
        // because the radio keyed for someone else.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"), Slice("B"));
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        connection.ReportTransmitting(true);

        Assert.All(viewModel.SliceOptions, option => Assert.False(option.IsTransmitting));
    }

    [Fact]
    public void Moving_the_transmit_slice_while_keyed_moves_the_red()
    {
        var (connection, viewModel) = DeckWithTwoSlices();
        connection.ReportTransmitting(true);
        Assert.True(Chip(viewModel, "A").IsTransmitting);

        // The operator moved TX to slice B in SmartSDR mid-transmission.
        // SetSlices only swaps the backing list, so raise the update the radio
        // would send; that event is what the property filter in
        // FlexLibRadioConnection was widened to let through for IsTransmitSlice.
        var slices = new[] { Slice("A"), Slice("B", isTransmitSlice: true) };
        connection.SetSlices(slices);
        connection.RaiseSliceUpdated(slices[1]);

        Assert.False(Chip(viewModel, "A").IsTransmitting);
        Assert.True(Chip(viewModel, "B").IsTransmitting);
    }

    [Fact]
    public void The_selected_slice_can_be_both_selected_and_transmitting()
    {
        // Both classes land on one chip, which is why the tx style has to be
        // declared after the lit style in the markup. Pinned here so the
        // ViewModel half of that pairing is not quietly narrowed later.
        var (connection, viewModel) = DeckWithTwoSlices();
        viewModel.SelectSliceCommand.Execute("A");

        connection.ReportTransmitting(true);

        var chip = Chip(viewModel, "A");
        Assert.True(chip.IsCurrent);
        Assert.True(chip.IsTransmitting);
    }

    [Fact]
    public void A_deck_opened_mid_transmission_shows_the_red_immediately()
    {
        // Start() adopts current state rather than waiting for the next
        // transition, which for CW keying might never come while the window is
        // open.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A", isTransmitSlice: true));
        connection.ReportTransmitting(true);

        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        Assert.True(viewModel.IsTransmitting);
        Assert.True(Chip(viewModel, "A").IsTransmitting);
    }

    [Fact]
    public void SWR_stays_gated_on_forward_power_rather_than_on_transmit_state()
    {
        // Deliberate: issue #69 plumbed real transmit state, and the plan said
        // to retire this power threshold with it. That would have been a
        // regression. The radio is keyed between CW elements and on SSB with no
        // audio, while no RF is going out, and the SWR meter floors at 1.0 --
        // so gating SWR on MOX would put a fake 1:1 match back on screen, which
        // is the bug the threshold was added to fix (2026-08-02). Keep them
        // separate: transmit state says "keyed", forward power says "RF".
        Assert.Equal("---", SmartDeckViewModel.FormatSwr(1.0, powerWatts: 0));
        Assert.Equal("1.0", SmartDeckViewModel.FormatSwr(1.0, powerWatts: 50));
    }

    // ── RIT and XIT (issue #73) ─────────────────────────────────────

    private static (FakeTelemetryConnection Connection, SmartDeckViewModel ViewModel) DeckWithSlice(
        SliceInfo slice)
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(slice);
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();
        return (connection, viewModel);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(120, "+120")]
    [InlineData(-50, "-50")]
    [InlineData(99_999, "+99999")]
    public void An_offset_reads_signed_so_it_cannot_be_mistaken_for_a_frequency(int hz, string expected)
    {
        Assert.Equal(expected, SmartDeckViewModel.FormatOffset(hz));
    }

    [Fact]
    public void With_no_slice_the_offsets_read_as_absent()
    {
        Assert.Equal("---", SmartDeckViewModel.FormatOffset(null));
    }

    [Fact]
    public void Both_offsets_are_readable_at_once_so_neither_can_hide()
    {
        // The reason there is no view toggle: XIT engaged from the Maestro must
        // never sit hidden behind a RIT view.
        var (_, viewModel) = DeckWithSlice(
            Slice("A", ritEnabled: true, ritOffsetHz: 120, xitEnabled: true, xitOffsetHz: -50));

        Assert.Equal("+120", viewModel.RitText);
        Assert.Equal("-50", viewModel.XitText);
        Assert.True(viewModel.IsRitEnabled);
        Assert.True(viewModel.IsXitEnabled);
    }

    [Fact]
    public void RIT_and_XIT_engage_independently()
    {
        // The radio allows both, one, or neither. An earlier design made them
        // mutually exclusive, which would have removed a state the radio
        // legitimately supports.
        var (_, viewModel) = DeckWithSlice(Slice("A", ritEnabled: true, xitEnabled: false));

        Assert.True(viewModel.IsRitEnabled);
        Assert.False(viewModel.IsXitEnabled);
    }

    [Fact]
    public async Task Clicking_engages_and_releases_without_touching_the_offset()
    {
        var (connection, viewModel) = DeckWithSlice(Slice("A", ritOffsetHz: 120));

        await viewModel.ToggleRitCommand.ExecuteAsync(null);

        Assert.Equal(("A", true), Assert.Single(connection.RitEnableWrites));
        Assert.Empty(connection.RitOffsetWrites);   // engaging is not a retune
    }

    [Fact]
    public async Task Wheeling_moves_the_offset_ten_hertz_a_notch_without_engaging()
    {
        // One gesture each, deliberately: the wheel must not turn RIT on behind
        // the operator. The cost is that wheeling a released RIT changes a
        // stored offset with no on-air effect, which the muted colour is the
        // cue for. Pinned because it reads like a bug and is the chosen design.
        var (connection, viewModel) = DeckWithSlice(Slice("A"));

        viewModel.NudgeRit(3);
        await Task.Yield();

        Assert.Equal(("A", 30), Assert.Single(connection.RitOffsetWrites));
        Assert.Empty(connection.RitEnableWrites);
    }

    [Fact]
    public async Task The_step_is_ten_hertz_and_not_the_slices_tune_step()
    {
        // A slice on a 500 Hz SSB step would otherwise move RIT 500 Hz a click.
        var (connection, viewModel) = DeckWithSlice(Slice("A", mode: "USB", tuneStepHz: 500));

        viewModel.NudgeRit(1);
        await Task.Yield();

        Assert.Equal(("A", 10), Assert.Single(connection.RitOffsetWrites));
    }

    [Fact]
    public void A_spin_is_coalesced_into_one_write_and_accumulates_across_notches()
    {
        // Coalesced for the same reason the frequency wheel is: RIT is folded
        // into the effective RX frequency the CW Skimmer sync tracker
        // publishes, so a write per notch would put a SKIMMER/QSY burst into
        // Skimmer for every detent of the spin.
        //
        // Each notch also advances a local target rather than re-reading the
        // slice, so none of a fast spin is lost to an echo that has not arrived.
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice("A"));
        var (settle, gate) = HeldSettle();
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: settle);
        viewModel.Start();

        viewModel.NudgeRit(1);
        viewModel.NudgeRit(1);
        viewModel.NudgeRit(2);
        Assert.Empty(connection.RitOffsetWrites);

        gate.SetResult();

        Assert.Equal(("A", 40), Assert.Single(connection.RitOffsetWrites));
    }

    [Fact]
    public async Task The_offset_clamps_at_the_radios_rail_rather_than_going_silent()
    {
        // FlexLib drops an out-of-range write instead of clamping it, so without
        // this the wheel would stop moving with nothing on the wire.
        var (connection, viewModel) = DeckWithSlice(Slice("A", ritOffsetHz: RitXitRange.LimitHz - 5));

        viewModel.NudgeRit(10);   // would reach +100089 unclamped
        await Task.Yield();

        Assert.Equal(RitXitRange.LimitHz, connection.RitOffsetWrites[^1].OffsetHz);
    }

    [Fact]
    public async Task XIT_wheels_and_toggles_on_its_own_path()
    {
        var (connection, viewModel) = DeckWithSlice(Slice("A"));

        viewModel.NudgeXit(-2);
        await viewModel.ToggleXitCommand.ExecuteAsync(null);
        await Task.Yield();

        Assert.Equal(("A", -20), Assert.Single(connection.XitOffsetWrites));
        Assert.Equal(("A", true), Assert.Single(connection.XitEnableWrites));
        Assert.Empty(connection.RitOffsetWrites);
        Assert.Empty(connection.RitEnableWrites);
    }

    [Fact]
    public void Wheeling_with_no_slice_selected_does_nothing()
    {
        var connection = new FakeTelemetryConnection();
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: ImmediateSettle);
        viewModel.Start();

        viewModel.NudgeRit(5);
        viewModel.NudgeXit(5);

        Assert.Empty(connection.RitOffsetWrites);
        Assert.Empty(connection.XitOffsetWrites);
        Assert.Equal("---", viewModel.RitText);
    }
}
