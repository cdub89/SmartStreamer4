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
    public string BandFreqDisplay => HamBands.BandFreq(FreqMHz);

    [ObservableProperty]
    private int _daxRxChannel;

    /// <summary>
    /// The slice's actual DAX audio channel assigned in SmartSDR (0 = none). This
    /// is what feeds WSJT-X / JTDX's RX audio, so the engine cannot decode without
    /// it. Distinct from <see cref="DaxRxChannel"/> (the operator's configured
    /// default used to seed the engine .ini). Issue #28.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DaxRxDisplay))]
    [NotifyPropertyChangedFor(nameof(HasDaxRx))]
    private int _sliceDaxRxChannel;

    /// <summary>"DAX RX N" for the slice's assigned channel, or "none".</summary>
    public string DaxRxDisplay => SliceDaxRxChannel > 0 ? $"DAX RX {SliceDaxRxChannel}" : "none";

    /// <summary>Whether the slice has a DAX RX audio channel assigned (required to start).</summary>
    public bool HasDaxRx => SliceDaxRxChannel > 0;

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
