#!/usr/bin/env python3
"""pty smoke test for the qxtui playground sample."""
import os
import pty
import select
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ansi_replay import emulate

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CMD = ["dotnet", "run", "-c", "Release", "--project", "samples/QxTui.Playground", "--no-build"]

captured = bytearray()
pid, fd = pty.fork()
if pid == 0:
    os.chdir(REPO)
    os.environ["TERM"] = "xterm-256color"
    os.execvp(CMD[0], CMD)

start = time.time()
script = [
    (2.0, b"t"),        # theme → Mono
    (3.0, b"t"),        # theme → Amber
    (4.0, b"]"),        # fps 120 → 240
    (5.0, b"o"),        # open floating popup
    (6.0, b"4"),        # modal action 4: 240fps
    (7.5, b"\x1b"),    # esc closes it
    (9.0, b"q"),
]
si = 0
try:
    while time.time() - start < 12:
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
            os.write(fd, script[si][1])
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
    "help line":        "t: theme" in text,
    "popup opens":      "modals swallow" in text,
    "popup fps buttons": "4:240fps" in text,
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
