#!/usr/bin/env python3
"""Diagnostic: drive a real scrollbar drag through a pty and record, per
presented frame, the thumb row and the first visible row index.

Usage: python3 samples/drag_probe.py
"""
import fcntl
import os
import pty
import re
import select
import struct
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ansi_replay import emulate

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CMD = ["./samples/QxTui.Playground/bin/Release/net10.0/QxTui.Playground"]
W, H = 40, 120

captured = bytearray()
pid, fd = pty.fork()
if pid == 0:
    os.chdir(REPO)
    os.environ["TERM"] = "xterm-256color"
    os.execvp(CMD[0], CMD)

fcntl.ioctl(fd, 0x5414, struct.pack("HHHH", W, H, 0, 0))


def send(b: bytes):
    os.write(fd, b)


def pump(deadline: float):
    """Read everything available until deadline; return bytes."""
    out = bytearray()
    while time.time() < deadline:
        r, _, _ = select.select([fd], [], [], 0.02)
        if r:
            try:
                d = os.read(fd, 65536)
            except OSError:
                break
            if not d:
                break
            out += d
    return out


start = time.time()
while time.time() - start < 2.0:      # boot
    pump(time.time() + 0.1)

send(b"4")                            # list tab
pump(time.time() + 1.0)

# list rect: header row 0, list starts y=2 .. y=38 (40 rows tall minus chrome)
# scrollbar column = x=119 (0-based) -> SGR x=120
SX = 120
frames = []


def snapshot(tag: str):
    global captured
    data = pump(time.time() + 0.06)
    captured += data
    text = emulate(bytes(captured).decode("utf-8", "replace"), H, W)
    lines = text.split("\n")
    thumb = next((i for i, l in enumerate(lines)
                  if l.rstrip().endswith("█") or "█" in l[-2:]), -1)
    m = re.search(r"row\s+(\d+)\s+value", text)
    first = int(m.group(1)) if m else -1
    frames.append((tag, thumb, first))


# list rect starts at row 6 (header/tabs/description above); thumb at top
send(f"\x1b[<0;{SX};7M".encode())
snapshot("press")

# drag down 12 cells, one motion event per cell, 40ms apart (real drag speed)
for i in range(1, 13):
    send(f"\x1b[<32;{SX};{7 + i}M".encode())   # 32 = motion, button 1 held
    snapshot(f"move+{i}")

send(f"\x1b[<0;{SX};19m".encode())             # release
snapshot("release")
pump(time.time() + 0.5)
snapshot("after")

os.write(fd, b"q")
time.sleep(0.3)
try:
    os.close(fd)
except OSError:
    pass

print(f"{'event':10} {'thumbRow':>8} {'firstRow':>9}")
prev = None
for tag, thumb, first in frames:
    d = "" if prev is None else f"   (thumb {thumb - prev[1]:+d}, content {first - prev[2]:+d})"
    print(f"{tag:10} {thumb:8} {first:9}{d}")
    prev = (tag, thumb, first)
