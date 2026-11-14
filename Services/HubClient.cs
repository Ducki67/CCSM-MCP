using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClaudeSessionManagerMCP.Config;
using ClaudeSessionManagerMCP.Hub;
using ClaudeSessionManagerMCP.Protocol;

namespace ClaudeSessionManagerMCP.Services;

/// <summary>
/// Client-side connection to the hub (one persistent TCP connection per MCP
/// server process). Auto-starts the hub exe if it is not running, and
/// transparently re-registers after a hub restart.
/// </summary>
public sealed class HubClient : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly object _lock = new();
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    private string? _sessionId;
    private string? _sessionName;
    private string? _sessionNote;
    private bool _registeredOnHub;

    public HubClient(AppConfig cfg) => _cfg = cfg;

    public string? SessionId => _sessionId;
    public string? SessionName => _sessionName;

    // ------------------------------------------------------------------
    // Public API used by the MCP tools
    // ------------------------------------------------------------------
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);

    private static readonly HashSet<string> SessionlessCmds = new(StringComparer.Ordinal)
    {
        "ping", "sessions_list", "usage_overview", "pc_awake_status", "team_status", "register",
    };

    /// <summary>Default name when a session uses a tool before register_session: working-folder name + pid.</summary>
    private static string DefaultName()
    {
        var env = Environment.GetEnvironmentVariable("CCSM_SESSION_NAME");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        string folder;
        try { folder = Path.GetFileName(Environment.CurrentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
        catch { folder = "claude"; }
        if (string.IsNullOrWhiteSpace(folder)) folder = "claude";
        return $"{folder}-{Environment.ProcessId}";
    }

    public async Task<HubResponse> RequestAsync(HubRequest req)
    {
        if (_sessionName == null && req.Cmd != null && !SessionlessCmds.Contains(req.Cmd))
            _sessionName = DefaultName();

        var (ok, error) = await EnsureConnectedAsync();
        if (!ok) return new HubResponse { Ok = false, Error = error };

        try
        {
            await EnsureRegisteredAsync(req);
            return await ExchangeAsync(Stamp(req));
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Connection died (hub restarted?) — try ONE full recovery cycle.
            ResetConnection();
            (ok, error) = await EnsureConnectedAsync();
            if (!ok) return new HubResponse { Ok = false, Error = error };
            try
            {
                await EnsureRegisteredAsync(req);
                return await ExchangeAsync(Stamp(req));
            }
            catch (Exception ex2)
            {
                ResetConnection();
                return new HubResponse { Ok = false, Error = $"Hub connection lost twice: {ex2.Message}" };
            }
        }
    }

    /// <summary>Requests carry the CURRENT connection's session id (it changes after a hub restart).</summary>
    private HubRequest Stamp(HubRequest req)
    {
        if (req.Cmd != "register" && _sessionId != null)
            req.SessionId = _sessionId;
        return req;
    }

    private async Task EnsureRegisteredAsync(HubRequest req)
    {
        if (_registeredOnHub || _sessionName == null || req.Cmd == "register") return;
        var resp = await ExchangeAsync(new HubRequest { Cmd = "register", Name = _sessionName, Note = _sessionNote, Pid = Environment.ProcessId });
        if (resp.Ok && resp.SessionId != null)
        {
            _sessionId = resp.SessionId;
            _sessionName = resp.Name ?? _sessionName;
            _registeredOnHub = true;
        }
    }

    /// <summary>Register (or re-register after a hub restart) with a stable name.</summary>
    public async Task<HubResponse> RegisterAsync(string name, string? note)
    {
        _sessionName = string.IsNullOrWhiteSpace(name) ? $"claude-{Environment.ProcessId}" : name.Trim();
        _sessionNote = note;
        var resp = await RequestAsync(new HubRequest { Cmd = "register", Name = _sessionName, Note = note, Pid = Environment.ProcessId });
        if (resp.Ok && resp.SessionId != null)
        {
            _sessionId = resp.SessionId;
            _sessionName = resp.Name ?? _sessionName;
            _registeredOnHub = true;
        }
        return resp;
    }

    public void Dispose() => ResetConnection();

    // ------------------------------------------------------------------
    // Connection management
    // ------------------------------------------------------------------
    private async Task<(bool ok, string? error)> EnsureConnectedAsync()
    {
        lock (_lock)
        {
            if (_client is { Connected: true } && _writer != null && _reader != null)
                return (true, null);
        }

        var started = DateTime.UtcNow;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (await TryConnectAsync()) return (true, null);
            if (attempt == 0)
            {
                HubBootLog($"Hub not reachable on 127.0.0.1:{_cfg.Port} — starting hub exe…");
                SpawnHub();
            }
            await Task.Delay(350);
        }
        return (false, $"Could not connect to the session hub on 127.0.0.1:{_cfg.Port} (tried for {(DateTime.UtcNow - started).TotalSeconds:0.0}s). Start 'ClaudeSessionManagerMCP.exe hub' manually and check config.ini / hub.log.");
    }

    private async Task<bool> TryConnectAsync()
    {
        var client = new TcpClient();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
            await client.ConnectAsync(IPAddress.Loopback, _cfg.Port, cts.Token);
            client.NoDelay = true;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };

            lock (_lock)
            {
                ResetConnectionLocked();
                _client = client;
                _reader = reader;
                _writer = writer;
                _registeredOnHub = false;
            }

            // Recover identity after a reconnect (also the first registration).
            if (_sessionName != null)
            {
                var resp = await ExchangeAsync(new HubRequest { Cmd = "register", Name = _sessionName, Note = _sessionNote, Pid = Environment.ProcessId });
                if (resp.Ok && resp.SessionId != null)
                {
                    _sessionId = resp.SessionId;
                    _sessionName = resp.Name ?? _sessionName;
                    _registeredOnHub = true;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            try { client.Close(); } catch { }
            return false;
        }
    }

    private async Task<HubResponse> ExchangeAsync(HubRequest req)
    {
        StreamReader reader;
        StreamWriter writer;
        lock (_lock)
        {
            if (_client is not { Connected: true } || _reader == null || _writer == null)
                throw new IOException("Not connected.");
            reader = _reader;
            writer = _writer;
        }

        // One request/response pair at a time; a hung hub can't block a tool call forever.
        using var cts = new CancellationTokenSource(ExchangeTimeout);
        await _exchangeGate.WaitAsync(cts.Token);
        string? line;
        try
        {
            await writer.WriteLineAsync(Wire.Serialize(req).AsMemory(), cts.Token);
            await writer.FlushAsync(cts.Token);
            line = await reader.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new IOException($"Hub did not answer '{req.Cmd}' within {ExchangeTimeout.TotalSeconds:0}s.");
        }
        finally
        {
            _exchangeGate.Release();
        }
        if (line == null)
            throw new IOException("Hub closed the connection.");
        var resp = Wire.Deserialize<HubResponse>(line);
        return resp ?? new HubResponse { Ok = false, Error = "Unreadable hub response." };
    }

    private void ResetConnection()
    {
        lock (_lock)
        {
            // Send bye best-effort before dropping.
            try
            {
                if (_registeredOnHub && _writer != null)
                {
                    _writer.WriteLine(Wire.Serialize(new HubRequest { Cmd = "bye", SessionId = _sessionId }));
                    _writer.Flush();
                }
            }
            catch { }
            ResetConnectionLocked();
        }
    }

    private void ResetConnectionLocked()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }
        _writer = null;
        _reader = null;
        _client = null;
    }

    private void SpawnHub()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                HubBootLog("Cannot locate own exe to spawn the hub. Start 'ClaudeSessionManagerMCP.exe hub' manually.");
                return;
            }

            var bootLog = Path.Combine(AppContext.BaseDirectory, "hub-boot.log");
            using var fs = new FileStream(bootLog, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs) { AutoFlush = true };
            sw.WriteLine($"[{DateTime.UtcNow:o}] spawning hub: {exe} hub");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "hub",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = Process.Start(psi);
            if (proc == null)
            {
                HubBootLog("Process.Start returned null.");
                return;
            }
            // The hub must not hold on to the MCP protocol's stdin.
            try { proc.StandardInput.Close(); } catch { }

            // Drain child output to the boot log so it can NEVER inherit the
            // parent's stdout (which is reserved for the MCP protocol). The
            // pumps below keep the streams alive until the hub exits — no
            // event handlers racing a disposed writer.
            _ = Task.Run(async () =>
            {
                try
                {
                    var text = await proc.StandardOutput.ReadToEndAsync();
                    if (text.Trim().Length > 0) HubBootLog("[hub:out] " + text.Trim());
                }
                catch { }
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    var text = await proc.StandardError.ReadToEndAsync();
                    if (text.Trim().Length > 0) HubBootLog("[hub:err] " + text.Trim());
                }
                catch { }
            });
            _ = proc; // detached intentionally; the hub outlives the MCP server process
        }
        catch (Exception ex)
        {
            HubBootLog($"Failed to spawn hub: {ex.Message}");
        }
    }

    private static void HubBootLog(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "hub-boot.log");
            File.AppendAllText(path, $"[{DateTime.UtcNow:o}] {message}{Environment.NewLine}");
        }
        catch { }
        try { Console.Error.WriteLine($"[HubClient] {message}"); } catch { }
    }
}
