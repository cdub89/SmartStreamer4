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
    string ClientStation)
{
    public string DisplayLabel =>
        RitEnabled && Math.Abs(RitOffsetHz) >= 0.5
            ? $"Slice {Letter}  {Mode}  {FreqMHz:F6} MHz  RIT {RitOffsetHz:+0;-0} Hz"
            : $"Slice {Letter}  {Mode}  {FreqMHz:F6} MHz";
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
