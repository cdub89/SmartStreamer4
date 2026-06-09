using System.Text.RegularExpressions;

namespace SDRIQStreamer.Digital;

/// <summary>
/// The reusable values pulled from an existing engine config's FlexRadio
/// profile, used to prepopulate the Digital Config tab for operators who have
/// already run WSJT-X / JTDX (issue #28).
/// </summary>
public sealed record HarvestedProfile(string MyCall, string MyGrid, string Rig);

/// <summary>
/// Reads an existing WSJT-X / JTDX <c>.ini</c> and returns the operator's
/// FlexRadio profile (callsign, grid, rig) so the Digital Config tab can
/// prepopulate it. Only FlexRadio profiles are considered; an unrelated rig
/// (e.g. a saved Yaesu config) is ignored.
///
/// Two layouts are handled:
/// <list type="bullet">
/// <item>Plain <c>[Configuration]</c> (JTDX, or a single-config WSJT-X): keys
/// such as <c>Rig</c>, <c>MyCall</c>, <c>MyGrid</c> appear directly.</item>
/// <item>WSJT-X MultiSettings: the active config is in <c>[Configuration]</c>;
/// other saved configs live in <c>[MultiSettings]</c> as
/// <c>&lt;hash&gt;\Configuration\&lt;key&gt;</c>. The active config is preferred;
/// otherwise the first saved config whose <c>Rig</c> is a FlexRadio.</item>
/// </list>
/// Never throws; returns null when no FlexRadio profile is found.
/// </summary>
public static class WsjtxProfileHarvester
{
    private const string FlexRig = "FlexRadio";

    public static HarvestedProfile? HarvestFromDefault(DigitalEngineDefinition engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        try
        {
            var path = DigitalConfigProvisioner.DefaultConfigPath(engine);
            return File.Exists(path) ? Harvest(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses <paramref name="configIniText"/> and returns the FlexRadio
    /// profile's identity, or null when none is present / input is unusable.
    /// </summary>
    public static HarvestedProfile? Harvest(string configIniText)
    {
        if (string.IsNullOrWhiteSpace(configIniText))
            return null;

        var keys = ParseKeys(configIniText);

        // 1) Active config: un-prefixed [Configuration] keys.
        if (Get(keys, "Configuration", "Rig") is { } activeRig && IsFlex(activeRig))
        {
            return new HarvestedProfile(
                Get(keys, "Configuration", "MyCall") ?? string.Empty,
                Get(keys, "Configuration", "MyGrid") ?? string.Empty,
                activeRig);
        }

        // 2) Saved MultiSettings configs: <hash>\Configuration\Rig under
        //    [MultiSettings]. Pick the first FlexRadio one.
        foreach (var (hash, rig) in FlexHashesInMultiSettings(keys))
        {
            return new HarvestedProfile(
                Get(keys, "MultiSettings", $@"{hash}\Configuration\MyCall") ?? string.Empty,
                Get(keys, "MultiSettings", $@"{hash}\Configuration\MyGrid") ?? string.Empty,
                rig);
        }

        return null;
    }

    private static bool IsFlex(string? rig) =>
        !string.IsNullOrWhiteSpace(rig) && rig.Contains(FlexRig, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string Hash, string Rig)> FlexHashesInMultiSettings(
        IReadOnlyDictionary<string, string> keys)
    {
        // Match keys like "MultiSettings|<hash>\Configuration\Rig".
        var rigPattern = new Regex(@"^MultiSettings\|(?<hash>[^\\]+)\\Configuration\\Rig$");
        foreach (var kvp in keys)
        {
            var match = rigPattern.Match(kvp.Key);
            if (match.Success && IsFlex(kvp.Value))
                yield return (match.Groups["hash"].Value, kvp.Value);
        }
    }

    private static string? Get(IReadOnlyDictionary<string, string> keys, string section, string key) =>
        keys.TryGetValue($"{section}|{key}", out var value) ? value : null;

    /// <summary>
    /// Flattens the INI into a <c>"section|key" =&gt; value</c> map. The key part
    /// keeps any MultiSettings prefix (e.g. <c>991a\Configuration\Rig</c>) intact.
    /// </summary>
    private static Dictionary<string, string> ParseKeys(string iniText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;

        foreach (var raw in iniText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            result[$"{section}|{key}"] = value;
        }

        return result;
    }
}
