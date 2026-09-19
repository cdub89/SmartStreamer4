using SDRIQStreamer.FlexRadio;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Issue #77. Radio.RFPower is a 0-100 percentage of the radio's rated output,
/// not watts, despite FlexLib's own comment calling it "Watts, from 0 to 100".
/// On a 100 W radio the two numbers coincide, which is why every radio before
/// the 500 W Aurora read correctly by accident. These pin the conversion so a
/// future change cannot quietly reintroduce the coincidence as an assumption.
/// </summary>
public class RfPowerScalingTests
{
    private const int Flex = 100;
    private const int Aurora = 500;

    [Theory]
    // A 100 W radio: percent and watts are the same integer, which is the
    // behaviour every existing deck test depends on.
    [InlineData(0, Flex, 0)]
    [InlineData(5, Flex, 5)]
    [InlineData(75, Flex, 75)]
    [InlineData(100, Flex, 100)]
    // A 500 W radio: one percent is five watts.
    [InlineData(0, Aurora, 0)]
    [InlineData(1, Aurora, 5)]
    [InlineData(5, Aurora, 25)]
    [InlineData(20, Aurora, 100)]
    [InlineData(100, Aurora, 500)]
    public void Percent_scales_to_watts_against_the_radios_rating(int percent, int max, int expectedWatts)
    {
        Assert.Equal(expectedWatts, FlexLibRadioConnection.PercentToWatts(percent, max));
    }

    [Theory]
    [InlineData(0, Flex, 0)]
    [InlineData(5, Flex, 5)]
    [InlineData(100, Flex, 100)]
    // The reporter's two numbers: QRP must write 1% on an Aurora to get a real
    // 5 W, where the old code wrote 5 and delivered 25 W.
    [InlineData(5, Aurora, 1)]
    [InlineData(25, Aurora, 5)]
    [InlineData(500, Aurora, 100)]
    public void Watts_scale_back_to_the_radios_percent(int watts, int max, int expectedPercent)
    {
        Assert.Equal(expectedPercent, FlexLibRadioConnection.WattsToPercent(watts, max));
    }

    [Fact]
    public void Watts_the_radio_cannot_represent_land_on_the_nearest_step()
    {
        // A 500 W PA steps in 5 W. 7 W is unreachable and must not silently
        // round down to nothing, nor up past what was asked for by more than
        // half a step.
        Assert.Equal(1, FlexLibRadioConnection.WattsToPercent(7, Aurora));
        Assert.Equal(2, FlexLibRadioConnection.WattsToPercent(8, Aurora));

        // Exactly half a step rounds away from zero, so 2.5 W asks for 5 W
        // rather than silently becoming 0 W and a dead transmitter.
        Assert.Equal(1, FlexLibRadioConnection.WattsToPercent(3, Aurora));
    }

    [Fact]
    public void A_write_past_the_PA_is_clamped_rather_than_overdriven()
    {
        Assert.Equal(100, FlexLibRadioConnection.WattsToPercent(9_999, Aurora));
        Assert.Equal(0, FlexLibRadioConnection.WattsToPercent(-50, Flex));
    }

    [Fact]
    public void Reported_watts_never_overstate_what_the_radio_will_transmit()
    {
        // Truncating, not rounding: a readout that claims more power than the
        // radio is set to deliver is the worse error of the two, and on any
        // rating that is not a multiple of 100 the division is inexact.
        const int OddRating = 250;
        Assert.Equal(2, FlexLibRadioConnection.PercentToWatts(1, OddRating));   // 2.5 W truncates to 2
        Assert.Equal(125, FlexLibRadioConnection.PercentToWatts(50, OddRating));
        Assert.Equal(250, FlexLibRadioConnection.PercentToWatts(100, OddRating));
    }

    [Fact]
    public void Round_tripping_a_representable_power_returns_it_unchanged()
    {
        foreach (var watts in new[] { 0, 5, 25, 100, 250, 500 })
        {
            var percent = FlexLibRadioConnection.WattsToPercent(watts, Aurora);
            Assert.Equal(watts, FlexLibRadioConnection.PercentToWatts(percent, Aurora));
        }
    }
}
