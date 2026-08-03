using System;
using System.Collections.Generic;
using System.Linq;

namespace SDRIQStreamer.App;

/// <summary>
/// Per-band frequency memory for the SmartDeck band buttons (issue #59 phase
/// 2b). Band change is not a FlexLib verb: it is a write to <c>Slice.Freq</c>,
/// so the behaviour operators expect from a band button lives here rather than
/// on the radio.
/// </summary>
/// <remarks>
/// Mechanic: pressing a band button first stores the frequency being left under
/// its own band, then returns the stored frequency for the band being entered,
/// falling back to that band's default the first time. Pure logic with no
/// radio or UI dependency, so the whole thing is unit-testable.
/// </remarks>
public sealed class BandMemory
{
    /// <summary>
    /// The HF bands SmartDeck offers, in the order the buttons appear, with the
    /// frequency each one lands on before the operator has ever left it.
    /// </summary>
    /// <remarks>
    /// Defaults are the SKCC calling frequencies (operator's choice,
    /// 2026-08-02), replacing an earlier band-edge-CW set that sat too low in
    /// each range to be a useful landing point. Every default is overwritten
    /// the first time the operator leaves that band, so any one of them only
    /// matters once.
    /// </remarks>
    private static readonly (string Band, double DefaultMhz)[] OfferedBands =
    [
        ("160m", 1.812_5),
        ("80m",  3.550),
        // 60m is channelized in the US and has no SKCC calling frequency; this
        // is a channel centre, not a free-tuning default like the others.
        ("60m",  5.332),
        ("40m",  7.055),
        ("30m",  10.120),
        ("20m",  14.050),
        ("17m",  18.080),
        ("15m",  21.050),
        ("12m",  24.910),
        ("10m",  28.050),
    ];

    /// <summary>Band labels in button order.</summary>
    public static IReadOnlyList<string> Bands { get; } = OfferedBands.Select(b => b.Band).ToArray();

    private readonly Dictionary<string, double> _remembered;

    /// <param name="remembered">
    /// Backing store, typically the persisted settings dictionary so band
    /// memory survives a restart. Mutated in place as bands are left.
    /// </param>
    public BandMemory(Dictionary<string, double>? remembered = null) =>
        _remembered = remembered ?? [];

    /// <summary>True when SmartDeck offers a button for this band label.</summary>
    public static bool IsOffered(string band) =>
        OfferedBands.Any(b => string.Equals(b.Band, band, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Records <paramref name="freqMhz"/> under whatever band it falls in.
    /// Frequencies outside the offered bands are ignored rather than stored
    /// under an empty key: a slice parked on 5 MHz WWV is not a band worth
    /// returning to.
    /// </summary>
    public void Remember(double freqMhz)
    {
        var band = HamBands.Label(freqMhz);
        if (string.IsNullOrEmpty(band) || !IsOffered(band)) return;

        _remembered[band] = freqMhz;
    }

    /// <summary>
    /// The frequency to tune when entering <paramref name="band"/>: the
    /// remembered one, or that band's default if it has never been left.
    /// Returns null for a band SmartDeck does not offer.
    /// </summary>
    public double? Resolve(string band)
    {
        if (_remembered.TryGetValue(band, out var remembered))
            return remembered;

        foreach (var (offered, defaultMhz) in OfferedBands)
            if (string.Equals(offered, band, StringComparison.OrdinalIgnoreCase))
                return defaultMhz;

        return null;
    }

    /// <summary>
    /// Stores the departing frequency and returns the frequency to tune for
    /// <paramref name="band"/>, or null if the band is not offered.
    /// </summary>
    public double? SwitchTo(string band, double currentFreqMhz)
    {
        Remember(currentFreqMhz);
        return Resolve(band);
    }
}
