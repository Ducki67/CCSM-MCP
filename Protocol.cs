using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeSessionManagerMCP.Protocol;

/// <summary>Shared JSON wire types + serializer options for the hub protocol (JSON lines over TCP).</summary>
public static class Wire
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}

// ---------------------------------------------------------------------------
// Client -> Hub
// ---------------------------------------------------------------------------

/// <summary>One JSON line from a client to the hub. All commands share this shape.</summary>
public sealed class HubRequest
{
    public string? Cmd { get; set; }           // register|checkin|sessions_list|usage_overview|message_send|message_inbox|pc_awake_set|pc_awake_status|hub_shutdown|ping|bye
    public string? SessionId { get; set; }
    public int? Pid { get; set; }              // register: the MCP server process id of the session
    public string? Name { get; set; }
    public string? Note { get; set; }
    public double? ContextPct { get; set; }
    public long? TokensUsed { get; set; }
    public bool? Working { get; set; }
    public string? To { get; set; }            // session name | "all"
    public string? Text { get; set; }
    public string? Feature { get; set; }       // team_feature_set: "keep_pc_on"
    public bool? Enabled { get; set; }         // pc_awake_set / team_feature_set
    public bool? Force { get; set; }           // hub_shutdown
}

// ---------------------------------------------------------------------------
// Hub -> Client
// ---------------------------------------------------------------------------

public sealed class SessionInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Pid { get; set; }
    public double? ContextPct { get; set; }
    public long? TokensUsed { get; set; }
    public bool? Working { get; set; }
    public string Status { get; set; } = "active";   // active | idle | stale
    public string? Note { get; set; }
    public string? LastSeenIso { get; set; }
}

public sealed class InboxMessage
{
    public string From { get; set; } = "";
    public string Text { get; set; } = "";
    public string TsIso { get; set; } = "";
    public bool Broadcast { get; set; }
}

public sealed class UsageOverview
{
    public double? AggregatePct { get; set; }        // average of reported sessions
    public int SessionsReported { get; set; }
    public List<string> HeavyNames { get; set; } = new();
    public List<string> CriticalNames { get; set; } = new();
    public string Advice { get; set; } = "";
    public string BroadcastText { get; set; } = "";  // ready to send via message_send(to:"all")
}

public sealed class PcAwakeStatus
{
    public bool FeatureEnabled { get; set; }
    public bool AwakeActive { get; set; }
    public int WorkingSessions { get; set; }
    public int ForcedSessions { get; set; }
    public int ConnectedSessions { get; set; }
}

public sealed class HubResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public string? SessionId { get; set; }
    public string? Name { get; set; }
    public List<string>? Directives { get; set; }
    public int? MessagesWaiting { get; set; }
    public List<string>? HeavyPeers { get; set; }
    public List<SessionInfo>? Sessions { get; set; }
    public UsageOverview? Overview { get; set; }
    public List<InboxMessage>? Messages { get; set; }
    public int? Remaining { get; set; }
    public List<string>? Delivered { get; set; }
    public List<string>? Undelivered { get; set; }
    public List<string>? QueuedOffline { get; set; }
    public PcAwakeStatus? Pc { get; set; }
    public double? UptimeSeconds { get; set; }
    public bool? ShuttingDown { get; set; }
}
