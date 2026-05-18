using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Linq;
using System.Globalization;

namespace SDRIQStreamer.CWSkimmer;

/// <summary>
/// Line-based TCP client for CW Skimmer's telnet server.
///
/// Login sequence (from SliceMaster reference):
///   ← server sends greeting ending with "Please enter your callsign: "
///   → client sends callsign\r\n
///   ← server may send "Please enter your password: "
///   → client sends password\r\n
///
/// After login, receive loop parses:
///   "To ALL de SKIMMER <seq> : Clicked on "<call>" at <freq_khz>"
///
/// Commands sent:
///   SKIMMER/LO_FREQ <hz>   — update LO frequency when panadapter moves
/// </summary>
public sealed class CwSkimmerTelnetClient : ICwSkimmerTelnetClient
{
    private static readonly string s_diagPath = ResolveDiagPath();
    private static readonly Channel<string> s_diagChannel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private static readonly Task s_diagWriterTask = Task.Run(DrainDiagQueueAsync);

    private TcpClient?              _tcp;
    private NetworkStream?          _stream;
    private StreamWriter?           _writer;
    private CancellationTokenSource? _readCts;
    private Task?                   _readTask;
    private readonly SemaphoreSlim  _writeLock = new(1, 1);
    private DateTime                _lastLoSyncStatusUtc;
    private DateTime                _lastQsySyncStatusUtc;
    private DateTime                _lastQsyStatusEmitUtc;
    private double?                 _lastQsyStatusFreqMHz;
    private volatile bool           _isSessionReady;
    private int                     _disconnectInProgress;

    public bool IsConnected => _tcp is not null && _stream is not null && _writer is not null && _isSessionReady;

    public event Action<string>? StatusChanged;
    public event Action<double>? FrequencyClicked;
    public event Action<CwSkimmerSpotInfo>? SpotReceived;

    public async Task ConnectAsync(string host, int port, string callsign, string password,
                                   CancellationToken ct = default)
    {
        var effectiveCallsign = string.IsNullOrWhiteSpace(callsign)
            ? "SDRIQStreamer"
            : callsign.Trim();

        LogDiag($"CONNECT start host={host} port={port} callsign={effectiveCallsign}");
        EmitStatus($"Telnet connecting to {host}:{port}...");
        try
        {
            _lastQsyStatusFreqMHz = null;
            _lastQsySyncStatusUtc = default;
            _lastQsyStatusEmitUtc = default;
            _tcp    = new TcpClient();
            await _tcp.ConnectAsync(host, port, ct);
            _stream = _tcp.GetStream();

            var loginOutcome = await PerformLoginAsync(_stream, effectiveCallsign, password, ct);

            _writer = new StreamWriter(_stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine   = "\r\n",
            };

            _readCts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _readTask = Task.Run(() => ReadLoopAsync(_stream, _readCts.Token));
            _isSessionReady = true;
            LogDiag("CONNECT success");
            EmitStatus($"Telnet connected ({host}:{port}).");

            if (loginOutcome == LoginOutcome.TimedOutAfterCredentialsSent)
            {
                // Surfaces the LogDiag-only fallback so an unconfirmed login is
                // visible in the Logs tab. Investigations like #30 (Maestro-C)
                // depend on this breadcrumb to distinguish silent-auth-failure
                // from real radio/DAX issues.
                EmitStatus("Telnet login: no session-ready banner within 4s; treating as success. " +
                           "If SKIMMER commands have no effect, verify callsign/password.");
            }
        }
        catch (Exception ex)
        {
            _isSessionReady = false;
            MarkDisconnected();
            LogDiag($"CONNECT error {ex.GetType().Name}: {ex.Message}");
            EmitStatus($"Telnet connect failed: {ex.Message}");
            throw;
        }
    }

    public async Task SendLoFreqAsync(long freqHz, CancellationToken ct = default)
    {
        if (_writer is null || !_isSessionReady)
        {
            LogDiag($"TX skipped SKIMMER/LO_FREQ {freqHz} (writer not ready)");
            return;
        }

        await _writeLock.WaitAsync(ct);
        try
        {
            var command = $"SKIMMER/LO_FREQ {freqHz}";
            await _writer.WriteLineAsync(command);
            LogDiag($"TX {command}");
            EmitThrottledSyncStatus(
                ref _lastLoSyncStatusUtc,
                TimeSpan.FromSeconds(2),
                $"LO sync: {freqHz} Hz");
        }
        catch (Exception ex)
        {
            LogDiag($"TX error SKIMMER/LO_FREQ {freqHz}: {ex.Message}");
            EmitStatus($"Telnet send failed (LO_FREQ): {ex.Message}");
            MarkDisconnected();
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SendQsyAsync(double freqKhz, CancellationToken ct = default)
    {
        if (_writer is null || !_isSessionReady)
        {
            LogDiag($"TX skipped SKIMMER/QSY {freqKhz:F3} (writer not ready)");
            return;
        }

        await _writeLock.WaitAsync(ct);
        try
        {
            var normalizedKhz = Math.Round((decimal)freqKhz, 3, MidpointRounding.AwayFromZero);

            // CW Skimmer expects frequency in kHz with up to 3 decimal places.
            // Outbound coalescing and idempotence are handled by CwSkimmerSyncTracker.
            var command = $"SKIMMER/QSY {normalizedKhz.ToString("F3", CultureInfo.InvariantCulture)}";
            await _writer.WriteLineAsync(command);
            LogDiag($"TX {command}");
            var freqMHz = freqKhz / 1000.0;
            EmitQsySyncStatus(freqMHz);
        }
        catch (Exception ex)
        {
            LogDiag($"TX error SKIMMER/QSY {freqKhz:F3}: {ex.Message}");
            EmitStatus($"Telnet send failed (QSY): {ex.Message}");
            MarkDisconnected();
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        if (Interlocked.CompareExchange(ref _disconnectInProgress, 1, 0) != 0)
            return;

        var hadConnection =
            _tcp is not null ||
            _stream is not null ||
            _writer is not null ||
            _readTask is not null ||
            _readCts is not null ||
            _isSessionReady;

        if (!hadConnection)
        {
            _isSessionReady = false;
            Interlocked.Exchange(ref _disconnectInProgress, 0);
            return;
        }

        LogDiag("DISCONNECT start");
        EmitStatus("Telnet disconnecting...");
        try
        {
            _readCts?.Cancel();
            if (_readTask is not null)
            {
                try { await _readTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            }

            _writer?.Dispose();
            _stream?.Dispose();
            _tcp?.Dispose();

            _isSessionReady = false;
            _writer   = null;
            _stream   = null;
            _tcp      = null;
            _readCts  = null;
            _readTask = null;
            _lastQsyStatusFreqMHz = null;
            _lastQsySyncStatusUtc = default;
            _lastQsyStatusEmitUtc = default;
            LogDiag("DISCONNECT complete");
            EmitStatus("Telnet disconnected.");
        }
        finally
        {
            Interlocked.Exchange(ref _disconnectInProgress, 0);
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    // ── Login ────────────────────────────────────────────────────────────────

    private enum LoginOutcome
    {
        Confirmed,                    // Server sent a session-ready banner.
        TimedOutAfterCredentialsSent, // Credentials sent, no banner within window; assumed-success.
    }

    private static async Task<LoginOutcome> PerformLoginAsync(
        NetworkStream stream, string callsign, string password, CancellationToken ct)
    {
        var buf  = new byte[4096];
        var text = new StringBuilder();

        using var loginCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        loginCts.CancelAfter(TimeSpan.FromSeconds(15));

        bool sentCallsign = false;

        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buf.AsMemory(), loginCts.Token);
                if (n == 0) break;

                text.Append(StripIac(buf.AsSpan(0, n)));
                var s = text.ToString();

                if (ContainsAny(s, "authentication failed", "access violation"))
                    throw new IOException("CW Skimmer rejected telnet authentication.");

                if (!sentCallsign &&
                    ContainsAny(s, "callsign", "login"))
                {
                    await WriteLineToStream(stream, callsign, ct);
                    LogDiag($"LOGIN callsign prompt -> sent callsign={callsign}");
                    sentCallsign = true;
                    text.Clear();
                }
                else if (sentCallsign &&
                         s.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteLineToStream(stream, password, ct);
                    LogDiag("LOGIN password prompt -> sent password");
                    text.Clear();
                }
                else if (sentCallsign)
                {
                    if (LooksLikeSessionReady(s))
                    {
                        LogDiag("LOGIN handshake complete");
                        return LoginOutcome.Confirmed;
                    }

                    // No password prompt — login may not require one; wait briefly
                    loginCts.CancelAfter(TimeSpan.FromSeconds(4));
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!sentCallsign)
                throw;

            // Timeout after credentials sent — treat as likely success for tolerant servers.
            LogDiag("LOGIN timeout/cancelled after credentials (treated as success)");
            return LoginOutcome.TimedOutAfterCredentialsSent;
        }

        // Falls through when the read loop exits via EOF (n == 0). Preserves the
        // pre-refactor semantics of returning normally — the connect flow continues
        // and any real socket-closed condition surfaces on the next send attempt.
        return LoginOutcome.Confirmed;
    }

    private static Task WriteLineToStream(NetworkStream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(text + "\r\n");
        return stream.WriteAsync(bytes.AsMemory(), ct).AsTask();
    }

    // ── Receive loop ──────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    LogDiag("RX remote-closed connection");
                    EmitStatus("Telnet connection closed by CW Skimmer.");
                    MarkDisconnected();
                    break;
                }
                ProcessLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            LogDiag("RX cancelled");
            EmitStatus("Telnet receive loop cancelled.");
        }
        catch (IOException ex)
        {
            LogDiag($"RX io-error {ex.Message}");
            EmitStatus($"Telnet receive error: {ex.Message}");
            MarkDisconnected();
        }
        catch (ObjectDisposedException ex)
        {
            LogDiag($"RX disposed {ex.ObjectName ?? "stream"}");
            EmitStatus("Telnet receive loop closed.");
            MarkDisconnected();
        }
    }

    private void ProcessLine(string line)
    {
        if (line.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("access violation", StringComparison.OrdinalIgnoreCase))
        {
            LogDiag($"RX auth-failed {line}");
            EmitStatus("Telnet authentication failed by CW Skimmer.");
            MarkDisconnected();
            return;
        }

        var isEmptyClickEcho = line.Contains("Clicked on \"\"", StringComparison.OrdinalIgnoreCase);
        if (!isEmptyClickEcho &&
            (line.Contains("Clicked on", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("SKIMMER/", StringComparison.OrdinalIgnoreCase) ||
             line.TrimStart().StartsWith("DX de ", StringComparison.OrdinalIgnoreCase)))
        {
            LogDiag($"RX {line}");
        }

        var freq = ParseClickedOn(line);
        if (freq.HasValue)
            FrequencyClicked?.Invoke(freq.Value);

        var spot = ParseDxSpot(line);
        if (spot is not null)
            SpotReceived?.Invoke(spot);
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses CW Skimmer's click notification:
    ///   "To ALL de SKIMMER &lt;seq&gt; : Clicked on "&lt;call&gt;" at &lt;freq_khz&gt;"
    /// Returns the frequency in kHz, or null if the line is not a click notification.
    /// </summary>
    public static double? ParseClickedOn(string line)
    {
        if (!line.Contains("Clicked on", StringComparison.OrdinalIgnoreCase))
            return null;

        int atIdx = line.LastIndexOf(" at ", StringComparison.OrdinalIgnoreCase);
        if (atIdx < 0) return null;

        var freqStr = line.AsSpan(atIdx + 4).Trim();
        if (double.TryParse(freqStr, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var freq))
            return freq;

        return null;
    }

    /// <summary>
    /// Parses CW Skimmer DX spot lines, e.g.
    /// <c>DX de N0CALL-#:  14015.3  9A3B  19 dB  25 WPM  CQ  1534Z</c>.
    /// Returns a structured spot, or null if the line is not a DX spot.
    /// </summary>
    public static CwSkimmerSpotInfo? ParseDxSpot(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var normalizedLine = line.TrimStart();
        if (!normalizedLine.StartsWith("DX de ", StringComparison.OrdinalIgnoreCase))
            return null;

        var colonIdx = normalizedLine.IndexOf(':');
        if (colonIdx <= "DX de ".Length)
            return null;

        var spotter = normalizedLine.Substring("DX de ".Length, colonIdx - "DX de ".Length).Trim();
        if (string.IsNullOrWhiteSpace(spotter))
            return null;

        var body = normalizedLine[(colonIdx + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var tokens = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            return null;

        if (!TryParseKhz(tokens[0], out var frequencyKhz) || frequencyKhz <= 0)
            return null;

        var callsign = tokens[1].Trim();
        if (string.IsNullOrWhiteSpace(callsign))
            return null;

        int? signalDb = TryParseMetric(tokens, "dB");
        int? speedWpm = TryParseMetric(tokens, "WPM");
        var comment = string.Join(' ', tokens.Skip(2));

        return new CwSkimmerSpotInfo(
            FrequencyKhz: frequencyKhz,
            Callsign: callsign,
            Spotter: spotter,
            SignalDb: signalDb,
            SpeedWpm: speedWpm,
            Comment: comment);
    }

    // ── IAC stripping ─────────────────────────────────────────────────────────

    private static string StripIac(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0xFF) // Telnet IAC
            {
                if (i + 1 < bytes.Length)
                    i += bytes[i + 1] >= 0xFB ? 2 : 1; // WILL/WONT/DO/DONT + option, or bare cmd
            }
            else if (bytes[i] == '\r' || bytes[i] == '\n' ||
                     (bytes[i] >= 32 && bytes[i] < 128))
            {
                sb.Append((char)bytes[i]);
            }
        }
        return sb.ToString();
    }

    private static void LogDiag(string message)
    {
        try
        {
            _ = s_diagWriterTask;
            _ = s_diagChannel.Writer.TryWrite(
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
        }
        catch
        {
            // Diagnostics must never break runtime behavior.
        }
    }

    private static async Task DrainDiagQueueAsync()
    {
        var reader = s_diagChannel.Reader;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s_diagPath)!);

            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var sb = new StringBuilder();
                while (reader.TryRead(out var line))
                {
                    sb.AppendLine(line);
                }

                if (sb.Length > 0)
                {
                    await File.AppendAllTextAsync(s_diagPath, sb.ToString(), Encoding.UTF8)
                              .ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Diagnostics must never break runtime behavior.
        }
    }

    private static string ResolveDiagPath()
    {
        return Path.Combine(RuntimePathResolver.ResolveLogsDir(), "cwskimmer-telnet-client.log");
    }

    private void EmitStatus(string message)
    {
        try
        {
            StatusChanged?.Invoke(message);
        }
        catch
        {
            // Status listeners must never break runtime behavior.
        }
    }

    private void MarkDisconnected()
    {
        var readCts = _readCts;
        _readCts = null;
        _readTask = null;

        if (readCts is not null)
        {
            try { readCts.Cancel(); } catch { }
            readCts.Dispose();
        }

        try { _writer?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }

        _isSessionReady = false;
        _writer = null;
        _stream = null;
        _tcp = null;
        _lastQsyStatusFreqMHz = null;
        _lastQsySyncStatusUtc = default;
        _lastQsyStatusEmitUtc = default;
    }

    private void EmitThrottledSyncStatus(ref DateTime lastStatusUtc, TimeSpan minInterval, string message)
    {
        var now = DateTime.UtcNow;
        if (now - lastStatusUtc < minInterval)
            return;

        lastStatusUtc = now;
        EmitStatus(message);
    }

    private void EmitQsySyncStatus(double freqMHz)
    {
        var now = DateTime.UtcNow;
        // Suppress duplicate status spam when QSY messages repeat same frequency.
        if (_lastQsyStatusFreqMHz.HasValue &&
            Math.Abs(freqMHz - _lastQsyStatusFreqMHz.Value) <= 0.0000005 &&
            now - _lastQsyStatusEmitUtc < TimeSpan.FromSeconds(12))
        {
            return;
        }

        if (now - _lastQsySyncStatusUtc < TimeSpan.FromSeconds(2))
            return;

        _lastQsyStatusFreqMHz = freqMHz;
        _lastQsySyncStatusUtc = now;
        _lastQsyStatusEmitUtc = now;
        EmitStatus($"QSY sync (VFO): {freqMHz.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} MHz");
    }

    private static bool ContainsAny(string text, params string[] markers)
        => markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseKhz(string token, out double khz)
    {
        var normalized = token.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out khz);
    }

    private static int? TryParseMetric(string[] tokens, string metricToken)
    {
        for (var i = 1; i < tokens.Length; i++)
        {
            if (!tokens[i].Equals(metricToken, StringComparison.OrdinalIgnoreCase))
                continue;

            if (int.TryParse(tokens[i - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return value;
        }

        return null;
    }

    private static bool LooksLikeSessionReady(string text)
        => ContainsAny(text,
            "to all de skimmer",
            "de skimmer",
            "clicked on",
            "skimmer/",
            "welcome");
}
