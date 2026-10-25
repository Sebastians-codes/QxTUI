#!/usr/bin/env python3
"""Resize stress test: rapidly shrink/grow the pty window while the demo
runs, then quit and report any crash output."""
import fcntl
import os
import pty
import select
import struct
import subprocess
import sys
import termios
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CMD = ["dotnet", "run", "-c", "Release", "--project", "samples/QxTui.PoC", "--no-build"]
TIOCSWINSZ = 0x5414

def set_winsize(fd, rows, cols):
    fcntl.ioctl(fd, TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))

captured = bytearray()
pid, fd = pty.fork()
if pid == 0:
    os.chdir(REPO)
    os.environ["TERM"] = "xterm-256color"
    os.execvp(CMD[0], CMD)

set_winsize(fd, 30, 100)
start = time.time()
phase = 0
sizes = [(30, 100), (24, 80), (10, 40), (6, 20), (4, 10), (3, 8), (2, 5), (1, 3),
         (24, 80), (40, 140), (12, 30), (30, 100)]
sent_q = False

try:
    while time.time() - start < 12:
        r, _, _ = select.select([fd], [], [], 0.05)
        if r:
            try:
                data = os.read(fd, 65536)
            except OSError:
                break
            if not data:
                break
            captured += data

        elapsed = time.time() - start
        if elapsed > 2 and phase < len(sizes):
            set_winsize(fd, *sizes[phase])
            phase += 1
            time.sleep(0.08)  # rapid: 80ms between resizes
        if phase >= len(sizes) and not sent_q and elapsed > 6:
            os.write(fd, b"q")
            sent_q = True
        if sent_q:
            w, status = os.waitpid(pid, os.WNOHANG)
            if w != 0:
                break
finally:
    try:
        _, status = os.waitpid(pid, 0)
    except ChildProcessError:
        status = 0
    os.close(fd)

text = captured.decode("utf-8", errors="replace")
with open("/tmp/opencode/qxtui-resize.log", "w") as f:
    f.write(text)

code = os.waitstatus_to_exitcode(status) if hasattr(os, "waitstatus_to_exitcode") else status >> 8
print(f"exit code: {code}, captured {len(captured)} bytes, resizes applied: {phase}")
if "Unhandled exception" in text or code not in (0,):
    idx = text.find("Unhandled exception")
    print("CRASH OUTPUT:" if idx >= 0 else "NONZERO EXIT, tail:")
    print(text[idx:idx + 2500] if idx >= 0 else text[-1500:])
    sys.exit(1)
print("no crash — resize stress OK")
