namespace SDRIQStreamer.Digital;

/// <summary>The digital-mode application family (issue #28).</summary>
public enum DigitalEngine
{
    WsjtX,
    Jtdx,
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
        new(DigitalEngine.Jtdx, "JTDX", exePath, "JTDX");
}
