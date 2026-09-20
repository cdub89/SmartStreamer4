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

    // settle: null keeps the ViewModel's real delay. HeldOpen parks a wheel
    // write in flight for the life of the test, so the deck's own held value is
    // what shows rather than an echo.
    private static (FakeTelemetryConnection Connection, SmartDeckViewModel ViewModel) Deck(
        SliceInfo? slice = null,
        bool diversityAllowed = false,
        Func<TimeSpan, Task>? settle = null)
    {
        var connection = new FakeTelemetryConnection { DiversityIsAllowed = diversityAllowed };
        connection.SetSlices(slice ?? Slice());
        var viewModel = new SmartDeckViewModel(connection, TestStation, postToUi: action => action(), settle: settle);
        viewModel.Start();
        return (connection, viewModel);
    }

    private static Task HeldOpen(TimeSpan _) => new TaskCompletionSource().Task;

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
    public void DIV_availability_follows_a_swap_to_a_different_radio()
    {
        // Regression, found in code review 2026-09-20. The deck stays open
        // across a disconnect, and the capability is radio-scoped with no event
        // of its own, so the binding kept the first radio's answer: connect a
        // 6400M, then a 6600, and DIV stayed greyed until the deck was reopened.
        var (connection, viewModel) = Deck(diversityAllowed: false);
        var raised = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SmartDeckViewModel.IsDiversityAvailable)) raised++;
        };

        connection.RaiseConnectionStateChanged(false);
        connection.DiversityIsAllowed = true;
        connection.RaiseConnectionStateChanged(true);

        Assert.Equal(2, raised);
        Assert.True(viewModel.IsDiversityAvailable);
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
        viewModel.Start();   // no slices at all, which Deck() cannot express

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
    public void The_power_button_shows_the_wattage_at_any_non_preset_level()
    {
        // This is the only place the power setting appears, so off a preset the
        // button has to show the number rather than a two-state label.
        var (connection, viewModel) = Deck(settle: HeldOpen);
        connection.ReportRfPower(50);

        // 50 W is neither preset, so the radio's own level shows straight away.
        Assert.Equal("50 W", viewModel.TxButtonText);

        viewModel.NudgeTxPower(1);

        Assert.Equal("51 W", viewModel.TxButtonText);
    }

    [Fact]
    public async Task The_wattage_stays_up_after_the_wheel_settles()
    {
        // Regression, operator-reported on v0.3.3-preview1: the button used to
        // revert to the preset label once the wheel stopped, so a radio sitting
        // at 51 W read "QRO". The display is now a function of the level alone
        // and has nothing to time out.
        var (connection, viewModel) = Deck(settle: _ => Task.CompletedTask);
        connection.ReportRfPower(50);

        viewModel.NudgeTxPower(1);
        await Task.Yield();

        Assert.Equal("51 W", viewModel.TxButtonText);
        Assert.False(viewModel.IsPresetActive);
    }

    [Fact]
    public void A_press_from_an_odd_level_lands_on_a_preset_and_names_it()
    {
        var (connection, viewModel) = Deck(settle: HeldOpen);
        connection.ReportRfPower(50);

        viewModel.NudgeTxPower(1);
        Assert.Equal("51 W", viewModel.TxButtonText);

        viewModel.ToggleQrpCommand.Execute(null);

        // On a preset the label replaces the number, and the button lights.
        Assert.Equal("QRP", viewModel.TxButtonText);
        Assert.True(viewModel.IsPresetActive);
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
