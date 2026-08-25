namespace SDRIQStreamer.CWSkimmer;

public enum LaunchResult
{
    Success,
    AlreadyRunning,
    ExeNotFound,

    /// <summary>
    /// The master cwskimmer.ini could not be used: absent, unset, a folder
    /// rather than a file, or present but carrying no usable [Audio]
    /// calibration. Issue #75 folded the former <c>DeviceNotFound</c> into
    /// this: after the issue #74 fix its gate could only fire for the
    /// no-calibration case, which is a template problem, and the caller
    /// distinguishes the variants from the filesystem rather than from a
    /// second enum member.
    /// </summary>
    TemplateIniNotFound,

    /// <summary>
    /// The master INI was fine but the per-channel INI could not be written:
    /// the artifacts directory could not be created, the target was locked, or
    /// the copy was denied.
    /// </summary>
    /// <remarks>
    /// Codex deep audit of issue #75, 2026-08-24. Folding the old
    /// <c>DeviceNotFound</c> into <see cref="TemplateIniNotFound"/> made the
    /// template message specific ("no usable [Audio] calibration"), which then
    /// misreported this case: a perfectly calibrated master plus an unwritable
    /// artifacts folder told the operator to re-run the Setup Wizard for what
    /// is a file I/O failure. Separated so each message is true.
    /// </remarks>
    ChannelIniWriteFailed,
    ProcessStartFailed
}

/// <summary>
/// The result of a launch attempt together with the device-enumeration report
/// built during it.
/// </summary>
/// <remarks>
/// Issue #75: the diagnostics used to live in a single mutable
/// <c>LastDiagnostics</c> property that every channel overwrote. Two
/// overlapping launches could leave the caller formatting channel 1's status
/// from channel 2's capture. Returning it with the result makes that
/// impossible rather than merely unlikely.
/// </remarks>
/// <param name="Result">How the launch attempt ended.</param>
/// <param name="Diagnostics">
/// Human-readable device enumeration report: the full WinMM capture device list
/// plus the selected indices. Empty when the attempt returned before the report
/// was built.
/// </param>
public sealed record LaunchOutcome(LaunchResult Result, string Diagnostics);

/// <summary>
/// Manages the CW Skimmer process lifecycle: write INI, launch, monitor, and stop.
/// Also owns the telnet client for two-way communication (LO freq sync, click→tune).
/// </summary>
public interface ICwSkimmerLauncher
{
    bool IsRunning { get; }
    bool IsChannelRunning(int daxIqChannel);

    /// <summary>Whether any channel telnet client is currently connected to CW Skimmer.</summary>
    bool TelnetConnected { get; }

    /// <summary>
    /// Returns the WinMM capture device name and CW-Skimmer index for the given DAX-IQ channel,
    /// without launching.  Used to preview the selected endpoints in the UI.
    /// Returns null if no matching device is found.
    /// </summary>
    (string SignalDevice, int SignalIdx, string AudioDevice, int AudioIdx)?
        PreviewDevices(int daxIqChannel);

    /// <summary>Fires on the thread pool when the running state changes.</summary>
    event Action<bool>? RunningStateChanged;

    /// <summary>
    /// Fires when the user clicks on a signal in CW Skimmer.
    /// Arguments are DAX-IQ channel and clicked frequency in kHz.
    /// </summary>
    event Action<int, double>? FrequencyClicked;

    /// <summary>
    /// Fires when CW Skimmer emits a DX spot line.
    /// Arguments are DAX-IQ channel and spot payload.
    /// </summary>
    event Action<int, CwSkimmerSpotInfo>? SpotReceived;

    /// <summary>
    /// Emits key telnet lifecycle/status messages suitable for UI status display.
    /// </summary>
    event Action<string>? TelnetStatusChanged;

    /// <summary>
    /// Fires after the sync tracker successfully sends a SKIMMER/QSY command.
    /// Arguments are DAX-IQ channel, frequency in MHz, and the send timestamp.
    /// Used by inbound echo suppression to recognize our own commands when
    /// they're echoed back by CW Skimmer as click events.
    /// </summary>
    event Action<int, double, DateTime>? OutboundQsyEmitted;

    /// <summary>
    /// Writes the INI, optionally waits <see cref="CwSkimmerConfig.LaunchDelaySeconds"/>,
    /// then starts CwSkimmer.exe with <c>ini=&lt;path&gt;</c>.
    /// Connects the telnet client in the background after
    /// <see cref="CwSkimmerConfig.ConnectDelaySeconds"/>.
    /// </summary>
    Task<LaunchOutcome> LaunchAsync(
        int             daxIqChannel,
        int             sampleRateHz,
        long            centerFreqHz,
        CwSkimmerConfig config);

    /// <summary>
    /// Update the desired CW Skimmer state for a channel. Either parameter may
    /// be null to leave it unchanged. The per-channel sync tracker coalesces
    /// rapid updates and sends only what differs from last-confirmed. No
    /// heartbeat — idle channels produce zero telnet traffic (the
    /// 7e8c58c 2026-05-02 fix). No-op if the channel telnet is not connected.
    /// </summary>
    void RequestSkimmerSync(int daxIqChannel, long? loHz = null, double? vfoMHz = null);

    /// <summary>Kills all CW Skimmer processes and disconnects telnet if running.</summary>
    void Stop();

    /// <summary>Kills a single CW Skimmer process for a DAX-IQ channel if running.</summary>
    void Stop(int daxIqChannel);

}
