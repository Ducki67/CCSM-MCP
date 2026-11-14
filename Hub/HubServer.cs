using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClaudeSessionManagerMCP.Config;
using ClaudeSessionManagerMCP.Protocol;

namespace ClaudeSessionManagerMCP.Hub;

/// <summary>
/// TCP hub (JSON lines on 127.0.0.1). Holds the session registry, per-name
/// message inboxes, team/boss state, usage stats and the PC-awake flag.
/// One MCP server process == one session == one connection.
/// </summary>
public sealed class HubServer : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly PcAwake _awake;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);   // by session id
    private readonly Dictionary<string, List<QueuedMessage>> _inboxes = new(StringComparer.Ordinal); // by session name
    private readonly HashSet<string> _knownNames = new(StringComparer.OrdinalIgnoreCase);              // every name ever registered (offline delivery)
    private readonly TeamState _team = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private TcpListener? _listener;
    private Timer? _timer;

    public HubServer(AppConfig cfg)
    {
        _cfg = cfg;
        _awake = new PcAwake(cfg.KeepDisplayOn);
    }

    public async Task RunAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, _cfg.Port);
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            HubLog.Error($"Port {_cfg.Port} unavailable ({ex.SocketErrorCode}). Is a hub already running? Exiting.");
            throw;
        }

        HubLog.Info($"Hub listening on 127.0.0.1:{_cfg.Port} (keepPCOn={_cfg.KeepPcOn}, heavy>={_cfg.HeavyUsagePercent}%, critical>={_cfg.CriticalUsagePercent}%)");

        // Periodic re-evaluation of the awake flag + stale logging.
        _timer = new Timer(_ =>
        {
            EvaluateAwake();
            if (HubLog.Level >= 2)
            {
                lock (_lock) HubLog.Debug($"tick: sessions={_sessions.Count}, awake={_awake.IsHeld}");
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    /// <summary>Release the sleep flag so the PC is never stuck awake after the hub exits.</summary>
    public void ReleaseAwakeAndShutdown()
    {
        try { _timer?.Dispose(); } catch { }
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _awake.Dispose();
    }

    public void Dispose() => ReleaseAwakeAndShutdown();

    // ------------------------------------------------------------------
    // Per-connection loop
    // ------------------------------------------------------------------
    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        SessionEntry? session = null;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 8192, leaveOpen: true) { AutoFlush = false };

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break; // client disconnected
                if (string.IsNullOrWhiteSpace(line)) continue;

                HubRequest? req;
                try { req = Wire.Deserialize<HubRequest>(line); }
                catch (JsonException ex)
                {
                    await WriteAsync(writer, new HubResponse { Ok = false, Error = $"Bad JSON: {ex.Message}" });
                    continue;
                }
                if (req?.Cmd == null) continue;

                HubResponse resp;
                lock (_lock)
                {
                    resp = Dispatch(req, ref session);
                    EvaluateAwake();
                }
                await WriteAsync(writer, resp);

                if (req.Cmd == "bye") break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // normal disconnects land here
        }
        finally
        {
            if (session != null)
            {
                lock (_lock)
                {
                    RemoveSession(session);
                    EvaluateAwake();
                }
            }
            try { client.Close(); } catch { }
        }
    }

    /// <summary>Remove a session (disconnect or bye); auto-release the boss role if it was the boss.</summary>
    private void RemoveSession(SessionEntry session)
    {
        if (_sessions.TryGetValue(session.Id, out var s) && s == session)
        {
            _sessions.Remove(session.Id);
            HubLog.Info($"Session '{session.Name}' ({session.Id}) disconnected. Connected: {_sessions.Count}.");
            if (IsBoss(session.Name))
            {
                var boss = _team.BossName;
                _team.BossName = null;
                _team.BossId = null;
                _team.BossClaimedUtc = null;
                HubLog.Info($"Boss '{boss}' went offline — boss role released. First session to call team_claim_boss becomes boss.");
                BroadcastDirective($"Boss '{boss}' went offline — boss role released. First session to call team_claim_boss becomes boss.");
            }
        }
    }

    private static async Task WriteAsync(StreamWriter writer, HubResponse resp)
    {
        try
        {
            await writer.WriteLineAsync(Wire.Serialize(resp));
            await writer.FlushAsync();
        }
        catch { /* client vanished */ }
    }

    // ------------------------------------------------------------------
    // Command dispatch (called under _lock)
    // ------------------------------------------------------------------
    private HubResponse Dispatch(HubRequest req, ref SessionEntry? session)
    {
        switch (req.Cmd)
        {
            case "ping":
                return new HubResponse { UptimeSeconds = (DateTime.UtcNow - _startedUtc).TotalSeconds };

            case "register":
                return Register(req, ref session);

            case "checkin":
                if (RequireSession(req, session, out var err1)) return err1!;
                return Checkin(session!, req);

            case "sessions_list":
                return SessionsList();

            case "usage_overview":
                return UsageOverview();

            case "message_send":
                if (RequireSession(req, session, out var err2)) return err2!;
                return MessageSend(session!, req);

            case "message_inbox":
                if (RequireSession(req, session, out var err3)) return err3!;
                return MessageInbox(session!);

            case "pc_awake_set":
                if (RequireSession(req, session, out var err4)) return err4!;
                session!.ForcedAwake = req.Enabled == true;
                HubLog.Info($"Session '{session.Name}' forced awake = {session.ForcedAwake}.");
                return new HubResponse { Pc = PcStatus() };

            case "pc_awake_status":
                return new HubResponse { Pc = PcStatus() };

            case "team_claim_boss":
                if (RequireSession(req, session, out var err5)) return err5!;
                return TeamClaimBoss(session!);

            case "team_release_boss":
                if (RequireSession(req, session, out var err6)) return err6!;
                return TeamReleaseBoss(session!);

            case "team_directive":
                if (RequireSession(req, session, out var err7)) return err7!;
                return TeamDirective(session!, req);

            case "team_feature_set":
                if (RequireSession(req, session, out var err8)) return err8!;
                return TeamFeatureSet(session!, req);

            case "team_status":
                return TeamStatus();

            case "hub_shutdown":
                if (RequireSession(req, session, out var err9)) return err9!;
                return HubShutdown(session!, req);

            case "bye":
                if (session != null)
                {
                    RemoveSession(session);
                    session = null;
                }
                return new HubResponse();

            default:
                return new HubResponse { Ok = false, Error = $"Unknown command '{req.Cmd}'." };
        }
    }

    private static bool RequireSession(HubRequest req, SessionEntry? session, out HubResponse error)
    {
        if (session == null)
        {
            error = new HubResponse { Ok = false, Error = "Not registered. Call register first (the MCP tools do this automatically)." };
            return true;
        }
        if (!string.IsNullOrEmpty(req.SessionId) && req.SessionId != session.Id)
        {
            error = new HubResponse { Ok = false, Error = $"SessionId mismatch: this connection is '{session.Id}'." };
            return true;
        }
        error = null!;
        return false;
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------
    private HubResponse Register(HubRequest req, ref SessionEntry? session)
    {
        if (session != null)
        {
            var wanted = req.Name?.Trim();
            if (!string.IsNullOrEmpty(wanted) && !wanted.Equals(session.Name, StringComparison.OrdinalIgnoreCase))
                RenameSession(session, UniqueName(wanted, session));
            if (req.Note != null) session.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
            if (req.Pid.HasValue) session.Pid = req.Pid;
            return new HubResponse { Ok = true, SessionId = session.Id, Name = session.Name, MessagesWaiting = InboxCount(session.Name) };
        }

        var baseName = string.IsNullOrWhiteSpace(req.Name) ? $"session-{req.Pid ?? 0}" : req.Name.Trim();
        var name = UniqueName(baseName, null);

        var entry = new SessionEntry
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Pid = req.Pid,
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
        };
        _sessions[entry.Id] = entry;
        _knownNames.Add(name);
        session = entry;
        HubLog.Info($"Session '{name}' registered as {entry.Id}. Connected: {_sessions.Count}.");

        var resp = new HubResponse { SessionId = entry.Id, Name = entry.Name, MessagesWaiting = InboxCount(name) };
        if (_team.DirectiveText != null)
        {
            resp.Directives = new List<string> { TeamDirectiveText() };
        }
        if (_team.BossName != null && !IsBoss(entry.Name))
            resp.HeavyPeers = new List<string>(); // reserved for future use; keep field shape stable
        return resp;
    }

    private HubResponse Checkin(SessionEntry s, HubRequest req)
    {
        if (req.ContextPct.HasValue) s.ContextPct = Math.Clamp(req.ContextPct.Value, 0, 100);
        if (req.TokensUsed.HasValue) s.TokensUsed = req.TokensUsed.Value;
        if (req.Working.HasValue)
        {
            if (s.Working != req.Working)
                HubLog.Info($"Session '{s.Name}' working = {req.Working}.");
            s.Working = req.Working;
        }
        if (req.Note != null) s.Note = req.Note.Trim();
        s.LastSeenUtc = DateTime.UtcNow;

        var directives = new List<string>();
        var ctx = s.ContextPct;
        if (ctx >= _cfg.CriticalUsagePercent)
        {
            directives.Add($"CONTEXT CRITICAL ({ctx:0}%): compaction is imminent. IMMEDIATELY write a resume note to memory (task state, file paths, next steps), then compact this conversation.");
        }
        else if (ctx >= _cfg.HeavyUsagePercent)
        {
            directives.Add($"CONTEXT HEAVY ({ctx:0}%): save important findings/decisions to persistent memory now, checkpoint the current task, then compact the conversation to continue safely.");
        }
        if (_team.DirectiveText != null)
            directives.Add(TeamDirectiveText());

        var heavyPeers = _sessions.Values
            .Where(o => o != s && o.ContextPct >= _cfg.HeavyUsagePercent)
            .Select(o => $"{o.Name} ({o.ContextPct:0}%)")
            .ToList();

        return new HubResponse
        {
            SessionId = s.Id,
            Name = s.Name,
            Directives = directives.Count > 0 ? directives : null,
            MessagesWaiting = InboxCount(s.Name),
            HeavyPeers = heavyPeers.Count > 0 ? heavyPeers : null,
        };
    }

    private HubResponse SessionsList()
    {
        lock (_lock)
        {
            var list = _sessions.Values
                .OrderByDescending(s => s.Working == true)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => s.ToInfo(_cfg))
                .ToList();
            return new HubResponse { Sessions = list };
        }
    }

    private HubResponse UsageOverview()
    {
        var reported = _sessions.Values.Where(s => s.ContextPct.HasValue).ToList();
        var heavy = reported.Where(s => s.ContextPct!.Value >= _cfg.HeavyUsagePercent && s.ContextPct!.Value < _cfg.CriticalUsagePercent).ToList();
        var critical = reported.Where(s => s.ContextPct!.Value >= _cfg.CriticalUsagePercent).ToList();

        var aggregate = reported.Count > 0 ? reported.Average(s => s.ContextPct!.Value) : (double?)null;

        string advice;
        if (critical.Count > 0)
        {
            advice = $"CRITICAL context in: {Names(critical)}. They must save state to memory NOW and compact — their context is about to be truncated.";
        }
        else if (heavy.Count > 0)
        {
            advice = $"Heavy context in: {Names(heavy)}. Tell them (message_send) to save findings to memory and compact; watch the critical threshold at {_cfg.CriticalUsagePercent}%.";
        }
        else if (reported.Count == 0)
        {
            advice = "No session has reported context usage yet. Sessions should call session_checkin(context_pct=...) regularly.";
        }
        else
        {
            advice = $"All healthy — average context {aggregate:0}% (heavy threshold {_cfg.HeavyUsagePercent}%).";
        }

        var warningList = critical.Concat(heavy).Select(s => $"{s.Name} {s.ContextPct:0}%").ToList();
        var broadcast = warningList.Count > 0
            ? $"[USAGE WARNING] Context heavy across the team: {string.Join(", ", warningList)}. Save important state to persistent memory and compact your conversations now."
            : "";

        return new HubResponse
        {
            Overview = new UsageOverview
            {
                AggregatePct = aggregate,
                SessionsReported = reported.Count,
                HeavyNames = heavy.Select(s => s.Name).ToList(),
                CriticalNames = critical.Select(s => s.Name).ToList(),
                Advice = advice,
                BroadcastText = broadcast,
            },
        };
    }

    private HubResponse MessageSend(SessionEntry sender, HubRequest req)
    {
        var text = req.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return new HubResponse { Ok = false, Error = "Text is required." };
        var to = (req.To ?? "all").Trim();

        List<SessionEntry> targets;
        if (to.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targets = _sessions.Values.Where(s => s != sender).ToList();
        }
        else
        {
            var byId = _sessions.Values.FirstOrDefault(s => s.Id.Equals(to, StringComparison.OrdinalIgnoreCase));
            var byName = _sessions.Values.FirstOrDefault(s => s.Name.Equals(to, StringComparison.OrdinalIgnoreCase));
            var target = byId ?? byName;
            if (target == null)
            {
                var offline = _knownNames.FirstOrDefault(k => k.Equals(to, StringComparison.OrdinalIgnoreCase));
                if (offline == null)
                    return new HubResponse { Ok = false, Error = $"No session named/with id '{to}' (online or known offline). Try sessions_list." };
                Enqueue(offline, new QueuedMessage { From = sender.Name, Text = text });
                HubLog.Info($"Message {sender.Name} -> [{offline}] (offline, queued): {Truncate(text, 80)}");
                return new HubResponse { Delivered = new List<string>(), QueuedOffline = new List<string> { offline } };
            }
            targets = new List<SessionEntry> { target };
        }

        var delivered = new List<string>();
        var from = sender.Name;
        foreach (var t in targets)
        {
            Enqueue(t.Name, new QueuedMessage { From = from, Text = text });
            delivered.Add(t.Name);
        }

        HubLog.Info($"Message {from} -> [{string.Join(", ", delivered)}]: {Truncate(text, 80)}");
        return new HubResponse { Delivered = delivered };
    }

    private HubResponse MessageInbox(SessionEntry s)
    {
        var messages = new List<InboxMessage>();
        lock (_lock)
        {
            var q = InboxFor(s.Name);
            if (q != null)
            {
                foreach (var m in q)
                {
                    messages.Add(new InboxMessage
                    {
                        From = m.From,
                        Text = m.Text,
                        TsIso = m.TsUtc.ToString("o"),
                        Broadcast = m.Broadcast,
                    });
                }
                q.Clear();
            }
        }
        return new HubResponse { Messages = messages, Remaining = 0 };
    }

    private HubResponse TeamClaimBoss(SessionEntry s)
    {
        if (_team.BossName != null && _sessions.Values.Any(x => x.Name.Equals(_team.BossName, StringComparison.OrdinalIgnoreCase)))
            return new HubResponse { Ok = false, Error = $"Boss already claimed by '{_team.BossName}'. They must team_release_boss first (or disconnect)." };

        _team.BossName = s.Name;
        _team.BossId = s.Id;
        _team.BossClaimedUtc = DateTime.UtcNow;
        HubLog.Info($"'{s.Name}' claimed TEAM BOSS.");
        BroadcastDirective($"'{s.Name}' is now the team boss. Team directives and the keep-PC-running switch belong to them.");
        return new HubResponse { Ok = true, Name = s.Name };
    }

    private HubResponse TeamReleaseBoss(SessionEntry s)
    {
        if (!IsBoss(s.Name))
            return new HubResponse { Ok = false, Error = "Only the current boss can release the boss role." };
        var old = _team.BossName;
        _team.BossName = null;
        _team.BossId = null;
        _team.BossClaimedUtc = null;
        HubLog.Info($"'{old}' released the boss role.");
        BroadcastDirective($"'{old}' released the boss role. First session to call team_claim_boss becomes boss.");
        return new HubResponse { Ok = true };
    }

    private HubResponse TeamDirective(SessionEntry s, HubRequest req)
    {
        if (!IsBoss(s.Name))
            return new HubResponse
            {
                Ok = false,
                Error = _team.BossName == null
                    ? "No boss claimed. Call team_claim_boss first, then set directives."
                    : $"Only the boss ('{_team.BossName}') can set team directives.",
            };

        var text = req.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return new HubResponse { Ok = false, Error = "Text is required." };

        _team.DirectiveText = text;
        _team.DirectiveUtc = DateTime.UtcNow;
        HubLog.Info($"TEAM DIRECTIVE from '{s.Name}': {Truncate(text, 80)}");
        BroadcastDirective(text, fromBoss: true);
        return new HubResponse { Ok = true };
    }

    private HubResponse TeamFeatureSet(SessionEntry s, HubRequest req)
    {
        if (!IsBoss(s.Name))
            return new HubResponse
            {
                Ok = false,
                Error = _team.BossName == null
                    ? "No boss claimed. team_claim_boss first — the keep-PC-running switch belongs to the boss."
                    : $"Only the boss ('{_team.BossName}') can turn the keep-PC-running feature on/off.",
            };

        var feature = (req.Feature ?? "keep_pc_on").Trim().ToLowerInvariant();
        if (feature != "keep_pc_on")
            return new HubResponse { Ok = false, Error = $"Unknown feature '{feature}'. Supported: keep_pc_on." };

        var enabled = req.Enabled == true;
        _cfg.KeepPcOn = enabled;
        HubLog.Info($"Boss '{s.Name}' set bKeepPCOn = {enabled}.");
        BroadcastDirective($"Boss {(enabled ? "ENABLED" : "DISABLED")} the keep-PC-running feature.");
        return new HubResponse { Ok = true, Pc = PcStatus() };
    }

    private HubResponse TeamStatus()
    {
        lock (_lock)
        {
            return new HubResponse
            {
                Ok = true,
                Name = _team.BossName,
                Directives = _team.DirectiveText != null
                    ? new List<string> { TeamDirectiveText() }
                    : null,
                Sessions = _sessions.Values.Select(s => s.ToInfo(_cfg)).ToList(),
                Pc = PcStatus(),
            };
        }
    }

    private HubResponse HubShutdown(SessionEntry s, HubRequest req)
    {
        var isBoss = _team.BossName != null && IsBoss(s.Name);
        if (_team.BossName != null && !isBoss && req.Force != true)
            return new HubResponse { Ok = false, Error = $"Only the boss ('{_team.BossName}') can shut the hub down (or pass force=true)." };
        if (_team.BossName == null && _sessions.Count > 1 && req.Force != true)
            return new HubResponse { Ok = false, Error = $"{_sessions.Count} sessions are connected. Pass force=true to shut down anyway (they will lose the hub until it auto-restarts)." };

        HubLog.Info($"Hub shutdown requested by '{s.Name}'{(req.Force == true ? " (forced)" : "")}.");
        ReleaseAwakeAndShutdown();
        return new HubResponse { Ok = true, ShuttingDown = true };
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private string UniqueName(string baseName, SessionEntry? self)
    {
        var name = baseName;
        for (var i = 2; _sessions.Values.Any(s => s != self && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{baseName}-{i}";
        return name;
    }

    private void RenameSession(SessionEntry s, string newName)
    {
        var old = s.Name;
        if (_inboxes.Remove(old, out var q))
        {
            if (_inboxes.TryGetValue(newName, out var existing)) existing.InsertRange(0, q);
            else _inboxes[newName] = q;
        }
        if (IsBoss(old)) _team.BossName = newName;
        s.Name = newName;
        _knownNames.Add(newName);
        HubLog.Info($"Session {s.Id} renamed '{old}' -> '{newName}'.");
    }

    private bool IsBoss(string name) =>
        _team.BossName != null && _team.BossName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private string TeamDirectiveText() =>
        $"[TEAM DIRECTIVE from boss '{_team.BossName}'] {_team.DirectiveText}";

    private void BroadcastDirective(string text, bool fromBoss = false)
    {
        foreach (var s in _sessions.Values)
            Enqueue(s.Name, new QueuedMessage
            {
                From = fromBoss ? "BOSS" : "HUB",
                Text = text,
                Broadcast = true,
            });
    }

    private void Enqueue(string name, QueuedMessage m)
    {
        var key = _inboxes.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (key != null) name = key;
        if (!_inboxes.TryGetValue(name, out var q))
            _inboxes[name] = q = new List<QueuedMessage>();
        q.Add(m);
        if (q.Count > 100) q.RemoveRange(0, q.Count - 100);
    }

    private List<QueuedMessage>? InboxFor(string name)
    {
        var key = _inboxes.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        return key != null ? _inboxes[key] : null;
    }

    private int InboxCount(string name) => InboxFor(name)?.Count ?? 0;

    private PcAwakeStatus PcStatus()
    {
        lock (_lock)
        {
            var working = _sessions.Values.Count(s => s.Working == true && !s.IsStale(_cfg));
            var forced = _sessions.Values.Count(s => s.ForcedAwake);
            return new PcAwakeStatus
            {
                FeatureEnabled = _cfg.KeepPcOn,
                AwakeActive = _cfg.KeepPcOn && (working > 0 || forced > 0),
                WorkingSessions = working,
                ForcedSessions = forced,
                ConnectedSessions = _sessions.Count,
            };
        }
    }

    private void EvaluateAwake()
    {
        var status = PcStatus();
        var shouldHold = status.AwakeActive;
        var held = _awake.IsHeld;
        if (shouldHold != held)
        {
            _awake.Set(shouldHold);
            HubLog.Info($"Keep-PC-awake: working={status.WorkingSessions}, forced={status.ForcedSessions}, connected={status.ConnectedSessions}, feature={status.FeatureEnabled} -> hold={shouldHold}.");
        }
    }

    private static string Names(IEnumerable<SessionEntry> sessions) =>
        string.Join(", ", sessions.Select(s => s.Name));

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
