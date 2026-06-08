using System.Diagnostics;

namespace SDRIQStreamer.Digital;

/// <summary>
/// Minimal abstraction over a launched engine process so
/// <see cref="DigitalAppLauncher"/> can be unit-tested without spawning real
/// processes.
/// </summary>
public interface IDigitalProcess
{
    bool HasExited { get; }
    void Kill();
    event EventHandler? Exited;
}

/// <summary>Starts engine processes. Injected into the launcher (fake in tests).</summary>
public interface IDigitalProcessRunner
{
    IDigitalProcess Start(string exePath, string arguments);
}

/// <summary>Real <see cref="IDigitalProcessRunner"/> backed by <see cref="Process"/>.</summary>
public sealed class ProcessDigitalProcessRunner : IDigitalProcessRunner
{
    public IDigitalProcess Start(string exePath, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null.");

        return new ProcessWrapper(process);
    }

    private sealed class ProcessWrapper : IDigitalProcess
    {
        // How long to let the engine shut down gracefully before forcing it.
        private const int GracefulExitMilliseconds = 5_000;

        private readonly Process _process;

        public ProcessWrapper(Process process)
        {
            _process = process;
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? Exited;

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch { return true; }
            }
        }

        // Bug fix 2026-06-08: stopping WSJT-X/JTDX from the streamer lost the
        // engine's settings (window geometry, last protocol FT8/FT4/WSPR, band),
        // while closing from the engine's own window saved them. Root cause: a
        // hard Process.Kill terminates the process before it runs its normal
        // shutdown, which is when WSJT-X writes its .ini. Fix: ask the main
        // window to close (so it saves), and force-kill only if it doesn't exit
        // in time. The wait runs off-thread so the UI (caller) isn't blocked;
        // chosen over Kill so operator settings persist, matching expectations.
        public void Kill()
        {
            try
            {
                if (_process.HasExited)
                    return;

                if (!_process.CloseMainWindow())
                {
                    // No responsive main window to close gracefully — force it.
                    _process.Kill(entireProcessTree: true);
                    return;
                }

                Task.Run(() =>
                {
                    try
                    {
                        if (!_process.WaitForExit(GracefulExitMilliseconds) && !_process.HasExited)
                            _process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Process already gone / access denied — treat as stopped.
                    }
                });
            }
            catch
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            }
        }
    }
}
