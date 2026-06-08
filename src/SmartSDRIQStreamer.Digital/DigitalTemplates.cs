using System.Reflection;

namespace SDRIQStreamer.Digital;

/// <summary>
/// Supplies the bundled, clean first-time-operator config template for each
/// engine (issue #28). A template carries the recommended FlexRadio / DAXv2
/// values and the byte-exact Qt <c>@Variant</c> enum blobs (PTT=CAT,
/// DataMode=data, Split=none, TX audio=front) that cannot be hand-typed, with
/// <c>MyCall</c> / <c>MyGrid</c> blank for the operator to fill in via the
/// Digital Config tab. No personal data ships in these resources. WSJT-X and
/// JTDX share the same recommended values but keep separate resources so each
/// can diverge later without code changes.
/// </summary>
public static class DigitalTemplates
{
    public static string ForEngine(DigitalEngine engine) => engine switch
    {
        DigitalEngine.WsjtX => Load("WSJT-X.template.ini"),
        DigitalEngine.Jtdx  => Load("JTDX.template.ini"),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown digital engine."),
    };

    private static string Load(string logicalName)
    {
        var assembly = typeof(DigitalTemplates).Assembly;
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded template '{logicalName}' was not found in {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
