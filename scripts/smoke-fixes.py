"""
Regression checks for the v1.0.1 fixes (runs against the real MCP exe, port 8779):
auto-register, real per-session PID, rename on register_session, messages queued
for offline sessions, and recovery after the hub process is killed.

Run: python scripts/smoke-fixes.py [path-to-exe]
"""

import importlib.util
import re
import subprocess
import sys
import time
from pathlib import Path

here = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("smoke_mcp", here / "smoke-mcp.py")
sm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sm)
check = sm.check


def hub_pids(port):
    out = subprocess.run(["netstat", "-ano", "-p", "TCP"], capture_output=True, text=True).stdout
    pids = set()
    for line in out.splitlines():
        m = re.search(rf"127\.0\.0\.1:{port}\s+\S+\s+LISTENING\s+(\d+)", line)
        if m:
            pids.add(int(m.group(1)))
    return pids


def kill_hub():
    for pid in hub_pids(sm.HUB_PORT):
        subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    time.sleep(0.8)


def start(exe, tag):
    p = sm.McpProc(exe, tag)
    p.call("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                          "clientInfo": {"name": "smoke-fixes", "version": "1.0.0"}})
    p.notify("notifications/initialized")
    return p


def find(sessions, name):
    return next((s for s in sessions if s["name"].lower() == name.lower()), None)


def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else str(
        here.parent / "bin" / "Debug" / "net10.0" / "ClaudeSessionManagerMCP.exe")
    kill_hub()
    a = start(exe, "A")
    b = start(exe, "B")
    a2 = None
    try:
        r = a.tool("session_checkin", {"working": False})
        check(r.get("ok") and r.get("name", "").endswith(f"-{a.proc.pid}"),
              f"tool before register_session auto-registers ({r.get('name')})")

        sessions = a.tool("sessions_list")["sessions"]
        me = find(sessions, r["name"])
        check(me is not None and me.get("pid") == a.proc.pid,
              f"sessions_list shows the session's own PID ({me and me.get('pid')} == {a.proc.pid})")

        r = a.tool("register_session", {"name": "alpha", "note": "fix test"})
        check(r.get("name") == "alpha", "register_session after auto-register renames to 'alpha'")
        b.tool("register_session", {"name": "beta"})
        sessions = b.tool("sessions_list")["sessions"]
        check(find(sessions, "alpha") and len(sessions) == 2, "list shows alpha + beta, no leftover auto name")

        a.close()
        time.sleep(0.8)
        r = b.tool("message_send", {"to": "alpha", "text": "for you when you are back"})
        check(r.get("ok") and r.get("queuedOffline") == ["alpha"], "message to offline 'alpha' is queued, not an error")

        a2 = start(exe, "A2")
        r = a2.tool("register_session", {"name": "alpha"})
        check(r.get("messagesWaiting", 0) >= 1, "reconnected 'alpha' sees the waiting message")
        msgs = a2.tool("message_inbox").get("messages", [])
        check(any("when you are back" in m["text"] for m in msgs), "offline message delivered from inbox")

        old_hubs = hub_pids(sm.HUB_PORT)
        kill_hub()
        t0 = time.time()
        r = a2.tool("session_checkin", {"working": True}, timeout=20)
        check(r.get("ok") is True, f"checkin after hub kill recovers ({time.time() - t0:.1f}s, err={r.get('error')})")
        new_hubs = hub_pids(sm.HUB_PORT)
        check(new_hubs and not (new_hubs & old_hubs), "a fresh hub process was spawned")
        b.tool("session_checkin", {"working": False}, timeout=20)
        r = a2.tool("message_send", {"to": "beta", "text": "after restart"}, timeout=20)
        check(r.get("ok") is True, f"no SessionId mismatch after hub restart (err={r.get('error')})")

        pc = a2.tool("pc_awake_status")["pc"]
        check(pc.get("awakeActive") is True, "working session holds the power request")
        a2.tool("session_checkin", {"working": False})
        time.sleep(0.3)
        pc = a2.tool("pc_awake_status")["pc"]
        check(pc.get("awakeActive") is False, "power request released when idle")
    finally:
        for p in (a, b, a2):
            if p:
                p.close()
        kill_hub()

    print("\nALL CHECKS PASSED" if sm.failures == 0 else f"\n{sm.failures} CHECK(S) FAILED")
    return 0 if sm.failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
