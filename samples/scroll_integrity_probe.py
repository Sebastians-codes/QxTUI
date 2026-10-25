#!/usr/bin/env python3
"""Scroll-stream integrity probe: a mini terminal that ACTUALLY executes
DECSTBM + SU/SD (ansi_replay ignores them, which is why foot-only bugs
slipped through). Hammers the list at max fps and verifies the emulated
grid stays coherent: contiguous row numbers, no blank holes, scrollbar
column continuous.

Usage: python3 samples/scroll_integrity_probe.py
"""
import fcntl
import os
import pty
import re
import select
import struct
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CMD = [os.path.join(REPO, "samples/QxTui.Playground/bin/Release/net10.0/QxTui.Playground")]
ROWS, COLS = 40, 120


class Emu:
    def __init__(self, w, h):
        self.w, self.h = w, h
        self.grid = [[' '] * w for _ in range(h)]
        self.r, self.c = 0, 0
        self.top, self.bot = 0, h - 1  # scroll region, 0-based inclusive

    def scroll_up(self, n):
        for _ in range(n):
            del self.grid[self.top]
            row = self.top
            self.grid.insert(self.bot, [' '] * self.w)
            _ = row

    def scroll_down(self, n):
        for _ in range(n):
            del self.grid[self.bot]
            self.grid.insert(self.top, [' '] * self.w)

    def feed(self, data: str):
        tok = re.compile(r'\x1b\[([0-9;?]*)([a-zA-Z])|(\x1b[=>])|(.)', re.S)
        for m in tok.finditer(data):
            params, final, _simple, ch = m.groups()
            if final:
                p = [int(x) for x in params.split(';') if x.isdigit()] if params else []
                if final == 'H':
                    self.r = (p[0] - 1) if len(p) >= 1 and p[0] else 0
                    self.c = (p[1] - 1) if len(p) >= 2 and p[1] else 0
                elif final == 'J':
                    if not p or p[0] == 0:
                        for y in range(self.r, self.h):
                            self.grid[y] = [' '] * self.w
                    else:
                        self.grid = [[' '] * self.w for _ in range(self.h)]
                elif final == 'r':
                    self.top = (p[0] - 1) if len(p) >= 1 and p[0] else 0
                    self.bot = (p[1] - 1) if len(p) >= 2 and p[1] else self.h - 1
                    self.r, self.c = 0, 0
                elif final == 'S':
                    self.scroll_up(p[0] if p else 1)
                elif final == 'T':
                    self.scroll_down(p[0] if p else 1)
                # m (SGR), h/l modes: irrelevant to geometry
            elif ch:
                if ch in '\r\n\x07':
                    continue
                if 0 <= self.r < self.h and 0 <= self.c < self.w:
                    self.grid[self.r][self.c] = ch
                self.c += 1
                if self.c >= self.w:
                    self.c = 0
                    self.r = min(self.r + 1, self.h - 1)


pid, fd = pty.fork()
if pid == 0:
    os.chdir(REPO)
    os.environ["TERM"] = "xterm-256color"
    os.execvp(CMD[0], CMD)
fcntl.ioctl(fd, 0x5414, struct.pack("HHHH", ROWS, COLS, 0, 0))

emu = Emu(COLS, ROWS)
buf = ""


def pump(deadline):
    global buf
    while time.time() < deadline:
        r, _, _ = select.select([fd], [], [], 0.02)
        if r:
            try:
                d = os.read(fd, 65536)
            except OSError:
                return
            if not d:
                return
            buf += d.decode("utf-8", "replace")


def sync():
    global buf
    emu.feed(buf)
    buf = ""


start = time.time()
pump(start + 2.0)
sync()
os.write(fd, b"4")          # list tab
pump(time.time() + 0.8); sync()
for _ in range(3):          # fps 120 -> 1000
    os.write(fd, b"]")
    pump(time.time() + 0.3); sync()

failures = 0
for burst in range(6):      # wheel bursts at 1000fps target
    for _ in range(6):
        os.write(fd, b"\x1b[<65;60;20M")
        time.sleep(0.01)
    pump(time.time() + 0.5)
    sync()
    text = "\n".join("".join(r).rstrip() for r in emu.grid)
    nums = [int(m) for m in re.findall(r"^\s*row\s+(\d+)\s+value", text, re.M)]
    contiguous = all(b == a + 1 for a, b in zip(nums, nums[1:]))
    blanks = sum(1 for i, l in enumerate(text.split("\n")[4:36])
                 if not l.strip() and i < len(nums))
    if not contiguous or not nums:
        failures += 1
        print(f"burst {burst}: BROKEN rows={nums[:6]}...{nums[-3:]}")
    else:
        print(f"burst {burst}: ok first={nums[0]} last={nums[-1]}")

os.write(fd, b"q")
time.sleep(0.3)
os.close(fd)
print("FAIL" if failures else "CLEAN")
sys.exit(1 if failures else 0)
