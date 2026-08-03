using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using SDRIQStreamer.FlexRadio;

namespace SDRIQStreamer.App;

/// <summary>
/// The slice state a band button captures on the way out and restores on the
/// way back in.
/// </summary>
/// <remarks>
/// Everything but the frequency is nullable, and absent is not the same as any
/// real value: null means this band has never been left, so the restore leaves
/// that setting exactly as the radio has it rather than guessing one. Guessing
/// an antenna would be a worse failure than changing nothing.
/// <para>
/// All five are slice-scoped. RF gain is deliberately absent: it is a
/// panadapter property reached through <see cref="SliceInfo.PanadapterStreamId"/>,
/// so including it would make a restore two write targets against two different
/// objects, and its range is radio-reported per panadapter rather than fixed by
/// the protocol.
/// </para>
/// </remarks>
/// <param name="Mode">
/// Only the modes SmartDeck offers. A slice sitting in a mode it does not offer
/// (DIGU and friends, which WSJT-X owns) records no mode and restores none,
/// rather than widening the radio write surface with an untyped mode verb.
/// Persisted by name so reordering <see cref="SliceMode"/> cannot silently
/// remap a saved band; the settings store has no global string-enum converter.
/// </param>
public sealed record BandState(
    double FreqMhz,
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    SliceMode? Mode = null,
    string? RxAntenna = null,
    string? TxAntenna = null,
    int? AgcThreshold = null);

/// <summary>
/// Per-band state memory for the SmartDeck band buttons (issue #59 phase 2b,
/// widened beyond frequency 2026-08-03). Band change is not a FlexLib verb: it
/// is a write to <c>Slice.Freq</c>, so the behaviour operators expect from a
/// band button lives here rather than on the radio.
/// </summary>
/// <remarks>
/// Mechanic: pressing a band button first stores the state being left under the
/// band it was in, then returns the stored state for the band being entered,
/// falling back to that band's default frequency the first time. Pure logic
/// with no radio or UI dependency, so the whole thing is unit-testable.
/// <para>
/// Why this cannot be left to the radio: a real band change on the radio tears
/// the slice down and rebuilds it from the radio's own slice persistence, which
/// is how antenna and gain normally follow a band. SmartDeck never does that.
/// It writes <c>Slice.Freq</c> on a live slice, deliberately, because CW
/// Skimmer and WSJT-X are bound per slice and the slice surviving is the whole
/// point. The cost of that choice is that nothing else follows the frequency,
/// so the restore has to be ours.
/// </para>
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

    private readonly Dictionary<string, BandState> _remembered;

    /// <param name="remembered">
    /// Backing store, typically the persisted settings dictionary so band
    /// memory survives a restart. Mutated in place as bands are left.
    /// </param>
    public BandMemory(Dictionary<string, BandState>? remembered = null) =>
        _remembered = remembered ?? [];

    /// <summary>True when SmartDeck offers a button for this band label.</summary>
    public static bool IsOffered(string band) =>
        OfferedBands.Any(b => string.Equals(b.Band, band, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Records <paramref name="state"/> under whatever band its frequency falls
    /// in. Frequencies outside the offered bands are ignored rather than stored
    /// under an empty key: a slice parked on 5 MHz WWV is not a band worth
    /// returning to.
    /// </summary>
    public void Remember(BandState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var band = HamBands.Label(state.FreqMhz);
        if (string.IsNullOrEmpty(band) || !IsOffered(band)) return;

        _remembered[band] = state;
    }

    /// <summary>
    /// The state to restore when entering <paramref name="band"/>: the
    /// remembered one, or a frequency-only default if it has never been left.
    /// Returns null for a band SmartDeck does not offer.
    /// </summary>
    public BandState? Resolve(string band)
    {
        if (_remembered.TryGetValue(band, out var remembered))
            return remembered;

        foreach (var (offered, defaultMhz) in OfferedBands)
            if (string.Equals(offered, band, StringComparison.OrdinalIgnoreCase))
                // Frequency only. Mode, antennas and AGC-T stay null so a first
                // visit tunes the band and changes nothing else.
                return new BandState(defaultMhz);

        return null;
    }

    /// <summary>
    /// Stores the departing state and returns the state to restore for
    /// <paramref name="band"/>, or null if the band is not offered.
    /// </summary>
    public BandState? SwitchTo(string band, BandState departing)
    {
        Remember(departing);
        return Resolve(band);
    }
}
