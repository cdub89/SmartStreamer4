using System;
using System.IO;

namespace SDRIQStreamer.CWSkimmer;

/// <summary>
/// Names and startup-size policy for the app's append-only log files.
/// Issue #58 (reported 2026-07): nothing bounded log growth; a field capture
/// reached 95 MB in a single file over 104 days. Each log is rotated once at
/// startup to <c>&lt;name&gt;.old</c> when it exceeds the cap, so worst-case
/// disk use is twice the cap per log. Rotation at startup (not on every append)
/// keeps the hot write paths allocation- and stat-free.
/// </summary>
public static class LogFiles
{
    public const string StreamerStatus = "streamer-status.log";
    public const string SpotPublish = "spot-publish.log";
    public const string TelnetClient = "cwskimmer-telnet-client.log";

    /// <summary>Startup rotation cap per log file (10 MB).</summary>
    public const long MaxLogBytes = 10 * 1_024 * 1_024;

    /// <summary>
    /// Rotates <paramref name="path"/> to <c>path + ".old"</c> (replacing any
    /// prior .old) when the file exceeds <paramref name="maxBytes"/>.
    /// Best-effort: never throws; the writers append regardless.
    /// </summary>
    public static void RotateIfOversized(string path, long maxBytes)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= maxBytes)
                return;

            File.Move(path, path + ".old", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort by design: a locked or unwritable log must never
            // block app startup.
        }
    }

    /// <summary>
    /// Rotates all three logs in the resolved logs directory. Call once at
    /// startup, before any writer opens a handle.
    /// </summary>
    public static void RotateAllAtStartup()
    {
        var dir = RuntimePathResolver.ResolveLogsDir();
        RotateIfOversized(Path.Combine(dir, StreamerStatus), MaxLogBytes);
        RotateIfOversized(Path.Combine(dir, SpotPublish), MaxLogBytes);
        RotateIfOversized(Path.Combine(dir, TelnetClient), MaxLogBytes);
    }
}
