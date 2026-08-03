using SDRIQStreamer.FlexRadio;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Stepping arithmetic behind the SmartDeck RF gain buttons (issue #59 phase
/// 2c). The range is radio-reported per panadapter, so these rules have to hold
/// for any low/high/step a model reports rather than for one hardcoded table.
/// </summary>
public class SteppedRangeTests
{
    // A representative FLEX range: -8 to +32 dB in 4 dB steps.
    private const int Low = -8;
    private const int High = 32;
    private const int Step = 4;

    [Fact]
    public void Stepping_up_adds_one_step()
    {
        Assert.Equal(4, SteppedRange.Next(0, direction: 1, Low, High, Step));
    }

    [Fact]
    public void Stepping_down_subtracts_one_step()
    {
        Assert.Equal(-4, SteppedRange.Next(0, direction: -1, Low, High, Step));
    }

    [Fact]
    public void Stepping_up_near_the_ceiling_lands_on_the_ceiling()
    {
        // Clamped rather than refused: from 30 with a 4 dB step, the operator
        // should reach 32, not be stuck because a full step would overshoot.
        Assert.Equal(32, SteppedRange.Next(30, direction: 1, Low, High, Step));
    }

    [Fact]
    public void Stepping_down_near_the_floor_lands_on_the_floor()
    {
        Assert.Equal(-8, SteppedRange.Next(-6, direction: -1, Low, High, Step));
    }

    [Fact]
    public void At_the_ceiling_stepping_up_is_absent_so_no_write_happens()
    {
        // Absent rather than the same number, so the caller skips the radio
        // write instead of re-sending a value the radio already holds.
        Assert.Null(SteppedRange.Next(High, direction: 1, Low, High, Step));
    }

    [Fact]
    public void At_the_floor_stepping_down_is_absent_so_no_write_happens()
    {
        Assert.Null(SteppedRange.Next(Low, direction: -1, Low, High, Step));
    }

    [Theory]
    // Before the radio answers GetRFGainInfo(), all three are zero. Stepping
    // against that range must do nothing rather than write a bogus gain.
    [InlineData(0, 0, 0)]
    [InlineData(-8, 32, 0)]   // no step size reported
    [InlineData(32, 32, 4)]   // degenerate range
    [InlineData(32, -8, 4)]   // inverted range
    public void An_unusable_range_never_produces_a_value(int low, int high, int step)
    {
        Assert.Null(SteppedRange.Next(0, direction: 1, low, high, step));
        Assert.Null(SteppedRange.Next(0, direction: -1, low, high, step));
    }

    [Fact]
    public void A_gain_outside_the_reported_range_is_pulled_back_into_it()
    {
        // Defensive: if the radio ever reports a gain outside its own range,
        // stepping should move toward the range rather than further out.
        Assert.Equal(High, SteppedRange.Next(100, direction: 1, Low, High, Step));
        Assert.Equal(Low, SteppedRange.Next(-100, direction: -1, Low, High, Step));
    }

    [Fact]
    public void A_range_that_is_not_a_whole_number_of_steps_still_reaches_both_ends()
    {
        // 0 to 10 in 3 dB steps: 0, 3, 6, 9, then 10 by clamping.
        Assert.Equal(10, SteppedRange.Next(9, direction: 1, low: 0, high: 10, step: 3));
        Assert.Null(SteppedRange.Next(10, direction: 1, low: 0, high: 10, step: 3));
    }

    [Fact]
    public void HasRfGainRange_is_false_until_the_radio_reports_one()
    {
        var beforeReply = new PanadapterInfo(100, 14.05, 1, "STATION");
        var afterReply = beforeReply with { RfGainLow = Low, RfGainHigh = High, RfGainStep = Step };

        Assert.False(beforeReply.HasRfGainRange);
        Assert.True(afterReply.HasRfGainRange);
    }
}
