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
