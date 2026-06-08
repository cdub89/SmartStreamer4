namespace SDRIQStreamer.CWSkimmer;

/// <summary>
/// Builds a read-only dump of the machine's audio endpoints, used to establish
/// the facts for issue #28 (WSJT-X / JTDX setup-and-launch on SmartSDR DAXv2).
///
/// WSJT-X and JTDX select their soundcard by the full Windows audio-endpoint
/// name (not the WinMM index CW Skimmer uses), so the DirectSound descriptions
/// (full, untruncated) are the authoritative list of what the operator must
/// pick for DAX Audio RX (input) and DAX Audio TX (output). The WinMM list
/// (truncated to 31 chars by <see cref="WdmAudioDeviceFinder"/>) is included as
/// a cross-reference to the CW Skimmer device numbering.
///
/// This is deliberately read-only: it does not touch the radio or assign any
/// DAX channel, so it is safe to run during a live operating session. To also
/// confirm that assigning a slice's DAX audio channel brings an endpoint up
/// under DAXv2, assign the channel in SmartSDR's slice-flag DAX dropdown and
/// re-run this dump to see the endpoint appear or disappear.
/// </summary>
public static class AudioEndpointDiagnostics
{
    public static string BuildReport(IAudioDeviceFinder finder)
    {
        ArgumentNullException.ThrowIfNull(finder);

        var lines = new List<string> { "=== Audio endpoint diagnostic (issue #28 / DAXv2 WSJT-X audio) ===", "" };

        void Section(string title, IEnumerable<string> entries)
        {
            lines.Add(title);
            var any = false;
            foreach (var entry in entries)
            {
                lines.Add($"  {entry}");
                any = true;
            }
            if (!any)
                lines.Add("  (none)");
            lines.Add("");
        }

        Section(
            "DirectSound CAPTURE endpoints (WSJT-X/JTDX Input candidates, e.g. DAX Audio RX):",
            DirectSoundProbe.EnumerateCaptureDevices().Select(d => $"[{d.Index}] {d.Description}"));

        Section(
            "DirectSound OUTPUT endpoints (WSJT-X/JTDX Output candidates, e.g. DAX Audio TX):",
            DirectSoundProbe.EnumerateOutputDevices().Select(d => $"[{d.Index}] {d.Description}"));

        Section(
            "WinMM capture devices (CW Skimmer index : name, truncated to 31 chars):",
            finder.ListAllSignalDevices().Select(d => $"[{d.CwSkimmerIndex}] {d.Name}"));

        Section(
            "WinMM playback devices (CW Skimmer index : name, truncated to 31 chars):",
            finder.ListAllAudioDevices().Select(d => $"[{d.CwSkimmerIndex}] {d.Name}"));

        lines.Add("=== end audio endpoint diagnostic ===");

        return string.Join(Environment.NewLine, lines);
    }
}
