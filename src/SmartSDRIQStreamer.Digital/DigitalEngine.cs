namespace SDRIQStreamer.Digital;

/// <summary>The digital-mode application family (issue #28).</summary>
public enum DigitalEngine
{
    WsjtX,
    Jtdx,
    WsjtZ,
}

/// <summary>
/// Describes a launchable digital engine: its identity, executable, and
/// per-user config root. WSJT-X and JTDX differ only by these three values
/// (verified 2026-06-08); everything else (the working <c>.ini</c> template,
/// the per-instance binding) is shared. <see cref="ConfigRoot"/> is the
/// <c>%LOCALAPPDATA%</c> folder base (e.g. <c>WSJT-X</c>), from which a
/// <c>--rig-name</c> instance dir is derived as <c>&lt;ConfigRoot&gt; - &lt;rig&gt;</c>.
/// </summary>
public sealed record DigitalEngineDefinition(
    DigitalEngine Engine,
    string DisplayName,
    string ExePath,
    string ConfigRoot);

/// <summary>Factory for the known engine definitions with their default config roots.</summary>
public static class DigitalEngines
{
    public static DigitalEngineDefinition WsjtX(string exePath) =>
        new(DigitalEngine.WsjtX, "WSJT-X", exePath, "WSJT-X");

    public static DigitalEngineDefinition Jtdx(string exePath) =>
        new(DigitalEngine.Jtdx, "JTDX-Improved", exePath, "JTDX");

    // WSJT-Z is a WSJT-X fork: its exe is itself named wsjtx.exe and it reads the
    // shared WSJT-X config root (%LOCALAPPDATA%\WSJT-X), not a WSJT-Z folder
    // (verified 2026-06-11). So it provisions from the WSJT-X template and shares
    // that root; the only difference from stock WSJT-X is the exe path.
    public static DigitalEngineDefinition WsjtZ(string exePath) =>
        new(DigitalEngine.WsjtZ, "WSJT-Z", exePath, "WSJT-X");
}
