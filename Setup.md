# CCSM-MCP Setup Guide

This guide is written so a human **or an AI agent (Claude, etc.)** can set the
MCP up end to end. Follow the steps in order.

## What you are setting up

One exe with two modes:

- **MCP server** (no args): one instance per Claude session, talks over stdio.
- **Hub** (`hub` argument): a small TCP server on `127.0.0.1:8779` that all
  sessions share for messaging, usage reports, team roles, and the
  keep-PC-running sleep block. It is **auto-spawned** by the MCP server on
  first use — you normally never start it yourself.

```
Claude #1 --stdio--> CCSM exe --\
Claude #2 --stdio--> CCSM exe ----> hub (127.0.0.1:8779, auto-spawned)
Claude #3 --stdio--> CCSM exe --/
```

## Prerequisites

- Windows (the sleep-block uses a Win32 API).
- Either the published exe, or the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build it.

## Step 1 — Get the exe

Option A, download a release from the GitHub releases page (single file,
no install needed).

Option B, build it:

```powershell
git clone https://github.com/Ducki67/CCSM-MCP.git
cd CCSM-MCP
scripts\publish.ps1
# -> publish\ClaudeSessionManagerMCP.exe
```

## Step 2 — Register the MCP server in Claude's config

Add the exe to the MCP config of **each session's** entry point
(`.mcp.json` in a project, `claude_desktop_config.json` for Claude Desktop,
or the equivalent "add MCP server" UI/command):

```json
{
  "mcpServers": {
    "ccsm": {
      "command": "C:\\path\\to\\ClaudeSessionManagerMCP.exe",
      "args": []
    }
  }
}
```

Notes:

- Use `\\` (or forward slashes) in the JSON path.
- Register it in every Claude session that should join the team.
- The server name key (`ccsm` above) is free-form.
- First run auto-creates a default `config.ini` next to the exe and
  auto-spawns the hub. No other setup is required.

## Step 3 — What each session does first

Every session should call **`register_session`** right away and claim its own
stable name, e.g.:

- `register_session(name: "session-alpha", note: "wiring the UI")`
- `register_session(name: "session-beta", note: "asset exports")`

All other tools use the session's own identity automatically. Duplicate names
get a `-2`/`-3` suffix instead of failing.

Then one session calls **`team_claim_boss`** — first come, first served.
The boss coordinates; the role auto-releases when the boss disconnects.

## Tools

| Tool | What it does |
|---|---|
| `register_session` | Claim a stable name (do it first; until then a session is auto-named `<folder>-<pid>`). |
| `session_checkin` | Report `working` + context %; receive directives. Call every few minutes while working. |
| `sessions_list` | Who is connected, working, context %. |
| `usage_overview` | Aggregate context report + ready-to-broadcast advice text. |
| `message_send` | Message one session (`to` = name/id/`all`) or broadcast. |
| `message_inbox` | Fetch-and-clear your inbox. |
| `pc_awake_set` | Personally hold/release the sleep block. |
| `pc_awake_status` | Is the PC held awake, why, since when. |
| `team_claim_boss` | Claim the boss role (first-come). |
| `team_release_boss` | Give up the boss role. |
| `team_directive` | **boss only** — pinned directive every session sees on check-in. |
| `team_feature_set` | **boss only** — toggle the global keep-PC-running switch. |
| `team_status` | Who is boss, current directive, feature states. |
| `hub_shutdown` | **boss only** (or `force=true`) — stop the hub; releases the sleep block first. |

## Typical team workflow

1. All sessions `register_session` with distinct names.
2. One session claims boss with `team_claim_boss`.
3. Sessions `session_checkin(working: true, contextPct: ...)` every few minutes.
4. At **>= 80%** context the hub tells that session: *save findings to memory
   and compact soon*. At **>= 92%** it becomes urgent — write a resume note
   first. The boss can see everyone's usage via `usage_overview` and steer
   with `team_directive`.
5. Sessions message each other with `message_send` / `message_inbox` to hand
   over work and avoid duplicating it.
6. Overnight: the hub keeps the PC awake while any session is `working`.
   When the last session disconnects, Windows may sleep again.

## Configuration (`config.ini`, next to the exe)

```ini
[server]
port = 8779               ; hub TCP port (127.0.0.1 only)
logLevel = info           ; quiet | info | debug

[sleep]
bKeepPCOn = true          ; keep PC awake while sessions report working=true
bKeepDisplayOn = false    ; also keep the screen on

[usage]
heavyUsagePercent = 80    ; "save to memory + compact" threshold
criticalUsagePercent = 92 ; urgent threshold
idleTimeoutSeconds = 120  ; no check-in for this long -> shown stale
```

Missing file or keys are auto-restored with defaults. The file contains no
secrets.

## Logs and troubleshooting

Both files live next to the exe:

- `hub.log` — hub activity (sessions, messages, boss changes, sleep block).
- `hub-boot.log` — hub auto-spawn attempts and any child output.

| Symptom | Fix |
|---|---|
| Tools time out / "Could not connect to the session hub" | Start `ClaudeSessionManagerMCP.exe hub` manually and check `hub-boot.log`. Port conflict -> change `port` in `config.ini`. |
| Stale hub from an old run | `hub_shutdown` from a session, or end the `ClaudeSessionManagerMCP.exe` (hub) process in Task Manager. |
| PC not staying awake | Check `pc_awake_status`; the block only holds while a session is `working` within `idleTimeoutSeconds` or forced it via `pc_awake_set`, and the boss may have disabled `keep_pc_on`. |
| Two sessions with the same name | They get `-2` suffixes automatically; address messages to the exact listed name. |

## Verify the install (optional)

```powershell
python scripts\smoke-test.py   # hub protocol, 27 checks
python scripts\smoke-mcp.py    # full MCP level, 25 checks
```

Both spawn the real exe and clean up after themselves (Python 3 required).

## License

GPL-3.0 — see [LICENSE](LICENSE).
