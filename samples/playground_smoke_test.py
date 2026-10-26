#!/usr/bin/env python3
"""pty smoke test for the qxtui playground sample."""
import fcntl
import os
import pty
import select
import struct
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ansi_replay import emulate

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CMD = ["./samples/QxTui.Playground/bin/Release/net10.0/QxTui.Playground"]

captured = bytearray()
pid, fd = pty.fork()
if pid == 0:
    os.chdir(REPO)
    os.environ["TERM"] = "xterm-256color"
    os.execvp(CMD[0], CMD)

fcntl.ioctl(fd, 0x5414, struct.pack("HHHH", 40, 120, 0, 0))

start = time.time()
script = [
    (2.0, b"t"),        # theme → Mono
    (3.0, b"t"),        # theme → Amber
    (4.0, b"]"),        # fps 120 → 240
    (5.0, b"o"),        # open floating popup
    (5.15, b"\x1b[<0;31;13M"),  # grab popup title bar
    (5.25, b"\x1b[<32;61;16M"), # drag it right+down
    (5.35, b"\x1b[<0;61;16m"),  # release
    (5.4, b"9"),        # popup action 9: hwscroll on
    (5.6, b"9"),        # ...and off again (opt-in feature exercised)
    (6.0, b"7"),        # popup action 7: stack confirm modal
    (6.5, b"2"),        # confirm action 2: ball room (live sim in overlay)
    (7.5, b"\x1b"),    # pop ball room
    (8.0, b"1"),        # confirm action 1: stack fps modal (3rd level)
    (8.6, b"3"),        # fps modal action 3: 240fps
    (9.1, b"\x1b"),    # pop level 3
    (9.5, b"\x1b"),    # pop level 2
    (9.9, b"\x1b"),    # pop level 1
    (10.2, b"o"),       # popup again
    (10.5, b"8"),       # popup action 8: three ball rooms at 8/30/120 Hz
    (11.3, b"\x1b"),   # pop room 120
    (11.6, b"\x1b"),   # pop room 30
    (11.9, b"\x1b"),   # pop room 8
    (12.2, b"\x1b"),   # pop popup
    (12.8, b"2"),       # tab: text page (unicode wrap)
    (13.8, b"3"),       # tab: ball page (slider + speed label)
    (14.8, b"1"),       # back to widgets
    (15.3, b"3"),       # ball page, then resize while it runs
    (15.5, ("resize", 30, 90)),
    (16.1, ("resize", 40, 120)),
    (16.7, b"4"),       # tab: virtualized list
    (17.3, b"\x1b[6~"),  # PageDown in list (unfocused: ignored safely)
    (17.9, b"1"),
    (19.2, b"q"),
]
si = 0
try:
    while time.time() - start < 26:
        r, _, _ = select.select([fd], [], [], 0.2)
        if r:
            try:
                data = os.read(fd, 65536)
            except OSError:
                break
            if not data:
                break
            captured += data
        el = time.time() - start
        while si < len(script) and el >= script[si][0]:
            payload = script[si][1]
            if isinstance(payload, tuple):  # ("resize", rows, cols)
                fcntl.ioctl(fd, 0x5414,
                            struct.pack("HHHH", payload[1], payload[2], 0, 0))
            else:
                os.write(fd, payload)
            si += 1
        if si >= len(script):
            w, _st = os.waitpid(pid, os.WNOHANG)
            if w:
                break
finally:
    try:
        _, status = os.waitpid(pid, 0)
    except ChildProcessError:
        status = 0
    # drain whatever the app wrote between our last read and its exit —
    # the alt-screen-leave sequence lives in those final bytes
    try:
        os.set_blocking(fd, False)
        while True:
            try:
                d = os.read(fd, 65536)
            except (BlockingIOError, OSError):
                break
            if not d:
                break
            captured += d
    except Exception:
        pass
    os.close(fd)

text = captured.decode("utf-8", errors="replace")
with open("/tmp/opencode/playground.log", "w") as f:
    f.write(text)
screen = emulate(text, 110, 34)

checks = {
    "hud brand":        "qxtui playground" in text,
    "theme cycled":     "Amber" in screen or "Amber" in text,
    "fps retargeted":   "240fps" in screen,
    "easing labels":    "out-back" in text,
    "sparkline feeds":  "sine" in text and "random walk" in text,
    "square pixels":    "square pixels" in text,
    "isolation panel":  "damage isolation" in text,
    "stats panel":      "static renders: 1" in screen or "static renders:" in text,
    "unicode wrap":     "日本語" in text,
    "default 1x":       "speed 1.0x" in text,
    "help line":        "t: theme" in text,
    "hwscroll line":    "hwscroll:" in text,
    "mouse mode line":  "mouse:" in text,
    "tab bar":          " widgets " in text and " ball " in text,
    "virtual list":     "row     0" in text or "row     1" in text,
    "popup opens":      "modals swallow" in text,
    "popup fps buttons": "240fps" in text,
    "modal stack":      "second modal" in text and "third level" in text,
    "ball room":        "ball room" in text,
    "ball rooms x3":    "ball 8hz" in text and "ball 30hz" in text and "ball 120hz" in text,
    "rate caps row":    "capped 8 Hz" in text and "capped 120 Hz" in text,
    "popup closes":     "modals swallow" not in screen,
    "clean exit":       "\x1b[?1049l" in text,
}

code = os.waitstatus_to_exitcode(status)
print(f"exit code: {code}, captured {len(captured)} bytes\n")
failed = 0
for name, ok in checks.items():
    print(f"  {'PASS' if ok else 'FAIL'}  {name}")
    failed += 0 if ok else 1

sys.exit(1 if failed or code != 0 else 0)
