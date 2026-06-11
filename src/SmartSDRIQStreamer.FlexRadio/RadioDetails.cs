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
