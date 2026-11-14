#!/usr/bin/env python3
"""
ClaudeSessionManagerMCP MCP-level smoke test (no Claude needed).

Spawns TWO MCP server processes (stdio) — exactly like two Claude sessions —
and drives them over JSON-RPC:
  initialize, tools/list, register_session, session_checkin (heavy directive),
  cross-session messaging via the auto-spawned hub, usage_overview,
  boss claim + directive + feature gating, sessions_list, hub_shutdown.

Run: python scripts/smoke-mcp.py [path-to-exe]
"""

import json
import socket
import subprocess
import sys
import threading
import time
from pathlib import Path

failures = 0
HUB_PORT = 8779


def kill_stale_hub():
    """A hub from a previous failed run may still be alive; force it down."""
    try:
        s = socket.create_connection(("127.0.0.1", HUB_PORT), timeout=2)
        s.sendall((json.dumps({"cmd": "hub_shutdown", "force": True}) + "\n").encode())
        s.settimeout(2)
        try:
            s.recv(4096)
        except (socket.timeout, OSError):
            pass
        s.close()
        time.sleep(0.5)
    except OSError:
        pass


def check(cond, msg):
    global failures
    print(("  ok - " if cond else "FAIL - ") + msg)
    if not cond:
        failures += 1


class McpProc:
    """One spawned MCP server process (one simulated Claude session)."""

    def __init__(self, exe, tag):
        self.tag = tag
        self.proc = subprocess.Popen(
            [exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL)
        self.next_id = 1
        self.responses = {}
        self.lock = threading.Lock()
        threading.Thread(target=self._reader, daemon=True).start()

    def _reader(self):
        for line in self.proc.stdout:
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if "id" in msg:
                with self.lock:
                    self.responses[msg["id"]] = msg

    def notify(self, method, params=None):
        self.proc.stdin.write((json.dumps(
            {"jsonrpc": "2.0", "method": method, "params": params or {}
        }) + "\n").encode())
        self.proc.stdin.flush()

    def call(self, method, params=None, timeout=10):
        rid = self.next_id
        self.next_id += 1
        self.proc.stdin.write((json.dumps(
            {"jsonrpc": "2.0", "id": rid, "method": method, "params": params or {}
        }) + "\n").encode())
        self.proc.stdin.flush()
        deadline = time.time() + timeout
        while time.time() < deadline:
            with self.lock:
                if rid in self.responses:
                    return self.responses.pop(rid)["result"]
            time.sleep(0.05)
        raise TimeoutError(f"{self.tag}: no response to {method} (id {rid})")

    def tool(self, name, args=None, timeout=10):
        r = self.call("tools/call", {"name": name, "arguments": args or {}}, timeout)
        if r.get("isError"):
            return {"_isError": True, "text": r["content"][0]["text"]}
        return json.loads(r["content"][0]["text"])

    def close(self):
        try:
            self.proc.terminate()
            self.proc.wait(timeout=5)
        except Exception:
            self.proc.kill()


def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else str(
        Path(__file__).resolve().parent.parent / "bin" / "Debug" / "net10.0" / "ClaudeSessionManagerMCP.exe"
    )
    assert Path(exe).exists(), f"exe not found: {exe}"

    kill_stale_hub()

    a = McpProc(exe, "A")
    b = McpProc(exe, "B")
    try:
        for tag, p in (("A", a), ("B", b)):
            init = p.call("initialize", {
                "protocolVersion": "2024-11-05", "capabilities": {},
                "clientInfo": {"name": "smoke", "version": "1.0.0"}})
            check(init["serverInfo"]["name"] == "ClaudeSessionManagerMCP",
                  f"{tag}: initialize serverInfo ok (v{init['serverInfo']['version']})")
            p.notify("notifications/initialized")

        tools = a.call("tools/list")["tools"]
        names = [t["name"] for t in tools]
        check(len(names) == 14, f"tools/list exposes 14 tools (got {len(names)})")
        for expected in ("register_session", "session_checkin", "usage_overview",
                         "message_send", "team_claim_boss", "team_feature_set"):
            check(expected in names, f"tool present: {expected}")

        # register both (auto-spawns the hub on first use)
        r = a.tool("register_session", {"name": "session-alpha", "note": "wiring the UI"})
        check(r["ok"] and r["sessionId"], f"A registered as session-alpha (id {r.get('sessionId')})")
        r = b.tool("register_session", {"name": "session-beta", "note": "asset exports"})
        check(r["ok"], "B registered as session-beta")

        # check-in with heavy context -> directive
        r = b.tool("session_checkin", {"contextPct": 88, "working": True})
        d = " ".join(r.get("directives") or [])
        check("CONTEXT HEAVY (88%)" in d, "B gets heavy-context directive at 88%")

        r = a.tool("session_checkin", {"contextPct": 35, "working": True})
        check(r.get("directives") is None, "A (35%) gets no directive")

        # cross-session messaging
        r = a.tool("message_send", {"to": "session-beta", "text": "Hand me the export list when done."})
        check(r["ok"] and r["delivered"] == ["session-beta"], "A -> B message delivered")
        r = b.tool("message_inbox")
        check(any("export list" in m["text"] for m in (r.get("messages") or [])), "B reads its inbox")
        r = a.tool("session_checkin", {"contextPct": 36})
        check(r.get("messagesWaiting") == 0, "A inbox empty (messagesWaiting 0)")

        # usage overview
        r = a.tool("usage_overview")
        ov = r["overview"]
        check(ov["sessionsReported"] == 2 and "session-beta" in ov["heavyNames"],
              "usage_overview flags session-beta heavy")

        # boss flow
        r = a.tool("team_claim_boss")
        check(r["ok"], "A claims boss")
        r = b.tool("team_feature_set", {"feature": "keep_pc_on", "enabled": False})
        check(not r["ok"], "B denied the boss-only feature switch")
        r = a.tool("team_directive", {"text": "Wrap up exports, save findings to memory."})
        check(r["ok"], "boss directive broadcast")
        r = b.tool("session_checkin", {"contextPct": 89})
        d = " ".join(r.get("directives") or [])
        check("TEAM DIRECTIVE from boss 'session-alpha'" in d, "B sees pinned boss directive")

        r = a.tool("sessions_list")
        listed = {s["name"] for s in r["sessions"]}
        check({"session-alpha", "session-beta"} <= listed, f"sessions_list shows both ({listed})")

        # pc awake status end-to-end (B is working -> hold active)
        r = a.tool("pc_awake_status")
        check(r["pc"]["awakeActive"] and r["pc"]["workingSessions"] >= 1,
              "PC kept awake while B is working")

        # shutdown
        r = b.tool("hub_shutdown")
        check(not r["ok"], "non-boss hub_shutdown denied")
        r = a.tool("hub_shutdown")
        check(r["ok"], "boss shuts the hub down")
    finally:
        a.close()
        b.close()

    print(f"\n{'ALL CHECKS PASSED' if failures == 0 else str(failures) + ' check(s) FAILED'}")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
