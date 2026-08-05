using SDRIQStreamer.App;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Translation of raw pointer-wheel deltas into SmartDeck step counts (issue
/// #65). This exists because taking the delta as the step count shipped in
/// v0.3.0b5 and moved every control two steps per detent on the operator's
/// FLEX-6400M seat: the tune step read 50 Hz and tuned 100, AGC-T stepped 10
/// against a step of 5, and TX power moved 2 W a notch.
/// </summary>
public class WheelNotchCounterTests
{
    private static readonly object Control = new();

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    [InlineData(120.0)]
    public void One_detent_is_one_step_whatever_magnitude_the_platform_reports(double delta)
    {
        // The regression itself. Direction is trustworthy across mice, drivers
        // and system scroll settings; magnitude is not.
        var counter = new WheelNotchCounter();

        Assert.Equal(1, counter.Add(delta, Control));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-2.0)]
    public void A_downward_detent_is_one_step_down(double delta)
    {
        var counter = new WheelNotchCounter();

        Assert.Equal(-1, counter.Add(delta, Control));
    }

    [Fact]
    public void Consecutive_detents_each_earn_their_own_step()
    {
        var counter = new WheelNotchCounter();

        Assert.Equal(1, counter.Add(2.0, Control));
        Assert.Equal(1, counter.Add(2.0, Control));
        Assert.Equal(1, counter.Add(2.0, Control));
    }

    [Fact]
    public void Fractional_deltas_accumulate_into_a_step_rather_than_being_dropped()
    {
        // A trackpad reports a stream of small values for one gesture. Dropping
        // them would make the control look dead on that hardware; rounding each
        // one up would make it wildly oversensitive.
        var counter = new WheelNotchCounter();

        Assert.Equal(0, counter.Add(0.3, Control));
        Assert.Equal(0, counter.Add(0.3, Control));
        Assert.Equal(1, counter.Add(0.4, Control));
    }

    [Fact]
    public void A_part_accumulated_gesture_does_not_carry_to_another_control()
    {
        // Half a turn over RF gain must not finish itself off as a frequency
        // step when the pointer moves; hover is the whole gesture, so the
        // pointer crosses controls constantly.
        var counter = new WheelNotchCounter();
        var other = new object();

        Assert.Equal(0, counter.Add(0.6, Control));
        Assert.Equal(0, counter.Add(0.6, other));
        Assert.Equal(1, counter.Add(0.6, other));
    }

    [Fact]
    public void Reversing_direction_abandons_the_part_accumulated_gesture()
    {
        // Half a turn down then half a turn up is no net movement, not a step
        // in whichever direction happened to cross the threshold first.
        var counter = new WheelNotchCounter();

        Assert.Equal(0, counter.Add(0.6, Control));
        Assert.Equal(0, counter.Add(-0.6, Control));
        Assert.Equal(-1, counter.Add(-0.6, Control));
    }

    [Fact]
    public void A_zero_delta_earns_nothing()
    {
        var counter = new WheelNotchCounter();

        Assert.Equal(0, counter.Add(0, Control));
    }
}
