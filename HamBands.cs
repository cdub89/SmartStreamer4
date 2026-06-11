using System;

namespace SDRIQStreamer.App;

/// <summary>
/// Shared ham-band lookup + compact frequency formatting (issue #28). The band
/// edges are transcribed verbatim (in Hz) from WSJT-X / JTDX <c>Bands.cpp</c>
/// (the authoritative ADIF table the engines themselves use), covering LF
/// through 70cm: every frequency a FlexRadio can tune. A frequency outside
/// every band falls through to an empty label. Used by both the Digital
/// Operating rows and the Launch-page slice summary so they share one table.
/// </summary>
public static class HamBands
{
    private static readonly (long LowHz, long HighHz, string Band)[] BandPlan =
    [
        (136_000,       137_000,       "2190m"),
        (472_000,       479_000,       "630m"),
        (501_000,       504_000,       "560m"),
        (1_800_000,     2_000_000,     "160m"),
        (3_500_000,     4_000_000,     "80m"),
        (5_102_000,     5_406_500,     "60m"),
        (7_000_000,     7_300_000,     "40m"),
        (10_000_000,    10_150_000,    "30m"),
        (14_000_000,    14_350_000,    "20m"),
        (18_068_000,    18_168_000,    "17m"),
        (21_000_000,    21_450_000,    "15m"),
        (24_890_000,    24_990_000,    "12m"),
        (28_000_000,    29_700_000,    "10m"),
        (40_000_000,    41_000_000,    "8m"),
        (50_000_000,    54_000_000,    "6m"),
        (70_000_000,    71_000_000,    "4m"),
        (144_000_000,   148_000_000,   "2m"),
        (222_000_000,   225_000_000,   "1.25m"),
        (420_000_000,   450_000_000,   "70cm"),
    ];

    /// <summary>The band label for a frequency in MHz, or empty if outside every band.</summary>
    public static string Label(double mhz)
    {
        var hz = (long)Math.Round(mhz * 1_000_000d);
        foreach (var (low, high, band) in BandPlan)
            if (hz >= low && hz <= high)
                return band;
        return string.Empty;
    }

    /// <summary>
    /// Compact "band (freq)" label, e.g. "20m (14.074 MHz)" (freq alone when the
    /// frequency is outside every known band).
    /// </summary>
    public static string BandFreq(double mhz)
    {
        var freq = $"{mhz:F3} MHz";
        var band = Label(mhz);
        return string.IsNullOrEmpty(band) ? freq : $"{band} ({freq})";
    }
}
