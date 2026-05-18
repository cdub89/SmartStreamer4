namespace SDRIQStreamer.CWSkimmer;

using System.Globalization;

/// <summary>
/// Builds a <see cref="CwSkimmerIniModel"/> for a specific DAX-IQ channel.
///
/// Two driver families are first-class; the operator picks one in the Setup
/// Wizard and supplies the per-channel device numbers they see in CW Skimmer.
/// We currently can't predict whether MME or WDM is the better choice on a
/// given host (it varies between FlexLib 4.1.x and 4.2.x, and may be host-
/// specific beyond that), so the factory trusts the wizard selection rather
/// than forcing a mode.
///
/// MME branch (default; <c>UseWdm=false</c>):
///   WinMM device names are fully enumerable, so per-channel MmeSignalDev is
///   resolved from <see cref="CwSkimmerConfig.OperatorMmeSignalDevIndex"/>
///   (wizard input) when present, otherwise by looking up "DAX IQ {N}"
///   (DAX v2) or "DAX IQ RX {N}" (DAX v1) in the live device list. MmeAudioDev
///   (the user's local speakers/headphones) is copied verbatim from the
///   master INI.
///
/// WDM branch (operator opt-in; <c>UseWdm=true</c>):
///   CW Skimmer's WDM Audio tab list uses a private / filtered enumeration
///   that does NOT match what DirectSound or WinMM report — field-verified
///   2026-05-18, see <c>DirectSoundProbe</c> header for the data. So there
///   is no reliable way to predict CW Skimmer's WDM slot for a given DAX-IQ
///   channel from outside the app. The operator must enter WDM indices into
///   the Setup Wizard by reading them off CW Skimmer's own Audio tab. When
///   <see cref="CwSkimmerConfig.OperatorWdmSignalDevIndex"/> is set the
///   factory emits a WDM-mode INI with that value (converted 1-based UI to
///   0-based INI); without an operator override the WDM fields from the
///   master INI are propagated inertly under MME mode.
/// </summary>
public sealed class CwSkimmerIniModelFactory
{
    private readonly IAudioDeviceFinder _deviceFinder;

    public CwSkimmerIniModelFactory(IAudioDeviceFinder deviceFinder)
    {
        _deviceFinder = deviceFinder;
    }

    public CwSkimmerIniModel Build(
        int              daxIqChannel,
        int              sampleRateHz,
        long             centerFreqHz,
        CwSkimmerConfig  config)
    {
        if (!TryReadCalibration(config.SkimmerIniPath,
                out var wdmIQ1, out var wdmAudio,
                out var mmeIQ1, out var mmeAudio))
        {
            return new CwSkimmerIniModel(
                WdmSignalDevIndex:          -1,
                WdmAudioDevIndex:           -1,
                MmeSignalDevIndex:          -1,
                MmeAudioDevIndex:           0,
                UseWdm:                     false,
                CalibrationBaseSignalIndex: -1,
                CalibrationBaseAudioIndex:  -1,
                SampleRateHz:               sampleRateHz,
                CenterFreqHz:               centerFreqHz,
                Config:                     config);
        }

        // MME signal: operator override (1-based UI) wins; otherwise per-channel
        // WinMM name lookup; otherwise sequential fallback. INI stores 0-based.
        int mmeSignal;
        if (config.OperatorMmeSignalDevIndex is int operatorMmeUi && operatorMmeUi > 0)
        {
            mmeSignal = operatorMmeUi - 1;
        }
        else
        {
            var uiMmeN = _deviceFinder.FindDaxIqSignalDeviceIndex(daxIqChannel);
            mmeSignal = uiMmeN >= 0
                ? uiMmeN - 1
                : mmeIQ1 + (daxIqChannel - 1);
        }

        // WDM signal: when the operator supplies a 1-based UI index for this
        // channel, write a WDM-mode INI with that index. This is the only
        // reliable way to drive multi-channel WDM (see issue #19). When null,
        // fall back to MME mode and copy the master WDM index inertly.
        var useWdm = config.OperatorWdmSignalDevIndex is int operatorWdmUi && operatorWdmUi > 0;
        var wdmSignal = useWdm
            ? config.OperatorWdmSignalDevIndex!.Value - 1
            : wdmIQ1;

        return new CwSkimmerIniModel(
            WdmSignalDevIndex:          wdmSignal,
            WdmAudioDevIndex:           wdmAudio,      // copied from master verbatim
            MmeSignalDevIndex:          mmeSignal,
            MmeAudioDevIndex:           mmeAudio,
            UseWdm:                     useWdm,
            CalibrationBaseSignalIndex: wdmIQ1,
            CalibrationBaseAudioIndex:  wdmAudio,
            SampleRateHz:               sampleRateHz,
            CenterFreqHz:               centerFreqHz,
            Config:                     config);
    }

    private static bool TryReadCalibration(
        string templateIniPath,
        out int wdmIQ1,
        out int wdmAudio,
        out int mmeIQ1,
        out int mmeAudio)
    {
        wdmIQ1   = -1;
        wdmAudio = -1;
        mmeIQ1   = 0;
        mmeAudio = 0;

        if (string.IsNullOrWhiteSpace(templateIniPath) || !File.Exists(templateIniPath))
            return false;

        bool inAudio = false;
        foreach (var raw in File.ReadLines(templateIniPath))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inAudio = string.Equals(line, "[Audio]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inAudio || !line.Contains('='))
                continue;

            var idx   = line.IndexOf('=');
            var key   = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                continue;

            if (key.Equals("WdmSignalDev", StringComparison.OrdinalIgnoreCase))
                wdmIQ1 = parsed;
            else if (key.Equals("WdmAudioDev", StringComparison.OrdinalIgnoreCase))
                wdmAudio = parsed;
            else if (key.Equals("MmeSignalDev", StringComparison.OrdinalIgnoreCase))
                mmeIQ1 = parsed;
            else if (key.Equals("MmeAudioDev", StringComparison.OrdinalIgnoreCase))
                mmeAudio = parsed;
        }

        // Master INI must be calibrated to at least know the user's audio output.
        // We accept the calibration if WDM fields are present (legacy users) OR
        // if MmeAudioDev is set (post-pivot users who calibrated in MME mode).
        return wdmIQ1 >= 0 && wdmAudio >= 0;
    }
}
