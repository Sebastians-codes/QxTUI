#!/usr/bin/env python3
"""pty smoke test for qxtui snake: plays a few turns, verifies rendering."""
import os
import pty
import select
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CMD = ["dotnet", "run", "-c", "Release", "--project", "samples/QxTui.Snake", "--no-build"]
UP, DOWN, LEFT, RIGHT = b"\x1b[A", b"\x1b[B", b"\x1b[D", b"\x1b[C"

captured = bytearray()
pid, fd = pty.fork()
if pid == 0:
    os.chdir(REPO)
    os.environ["TERM"] = "xterm-256color"
    os.execvp(CMD[0], CMD)

start = time.time()
script = [
    (1.0, RIGHT),
    (2.0, DOWN),
    (3.0, LEFT),   # reversal ignored by rules; snake keeps going down
    (4.0, None),   # pause
    (4.0, b" "),
    (5.0, b" "),   # resume
    (6.5, None),
    (6.5, b"q"),
]
si = 0
try:
    while time.time() - start < 12:
        r, _, _ = select.select([fd], [], [], 0.1)
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
            if script[si][1]:
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
with open("/tmp/opencode/snake.log", "w") as f:
    f.write(text)

checks = {
    "hud brand":       "qxtui snake" in text,
    "score shown":     "score" in text,
    "alive state":     "ALIVE" in text,
    "snake pixels":    any(g in text for g in "█▀▄"),
    "food pixel":      "●" in text or any(g in text for g in "▀▄█"),
    "pause overlay":   "PAUSED" in text,
    "help line":       "arrows/wasd" in text,
    "clean exit":      "\x1b[?1049l" in text,
}

code = os.waitstatus_to_exitcode(status)
print(f"exit code: {code}, captured {len(captured)} bytes\n")
failed = 0
for name, ok in checks.items():
    print(f"  {'PASS' if ok else 'FAIL'}  {name}")
    failed += 0 if ok else 1

sys.exit(1 if failed or code != 0 else 0)
