using System;
using System.Linq;
using System.Threading.Tasks;
using SDRIQStreamer.CWSkimmer;
using SDRIQStreamer.FlexRadio;

namespace SDRIQStreamer.App;

/// <summary>
/// Encapsulates CW Skimmer launch/stop workflow and status formatting.
/// </summary>
public sealed class CwSkimmerWorkflowService
{
    private readonly IRadioConnection _connection;
    private readonly ICwSkimmerLauncher _launcher;
    private readonly AppSettings _settings;

    public CwSkimmerWorkflowService(
        IRadioConnection connection,
        ICwSkimmerLauncher launcher,
        AppSettings settings)
    {
        _connection = connection;
        _launcher = launcher;
        _settings = settings;
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

        var result = await _launcher.LaunchAsync(
            stream.DAXIQChannel, stream.SampleRate, centerFreqHz, config);

        var status = result switch
        {
            LaunchResult.Success => FormatLaunchSuccess(),
            LaunchResult.AlreadyRunning => "Already running.",
            LaunchResult.ExeNotFound => "CW Skimmer exe not found — check the path.",
            LaunchResult.TemplateIniNotFound => "CW Skimmer INI template not found — set a valid cwskimmer.ini path.",
            LaunchResult.DeviceNotFound => FormatDeviceNotFound(stream.DAXIQChannel),
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

    private string FormatLaunchSuccess()
    {
        // Bug fix 2026-05-18: pre-fix line showed "Baseline WdmSignalDev/AudioDev"
        // because Contains() picked the first matching line in the diagnostic,
        // which is always the master-INI calibration baseline regardless of mode.
        // Now picks the runtime-effective MME or WDM field by StartsWith (skipping
        // the "Baseline " prefix) and tags the line with the active mode so the
        // operator can see at a glance which driver family the channel uses and
        // what indices were written to the channel INI.
        var diag = _launcher.LastDiagnostics;
        var wdmMode = IsWdmModeSelected();
        var signalLabel = wdmMode ? "WdmSignalDev" : "MmeSignalDev";
        var audioLabel  = wdmMode ? "WdmAudioDev"  : "MmeAudioDev";

        var loLine     = diag.Split('\n').FirstOrDefault(l => l.Contains("CenterFreq"))?.Trim() ?? "";
        var signalLine = diag.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(signalLabel))?.Trim() ?? "";
        var audioLine  = diag.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(audioLabel))?.Trim()  ?? "";

        var modeTag = wdmMode ? "WDM" : "MME";
        return $"CW Skimmer running ({modeTag})  |  {loLine}  |  {signalLine}  |  {audioLine}";
    }

    private string FormatDeviceNotFound(int channel)
    {
        var diag = _launcher.LastDiagnostics;
        if (string.IsNullOrEmpty(diag))
            return $"DAX IQ {channel} audio device not found.";

        var lines = diag.Split('\n')
            .SkipWhile(l => !l.Contains("WinMM WaveIn"))
            .Take(12)
            .ToArray();
        return $"DAX IQ {channel} not found in WinMM list:\n{string.Join("\n", lines)}\n" +
               "(Full log: artifacts\\cwskimmer\\ini\\device-diagnostic.txt)";
    }

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
