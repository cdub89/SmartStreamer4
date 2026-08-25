using System.Net.Sockets;

namespace SDRIQStreamer.Digital;

/// <summary>
/// Checks whether a SmartSDR CAT TCP port is actually accepting connections
/// before an engine is launched against it (issue #66).
/// </summary>
/// <remarks>
/// On a fresh install the operator has not yet added a CAT port in SmartSDR
/// CAT, so WSJT-X starts and fails with its own configuration error, which says
/// nothing about SmartStreamer4 and does not name the fix. CW Mode already
/// refuses to start without a DAX-IQ channel; this is the same gate for the
/// mode that needs CAT, which is where the operator asked for it.
///
/// A live connect rather than a config read, because a port present in
/// CAT.settings but not listening (SmartSDR CAT closed) fails for WSJT-X
/// exactly as an absent one does. The config is read only to make the message
/// name the fix.
/// </remarks>
public interface ICatPortProbe
{
    /// <summary>
    /// True when something is accepting TCP connections on
    /// <c>127.0.0.1:<paramref name="port"/></c>.
    /// </summary>
    Task<bool> IsListeningAsync(int port, CancellationToken cancellationToken = default);
}

/// <summary>Connects to loopback to see whether the CAT port is live.</summary>
public sealed class TcpCatPortProbe : ICatPortProbe
{
    /// <summary>
    /// The engine config points at loopback (<c>CATNetworkPort</c> is written as
    /// <c>127.0.0.1:&lt;port&gt;</c>, see <see cref="DigitalConfigProvisioner"/>),
    /// so that is what gets probed.
    /// </summary>
    private const string CatHost = "127.0.0.1";

    /// <summary>
    /// A loopback connect either completes or is refused immediately, so this
    /// only bounds the case where a firewall drops the SYN rather than
    /// rejecting it. Long enough not to false-negative a busy machine, short
    /// enough that a Start press never feels hung.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1_500);

    public async Task<bool> IsListeningAsync(int port, CancellationToken cancellationToken = default)
    {
        // Out-of-range ports would throw from ConnectAsync rather than report
        // unreachable, and "not a usable port" is the same answer to the caller.
        if (port is <= 0 or > 65_535)
            return false;

        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);

            await client.ConnectAsync(CatHost, port, timeout.Token);
            return client.Connected;
        }
        catch (Exception)
        {
            // Refused, timed out, cancelled, or the stack said no. All of them
            // mean the engine would not have reached CAT either.
            return false;
        }
    }
}

/// <summary>
/// Builds the operator-facing message for a CAT port that is not listening.
/// </summary>
public static class CatPortHint
{
    /// <summary>
    /// Names the port that failed and the fix, using the ports SmartSDR CAT
    /// actually has configured to tell "you never added one" apart from
    /// "you added one but CAT is not running", which need different actions.
    /// </summary>
    /// <param name="port">The port the engine would have used.</param>
    /// <param name="configuredPorts">
    /// What <see cref="CatSettingsReader"/> found in CAT.settings. Empty when
    /// the file is missing, unreadable, or lists no CAT TCP ports.
    /// </param>
    public static string ForUnreachablePort(int port, IReadOnlyList<CatTcpPort> configuredPorts)
    {
        if (configuredPorts.Count == 0)
            return $"CAT port {port} is not reachable, and SmartSDR CAT has no TCP ports configured. "
                 + $"In SmartSDR CAT choose ADD, click TCP, and set port {port} for this slice.";

        if (configuredPorts.Any(p => p.TcpPort == port))
            return $"CAT port {port} is configured in SmartSDR CAT but nothing is listening on it. "
                 + "Start the SmartSDR CAT application, then try again.";

        var known = string.Join(", ", configuredPorts
            .OrderBy(p => p.TcpPort)
            .Select(p => $"{p.TcpPort} (slice {p.SliceIndex})"));

        return $"CAT port {port} is not reachable. SmartSDR CAT has: {known}. "
             + $"Either set this slice's CAT port to one of those on the Config tab, or add port {port} in SmartSDR CAT.";
    }
}
