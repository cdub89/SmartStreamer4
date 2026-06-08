using SDRIQStreamer.CWSkimmer;
using SDRIQStreamer.Digital;
using SDRIQStreamer.FlexRadio;

namespace SDRIQStreamer.App;

/// <summary>
/// Lightweight composition root for application services.
/// Keeps startup wiring in one place without adding a DI framework.
/// </summary>
public sealed class AppServices
{
    public AppSettingsStore SettingsStore { get; }
    public AppSettingsSession SettingsSession { get; }
    public IRadioDiscovery Discovery { get; }
    public IRadioConnection Connection { get; }
    public IAudioDeviceFinder DeviceFinder { get; }
    public ICwSkimmerLauncher Launcher { get; }
    public IDigitalAppLauncher DigitalLauncher { get; }
    public IReleaseUpdateService ReleaseUpdateService { get; }

    public AppServices()
    {
        SettingsStore = new AppSettingsStore();
        SettingsSession = new AppSettingsSession(SettingsStore);
        Discovery = new FlexLibRadioDiscovery();
        Connection = new FlexLibRadioConnection();
        DeviceFinder = new WdmAudioDeviceFinder();
        ReleaseUpdateService = new ReleaseUpdateService();

        var modelFactory = new CwSkimmerIniModelFactory(DeviceFinder);
        var iniWriter = new CwSkimmerIniWriter();
        Launcher = new CwSkimmerLauncher(modelFactory, iniWriter, DeviceFinder, () => new CwSkimmerTelnetClient());
        DigitalLauncher = new DigitalAppLauncher();
    }

    public MainWindowViewModel CreateMainWindowViewModel()
        => new(Discovery, Connection, Launcher, DigitalLauncher, SettingsSession, ReleaseUpdateService, DeviceFinder);
}
