using ClaudeSessionManagerMCP.Config;
using ClaudeSessionManagerMCP.Protocol;

namespace ClaudeSessionManagerMCP.Hub;

/// <summary>
/// One registered session (one MCP server process == one session).
/// Identity is the session NAME (stable across reconnects); Id is per-connection.
/// </summary>
public sealed class SessionEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Pid { get; set; }
    public double? ContextPct { get; set; }
    public long? TokensUsed { get; set; }
    public bool? Working { get; set; }
    public bool ForcedAwake { get; set; }
    public string? Note { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public bool IsStale(AppConfig cfg) => (DateTime.UtcNow - LastSeenUtc).TotalSeconds > cfg.IdleTimeoutSeconds;

    public SessionInfo ToInfo(AppConfig cfg) => new()
    {
        Id = Id,
        Name = Name,
        Pid = Pid,
        ContextPct = ContextPct,
        TokensUsed = TokensUsed,
        Working = Working,
        Status = IsStale(cfg) ? "stale" : (Working == true ? "active" : "idle"),
        Note = Note,
        LastSeenIso = LastSeenUtc.ToString("o"),
    };
}

/// <summary>Team state: boss designation + the current team-wide directive.</summary>
public sealed class TeamState
{
    public string? BossName { get; set; }
    public string? BossId { get; set; }
    public DateTime? BossClaimedUtc { get; set; }
    public string? DirectiveText { get; set; }
    public DateTime? DirectiveUtc { get; set; }
}

/// <summary>Message queued for a recipient name (delivered when that session fetches its inbox).</summary>
public sealed class QueuedMessage
{
    public required string From { get; init; }
    public required string Text { get; init; }
    public DateTime TsUtc { get; init; } = DateTime.UtcNow;
    public bool Broadcast { get; init; }
    public bool Directive { get; init; }
}
