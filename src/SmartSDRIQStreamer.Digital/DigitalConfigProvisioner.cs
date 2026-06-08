namespace SDRIQStreamer.Digital;

public enum DigitalProvisionOutcome
{
    Success,
    TemplateNotFound,
    Failed,
}

public sealed record DigitalProvisionResult(DigitalProvisionOutcome Outcome, string InstanceConfigPath);

/// <summary>
/// Seeds a per-instance WSJT-X / JTDX config (<c>.ini</c>) by cloning the
/// engine's working *default* config and overriding only the per-slice keys
/// (RX audio device, CAT TCP port, UDP port). The default config is the
/// operator's known-good template, so the Qt <c>@Variant</c> values
/// (PTT/Data/Split), Rig, callsign, etc. carry over verbatim and version-safe
/// (issue #28). Per-instance configs live at
/// <c>%LOCALAPPDATA%\&lt;ConfigRoot&gt; - &lt;rig&gt;\&lt;ConfigRoot&gt; - &lt;rig&gt;.ini</c>,
/// matching WSJT-X's <c>--rig-name</c> layout.
/// </summary>
public static class DigitalConfigProvisioner
{
    private const string ConfigurationSection = "Configuration";

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DefaultConfigPath(DigitalEngineDefinition engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return Path.Combine(LocalAppData, engine.ConfigRoot, $"{engine.ConfigRoot}.ini");
    }

    public static string InstanceConfigDir(DigitalEngineDefinition engine, string rigName)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return Path.Combine(LocalAppData, $"{engine.ConfigRoot} - {rigName}");
    }

    public static string InstanceConfigPath(DigitalEngineDefinition engine, string rigName) =>
        Path.Combine(InstanceConfigDir(engine, rigName), $"{engine.ConfigRoot} - {rigName}.ini");

    /// <summary>
    /// Pure transform (no I/O): apply the per-slice overrides to a template
    /// <c>.ini</c>'s <c>[Configuration]</c> section. Everything else (Rig,
    /// <c>@Variant</c> blobs, callsign, other sections) is preserved.
    /// </summary>
    public static string ApplyOverrides(string templateIni, int daxRxChannel, int catTcpPort, int udpPort) =>
        IniEditor.SetKeys(templateIni, ConfigurationSection,
        [
            new("SoundInName", $"DAX RX {daxRxChannel} (FlexRadio DAX)"),
            new("SoundOutName", "DAX TX (FlexRadio DAX)"),   // shared TX
            new("CATNetworkPort", $"127.0.0.1:{catTcpPort}"),
            new("UDPServerPort", udpPort.ToString()),
        ]);

    /// <summary>
    /// Clones the engine's default config to the per-instance path with the
    /// per-slice overrides applied. Returns <see cref="DigitalProvisionOutcome.TemplateNotFound"/>
    /// when no default config exists (operator must configure the default first).
    /// </summary>
    public static DigitalProvisionResult Provision(
        DigitalEngineDefinition engine, string rigName, int daxRxChannel, int catTcpPort, int udpPort)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(rigName);

        var instancePath = InstanceConfigPath(engine, rigName);
        var templatePath = DefaultConfigPath(engine);

        if (!File.Exists(templatePath))
            return new DigitalProvisionResult(DigitalProvisionOutcome.TemplateNotFound, instancePath);

        try
        {
            var provisioned = ApplyOverrides(File.ReadAllText(templatePath), daxRxChannel, catTcpPort, udpPort);
            Directory.CreateDirectory(InstanceConfigDir(engine, rigName));
            File.WriteAllText(instancePath, provisioned);
            return new DigitalProvisionResult(DigitalProvisionOutcome.Success, instancePath);
        }
        catch
        {
            return new DigitalProvisionResult(DigitalProvisionOutcome.Failed, instancePath);
        }
    }
}
