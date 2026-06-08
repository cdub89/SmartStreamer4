using System.Xml.Linq;

namespace SDRIQStreamer.Digital;

/// <summary>
/// A SmartSDR CAT TCP port bound to a slice, as configured by the operator in
/// the SmartSDR CAT application (issue #28).
/// </summary>
public sealed record CatTcpPort(string SliceIndex, int TcpPort, string Name);

/// <summary>
/// Reads SmartSDR CAT's port configuration to discover which TCP port serves
/// which slice, so the Digital setup can auto-fill WSJT-X's Network Server
/// (<c>127.0.0.1:&lt;port&gt;</c>). SmartSDR CAT owns port creation; this is
/// read-only.
///
/// Source: <c>%APPDATA%\FlexRadio Systems\CAT.settings</c> (XML). FlexLib does
/// not expose these ports, so a file read is the only discovery path. The
/// <c>&lt;PortList&gt;</c> element's text is itself an XML-escaped
/// <c>ArrayOfPortSettings</c> document; each <c>PortSettings</c> carries
/// <c>Protocol</c>, <c>PortCommType</c>, <c>TCPPortNumber</c>,
/// <c>SliceIndex</c>, and <c>Name</c>. Only <c>Protocol=CAT</c> +
/// <c>PortCommType=TCP</c> entries are returned.
/// </summary>
public static class CatSettingsReader
{
    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FlexRadio Systems",
        "CAT.settings");

    /// <summary>
    /// Reads CAT TCP ports from the default <c>CAT.settings</c> location.
    /// Returns an empty list when the file is missing or unreadable.
    /// </summary>
    public static IReadOnlyList<CatTcpPort> ReadCatTcpPorts()
    {
        try
        {
            var path = DefaultSettingsPath;
            return File.Exists(path) ? ParseCatTcpPorts(File.ReadAllText(path)) : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Parses CAT TCP ports from <c>CAT.settings</c> XML content. Returns an
    /// empty list for null/blank/malformed input (never throws).
    /// </summary>
    public static IReadOnlyList<CatTcpPort> ParseCatTcpPorts(string catSettingsXml)
    {
        if (string.IsNullOrWhiteSpace(catSettingsXml))
            return [];

        // The PortList element's value is an XML-escaped inner document, so this
        // is a two-stage parse: outer Settings -> PortList text -> inner
        // ArrayOfPortSettings.
        string? portListXml;
        try
        {
            portListXml = XDocument.Parse(catSettingsXml).Root?.Element("PortList")?.Value;
        }
        catch
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(portListXml))
            return [];

        XDocument inner;
        try
        {
            inner = XDocument.Parse(portListXml);
        }
        catch
        {
            return [];
        }

        var ports = new List<CatTcpPort>();
        foreach (var ps in inner.Descendants("PortSettings"))
        {
            if (!IsValue(ps, "Protocol", "CAT") || !IsValue(ps, "PortCommType", "TCP"))
                continue;

            if (!int.TryParse((string?)ps.Element("TCPPortNumber"), out var port) || port <= 0)
                continue;

            var slice = ((string?)ps.Element("SliceIndex") ?? string.Empty).Trim();
            var name = ((string?)ps.Element("Name") ?? string.Empty).Trim();
            ports.Add(new CatTcpPort(slice, port, name));
        }

        return ports;
    }

    /// <summary>
    /// The CAT TCP port bound to <paramref name="sliceLetter"/> (first match),
    /// or null when none is configured for that slice.
    /// </summary>
    public static CatTcpPort? FindPortForSlice(IReadOnlyList<CatTcpPort> ports, string sliceLetter)
    {
        ArgumentNullException.ThrowIfNull(ports);
        return ports.FirstOrDefault(p =>
            string.Equals(p.SliceIndex, sliceLetter, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsValue(XElement parent, string element, string expected) =>
        string.Equals((string?)parent.Element(element), expected, StringComparison.OrdinalIgnoreCase);
}
