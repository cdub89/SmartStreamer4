using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SDRIQStreamer.App;

/// <summary>
/// One row of the Digital Operating screen: a live radio slice paired with the
/// per-slice binding (DAX RX / CAT / UDP) it launches the active engine with
/// (issue #28). The Start/Stop commands live on the owning ViewModel and take
/// this row as a parameter; this type is the data + per-row UI state.
/// </summary>
public partial class DigitalOperatingRowViewModel : ObservableObject
{
    // Modes that carry the USB/DIGU audio WSJT-X / JTDX decode. Anything else
    // (CW, LSB, ...) raises a soft, non-blocking mismatch notice.
    private static readonly string[] DigitalCompatibleModes = ["USB", "DIGU"];

    public DigitalOperatingRowViewModel(string sliceLetter, string station)
    {
        SliceLetter = sliceLetter;
        Station = station;
        RigName = $"Slice{sliceLetter}";   // per-instance --rig-name profile
    }

    public string SliceLetter { get; }
    public string Station { get; }

    /// <summary>The <c>--rig-name</c> profile / per-instance config name.</summary>
    public string RigName { get; }

    public string SliceLabel => $"Slice {SliceLetter}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModeMismatch))]
    private string _mode = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BandFreqDisplay))]
    private double _freqMHz;

    /// <summary>Compact "band (freq)" label, e.g. "20m (14.074 MHz)".</summary>
    public string BandFreqDisplay
    {
        get
        {
            var freq = $"{FreqMHz:F3} MHz";
            var band = BandLabel(FreqMHz);
            return string.IsNullOrEmpty(band) ? freq : $"{band} ({freq})";
        }
    }

    // Exact ADIF band-edge mapping transcribed verbatim (in Hz) from WSJT-X /
    // JTDX Bands.cpp (the authoritative source the engines themselves use).
    // Covers LF through 70cm, i.e. every frequency a FlexRadio can tune. A
    // frequency outside every band falls through to an empty label.
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

    private static string BandLabel(double mhz)
    {
        var hz = (long)Math.Round(mhz * 1_000_000d);
        foreach (var (low, high, band) in BandPlan)
            if (hz >= low && hz <= high)
                return band;
        return string.Empty;
    }

    [ObservableProperty]
    private int _daxRxChannel;

    [ObservableProperty]
    private int _catPort;

    [ObservableProperty]
    private int _udpPort;

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Launch result / error feedback for this row.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// True when the slice's demod mode is not USB/DIGU, so WSJT-X / JTDX would
    /// not receive decodable audio. Drives a soft, non-blocking notice.
    /// </summary>
    public bool IsModeMismatch =>
        !string.IsNullOrWhiteSpace(Mode) &&
        Array.IndexOf(DigitalCompatibleModes, Mode.ToUpperInvariant()) < 0;
}
