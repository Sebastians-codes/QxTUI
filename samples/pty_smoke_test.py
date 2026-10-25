#!/usr/bin/env python3
"""Headless pty smoke test for the qxtui PoC demo.

Launches the demo under a pseudo-terminal, lets it run for a few seconds,
sends 'q' to quit, and verifies the captured ANSI stream contains the
expected proof markers.
"""
import os
import pty
import select
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # repo root
CMD = ["dotnet", "run", "-c", "Release", "--project", "samples/QxTui.PoC", "--no-build"]
RUN_SECONDS = 4.0

captured = bytearray()

pid, fd = pty.fork()
if pid == 0:
    os.chdir(REPO)
    os.environ["TERM"] = "xterm-256color"
    os.execvp(CMD[0], CMD)

start = time.time()
sent_q = False
try:
    while time.time() - start < RUN_SECONDS + 5:
        r, _, _ = select.select([fd], [], [], 0.25)
        if r:
            try:
                data = os.read(fd, 65536)
            except OSError:
                break
            if not data:
                break
            captured += data
        if not sent_q and time.time() - start > RUN_SECONDS:
            os.write(fd, b"q")
            sent_q = True
        if sent_q:
            # give it a moment to flush the exit sequence, then stop
            _, status = os.waitpid(pid, os.WNOHANG)
            if _ != 0:
                break
finally:
    try:
        _, status = os.waitpid(pid, 0)
    except ChildProcessError:
        status = 0
    os.close(fd)

text = captured.decode("utf-8", errors="replace")
out_path = "/tmp/opencode/qxtui-demo.log"
with open(out_path, "w") as f:
    f.write(text)

checks = {
    "alt-screen entered":      "\x1b[?1049h" in text,
    "cursor hidden":           "\x1b[?25l" in text,
    "synchronized output":     "\x1b[?2026h" in text,
    "stats panel visible":     "bytes/frame" in text,
    "fps counter visible":     "fps:" in text,
    "relayouts counter":       "relayouts:" in text,
    "static panel rendered":   "STATIC PANEL" in text,
    "spinner title":           "SPINNER" in text,
    "progress bar blocks":     "█" in text,
    "particles drawn":         "•" in text,
    "clean exit (alt leave)":  "\x1b[?1049l" in text,
}

print(f"captured {len(captured)} bytes, exit status {status}\n")
failed = 0
for name, ok in checks.items():
    print(f"  {'PASS' if ok else 'FAIL'}  {name}")
    if not ok:
        failed += 1

sys.exit(1 if failed else 0)
