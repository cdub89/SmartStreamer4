namespace SDRIQStreamer.FlexRadio;

public enum RequestStreamResult
{
    Success,
    StreamAlreadyActive,
    NoChannelAssigned,
    Timeout
}

public sealed record PanadapterInfo(
    uint   StreamId,
    double CenterFreqMHz,
    int    DAXIQChannel,
    string ClientStation,
    uint   ClientHandle = 0)
{
    // Issue #59 phase 2c: RF gain for the SmartDeck control surface. The range
    // is radio-reported per panadapter rather than a per-model table we would
    // have to maintain, so it is correct on any model. All four are zero until
    // the radio answers GetRFGainInfo().
    public int RfGain { get; init; }
    public int RfGainLow { get; init; }
    public int RfGainHigh { get; init; }
    public int RfGainStep { get; init; }

    /// <summary>
    /// True once the radio has reported a usable RF gain range. Until then the
    /// control has no limits to clamp against and stays disabled, rather than
    /// stepping against a 0-to-0 range.
    /// </summary>
    public bool HasRfGainRange => RfGainHigh > RfGainLow && RfGainStep > 0;

    private long CenterFreqHz => (long)Math.Round(CenterFreqMHz * 1_000_000d);

    public string DisplayLabel =>
        $"Center Frequency {CenterFreqHz} Hz  (DAX-IQ ch: {(DAXIQChannel > 0 ? DAXIQChannel.ToString() : "–")})";
}

/// <summary>
/// The demodulation modes the SmartDeck control surface offers (issue #59
/// phase 2a). Slice mode is an untyped string discriminator on the wire, so
/// this keeps typo'd literals out of call sites. Modes the radio supports but
/// SmartDeck does not offer (DIGU, DIGL, RTTY, and the rest) are deliberately
/// absent: WSJT-X owns mode selection for digital operating.
/// </summary>
public enum SliceMode
{
    Cw,
    Usb,
    Lsb,
    Am
}

/// <summary>
/// Stepping arithmetic for the SmartDeck up/down controls: RF gain (issue #59
/// phase 2c) and AGC-T. Separated from the connection so the clamping rules are
/// unit-testable without a radio, and shared because both controls step an
/// integer within a bounded range; only where the bounds come from differs.
/// </summary>
public static class SteppedRange
{
    /// <summary>
    /// The value one step up (<paramref name="direction"/> +1) or down (-1)
    /// from <paramref name="current"/>, clamped to [low, high].
    /// </summary>
    /// <returns>
    /// The new value, or <c>null</c> when the range is unusable or the value
    /// would not change, so the caller can skip the radio write entirely
    /// rather than re-sending the value the radio already holds.
    /// </returns>
    public static int? Next(int current, int direction, int low, int high, int step)
    {
        if (step <= 0 || high <= low) return null;

        var next = current + (direction * step);

        // Clamp rather than refuse: stepping up from one step below the ceiling
        // should land on the ceiling, not do nothing because the full step
        // would overshoot.
        next = Math.Clamp(next, low, high);

        return next == current ? null : next;
    }
}

/// <summary>
/// The radio's accepted range for a RIT or XIT offset (issue #73).
/// </summary>
/// <remarks>
/// Lives here rather than on the connection because it is a fact about the
/// radio that both the write path and the UI need: the wheel has to know where
/// the rail is to stop pretending it moved.
///
/// FlexLib does not clamp an out-of-range write, it <em>drops</em> it. The
/// setter raises PropertyChanged and returns without sending a command
/// (<c>Slice.cs:1500-1505</c> for RIT, <c>1537-1542</c> for XIT), so a value
/// past the limit produces no command on the wire and no error. Clamping on
/// this side is what keeps that from looking like a dead control.
/// </remarks>
public static class RitXitRange
{
    /// <summary>Widest offset the radio accepts, in Hz, either direction.</summary>
    public const int LimitHz = 99_999;

    public static int Clamp(int offsetHz) => Math.Clamp(offsetHz, -LimitHz, LimitHz);
}

public static class SliceModes
{
    /// <summary>The string FlexLib sends as <c>slice set N mode=X</c>.</summary>
    public static string ToRadioValue(this SliceMode mode) => mode switch
    {
        SliceMode.Cw  => "CW",
        SliceMode.Usb => "USB",
        SliceMode.Lsb => "LSB",
        SliceMode.Am  => "AM",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown slice mode.")
    };

    /// <summary>
    /// Maps a radio-reported mode string onto the offered set, or <c>null</c>
    /// when the slice is in a mode SmartDeck does not offer. Null is the
    /// "none of our buttons are lit" state, which is a real condition rather
    /// than an error: a slice sitting in DIGU is perfectly valid.
    /// </summary>
    public static SliceMode? FromRadioValue(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "CW"  => SliceMode.Cw,
        "USB" => SliceMode.Usb,
        "LSB" => SliceMode.Lsb,
        "AM"  => SliceMode.Am,
        _ => null
    };
}

public sealed record SliceInfo(
    string Letter,
    string Mode,
    double FreqMHz,
    bool   RitEnabled,
    double RitOffsetHz,
    int    TuneStepHz,
    uint   PanadapterStreamId,
    string ClientStation,
    int    DaxAudioChannel = 0)
{
    // Issue #59 phase 2a: antenna state for the SmartDeck control surface.
    // Init properties rather than positional parameters so the many existing
    // SliceInfo consumers are untouched (same approach as
    // DaxIQStreamInfo.IsSkimmerRunning). The option lists are radio-reported
    // and vary by model, so they stay strings rather than becoming an enum.
    public string RxAntenna { get; init; } = string.Empty;
    public string TxAntenna { get; init; } = string.Empty;
    public IReadOnlyList<string> RxAntennaOptions { get; init; } = [];
    public IReadOnlyList<string> TxAntennaOptions { get; init; } = [];

    /// <summary>The slice's mode when SmartDeck offers it, otherwise null.</summary>
    public SliceMode? OfferedMode => SliceModes.FromRadioValue(Mode);

    /// <summary>
    /// AGC threshold (AGC-T), 0-100. Slice-scoped, unlike RF gain which belongs
    /// to the panadapter. The range is fixed by the protocol rather than
    /// radio-reported: FlexLib clamps to 0-100 on write and rejects anything
    /// above 100 on read (Slice.cs:1361-1380, 2074-2085), so there is no
    /// range-request round trip and no "not yet known" state.
    /// </summary>
    public int AgcThreshold { get; init; }

    /// <summary>
    /// True when XIT is engaged on this slice (issue #73). Independent of RIT:
    /// the radio allows both, one, or neither.
    /// </summary>
    public bool XitEnabled { get; init; }

    /// <summary>Audio peaking filter, on or off (issue #76).</summary>
    /// <remarks>
    /// State only, deliberately. FlexLib also exposes <c>APFLevel</c>, but
    /// SmartSDR dropped the NR, NB and APF sliders in 4.1/4.2 and neither the
    /// Maestro nor the SmartSDR client offers them, because the radio adapts
    /// these itself. Driving the level from here would fight that adaptation,
    /// so check that SmartSDR has started exposing the sliders again before
    /// adding a level setter or stepper anywhere. This is the one full copy of
    /// the reasoning; the connection, the ViewModel and the deck XAML point here.
    /// The same reasoning covers <see cref="NrOn"/> and <see cref="NbOn"/>.
    /// </remarks>
    public bool ApfOn { get; init; }

    /// <summary>Noise reduction, on or off. State only; see <see cref="ApfOn"/>.</summary>
    public bool NrOn { get; init; }

    /// <summary>Noise blanker, on or off. State only; see <see cref="ApfOn"/>.</summary>
    public bool NbOn { get; init; }

    /// <summary>
    /// True when this slice is running diversity reception (issue #76).
    /// </summary>
    /// <remarks>
    /// Only meaningful on a radio that allows it: see
    /// <c>IRadioConnection.DiversityIsAllowed</c>, which is false on the 6300,
    /// 6400, 6400M and 6500.
    /// </remarks>
    public bool DiversityOn { get; init; }

    /// <summary>
    /// The XIT offset in Hz, whether or not <see cref="XitEnabled"/> is set.
    /// The radio stores the offset and the on/off flag separately, so turning
    /// XIT off leaves this value in place.
    /// </summary>
    public double XitOffsetHz { get; init; }

    /// <summary>
    /// True when this is the slice the radio will transmit on (issue #69).
    /// Radio-reported and exactly one slice carries it. Says nothing about
    /// whether the radio is keyed right now: that is
    /// <see cref="IRadioConnection.IsTransmitting"/>, which is radio-scoped.
    /// It takes both to know that <em>this</em> slice is on the air.
    /// </summary>
    public bool IsTransmitSlice { get; init; }

    public string DisplayLabel
    {
        get
        {
            // 0.###### trims trailing zeros (7.035000 -> 7.035) while keeping up
            // to Hz precision when present, so the label stays compact.
            var label = $"Slice {Letter}  {Mode}  {FreqMHz:0.######} MHz";
            // Issue #28: show the slice's DAX audio channel (DAX RX N) when
            // assigned — the WSJT-X/JTDX RX input. 0 = none.
            if (DaxAudioChannel > 0)
                label += $"  DAX RX {DaxAudioChannel}";
            if (RitEnabled && Math.Abs(RitOffsetHz) >= 0.5)
                label += $"  RIT {RitOffsetHz:+0;-0} Hz";
            return label;
        }
    }
}

public sealed record DaxIQStreamInfo(
    int    DAXIQChannel,
    int    SampleRate,
    bool   IsActive,
    double CenterFreqMHz,
    uint   ClientHandle = 0)
{
    public bool IsSkimmerRunning { get; init; }

    private long CenterFreqHz => (long)Math.Round(CenterFreqMHz * 1_000_000d);

    public string DisplayLabel =>
        CenterFreqMHz > 0
            ? $"DAX-IQ ch {DAXIQChannel}  Center Frequency {CenterFreqHz} Hz  {SampleRate / 1000} kHz  {(IsActive ? "Active" : "Inactive")}"
            : $"DAX-IQ ch {DAXIQChannel}  No Panadapter  {SampleRate / 1000} kHz  {(IsActive ? "Active" : "Inactive")}";

    public string SkimmerRowLabel =>
        $"DAX-IQ ch {DAXIQChannel}  {(IsActive ? "Active" : "Off")}";
}

public sealed record GuiClientInfo(
    uint   ClientHandle,
    string Program,
    string Station)
{
    /// <summary>
    /// The radio's own identifier for this GUI client, as opposed to the
    /// per-session <see cref="ClientHandle"/>. Empty until the radio reports
    /// one. This is what <c>client bind client_id=</c> takes, so it is what
    /// puts our non-GUI connection into the station's context (issue #64).
    /// </summary>
    /// <remarks>
    /// An init property rather than a positional parameter, so the existing
    /// constructions stay valid.
    /// </remarks>
    public string ClientID { get; init; } = string.Empty;

    public string DisplayLabel => $"{Program}/{Station}";
}

public enum NetworkHealthLevel
{
    Unknown,
    Excellent,
    Good,
    Poor
}

public sealed record NetworkStatusInfo(
    NetworkHealthLevel Health,
    int CurrentRttMs,
    int MaxRttMs)
{
    public static NetworkStatusInfo Empty { get; } =
        new(NetworkHealthLevel.Unknown, -1, -1);
}
