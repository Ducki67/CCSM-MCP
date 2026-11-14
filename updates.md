# ClaudeSessionManagerMCP — Updates

## 1.0.1 — 2026-11-14 (bug fixes)

### Fixed
- **PC could get stuck awake.** `SetThreadExecutionState` only applies to the
  calling *thread*, and the hub called it from random threadpool threads, so a
  release could run on a different thread and do nothing. It now uses a power
  request object (`PowerCreateRequest`/`PowerSetRequest`/`PowerClearRequest`),
  which belongs to the process. It shows up in `powercfg /requests` as CCSM-MCP,
  and Windows drops it if the hub dies.
- **Every session showed the hub's PID.** The client now sends its own PID on register.
- **Tools failed until `register_session` was called**, although the error said
  registration was automatic. Any session tool now auto-registers as
  `<folder>-<pid>` (or `CCSM_SESSION_NAME` if set). A later `register_session`
  renames the session, moving its inbox and boss role with it.
- **SessionId mismatch after a hub restart.** The retried request carried the
  old id. Requests now always carry the current connection's id.
- **A tool call could hang forever** on a frozen hub (blocking `ReadLine` with no
  timeout). Exchanges are async with a 10s timeout, then reconnect.
- **The spawned hub inherited the MCP server's stdin.** It now gets its own pipe,
  which is closed right away.

### Changed
- `message_send` to a known session that is offline queues the message
  (`queuedOffline`) instead of failing; it's delivered when that name registers again.
- Inbox lookups are case-insensitive, like name lookups already were.

### Tests
- New `scripts/smoke-fixes.py`: 12 checks for the fixes above, including killing
  the hub mid-session. `smoke-test.py`, `smoke-mcp.py` and `smoke-fixes.py` all pass.

## 1.0.0 — 2026-11-14 (initial release)

First version. One C# exe, two modes, built for running several Claude sessions
as a coordinated team on one Windows machine.

### The exe
- `ClaudeSessionManagerMCP.exe` with no args = MCP stdio server; with the
  argument `hub` = TCP hub on `127.0.0.1` (port from `config.ini`, default 8779).
- The MCP server auto-spawns the hub on first use (`Environment.ProcessPath`)
  and redirects the child's stdout/stderr into `hub-boot.log`, so the hub can
  never inherit the parent's stdout (reserved for the MCP protocol).
- The hub is detached and outlives the Claude sessions that started it.

### Configuration
- Hand-rolled INI parser (`config.ini` next to the exe; auto-written with
  defaults when missing): `server.port`, `server.logLevel`,
  `sleep.bKeepPCOn`, `sleep.bKeepDisplayOn`, `usage.heavyUsagePercent`,
  `usage.criticalUsagePercent`, `usage.idleTimeoutSeconds`.
- Critical threshold is auto-clamped above the heavy threshold if misconfigured.

### Session management
- `register_session` claims a stable name; name collisions get `-2`, `-3`…
  suffixes instead of failing. Identity survives hub restarts (auto
  re-register by name).
- `session_checkin` reports `working` + context usage; stale sessions
  (no check-in within `idleTimeoutSeconds`) are marked in listings.
- 14 tools total: register/checkin/list/overview/messaging/PC-awake/team/boss.

### Messaging
- Inboxes keyed by session **name** (survive reconnects, 100-message cap).
- `message_send(to=name|id|all)` direct + broadcast; `message_inbox` is
  fetch-and-clear; `session_checkin` reports `messagesWaiting`.

### Usage warnings
- >= `heavyUsagePercent` context -> "save findings to memory + compact" directive.
- >= `criticalUsagePercent` -> urgent "write a resume note first" directive.
- `usage_overview` aggregates everyone and produces a ready-to-broadcast text.

### Team / boss (user-requested)
- `team_claim_boss` is first-come; auto-released when the boss disconnects
  (all sessions are notified). Boss-only: `team_directive` (pinned, shown in
  every check-in) and `team_feature_set(keep_pc_on)` — the global sleep-block
  switch. `hub_shutdown` is boss-only unless `force=true`.
- Non-boss attempts are denied with a clear reason; sessions are told to claim
  boss first if nobody has.

### Keep-PC-running (user-requested, `bKeepPCOn`)
- Hub holds `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)`
  while the feature is on and any session is working (or personally forced
  awake via `pc_awake_set`); releases instantly when the last working session
  disconnects or the feature is switched off — Windows sleeps again normally.
- `bKeepDisplayOn` (default false) keeps the screen on too; default overnight
  behaviour keeps the machine awake but lets the display sleep.
- `pc_awake_status` reports engaged state, reason (working/forced counts),
  and since when.

### Notable implementation decisions
- Protocol: JSON lines over TCP with camelCase fields; one request = exactly
  one response line; exchanges serialized client-side by a lock.
- Hub state guarded by a single lock; PC-awake evaluation runs on a 30s timer
  plus on every state change; disconnects always release the sleep block.
- Fix during bring-up: the first `SpawnHub` used `using`-disposed log streams
  referenced by async output handlers — the race crashed the MCP server right
  after spawning the hub. Replaced with `ReadToEndAsync` pumps that keep the
  streams alive for the hub's lifetime.
- `dotnet build`: 0 warnings / 0 errors (net10.0; ModelContextProtocol 2.1.0,
  Microsoft.Extensions.Hosting 10.0.12).
- Tests: `scripts/smoke-test.py` (27 hub-protocol checks) and
  `scripts/smoke-mcp.py` (25 checks over two real stdio MCP processes) — both
  pass. Single-file publish verified afterwards.
- Licensing for open source: **GPL-3.0** (repo `CCSM-MCP` at github.com/Ducki67/CCSM-MCP;
  an MIT placeholder was swapped for the repo's GPL-3.0 LICENSE before publish).
