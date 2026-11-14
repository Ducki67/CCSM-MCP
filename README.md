# CCSM-MCP
CCSM (Claude Code Session Manager) MCP (Model Context Protocol) for working accorss multiple sessions, with a team of sessions and for late night workflow

Claude sessions that can talk to each other, watch each other's context windows, pick a boss to coordinate the work, and keep the PC awake while jobs run overnight.

- **Session-to-session messaging** — direct messages and broadcasts between sessions.
- **Shared context awareness** — heavy sessions get told to *save findings to memory and compact* before they blow up.
- **Boss / team roles** — one session claims boss; only the boss can issue team directives, toggle the global keep-PC-running switch, or shut the hub down.
- **Keep-PC-running** — blocks Windows sleep while any session reports `working=true`; releases as soon as the last one disconnects, so the machine sleeps normally again.

One C# exe, two modes: no args = MCP stdio server, `hub` = a local TCP hub on `127.0.0.1` that is auto-spawned on first use and outlives the sessions.

Setup instructions (written so Claude or any AI agent can follow them): **[Setup.md](Setup.md)**

## License

GPL-3.0 — see [LICENSE](LICENSE).
