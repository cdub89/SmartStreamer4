using SDRIQStreamer.App;
using SDRIQStreamer.FlexRadio;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Issue #76: the receive-chain strip (DIV, APF, NB, NR) and the power button
/// that replaced the separate wattage readout beside it.
/// </summary>
public class SmartDeckToggleTests
{
    private const string TestStation = "TestStation";

    private static SliceInfo Slice(
        string letter = "A",
        bool apfOn = false,
        bool nrOn = false,
        bool nbOn = false,
        bool diversityOn = false) =>
        new(letter, "CW", 14.050, RitEnabled: false, RitOffsetHz: 0, TuneStepHz: 0,
            PanadapterStreamId: 100, ClientStation: TestStation)
        {
            RxAntenna = "ANT1",
            TxAntenna = "ANT1",
            ApfOn = apfOn,
            NrOn = nrOn,
            NbOn = nbOn,
            DiversityOn = diversityOn,
        };

    private static (FakeTelemetryConnection Connection, SmartDeckViewModel ViewModel) Deck(
        SliceInfo? slice = null,
        bool diversityAllowed = false)
    {
        var connection = new FakeTelemetryConnection { DiversityIsAllowed = diversityAllowed };
        connection.SetSlices(slice ?? Slice());
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();
        return (connection, viewModel);
    }

    [Theory]
    [InlineData("APF")]
    [InlineData("NR")]
    [InlineData("NB")]
    public void A_press_asks_the_radio_for_the_opposite_state(string control)
    {
        var (connection, viewModel) = Deck();

        Execute(viewModel, control);

        var write = Assert.Single(connection.ToggleWrites);
        Assert.Equal(control, write.Control);
        Assert.Equal("A", write.Letter);
        Assert.True(write.Enabled);
    }

    [Theory]
    [InlineData("APF")]
    [InlineData("NR")]
    [InlineData("NB")]
    public void A_press_while_on_turns_it_off(string control)
    {
        var (connection, viewModel) = Deck(Slice(apfOn: true, nrOn: true, nbOn: true));

        Execute(viewModel, control);

        Assert.False(Assert.Single(connection.ToggleWrites).Enabled);
    }

    [Fact]
    public void The_buttons_follow_the_radio_rather_than_only_our_own_presses()
    {
        // The radio echoes every change, including one made on the Maestro or
        // in SmartSDR, so the lit state has to come from the slice rather than
        // from what the deck last asked for.
        var (connection, viewModel) = Deck();

        Assert.False(viewModel.IsNrOn);

        connection.UpdateSlice(Slice(nrOn: true));

        Assert.True(viewModel.IsNrOn);
    }

    [Fact]
    public void DIV_is_enabled_only_where_the_radio_allows_diversity()
    {
        // The button always occupies its cell and greys out instead of hiding
        // (operator, 2026-09-19): hiding it would change the strip's width
        // between radios, and the deck's geometry must not move. False on the
        // operator's own 6400M, and on the 6300, 6400 and 6500.
        var (_, without) = Deck(diversityAllowed: false);
        Assert.False(without.IsDiversityAvailable);

        var (_, with) = Deck(diversityAllowed: true);
        Assert.True(with.IsDiversityAvailable);
    }

    [Fact]
    public void A_DIV_press_on_a_radio_that_cannot_do_it_reaches_nothing()
    {
        // Belt and braces with greying the button: the connection refuses the
        // write too, so a stale binding cannot put the radio in a state it
        // does not support.
        var (connection, viewModel) = Deck(diversityAllowed: false);

        viewModel.ToggleDiversityCommand.Execute(null);

        Assert.Empty(connection.ToggleWrites);
    }

    [Fact]
    public void A_DIV_press_where_it_is_allowed_reaches_the_radio()
    {
        var (connection, viewModel) = Deck(diversityAllowed: true);

        viewModel.ToggleDiversityCommand.Execute(null);

        var write = Assert.Single(connection.ToggleWrites);
        Assert.Equal("DIV", write.Control);
        Assert.True(write.Enabled);
    }

    [Fact]
    public void No_slice_selected_means_no_write()
    {
        var connection = new FakeTelemetryConnection { DiversityIsAllowed = true };
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action());
        viewModel.Start();

        viewModel.ToggleApfCommand.Execute(null);
        viewModel.ToggleNrCommand.Execute(null);
        viewModel.ToggleNbCommand.Execute(null);
        viewModel.ToggleDiversityCommand.Execute(null);

        Assert.Empty(connection.ToggleWrites);
    }

    // ── The power button (issue #76) ─────────────────────────────────────────

    [Fact]
    public void The_power_button_names_the_state_at_rest()
    {
        var (connection, viewModel) = Deck();
        connection.ReportRfPower(100);

        Assert.Equal("QRO", viewModel.TxButtonText);

        connection.ReportRfPower(5);

        Assert.Equal("QRP", viewModel.TxButtonText);
    }

    [Fact]
    public void The_power_button_shows_watts_while_the_wheel_moves_it()
    {
        // The separate wattage readout went away when power became one button
        // on a row of toggles, so this is the only place the setting shows.
        var settle = new TaskCompletionSource();
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice());
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: _ => settle.Task);
        viewModel.Start();
        connection.ReportRfPower(50);

        Assert.Equal("QRO", viewModel.TxButtonText);

        viewModel.NudgeTxPower(1);

        Assert.Equal("51 W", viewModel.TxButtonText);
    }

    [Fact]
    public async Task The_watts_give_way_to_the_state_once_the_wheel_settles()
    {
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice());
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: _ => Task.CompletedTask);
        viewModel.Start();
        connection.ReportRfPower(50);

        viewModel.NudgeTxPower(1);
        await Task.Yield();

        Assert.Equal("QRO", viewModel.TxButtonText);
    }

    [Fact]
    public void A_press_puts_the_state_back_immediately_even_mid_linger()
    {
        // A press is a state change, so the label must not keep showing watts
        // from a wheel gesture that has not finished lingering.
        var settle = new TaskCompletionSource();
        var connection = new FakeTelemetryConnection();
        connection.SetSlices(Slice());
        var viewModel = new SmartDeckViewModel(
            connection, TestStation, postToUi: action => action(), settle: _ => settle.Task);
        viewModel.Start();
        connection.ReportRfPower(50);

        viewModel.NudgeTxPower(1);
        Assert.Equal("51 W", viewModel.TxButtonText);

        viewModel.ToggleQrpCommand.Execute(null);

        Assert.Equal("QRP", viewModel.TxButtonText);
    }

    private static void Execute(SmartDeckViewModel viewModel, string control)
    {
        switch (control)
        {
            case "APF": viewModel.ToggleApfCommand.Execute(null); break;
            case "NR": viewModel.ToggleNrCommand.Execute(null); break;
            case "NB": viewModel.ToggleNbCommand.Execute(null); break;
            default: throw new ArgumentOutOfRangeException(nameof(control), control, "Unknown toggle.");
        }
    }
}
