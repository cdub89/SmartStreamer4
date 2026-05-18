using SDRIQStreamer.CWSkimmer;

namespace SDRIQStreamer.App;

/// <summary>
/// Detects shifts in the auto-derived DAX-IQ MME device index between sessions.
/// Captures a baseline on first observation; reports a change when the live
/// probe disagrees with the baseline. Used by MainWindowViewModel to surface
/// a "rerun the Set Up Wizard" notice after SmartSDR or DAX upgrades that
/// reshuffle the device-list ordering (issue #38).
///
/// MME only — WDM index detection is not feasible. CW Skimmer's WDM Audio
/// tab uses a private/filtered enumeration that does not match either WinMM
/// or DirectSound order (verified Dallas 2026-05-18: CW Skimmer had DAX IQ 1
/// at slot 4, both WinMM and DirectSound at slot 13/14). The operator must
/// verify WDM values manually in the wizard after any audio-config change.
///
/// Pure functions; mutation of <see cref="AppSettings"/> is the caller's
/// responsibility via <see cref="CommitBaseline"/>.
/// </summary>
public static class AudioIndexChangeDetection
{
    /// <summary>
    /// Outcome of probing a single DAX-IQ channel. Carries both the live
    /// value and the previously-baselined one so the caller can render a
    /// "was X, now Y" line.
    /// </summary>
    public sealed record Result(
        int Channel,
        bool WasFirstRun,
        int CurrentMme,
        int? PriorMme)
    {
        public bool MmeChanged => !WasFirstRun && PriorMme.HasValue && PriorMme.Value != CurrentMme;
        public bool AnyChanged => MmeChanged;

        /// <summary>True when the live probe could not resolve a current value (DAX not running, channel not exposed, …).</summary>
        public bool ProbeUnavailable => CurrentMme < 0;
    }

    public static Result Probe(int channel, IAudioDeviceFinder finder, AppSettings settings)
    {
        var currentMme = finder.FindDaxIqSignalDeviceIndex(channel);
        var priorMme = GetLastSeenMme(settings, channel);
        var firstRun = !priorMme.HasValue;
        return new Result(channel, firstRun, currentMme, priorMme);
    }

    /// <summary>
    /// Writes the result's current MME value into the corresponding
    /// <c>LastSeenMme…</c> field. Skips a negative current value so a
    /// transient probe failure (e.g. DAX still initializing) doesn't poison
    /// the baseline.
    /// </summary>
    public static void CommitBaseline(AppSettings settings, Result result)
    {
        if (result.CurrentMme >= 0)
            SetLastSeenMme(settings, result.Channel, result.CurrentMme);
    }

    public static int? GetLastSeenMme(AppSettings s, int channel) => channel switch
    {
        1 => s.LastSeenMmeDeviceIndexCh1,
        2 => s.LastSeenMmeDeviceIndexCh2,
        3 => s.LastSeenMmeDeviceIndexCh3,
        4 => s.LastSeenMmeDeviceIndexCh4,
        _ => null,
    };

    private static void SetLastSeenMme(AppSettings s, int channel, int value)
    {
        switch (channel)
        {
            case 1: s.LastSeenMmeDeviceIndexCh1 = value; break;
            case 2: s.LastSeenMmeDeviceIndexCh2 = value; break;
            case 3: s.LastSeenMmeDeviceIndexCh3 = value; break;
            case 4: s.LastSeenMmeDeviceIndexCh4 = value; break;
        }
    }
}
