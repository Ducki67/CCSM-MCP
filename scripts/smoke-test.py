#!/usr/bin/env python3
"""
ClaudeSessionManagerMCP protocol smoke test (no Claude needed).

Spawns the built exe as a hub on a test port, connects two raw TCP clients
simulating two Claude sessions, and verifies:
  registration + unique names, check-in directives (heavy/critical),
  messaging (direct + broadcast), usage overview, team/boss gating,
  team directives, PC-awake state transitions, shutdown rules.

Run: python scripts/smoke-test.py [path-to-exe]
"""

import json
import socket
import subprocess
import sys
import time
from pathlib import Path

PORT = 8791
failures = 0


def check(cond, msg):
    global failures
    print(("  ok - " if cond else "FAIL - ") + msg)
    if not cond:
        failures += 1


class Client:
    def __init__(self, name):
        self.sock = socket.create_connection(("127.0.0.1", PORT), timeout=5)
        self.name = name

    def send(self, **cmd):
        self.sock.sendall((json.dumps(cmd) + "\n").encode())

    def recv(self):
        buf = b""
        while b"\n" not in buf:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise ConnectionError("hub closed")
            buf += chunk
        return json.loads(buf.split(b"\n")[0])

    def cmd(self, **kw):
        self.send(**kw)
        return self.recv()

    def close(self):
        try:
            self.sock.close()
        except OSError:
            pass


def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else str(
        Path(__file__).resolve().parent.parent / "bin" / "Debug" / "net10.0" / "ClaudeSessionManagerMCP.exe"
    )
    assert Path(exe).exists(), f"exe not found: {exe}"

    hub = subprocess.Popen([exe, "hub", "--port", str(PORT)],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        # wait for the hub port
        for _ in range(40):
            try:
                probe = socket.create_connection(("127.0.0.1", PORT), timeout=1)
                probe.close()
                break
            except OSError:
                time.sleep(0.15)
        else:
            raise RuntimeError("hub did not start")

        # ping
        a = Client("A")
        r = a.cmd(cmd="ping")
        check(r.get("ok") and "uptimeSeconds" in r, "ping answers with uptime")

        # 1. registration + unique names
        r = a.cmd(cmd="register", name="worker-alpha", note="exports")
        check(r["ok"] and r["sessionId"], f"register A -> id {r.get('sessionId')}")
        aid = r["sessionId"]
        b = Client("B")
        r = b.cmd(cmd="register", name="worker-alpha")  # name collision
        check(r["ok"] and r["name"] == "worker-alpha-2", f"register B dedupes name -> {r.get('name')}")

        # 2. heavy context directive on check-in
        r = a.cmd(cmd="checkin", sessionId=aid, contextPct=85, working=True, note="running exports")
        d = " ".join(r.get("directives") or [])
        check("CONTEXT HEAVY (85%)" in d, "85% -> CONTEXT HEAVY directive")
        check(r.get("messagesWaiting") == 0, "messagesWaiting 0")

        # 3. critical context + usage overview
        r = b.cmd(cmd="checkin", contextPct=95, working=False)
        d = " ".join(r.get("directives") or [])
        check("CONTEXT CRITICAL (95%)" in d, "95% -> CONTEXT CRITICAL directive")
        r = b.cmd(cmd="checkin", contextPct=95)  # again for stable state
        r = a.cmd(cmd="usage_overview")
        ov = r["overview"]
        check(ov["sessionsReported"] == 2, f"overview sessionsReported == 2 (got {ov['sessionsReported']})")
        check("worker-alpha-2" in ov["criticalNames"], "B in criticalNames")
        check(ov["broadcastText"] != "", "overview provides ready broadcast text")

        # heavyPeers: B should see A as heavy
        r = b.cmd(cmd="checkin", contextPct=95)
        peers = r.get("heavyPeers") or []
        check(any("worker-alpha" in p and "85" in p for p in peers), f"B sees heavy peer A {peers}")

        # 4. direct messaging + inbox
        r = a.cmd(cmd="message_send", sessionId=aid, to="worker-alpha-2", text="Start the second export batch.")
        check(r["ok"] and r["delivered"] == ["worker-alpha-2"], "direct message delivered")
        r = b.cmd(cmd="message_inbox")
        msgs = r.get("messages") or []
        check(len(msgs) == 1 and msgs[0]["from"] == "worker-alpha"
              and "second export batch" in msgs[0]["text"], "B inbox receives the message")

        # 5. broadcast
        r = a.cmd(cmd="message_send", sessionId=aid, to="all", text="BREAK: dataset updated.")
        check(r["ok"] and r["delivered"] == ["worker-alpha-2"], "broadcast reaches the other session")
        r = b.cmd(cmd="message_inbox")
        check(any("BREAK" in m["text"] for m in (r.get("messages") or [])), "B receives broadcast")

        # 6. team/boss gating
        r = a.cmd(cmd="team_claim_boss", sessionId=aid)
        check(r["ok"], "A claims boss")
        r = b.cmd(cmd="team_claim_boss")
        check(not r["ok"] and "already claimed" in r["error"], "second claim denied while boss online")
        r = b.cmd(cmd="team_feature_set", feature="keep_pc_on", enabled=False)
        check(not r["ok"] and "Only the boss" in r["error"], "non-boss feature toggle DENIED")
        r = b.cmd(cmd="team_directive", sessionId=None, text="ignore")
        check(not r["ok"], "non-boss directive DENIED")
        r = a.cmd(cmd="team_feature_set", feature="keep_pc_on", enabled=True)
        check(r["ok"] and r["pc"]["featureEnabled"], "boss toggles keep_pc_on")
        r = a.cmd(cmd="team_directive", text="Phase 1 done — start exports now.")
        check(r["ok"], "boss sets team directive")
        r = b.cmd(cmd="checkin", contextPct=95)
        d = " ".join(r.get("directives") or [])
        check("TEAM DIRECTIVE from boss 'worker-alpha'" in d and "Phase 1 done" in d,
              "B sees team directive on check-in")
        r = a.cmd(cmd="team_status")
        check(r["name"] == "worker-alpha" and r["sessions"], "team_status shows boss + sessions")

        # 7. pc-awake state transitions
        r = b.cmd(cmd="pc_awake_set", enabled=True)
        check(r["pc"]["awakeActive"] and r["pc"]["forcedSessions"] == 1, "forced awake holds the flag")
        r = b.cmd(cmd="pc_awake_set", enabled=False)
        # A is still working=True (not stale within idleTimeout) -> still held
        check(r["pc"]["awakeActive"] and r["pc"]["workingSessions"] >= 1,
              "working session keeps the flag held")
        a.close()  # A disconnects
        time.sleep(0.4)
        r = b.cmd(cmd="pc_awake_status")
        check(not r["pc"]["awakeActive"], "flag released after last working session disconnects")

        # 8. shutdown rules (boss A gone, boss name persists but offline -> B can claim)
        r = b.cmd(cmd="hub_shutdown")
        check(r["ok"] and r.get("shuttingDown"), "hub_shutdown accepted")

        # give the hub a moment to exit, then verify port closed
        time.sleep(0.8)
        try:
            probe = socket.create_connection(("127.0.0.1", PORT), timeout=1)
            probe.close()
            check(False, "hub port still open after shutdown")
        except OSError:
            check(True, "hub port closed after shutdown")

        b.close()
    finally:
        hub.terminate()
        try:
            hub.wait(timeout=5)
        except subprocess.TimeoutExpired:
            hub.kill()

    print(f"\n{'ALL CHECKS PASSED' if failures == 0 else str(failures) + ' check(s) FAILED'}")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
