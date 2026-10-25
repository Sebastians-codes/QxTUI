#!/usr/bin/env python3
"""pty smoke test for qxtop (samples/QxTui.SysMon).

Runs the monitor under a pseudo-terminal, presses 'p' (pause processes),
then 'q' (quit), and verifies every panel appeared and pause took effect.
"""
import os
import pty
import select
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CMD = ["dotnet", "run", "-c", "Release", "--project", "samples/QxTui.SysMon", "--no-build"]

captured = bytearray()
pid, fd = pty.fork()
if pid == 0:
    os.chdir(REPO)
    os.environ["TERM"] = "xterm-256color"
    os.execvp(CMD[0], CMD)

start = time.time()
sent_p = sent_q = False
try:
    while time.time() - start < 15:
        r, _, _ = select.select([fd], [], [], 0.25)
        if r:
            try:
                data = os.read(fd, 65536)
            except OSError:
                break
            if not data:
                break
            captured += data

        elapsed = time.time() - start
        if not sent_p and elapsed > 4:
            os.write(fd, b"p")
            sent_p = True
        if sent_p and not sent_q and elapsed > 6:
            os.write(fd, b"q")
            sent_q = True
        if sent_q:
            w, _st = os.waitpid(pid, os.WNOHANG)
            if w:
                break
finally:
    try:
        _, status = os.waitpid(pid, 0)
    except ChildProcessError:
        status = 0
    os.close(fd)

text = captured.decode("utf-8", errors="replace")
with open("/tmp/opencode/qxtop.log", "w") as f:
    f.write(text)

checks = {
    "header brand":        "qxtop" in text,
    "uptime shown":        "up " in text,
    "cpu panel":           "CPU" in text,
    "per-core bars":       "░" in text and "%" in text,
    "cpu sparkline":       "▂" in text or "▁" in text,
    "memory panel":        "MEMORY" in text,
    "mem human units":     "GiB" in text or "MiB" in text,
    "network panel":       "NETWORK" in text,
    "net rates":           "/s" in text,
    "load panel":          "LOAD" in text,
    "running count":       "running" in text,
    "process table":       "PROCESSES" in text,
    "proc header cols":    "CPU%" in text and "MEM%" in text,
    "process rows present": __import__("re").search(
        r"\d+ +[SRDtZXKPW] +\d+\.\d +\d+(\.\d)?[KMG]", text) is not None,
    "pause took effect":   "paused" in text,
    "clean exit":          "\x1b[?1049l" in text,
}

code = os.waitstatus_to_exitcode(status)
print(f"exit code: {code}, captured {len(captured)} bytes\n")
failed = 0
for name, ok in checks.items():
    print(f"  {'PASS' if ok else 'FAIL'}  {name}")
    failed += 0 if ok else 1

sys.exit(1 if failed or code != 0 else 0)
