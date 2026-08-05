using System;

namespace SDRIQStreamer.App;

/// <summary>
/// Turns raw pointer-wheel deltas into step counts for the SmartDeck controls
/// (issue #65).
/// </summary>
/// <remarks>
/// One physical detent is one step, whatever magnitude the platform reports.
/// Windows sends 120 units per detent and Avalonia divides that to 1.0, but not
/// every seat agrees: a FLEX-6400M seat running v0.3.0b5 stepped every control
/// exactly twice per detent (operator-reported 2026-08-05, with AGC-T logged
/// moving 15, 25, 35 against a step of 5), which is a delta of 2.0 for one
/// click of the wheel. Taking the delta as a notch count therefore multiplies
/// the tune step by whatever the mouse, its driver, or the system scroll
/// setting happens to say. Direction is reliable; magnitude is not.
///
/// Fractional deltas still accumulate rather than being dropped or rounded up,
/// so a trackpad's stream of small values produces one step once they add up to
/// a whole one, instead of a step per event.
///
/// Deliberately no velocity acceleration: a fast spin moves one step per
/// detent, the same as a slow one. This wheel is for fine tuning, and full band
/// changes are made on the radio's own VFO dial (operator, 2026-08-05, asked
/// directly after the two-steps-per-detent fix). Scaling the step with spin
/// speed would trade the one thing the fix bought, a predictable step, for
/// speed that the dial already provides. Do not add it without being asked.
/// </remarks>
internal sealed class WheelNotchCounter
{
    /// <summary>
    /// Below this, accumulated deltas have not yet earned a step. One whole
    /// unit is Avalonia's nominal one-detent delta.
    /// </summary>
    private const double DetentThreshold = 1d;

    private double _residue;
    private object? _source;

    /// <summary>
    /// The steps <paramref name="deltaY"/> has earned over the control
    /// identified by <paramref name="source"/>: +1, -1, or 0 while a partial
    /// gesture is still accumulating.
    /// </summary>
    public int Add(double deltaY, object? source)
    {
        // Moving to another control abandons whatever the last one had part-way
        // accumulated, so a half-turn over RF gain cannot finish itself off as a
        // frequency step when the pointer moves.
        if (!ReferenceEquals(source, _source))
        {
            _residue = 0;
            _source = source;
        }

        // A reversal abandons it too: half a turn down then half a turn up is
        // no net movement, not a step in whichever direction crossed first.
        if (_residue != 0 && Math.Sign(deltaY) != Math.Sign(_residue))
            _residue = 0;

        _residue += deltaY;

        if (Math.Abs(_residue) < DetentThreshold)
            return 0;

        _residue = 0;
        return Math.Sign(deltaY);
    }
}
