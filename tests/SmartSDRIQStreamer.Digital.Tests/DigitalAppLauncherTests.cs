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
    public async Task ClosingOneOfTwoInstances_NotifiesPerInstance_AndPrunesAndDisposesIt()
    {
        var exe = TempExe();
        try
        {
            var runner = new FakeRunner();
            var launcher = new DigitalAppLauncher(runner);
            await launcher.LaunchAsync(DigitalEngines.WsjtX(exe), "SliceA");
            await launcher.LaunchAsync(DigitalEngines.WsjtX(exe), "SliceB");

            var instancesChanged = 0;
            bool? aggregateState = null;
            launcher.InstancesChanged += () => instancesChanged++;
            launcher.RunningStateChanged += s => aggregateState = s;

            // Close only SliceA (e.g. from its own window).
            runner.Processes[0].SimulateExit();

            // Per-instance event fired even though the aggregate stayed running...
            Assert.True(instancesChanged >= 1);
            Assert.Null(aggregateState);                       // aggregate unchanged -> not raised
            Assert.False(launcher.IsInstanceRunning("SliceA")); // pruned
            Assert.True(launcher.IsInstanceRunning("SliceB"));  // other survives
            Assert.True(launcher.IsRunning);
            Assert.True(runner.Processes[0].Disposed);          // exited instance disposed
            Assert.False(runner.Processes[1].Disposed);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task RelaunchRig_WhosePriorInstanceExitedUnpruned_DisposesOldAndStartsNew()
    {
        var exe = TempExe();
        try
        {
            var runner = new FakeRunner();
            var launcher = new DigitalAppLauncher(runner);
            await launcher.LaunchAsync(DigitalEngines.WsjtX(exe), "SliceA");

            // Prior instance exits but its event is never delivered, so it lingers
            // in the map (the overwrite-without-cleanup edge from re-review #1).
            runner.Processes[0].SimulateSilentExit();
            Assert.False(launcher.IsInstanceRunning("SliceA"));

            var result = await launcher.LaunchAsync(DigitalEngines.WsjtX(exe), "SliceA");

            Assert.Equal(DigitalLaunchResult.Success, result);
            Assert.True(runner.Processes[0].Disposed);          // old wrapper disposed, not orphaned
            Assert.True(launcher.IsInstanceRunning("SliceA"));  // new instance tracked
            Assert.Equal(2, runner.Processes.Count);
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

        /// <summary>Marks the process exited WITHOUT raising Exited (fast-exit / missed event).</summary>
        public void SimulateSilentExit() => HasExited = true;

        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
