using System;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SDRIQStreamer.FlexRadio;

namespace SDRIQStreamer.App;

/// <summary>
/// Backs the SmartDeck window's telemetry footer (issue #59, phase 1).
/// Owns the telemetry subscription lifetime: it starts when the window opens
/// and stops when it closes, so a session that never opens SmartDeck does no
/// coalescing work.
/// </summary>
public sealed partial class SmartDeckViewModel : ObservableObject, IDisposable
{
    /// <summary>Shown in place of a value that has never been reported.</summary>
    private const string Absent = "---";

    private readonly IRadioConnection _connection;
    private bool _started;

    public SmartDeckViewModel(IRadioConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    [ObservableProperty]
    private string _powerText = Absent;

    [ObservableProperty]
    private string _swrText = Absent;

    [ObservableProperty]
    private string _tempText = Absent;

    [ObservableProperty]
    private string _voltsText = Absent;

    /// <summary>Subscribes and starts the radio publishing telemetry. Idempotent.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        _connection.TelemetryChanged += OnTelemetryChanged;
        _connection.ConnectionStateChanged += OnConnectionStateChanged;
        _connection.StartTelemetry();

        // Adopt whatever the connection already holds, so a reopened window
        // shows values immediately instead of dashes until the next event.
        Apply(_connection.Telemetry);
    }

    /// <summary>Unsubscribes and stops the radio publishing telemetry. Idempotent.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;

        _connection.TelemetryChanged -= OnTelemetryChanged;
        _connection.ConnectionStateChanged -= OnConnectionStateChanged;
        _connection.StopTelemetry();
        Apply(RadioTelemetryInfo.Empty);
    }

    // TelemetryChanged fires on the pump thread, not the UI thread.
    private void OnTelemetryChanged(RadioTelemetryInfo telemetry) =>
        Dispatcher.UIThread.Post(() => Apply(telemetry));

    // Bug fix 2026-08-02 (found by the Codex deep audit before this change
    // shipped): with SmartDeck left open across a radio-side drop and
    // reconnect, the footer stayed on dashes until the window was closed and
    // reopened. Root cause is that a disconnect makes FlexLibRadioConnection
    // call its own StopTelemetry(), which this ViewModel has no way to observe,
    // so _started stayed true and nothing re-armed the subscription. Re-arming
    // from the connection-state event rather than tracking a "wanted" flag
    // inside the connection keeps the desired-state logic with the window whose
    // lifetime defines it.
    private void OnConnectionStateChanged(bool connected)
    {
        if (!connected) return;
        _connection.StartTelemetry();
    }

    private void Apply(RadioTelemetryInfo telemetry)
    {
        PowerText = FormatPower(telemetry.PowerWatts);
        SwrText   = FormatSwr(telemetry.Swr, telemetry.PowerWatts);
        TempText  = Format(telemetry.PaTempCelsius, "0");
        VoltsText = Format(telemetry.VoltsDc, "0.0");
    }

    // Above this, the radio is putting out RF. The SWR meter floors at 1.0 and
    // the forward-power meter floors at 0 dBm (0.001 W), while the lowest real
    // transmit power is 1 W (30 dBm), so this threshold sits with an order of
    // magnitude of clearance on both sides.
    private const double TransmitPowerThresholdWatts = 0.01;

    /// <summary>
    /// SWR, shown only while the radio is actually transmitting.
    /// </summary>
    /// <remarks>
    /// Bug fix 2026-08-02, reported by the operator during the phase-1 live
    /// test: at rest the footer showed SWR 1.0, which reads as a real 1:1
    /// match. Root cause is that the SWR meter floors at 1.0 rather than
    /// reporting nothing, so its idle floor was being formatted as a
    /// measurement. Gated on forward power rather than on Radio.Mox because
    /// power is already in the snapshot and "no RF going out" is the condition
    /// that actually makes SWR meaningless; MOX would need new plumbing to
    /// reach the same answer. Dashes rather than 0 because SWR is undefined
    /// below 1.0, so a displayed 0 would be a value that cannot physically
    /// occur.
    /// </remarks>
    internal static string FormatSwr(double? swr, double? powerWatts) =>
        powerWatts is { } watts && watts >= TransmitPowerThresholdWatts
            ? Format(swr, "0.0")
            : Absent;

    /// <summary>
    /// Whole watts at 10 W and above, one decimal below it. A 93 W reading does
    /// not need a tenth of a watt, but a QRP operator running 5 W does.
    /// </summary>
    internal static string FormatPower(double? watts) =>
        watts is { } value
            ? value.ToString(value >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture)
            : Absent;

    /// <summary>
    /// Absent renders as dashes; a real zero renders as zero. Forward power
    /// reports a genuine 0 W on receive, which is a useful TX-idle signal and
    /// must not look like "no telemetry".
    /// </summary>
    internal static string Format(double? value, string format) =>
        value is { } present
            ? present.ToString(format, CultureInfo.InvariantCulture)
            : Absent;

    public void Dispose() => Stop();
}
