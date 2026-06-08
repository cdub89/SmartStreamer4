namespace SDRIQStreamer.Digital;

public enum DigitalProvisionOutcome
{
    Success,
    Failed,
}

public sealed record DigitalProvisionResult(DigitalProvisionOutcome Outcome, string InstanceConfigPath);

/// <summary>
/// The values the Digital Config tab supplies for a per-slice instance: the
/// operator identity (harvested from an existing FlexRadio profile or entered
/// for first-time operators) plus the per-slice binding (DAX RX channel, CAT
/// TCP port, UDP reporting port). The recommended technical settings and the
/// Qt <c>@Variant</c> enum blobs come from the bundled template, not from here.
/// </summary>
public sealed record DigitalProvisionValues(
    string MyCall,
    string MyGrid,
    string Rig,
    int DaxRxChannel,
    int CatTcpPort,
    int UdpPort);

/// <summary>
/// Seeds a per-instance WSJT-X / JTDX config (<c>.ini</c>) by starting from the
/// engine's bundled clean template (<see cref="DigitalTemplates"/>) and writing
/// only the operator identity and the per-slice binding keys over it (issue
/// #28). The template supplies the recommended FlexRadio/DAXv2 values and the
/// byte-exact Qt <c>@Variant</c> blobs verbatim, so PTT/Data/Split, audio
/// channel layout, etc. are version-safe. Per-instance configs live at
/// <c>%LOCALAPPDATA%\&lt;ConfigRoot&gt; - &lt;rig&gt;\&lt;ConfigRoot&gt; - &lt;rig&gt;.ini</c>,
/// matching WSJT-X's <c>--rig-name</c> layout.
///
/// This is the single provisioning path for every operator. Existing operators'
/// own FlexRadio values are surfaced into the Config tab by
/// <see cref="WsjtxProfileHarvester"/> (prepopulation only); provisioning still
/// flows through the bundled template so no personal config file is cloned.
/// </summary>
public static class DigitalConfigProvisioner
{
    private const string ConfigurationSection = "Configuration";

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// The engine's existing default config path. Used only by the harvester to
    /// prepopulate the Config tab; provisioning does not read it.
    /// </summary>
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
    /// Pure transform (no I/O): apply the operator identity and per-slice
    /// overrides to a template <c>.ini</c>'s <c>[Configuration]</c> section.
    /// Everything else (the <c>@Variant</c> blobs, recommended settings, other
    /// sections) is preserved verbatim.
    /// </summary>
    public static string ApplyOverrides(string templateIni, DigitalProvisionValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return IniEditor.SetKeys(templateIni, ConfigurationSection,
        [
            new("MyCall", values.MyCall),
            new("MyGrid", values.MyGrid),
            new("Rig", values.Rig),
            new("SoundInName", $"DAX RX {values.DaxRxChannel} (FlexRadio DAX)"),
            new("SoundOutName", "DAX TX (FlexRadio DAX)"),   // shared TX
            new("CATNetworkPort", $"127.0.0.1:{values.CatTcpPort}"),
            new("UDPServerPort", values.UdpPort.ToString()),
        ]);
    }

    /// <summary>
    /// Writes the per-instance config from the engine's bundled template with
    /// <paramref name="values"/> applied. Returns
    /// <see cref="DigitalProvisionOutcome.Failed"/> on any I/O error.
    /// </summary>
    public static DigitalProvisionResult Provision(
        DigitalEngineDefinition engine, string rigName, DigitalProvisionValues values)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(rigName);
        ArgumentNullException.ThrowIfNull(values);

        var instancePath = InstanceConfigPath(engine, rigName);

        try
        {
            // Preserve an existing instance config and re-apply only our keys.
            // WSJT-X / JTDX persist window geometry, the last protocol (FT8 /
            // FT4 / WSPR), and band settings into this file on exit, so seeding
            // from the bundled template every launch would wipe them. Seed from
            // the clean template only on the first run (no file yet).
            var baseIni = File.Exists(instancePath)
                ? File.ReadAllText(instancePath)
                : DigitalTemplates.ForEngine(engine.Engine);

            var provisioned = ApplyOverrides(baseIni, values);
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
