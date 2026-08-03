namespace SDRIQStreamer.FlexRadio;

/// <summary>
/// Abstraction over connecting to a single discovered radio.
/// FlexLib types do not appear in this interface.
/// </summary>
public interface IRadioConnection
{
    // ── Connection ───────────────────────────────────────────────────────────

    bool IsConnected { get; }
    string? ConnectedModel { get; }
    string? ConnectedSerial { get; }
    string? Versions { get; }

    /// <summary>Our own client handle assigned by the radio after connect.</summary>
    uint OwnClientHandle { get; }

    /// <summary>Station name of our own connected client.</summary>
    string OwnClientStation { get; }

    /// <summary>Connect to the given radio. Returns true on success.</summary>
    Task<bool> ConnectAsync(DiscoveredRadio radio);

    /// <summary>Disconnect from the currently connected radio.</summary>
    void Disconnect();

    /// <summary>Fires on the thread pool when the connection state changes.</summary>
    event Action<bool> ConnectionStateChanged;

    // ── Post-connect radio details ───────────────────────────────────────────

    IReadOnlyList<PanadapterInfo>  Panadapters  { get; }
    IReadOnlyList<SliceInfo>       Slices       { get; }
    IReadOnlyList<DaxIQStreamInfo> DaxIQStreams { get; }

    /// <summary>
    /// GUI clients currently connected to this radio (SmartSDR, Maestro, etc.)
    /// as reported by FlexLib. Surfaced on the Logs tab as a multi-station
    /// troubleshooting aid. Note: DAX-the-app does not register here on tested
    /// firmware, so this list is not a reliable "is DAX bound to us" signal.
    /// </summary>
    IReadOnlyList<GuiClientInfo> GuiClients { get; }

    /// <summary>Rolling average DAX bandwidth in kbps (all DAX channels combined).</summary>
    int AvgDAXKbps { get; }

    /// <summary>Current network health and RTT values for the connected radio session.</summary>
    NetworkStatusInfo NetworkStatus { get; }

    /// <summary>
    /// Request a DAX-IQ stream for the panadapter associated with <paramref name="pan"/>.
    /// Returns the outcome; on Success the stream will appear in DaxIQStreams.
    /// </summary>
    Task<RequestStreamResult> RequestDaxIQStreamAsync(PanadapterInfo pan);

    /// <summary>
    /// Stop (remove) the DAX-IQ stream for the panadapter associated with <paramref name="pan"/>.
    /// Returns the outcome; on Success the stream will disappear from DaxIQStreams.
    /// </summary>
    Task<RequestStreamResult> StopDaxIQStreamAsync(PanadapterInfo pan);

    event Action<PanadapterInfo>  PanadapterAdded;
    event Action<PanadapterInfo>  PanadapterRemoved;
    event Action<PanadapterInfo>  PanadapterUpdated;
    event Action<SliceInfo>       SliceAdded;
    event Action<SliceInfo>       SliceRemoved;
    event Action<SliceInfo>       SliceUpdated;
    event Action<DaxIQStreamInfo> DaxIQStreamAdded;
    event Action<DaxIQStreamInfo> DaxIQStreamRemoved;

    /// <summary>Fires when an existing stream's properties (e.g. centre frequency) change.</summary>
    event Action<DaxIQStreamInfo> DaxIQStreamUpdated;

    /// <summary>Fires when AvgDAXKbps changes.</summary>
    event Action<int> AvgDAXKbpsChanged;

    /// <summary>Fires when network quality or RTT values change.</summary>
    event Action<NetworkStatusInfo> NetworkStatusChanged;

    /// <summary>Fires when the GUI client list changes (client connects or disconnects).</summary>
    event Action<IReadOnlyList<GuiClientInfo>> GuiClientsChanged;

    /// <summary>
    /// Fires when a connect-path / object-lifecycle diagnostic line is available.
    /// Subscribers route these lines into `streamer-status.log` with a `[FLEX]`
    /// category prefix. Fires per discrete event (connect, pan/slice/IQ add or
    /// remove, GUI client add or remove, disconnect trigger). Not a hot path.
    /// </summary>
    event Action<string> DiagnosticEvent;

    /// <summary>
    /// When true, high-churn object-lifecycle diagnostics (DAX-IQ stream
    /// add/remove, which fires for every station's DAX activity on the radio)
    /// are emitted via <see cref="DiagnosticEvent"/>. Lifecycle diagnostics
    /// (connect, disconnect, GUI clients, pan/slice add/remove) always fire.
    /// Default false; toggled by the operator's debug-logging setting
    /// (issue #58).
    /// </summary>
    bool VerboseDiagnostics { get; set; }

    /// <summary>
    /// Tune the given slice to <paramref name="freqMHz"/>.
    /// No-op if the slice is not found or the radio is not connected.
    /// </summary>
    Task SetSliceFrequencyAsync(SliceInfo slice, double freqMHz);

    /// <summary>
    /// Publish a spot to the connected radio.
    /// No-op when disconnected or when spot payload is invalid.
    /// </summary>
    Task PublishSpotAsync(RadioSpotInfo spot);

    /// <summary>
    /// Reset session network status display values.
    /// Subsequent FlexLib updates repopulate current and max RTT values.
    /// </summary>
    void ResetNetworkStatus();

    // ── Telemetry (issue #59, SmartDeck) ─────────────────────────────────────

    /// <summary>
    /// Latest coalesced telemetry snapshot, or <see cref="RadioTelemetryInfo.Empty"/>
    /// before the first reading. Radio-level values only; nothing here is
    /// slice-scoped.
    /// </summary>
    RadioTelemetryInfo Telemetry { get; }

    /// <summary>
    /// Fires off the UI thread when a new telemetry snapshot is available, at
    /// most once per display interval. Subscribers must marshal to the UI
    /// thread themselves.
    /// </summary>
    event Action<RadioTelemetryInfo> TelemetryChanged;

    /// <summary>
    /// Begin publishing telemetry. Idempotent, and a no-op while disconnected.
    /// Subscription is started on demand rather than at connect so a session
    /// that never opens SmartDeck does no coalescing work: the underlying meter
    /// events stream at roughly 28 events/sec combined.
    /// </summary>
    void StartTelemetry();

    /// <summary>
    /// Stop publishing telemetry and drop accumulated readings, so the next
    /// start begins from absent rather than from stale values. Idempotent.
    /// </summary>
    void StopTelemetry();
}
