namespace SDRIQStreamer.Digital;

/// <summary>
/// Minimal, preservation-first editor for Qt-style INI config (WSJT-X / JTDX).
/// Sets specific keys within a named section and leaves every other line
/// byte-for-byte unchanged. This matters because these files contain Qt
/// <c>@Variant(...)</c> binary values and percent-encoded section names that a
/// full parse-and-reserialize could corrupt — so we only touch the target keys.
/// </summary>
internal static class IniEditor
{
    /// <summary>
    /// Returns <paramref name="iniText"/> with each of <paramref name="keyValues"/>
    /// set inside <paramref name="section"/> (replacing an existing key in place,
    /// or appending it to the end of the section). The section is created if absent.
    /// Original newline style is preserved.
    /// </summary>
    public static string SetKeys(
        string iniText,
        string section,
        IReadOnlyList<KeyValuePair<string, string>> keyValues)
    {
        var newline = iniText.Contains("\r\n") ? "\r\n" : "\n";
        var lines = iniText.Replace("\r\n", "\n").Split('\n').ToList();

        var header = $"[{section}]";
        var sectionStart = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0)
                lines.Add(string.Empty);
            lines.Add(header);
            foreach (var kv in keyValues)
                lines.Add($"{kv.Key}={kv.Value}");
            return string.Join(newline, lines);
        }

        // Section runs until the next "[...]" header (or end of file).
        var sectionEnd = lines.Count;
        for (var i = sectionStart + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('['))
            {
                sectionEnd = i;
                break;
            }
        }

        foreach (var kv in keyValues)
        {
            var keyIndex = -1;
            for (var i = sectionStart + 1; i < sectionEnd; i++)
            {
                var eq = lines[i].IndexOf('=');
                if (eq > 0 && lines[i][..eq].Trim().Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    keyIndex = i;
                    break;
                }
            }

            if (keyIndex >= 0)
            {
                lines[keyIndex] = $"{kv.Key}={kv.Value}";
            }
            else
            {
                lines.Insert(sectionEnd, $"{kv.Key}={kv.Value}");
                sectionEnd++;
            }
        }

        return string.Join(newline, lines);
    }
}
