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

    /// <summary>The operator's own name for the radio. Can be blank: it is optional on the radio.</summary>
    string? ConnectedNickname { get; }
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

    // ── Slice control surface (issue #59 phase 2a) ───────────────────────────

    /// <summary>
    /// Set the slice's demodulation mode. No-op if the slice is not found.
    /// </summary>
    Task SetSliceModeAsync(SliceInfo slice, SliceMode mode);

    // ── RIT and XIT (issue #73) ──────────────────────────────────────────────

    /// <summary>
    /// Engage or release RIT on the slice. Independent of the offset: the radio
    /// keeps the two separately, so turning RIT off leaves the offset stored and
    /// turning it back on restores it rather than starting from zero.
    /// </summary>
    Task SetSliceRitEnabledAsync(SliceInfo slice, bool enabled);

    /// <summary>
    /// Set the slice's RIT offset in Hz. Clamped to the radio's range by the
    /// implementation, because FlexLib silently drops an out-of-range write
    /// rather than clamping it.
    /// </summary>
    Task SetSliceRitOffsetAsync(SliceInfo slice, int offsetHz);

    /// <summary>Engage or release XIT on the slice. See <see cref="SetSliceRitEnabledAsync"/>.</summary>
    Task SetSliceXitEnabledAsync(SliceInfo slice, bool enabled);

    /// <summary>Set the slice's XIT offset in Hz. See <see cref="SetSliceRitOffsetAsync"/>.</summary>
    Task SetSliceXitOffsetAsync(SliceInfo slice, int offsetHz);

    /// <summary>
    /// Set the slice's AGC threshold (AGC-T), 0-100. FlexLib clamps the value
    /// itself, so callers need not. No-op if the slice is not found or already
    /// holds the value.
    /// </summary>
    Task SetSliceAgcThresholdAsync(SliceInfo slice, int threshold);

    // ── Receive-chain toggles (issue #76) ────────────────────────────────────
    //
    // On/off only, no levels: see the remarks on SliceInfo.ApfOn.

    /// <summary>Turn the audio peaking filter on or off for the slice.</summary>
    Task SetSliceApfEnabledAsync(SliceInfo slice, bool enabled);

    /// <summary>Turn noise reduction on or off for the slice.</summary>
    Task SetSliceNrEnabledAsync(SliceInfo slice, bool enabled);

    /// <summary>Turn the noise blanker on or off for the slice.</summary>
    Task SetSliceNbEnabledAsync(SliceInfo slice, bool enabled);

    /// <summary>
    /// Turn diversity reception on or off for the slice. No-op when
    /// <see cref="DiversityIsAllowed"/> is false.
    /// </summary>
    Task SetSliceDiversityEnabledAsync(SliceInfo slice, bool enabled);

    /// <summary>
    /// True when this radio supports diversity reception (issue #76).
    /// </summary>
    /// <remarks>
    /// Radio-reported, with a FlexLib model fallback for older firmware. False
    /// on the 6300, 6400, 6400M and 6500, so a diversity control has to hide
    /// rather than sit dead on those radios. Read this rather than testing the
    /// model name: the radio knows and the model list goes stale.
    /// </remarks>
    bool DiversityIsAllowed { get; }

    /// <summary>
    /// Set the panadapter's RF gain, in dB. Callers clamp to the panadapter's
    /// radio-reported range first (see <see cref="SteppedRange.Next"/>); this
    /// no-ops if the panadapter is not found or already holds the value.
    /// </summary>
    Task SetPanadapterRfGainAsync(PanadapterInfo panadapter, int rfGain);

    /// <summary>
    /// Set the slice's receive antenna to one of its radio-reported
    /// <see cref="SliceInfo.RxAntennaOptions"/>. The radio refuses antenna
    /// changes while transmitting, so no app-side transmit guard is applied.
    /// </summary>
    Task SetSliceRxAntennaAsync(SliceInfo slice, string antenna);

    /// <summary>
    /// Set the slice's transmit antenna to one of its radio-reported
    /// <see cref="SliceInfo.TxAntennaOptions"/>.
    /// </summary>
    Task SetSliceTxAntennaAsync(SliceInfo slice, string antenna);

    /// <summary>
    /// The station whose context this connection operates in, matching the
    /// app's selected control station. Setting it binds our non-GUI client to
    /// that station's GUI client, which is what makes client-scoped radio
    /// state (transmit power) readable. Empty leaves the connection unbound.
    /// </summary>
    /// <remarks>
    /// Slice-scoped state never needed this, which is why the connection went
    /// without it until issue #64. Left unset, FlexLib still binds at connect,
    /// but to an empty client id (<c>Radio.cs:2249</c>), and the radio answers
    /// in a context belonging to no station.
    /// </remarks>
    string ControlStation { get; set; }

    // ── Transmit power (issue #64; SmartDeck's PWR cell) ─────────────────────

    /// <summary>
    /// The radio's transmit power setting in watts, or <c>null</c> until the
    /// radio has reported one. Absent is distinct from zero here: 0 W is a
    /// setting the operator can select, so it cannot double as "not known yet".
    /// </summary>
    /// <remarks>
    /// Radio-scoped, not slice-scoped, and the radio persists it per band on
    /// its own. This is the power <em>setting</em>, not the forward power
    /// <see cref="RadioTelemetryInfo.PowerWatts"/> reads off the meter.
    /// </remarks>
    int? RfPowerWatts { get; }

    /// <summary>
    /// The radio's rated PA output in watts, or <c>null</c> until the radio has
    /// reported it in the station's context.
    /// </summary>
    /// <remarks>
    /// Issue #77. This is the ceiling <see cref="RfPowerWatts"/> is measured
    /// against, and it is not always 100: a FLEX-6000 or 8000 reports 100, an
    /// Aurora (AU-520) reports 500. Callers that offer a full-power preset or
    /// step the power should read the ceiling from here rather than assuming
    /// one, which is the bug this property exists to prevent.
    /// </remarks>
    int? MaxRfPowerWatts { get; }

    /// <summary>
    /// Fires when the radio reports a new transmit power, whether we asked for
    /// it or another client did. May fire off the UI thread.
    /// </summary>
    event Action<int?> RfPowerChanged;

    /// <summary>
    /// Set the radio's transmit power, in watts. Clamped to the radio's rated
    /// output, so callers need not. No-op while disconnected, before the radio
    /// has reported its rating, or when it already holds the value.
    /// </summary>
    /// <remarks>
    /// Watts are converted to the radio's 0-100 setting on the way down, so a
    /// value the radio cannot represent lands on the nearest one it can: on a
    /// 500 W PA the granularity is 5 W (issue #77).
    /// <para>
    /// This changes what a subsequent transmission will do; it does not key the
    /// radio.
    /// </para>
    /// </remarks>
    Task SetRfPowerAsync(int watts);

    // ── Transmit state (issue #69, SmartDeck TX indication) ──────────────────

    /// <summary>
    /// True while the radio is keyed. Radio-scoped, not slice-scoped: pair it
    /// with <see cref="SliceInfo.IsTransmitSlice"/> to know which slice is on
    /// the air. False while disconnected.
    /// </summary>
    /// <remarks>
    /// Derived from FlexLib's <c>Radio.Mox</c>, which follows the interlock
    /// state, so CW key-down raises it and not just the MOX button. It says the
    /// radio is keyed, not that RF is leaving it: between CW elements, or on
    /// SSB with no audio, this is true while forward power is nil. Anything
    /// that needs "RF is actually going out" must gate on forward power
    /// instead, as the SWR readout does.
    /// </remarks>
    bool IsTransmitting { get; }

    /// <summary>
    /// Fires on a change of transmit state. May fire off the UI thread, and
    /// fires per transition, so a CW keying burst raises it repeatedly.
    /// </summary>
    event Action<bool> TransmitStateChanged;

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
