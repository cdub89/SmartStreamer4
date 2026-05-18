namespace SDRIQStreamer.CWSkimmer;

public enum LaunchResult
{
    Success,
    AlreadyRunning,
    ExeNotFound,
    TemplateIniNotFound,
    DeviceNotFound,
    ProcessStartFailed
}

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
    /// Human-readable device enumeration report from the most recent launch attempt.
    /// Contains the full WinMM capture device list plus the selected indices.
    /// Empty string before the first launch attempt.
    /// </summary>
    string LastDiagnostics { get; }

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
    Task<LaunchResult> LaunchAsync(
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
