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

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process already gone / access denied — treat as stopped.
            }
        }
    }
}
