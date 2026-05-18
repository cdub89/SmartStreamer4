using System.Runtime.InteropServices;

namespace SDRIQStreamer.CWSkimmer;

/// <summary>
/// Enumerates DirectSound capture and output devices. Used for diagnostic
/// logging only — see <c>CwSkimmerLauncher.BuildDiagnostics</c>.
///
/// IMPORTANT: an earlier version of this header claimed the callback order
/// here matches CW Skimmer's WDM Audio tab slot numbering. That is WRONG and
/// was field-disproven 2026-05-18 on Dallas: CW Skimmer's WDM list had
/// DAX IQ 1 at slot 4, while both WinMM and DirectSound put it at slot 13/14.
/// CW Skimmer's WDM Audio tab is built from a different / filtered
/// enumeration we cannot access from outside the app. Do not use the
/// DirectSound enumeration to predict CW Skimmer's WDM slot numbers.
///
/// Empirically the WinMM and DirectSound lists track each other (DirectSound
/// just adds a "Primary Sound Driver" entry at callback index 0), so the
/// DirectSound dump in the diagnostic log is approximately the WinMM list
/// shifted by one — useful as a sanity reference, not as a CW Skimmer
/// proxy.
/// </summary>
internal static class DirectSoundProbe
{
    public readonly record struct DirectSoundDevice(int Index, string Description, string Module);

    public static IReadOnlyList<DirectSoundDevice> EnumerateCaptureDevices()
    {
        var list = new List<DirectSoundDevice>();
        DSEnumCallback callback = (IntPtr _, string description, string module, IntPtr _) =>
        {
            list.Add(new DirectSoundDevice(list.Count, description ?? string.Empty, module ?? string.Empty));
            return true;
        };

        try { DirectSoundCaptureEnumerateW(callback, IntPtr.Zero); }
        catch { /* dsound.dll absent or call failed — return whatever we have */ }

        GC.KeepAlive(callback);
        return list;
    }

    public static IReadOnlyList<DirectSoundDevice> EnumerateOutputDevices()
    {
        var list = new List<DirectSoundDevice>();
        DSEnumCallback callback = (IntPtr _, string description, string module, IntPtr _) =>
        {
            list.Add(new DirectSoundDevice(list.Count, description ?? string.Empty, module ?? string.Empty));
            return true;
        };

        try { DirectSoundEnumerateW(callback, IntPtr.Zero); }
        catch { }

        GC.KeepAlive(callback);
        return list;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate bool DSEnumCallback(
        IntPtr lpGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string lpcstrDescription,
        [MarshalAs(UnmanagedType.LPWStr)] string lpcstrModule,
        IntPtr lpContext);

    [DllImport("dsound.dll", CharSet = CharSet.Unicode, EntryPoint = "DirectSoundCaptureEnumerateW")]
    private static extern int DirectSoundCaptureEnumerateW(DSEnumCallback lpDSEnumCallback, IntPtr lpContext);

    [DllImport("dsound.dll", CharSet = CharSet.Unicode, EntryPoint = "DirectSoundEnumerateW")]
    private static extern int DirectSoundEnumerateW(DSEnumCallback lpDSEnumCallback, IntPtr lpContext);
}
