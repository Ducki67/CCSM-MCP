using System.ComponentModel;
using System.Text.Json;
using ClaudeSessionManagerMCP.Protocol;
using ClaudeSessionManagerMCP.Services;
using ModelContextProtocol.Server;

namespace ClaudeSessionManagerMCP.Tools;

/// <summary>
/// Session coordination tools: find other sessions, message them, share
/// context/usage, warn about heavy context, team/boss controls, keep-PC-awake.
/// </summary>
[McpServerToolType]
public sealed class SessionTools
{
    private readonly HubClient _hub;
    public SessionTools(HubClient hub) { _hub = hub; }

    private static string Json(HubResponse resp) => Wire.Serialize(resp);

    // ------------------------------------------------------------------
    // Identity
    // ------------------------------------------------------------------
    [McpServerTool(Name = "register_session", Idempotent = true),
     Description("Register THIS Claude session with the session hub under a stable name (e.g. 'frontend-fix', 'overnight-export'). Other sessions will see this name. Call once at the start of a multi-session job; later tools re-register automatically if the hub restarts.")]
    public async Task<string> RegisterSession(
        [Description("Stable display name for this session (must be recognizable to other sessions)")] string name,
        [Description("Optional one-line note, e.g. what this session is working on")] string? note = null)
    {
        return Json(await _hub.RegisterAsync(name, note));
    }

    [McpServerTool(Name = "session_checkin", Idempotent = false),
     Description("HEARTBEAT: report this session's context usage and whether it is actively working. Returns directives (heavy-context warnings for you, the current team directive), how many inbox messages are waiting, and which OTHER sessions are context-heavy. Call this regularly while working — it also keeps the PC-awake logic informed.")]
    public async Task<string> SessionCheckin(
        [Description("Your context window usage in percent (0-100), if known")] double? contextPct = null,
        [Description("Tokens used so far, if known")] long? tokensUsed = null,
        [Description("True while you are actively working on a task (keeps the PC awake if the boss enabled that feature); false when idle")] bool? working = null,
        [Description("Optional updated note about what you are doing")] string? note = null)
    {
        var resp = await _hub.RequestAsync(new HubRequest
        {
            Cmd = "checkin",
            SessionId = _hub.SessionId,
            ContextPct = contextPct,
            TokensUsed = tokensUsed,
            Working = working,
            Note = note,
        });
        return Json(resp);
    }

    // ------------------------------------------------------------------
    // Awareness
    // ------------------------------------------------------------------
    [McpServerTool(Name = "sessions_list", ReadOnly = true, Idempotent = true),
     Description("List every session connected to the hub: name, id, context usage, working flag, status (active/idle/stale) and notes. Use it to find who to message.")]
    public async Task<string> SessionsList()
        => Json(await _hub.RequestAsync(new HubRequest { Cmd = "sessions_list" }));

    [McpServerTool(Name = "usage_overview", ReadOnly = true, Idempotent = true),
     Description("Team-wide context/usage overview: average context %, which sessions are heavy or critical, advice, and a ready-made broadcast text you can send via message_send(to='all') to tell everyone to save state to memory and compact.")]
    public async Task<string> UsageOverview()
        => Json(await _hub.RequestAsync(new HubRequest { Cmd = "usage_overview" }));

    // ------------------------------------------------------------------
    // Messaging
    // ------------------------------------------------------------------
    [McpServerTool(Name = "message_send"),
     Description("Send a message to another session (by NAME or id) or to every session with to='all'. Messages wait in the recipient's inbox until they call message_inbox (or session_checkin, which shows how many are waiting).")]
    public async Task<string> MessageSend(
        [Description("Recipient: session NAME, session id, or 'all' for broadcast")] string to,
        [Description("Message text")] string text)
        => Json(await _hub.RequestAsync(new HubRequest { Cmd = "message_send", SessionId = _hub.SessionId, To = to, Text = text }));

    [McpServerTool(Name = "message_inbox"),
     Description("Fetch and clear this session's inbox (messages from other sessions, boss directives, hub broadcasts). Check it after session_checkin reports messagesWaiting > 0.")]
    public async Task<string> MessageInbox()
        => Json(await _hub.RequestAsync(new HubRequest { Cmd = "message_inbox", SessionId = _hub.SessionId }));

    // ------------------------------------------------------------------
    // Keep PC awake
    // ------------------------------------------------------------------
    [McpServerTool(Name = "pc_awake_set"),
     Description("Per-session override: force the PC to stay awake (enabled=true) or release your hold (enabled=false) regardless of your working flag. The team-wide feature toggle is boss-only via team_feature_set.")]
    public async Task<string> PcAwakeSet(
        [Description("true = hold the PC awake, false = release your hold")] bool enabled)
        => Json(await _hub.RequestAsync(new HubRequest { Cmd = "pc_awake_set", SessionId = _hub.SessionId, Enabled = enabled }));

    [McpServerTool(Name = "pc_awake_status", ReadOnly = true, Idempotent = true),
     Description("Keep-PC-awake status: is the feature enabled, is a hold currently active, how many sessions are working/forcing/connected.")]
    public async Task<string> PcAwakeStatus()
        => Json(await _hub.RequestAsync(new HubRequest { Cmd = "pc_awake_status" }));

    // ------------------------------------------------------------------
    // Team / boss
    // ------------------------------------------------------------------
    [McpServerTool(Name = "team_claim_boss"),
     Description("Claim the BOSS/LEADER role for this session (first claim wins; boss must release or disconnect before someone else can claim). The boss owns team directives and the team-wide keep-PC-running switch.")]
    public async Task<string> TeamClaimBoss()
        => Json(await _hub.RequestAsync(new HubRequest { Cmd = "team_claim_boss", SessionId = _hub.SessionId }));

    [McpServerTool(Name = "team_release_boss"),
     Description("Boss-only: release the boss role so another session can claim it.")]
    public async Task<string> TeamReleaseBoss()
        => Json(await _hub.RequestAsync(new HubRequest { Cmd = "team_release_boss", SessionId = _hub.SessionId }));

    [McpServerTool(Name = "team_directive"),
     Description("BOSS ONLY: set the current team-wide directive (e.g. 'Phase 2 done — start the exports now'). It is broadcast to every session's inbox AND pinned so every session sees it on every session_checkin until replaced.")]
    public async Task<string> TeamDirective(
        [Description("The directive text for the whole team")] string text)
        => Json(await _hub.RequestAsync(new HubRequest { Cmd = "team_directive", SessionId = _hub.SessionId, Text = text }));

    [McpServerTool(Name = "team_feature_set"),
     Description("BOSS ONLY: turn the team-wide bKeepPCOn feature on/off. On = PC stays awake while sessions report working; off = Windows sleeps normally. If no boss is claimed, the switch stays at its config.ini default.")]
    public async Task<string> TeamFeatureSet(
        [Description("Feature to toggle (currently: keep_pc_on)")] string feature,
        [Description("true = enable, false = disable")] bool enabled)
        => Json(await _hub.RequestAsync(new HubRequest { Cmd = "team_feature_set", SessionId = _hub.SessionId, Feature = feature, Enabled = enabled }));

    [McpServerTool(Name = "team_status", ReadOnly = true, Idempotent = true),
     Description("Team status: who is boss, the current team directive, PC-awake state and the session list.")]
    public async Task<string> TeamStatus()
        => Json(await _hub.RequestAsync(new HubRequest { Cmd = "team_status" }));

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------
    [McpServerTool(Name = "hub_shutdown"),
     Description("Stop the session hub (auto-restarts on demand). Only the boss can shut it down while sessions are connected; pass force=true to override.")]
    public async Task<string> HubShutdown(
        [Description("Shut down even if sessions are connected")] bool force = false)
        => Json(await _hub.RequestAsync(new HubRequest { Cmd = "hub_shutdown", SessionId = _hub.SessionId, Force = force }));
}
