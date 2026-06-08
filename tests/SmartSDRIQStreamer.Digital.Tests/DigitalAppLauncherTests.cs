using SDRIQStreamer.Digital;

namespace SmartSDRIQStreamer.Digital.Tests;

public sealed class DigitalAppLauncherTests
{
    private static string TempExe() => Path.GetTempFileName(); // a real file so File.Exists passes

    [Fact]
    public async Task LaunchAsync_StartsWithRigName_AndTracksInstance()
    {
        var exe = TempExe();
        try
        {
            var runner = new FakeRunner();
            var launcher = new DigitalAppLauncher(runner);
            bool? lastState = null;
            launcher.RunningStateChanged += s => lastState = s;

            var result = await launcher.LaunchAsync(DigitalEngines.WsjtX(exe), "SliceA");

            Assert.Equal(DigitalLaunchResult.Success, result);
            Assert.Single(runner.Calls);
            Assert.Equal(exe, runner.Calls[0].Exe);
            Assert.Equal("--rig-name=SliceA", runner.Calls[0].Args);
            Assert.True(launcher.IsInstanceRunning("SliceA"));
            Assert.True(launcher.IsRunning);
            Assert.True(lastState);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task LaunchAsync_ExeMissing_ReturnsExeNotFound_AndDoesNotStart()
    {
        var runner = new FakeRunner();
        var launcher = new DigitalAppLauncher(runner);

        var result = await launcher.LaunchAsync(
            DigitalEngines.Jtdx(@"C:\nope\does-not-exist\jtdx.exe"), "SliceA");

        Assert.Equal(DigitalLaunchResult.ExeNotFound, result);
        Assert.Empty(runner.Calls);
        Assert.False(launcher.IsRunning);
    }

    [Fact]
    public async Task LaunchAsync_SameRigTwice_ReturnsAlreadyRunning()
    {
        var exe = TempExe();
        try
        {
            var launcher = new DigitalAppLauncher(new FakeRunner());
            await launcher.LaunchAsync(DigitalEngines.WsjtX(exe), "SliceA");

            var second = await launcher.LaunchAsync(DigitalEngines.WsjtX(exe), "SliceA");

            Assert.Equal(DigitalLaunchResult.AlreadyRunning, second);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task TwoInstances_StopOne_LeavesTheOther()
    {
        var exe = TempExe();
        try
        {
            var launcher = new DigitalAppLauncher(new FakeRunner());
            await launcher.LaunchAsync(DigitalEngines.WsjtX(exe), "SliceA");
            await launcher.LaunchAsync(DigitalEngines.WsjtX(exe), "SliceB");

            launcher.Stop("SliceA");

            Assert.False(launcher.IsInstanceRunning("SliceA"));
            Assert.True(launcher.IsInstanceRunning("SliceB"));
            Assert.True(launcher.IsRunning);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task ProcessExit_FlipsRunningStateToFalse()
    {
        var exe = TempExe();
        try
        {
            var runner = new FakeRunner();
            var launcher = new DigitalAppLauncher(runner);
            bool? lastState = null;
            launcher.RunningStateChanged += s => lastState = s;

            await launcher.LaunchAsync(DigitalEngines.WsjtX(exe), "SliceA");
            runner.Processes[0].SimulateExit();

            Assert.False(launcher.IsRunning);
            Assert.False(lastState);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task InvalidRigName_ReturnsInvalidRigName()
    {
        var exe = TempExe();
        try
        {
            var launcher = new DigitalAppLauncher(new FakeRunner());
            Assert.Equal(DigitalLaunchResult.InvalidRigName,
                await launcher.LaunchAsync(DigitalEngines.WsjtX(exe), "  "));
        }
        finally { File.Delete(exe); }
    }

    private sealed class FakeRunner : IDigitalProcessRunner
    {
        public List<(string Exe, string Args)> Calls { get; } = [];
        public List<FakeProcess> Processes { get; } = [];

        public IDigitalProcess Start(string exePath, string arguments)
        {
            Calls.Add((exePath, arguments));
            var p = new FakeProcess();
            Processes.Add(p);
            return p;
        }
    }

    private sealed class FakeProcess : IDigitalProcess
    {
        public bool HasExited { get; private set; }
        public event EventHandler? Exited;

        public void Kill()
        {
            if (HasExited) return;
            HasExited = true;
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public void SimulateExit()
        {
            if (HasExited) return;
            HasExited = true;
            Exited?.Invoke(this, EventArgs.Empty);
        }
    }
}
