namespace SDRIQStreamer.App;

/// <summary>
/// Persisted per-slice digital binding: the slice's DAX RX audio channel and
/// its CAT / UDP ports. Pre-set to recommended defaults but operator-editable
/// on the Digital Config tab (issue #28). Mutable for System.Text.Json.
/// </summary>
public sealed class DigitalSliceBinding
{
    public string SliceLetter { get; set; } = "A";
    public int DaxRxChannel { get; set; } = 1;
    public int CatPort { get; set; } = 60_000;
    public int UdpPort { get; set; } = 2_237;
}
