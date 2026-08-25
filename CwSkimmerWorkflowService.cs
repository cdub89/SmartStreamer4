using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SDRIQStreamer.CWSkimmer;
using SDRIQStreamer.FlexRadio;

namespace SDRIQStreamer.App;

public enum DaxStationConfirmResult { Start, Cancel }

public sealed record DaxStationConfirmRequest(
    int DaxIqChannel,
    string OwnStation,
    string OtherStation);

/// <summary>
/// Operator-facing confirmation hook for issue #39: when the requested DAX-IQ
/// channel is assigned on multiple stations, the workflow service blocks the
/// launch and asks the operator to verify DAX-the-app is set to the right
/// station. Implementations are expected to surface a modal dialog.
/// </summary>
public interface IDaxStationConfirmer
{
    Task<DaxStationConfirmResult> ConfirmAsync(int daxIqChannel, string ownStation, string otherStation);
}

/// <summary>Safe default: when no confirmer is wired, treat a collision as a Cancel.</summary>
internal sealed class CancelingDaxStationConfirmer : IDaxStationConfirmer
{
    public Task<DaxStationConfirmResult> ConfirmAsync(int daxIqChannel, string ownStation, string otherStation)
        => Task.FromResult(DaxStationConfirmResult.Cancel);
}

/// <summary>
/// Encapsulates CW Skimmer launch/stop workflow and status formatting.
/// </summary>
public sealed class CwSkimmerWorkflowService
{
    private readonly IRadioConnection _connection;
    private readonly ICwSkimmerLauncher _launcher;
    private readonly AppSettings _settings;
    private readonly IDaxStationConfirmer _daxStationConfirmer;

    public CwSkimmerWorkflowService(
        IRadioConnection connection,
        ICwSkimmerLauncher launcher,
        AppSettings settings,
        IDaxStationConfirmer? daxStationConfirmer = null)
    {
        _connection = connection;
        _launcher = launcher;
        _settings = settings;
        _daxStationConfirmer = daxStationConfirmer ?? new CancelingDaxStationConfirmer();
    }

    public bool CanLaunch(DaxIQStreamInfo? stream, string exePath) =>
        stream is not null &&
        !string.IsNullOrWhiteSpace(exePath) &&
        !_launcher.IsChannelRunning(stream.DAXIQChannel);

    public bool CanStop(DaxIQStreamInfo? stream) =>
        stream is not null && _launcher.IsChannelRunning(stream.DAXIQChannel);

    public async Task LaunchForChannelAsync(
        DaxIQStreamInfo? stream,
        string clientStation,
        string exePath,
        Action<string> addStatus)
    {
        if (stream is null) return;

        if (string.IsNullOrWhiteSpace(clientStation))
        {
            addStatus($"ch {stream.DAXIQChannel}: no control station selected — connect to a radio first.");
            return;
        }

        // Issue #39 (2026-05-18): when the requested DAX-IQ channel is also
        // assigned on another station's panadapter, DAX-the-app on this PC
        // can only bridge one station at a time, and we can't tell from any
        // FlexLib query which one is selected. Live-test on Dallas FLEX-6400:
        // DAX bound to WX7V while streamer connected as Maestro produced
        // silently-wrong CW Skimmer decoding (~18.5 kHz offset). Surface a
        // soft confirmation so the operator owns the DAX-set-correctly check
        // (we can't observe it). Only fire when our station also holds the
        // channel; otherwise the existing pan-lookup gate below produces the
        // actionable "no panadapter on '{clientStation}'" message instead.
        var foreignOwners = DaxChannelOwnership.GetForeignOwners(
            _connection.Panadapters, stream.DAXIQChannel, clientStation);
        var ownHasChannel = _connection.Panadapters.Any(p =>
            p.DAXIQChannel == stream.DAXIQChannel &&
            string.Equals(p.ClientStation, clientStation, StringComparison.OrdinalIgnoreCase));

        if (foreignOwners.Count > 0 && ownHasChannel)
        {
            var otherStation = foreignOwners[0];
            var confirmResult = await _daxStationConfirmer.ConfirmAsync(
                stream.DAXIQChannel, clientStation, otherStation);

            if (confirmResult == DaxStationConfirmResult.Cancel)
            {
                addStatus($"ch {stream.DAXIQChannel}: launch cancelled. DAX-IQ ch {stream.DAXIQChannel} also assigned to {otherStation}. In the SmartSDR DAX application, select {clientStation} to launch CW Skimmer properly.");
                return;
            }
            // Start: fall through to launch.
        }

        // Bug fix 2026-05-18 (issue #40): pre-fix code did
        //   Panadapters.FirstOrDefault(p => p.DAXIQChannel == ch)
        //   Slices.FirstOrDefault(s => s.PanadapterStreamId == pan.StreamId)
        // which mixed pan/slice from any client station the radio had. In a
        // multi-station setup where both stations have DAX-IQ ch N assigned,
        // the launcher could pick the wrong station's slice and send a foreign
        // QSY to CW Skimmer at startup (live-test 2026-05-18: connected to
        // Maestro with slice 7.030700, launcher sent QSY 7.060 — WX7V-M's
        // slice on the same radio). Filter by clientStation here and also use
        // pan.CenterFreqMHz instead of stream.CenterFreqMHz, since the same
        // FirstOrDefault leak exists in FlexLibRadioConnection.ToDaxIQStreamInfo.
        var pan = _connection.Panadapters.FirstOrDefault(p =>
            p.DAXIQChannel == stream.DAXIQChannel &&
            string.Equals(p.ClientStation, clientStation, StringComparison.OrdinalIgnoreCase));

        if (pan is null)
        {
            addStatus($"ch {stream.DAXIQChannel}: no panadapter on '{clientStation}' is assigned to DAX-IQ channel {stream.DAXIQChannel}.");
            return;
        }

        if (pan.CenterFreqMHz <= 0)
        {
            addStatus($"ch {stream.DAXIQChannel}: panadapter on '{clientStation}' has no center frequency yet.");
            return;
        }

        // Slice may be null on a fresh pan with no slice opened yet; tracker
        // catches up on the first SliceUpdated event after telnet connect.
        var slice = _connection.Slices.FirstOrDefault(s =>
            s.PanadapterStreamId == pan.StreamId &&
            string.Equals(s.ClientStation, clientStation, StringComparison.OrdinalIgnoreCase));

        var centerFreqHz = (long)Math.Round(pan.CenterFreqMHz * 1_000_000d);

        var config = new CwSkimmerConfig
        {
            ExePath = exePath,
            SkimmerIniPath = _settings.CwSkimmerIniPath,
            ConnectDelaySeconds = _settings.ConnectDelaySeconds,
            LaunchDelaySeconds = _settings.LaunchDelaySeconds,
            Callsign = ResolveTelnetCallsign(),
            TelnetPort = _settings.TelnetPortBase + (stream.DAXIQChannel * 10),
            TelnetClusterEnabled = _settings.TelnetClusterEnabled,
            InitialSliceFreqMHz = slice?.FreqMHz ?? 0,
            InitialLoFreqHz = centerFreqHz,
            OperatorMmeSignalDevIndex = IsWdmModeSelected() ? null : ResolveOperatorMmeIndex(stream.DAXIQChannel),
            OperatorWdmSignalDevIndex = IsWdmModeSelected() ? ResolveOperatorWdmIndex(stream.DAXIQChannel) : null,
        };
        addStatus($"Launching CW Skimmer on ch {stream.DAXIQChannel} ({stream.SampleRate / 1000} kHz).");

        var outcome = await _launcher.LaunchAsync(
            stream.DAXIQChannel, stream.SampleRate, centerFreqHz, config);

        var status = outcome.Result switch
        {
            LaunchResult.Success => FormatLaunchSuccess(outcome.Diagnostics),
            LaunchResult.AlreadyRunning => "Already running.",
            // No em dash: this is operator-facing prose (Codex audit, 2026-08-24).
            LaunchResult.ExeNotFound => "CW Skimmer exe not found. Check the path.",
            LaunchResult.TemplateIniNotFound => FormatTemplateIniNotFound(),
            LaunchResult.ChannelIniWriteFailed => ChannelIniWriteFailedMessage,
            LaunchResult.ProcessStartFailed => "Failed to start CW Skimmer process.",
            _ => "Launch failed."
        };
        addStatus(status);
    }

    public void StopForChannel(DaxIQStreamInfo? stream, Action<string> addStatus)
    {
        if (stream is null) return;
        _launcher.Stop(stream.DAXIQChannel);
        addStatus($"Stopped CW Skimmer on channel {stream.DAXIQChannel}.");
    }

    private string FormatLaunchSuccess(string diag)
    {
        // Bug fix 2026-05-18: pre-fix line showed "Baseline WdmSignalDev/AudioDev"
        // because Contains() picked the first matching line in the diagnostic,
        // which is always the master-INI calibration baseline regardless of mode.
        // Now picks the runtime-effective MME or WDM field by StartsWith (skipping
        // the "Baseline " prefix) and tags the line with the active mode so the
        // operator can see at a glance which driver family the channel uses and
        // what indices were written to the channel INI.
        //
        // Issue #75: the report now arrives with the launch result instead of
        // being read back from a shared property, so a concurrent launch on
        // another channel can no longer supply the lines formatted here.
        var wdmMode = IsWdmModeSelected();
        var signalLabel = wdmMode ? "WdmSignalDev" : "MmeSignalDev";
        var audioLabel  = wdmMode ? "WdmAudioDev"  : "MmeAudioDev";

        var loLine     = diag.Split('\n').FirstOrDefault(l => l.Contains("CenterFreq"))?.Trim() ?? "";
        var signalLine = diag.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(signalLabel))?.Trim() ?? "";
        var audioLine  = diag.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(audioLabel))?.Trim()  ?? "";

        var modeTag = wdmMode ? "WDM" : "MME";
        return $"CW Skimmer running ({modeTag})  |  {loLine}  |  {signalLine}  |  {audioLine}";
    }

    /// <summary>
    /// Turns a <see cref="LaunchResult.TemplateIniNotFound"/> into a message
    /// naming what is actually wrong with the configured path.
    /// </summary>
    /// <remarks>
    /// Issue #74 (2026-08-24): the operator had entered the CW Skimmer folder
    /// instead of the CwSkimmer.ini file and the old generic message gave no
    /// hint. Name the configured path and the folder-vs-file mistake directly.
    /// Issue #75 (2026-08-24) added the fourth branch: the retired
    /// DeviceNotFound result folded in here, and a file that exists but carries
    /// no [Audio] calibration must not be reported as missing.
    /// The variants are derived from the filesystem rather than from a second
    /// enum member, which is what let that member be deleted.
    /// </remarks>
    internal static string FormatTemplateIniNotFound(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "CW Skimmer INI path is not set. Point the Config tab's cwskimmer.ini field at your CwSkimmer.ini file.";
        if (Directory.Exists(path))
            return $"CW Skimmer INI path '{path}' is a folder. Select the CwSkimmer.ini file inside it on the Config tab.";
        if (File.Exists(path))
            return $"CW Skimmer INI at '{path}' has no usable [Audio] calibration. Re-run the Set Up Wizard on the CW Config tab. (Device log: artifacts\\cwskimmer\\ini\\device-diagnostic.txt)";
        return $"CW Skimmer INI not found at '{path}'. Check the cwskimmer.ini path on the Config tab.";
    }

    private string FormatTemplateIniNotFound() =>
        FormatTemplateIniNotFound(_settings.CwSkimmerIniPath);

    /// <summary>
    /// The master INI was readable; writing the per-channel copy is what failed.
    /// Kept distinct from the template message so a locked or unwritable
    /// artifacts folder is not reported as a calibration problem (Codex deep
    /// audit of issue #75, 2026-08-24).
    /// </summary>
    internal const string ChannelIniWriteFailedMessage =
        "Could not write the per-channel CW Skimmer INI. Check that artifacts\\cwskimmer\\ini is writable and that CW Skimmer is not holding the file.";

    private bool IsWdmModeSelected() =>
        string.Equals(_settings.SkimmerSoundcardDriverMode, "WDM", StringComparison.OrdinalIgnoreCase);

    private int? ResolveOperatorMmeIndex(int daxIqChannel) => daxIqChannel switch
    {
        1 => _settings.MmeDeviceIndexCh1,
        2 => _settings.MmeDeviceIndexCh2,
        3 => _settings.MmeDeviceIndexCh3,
        4 => _settings.MmeDeviceIndexCh4,
        _ => null,
    };

    private int? ResolveOperatorWdmIndex(int daxIqChannel) => daxIqChannel switch
    {
        1 => _settings.WdmDeviceIndexCh1,
        2 => _settings.WdmDeviceIndexCh2,
        3 => _settings.WdmDeviceIndexCh3,
        4 => _settings.WdmDeviceIndexCh4,
        _ => null,
    };

    private string ResolveTelnetCallsign()
    {
        if (IsLikelyCallsign(_settings.Callsign))
            return _settings.Callsign.Trim();

        if (IsLikelyCallsign(_connection.OwnClientStation))
            return _connection.OwnClientStation.Trim();

        return "N0CALL";
    }

    private static bool IsLikelyCallsign(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Length < 3 || trimmed.Length > 16)
            return false;

        // Reject Flex client-handle style placeholders like 0x32F4599F.
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            trimmed.Length > 2 &&
            trimmed[2..].All(Uri.IsHexDigit))
        {
            return false;
        }

        bool hasLetter = false;
        bool hasDigit = false;
        foreach (var ch in trimmed)
        {
            if (char.IsLetter(ch)) hasLetter = true;
            else if (char.IsDigit(ch)) hasDigit = true;
            else if (ch != '-' && ch != '/')
                return false;
        }

        return hasLetter && hasDigit;
    }
}
