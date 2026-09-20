using SDRIQStreamer.App;
using SDRIQStreamer.FlexRadio;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Issue #76: the receive-chain strip (DIV, NB, NR, APF). The power button
/// that shared this strip, and its tests, were removed on 2026-09-20.
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
