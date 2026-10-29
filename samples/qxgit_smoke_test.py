#!/usr/bin/env python3
"""pty smoke test for qxgit (samples/QxTui.QxGit).

Runs the git browser under a pseudo-terminal pointed at THIS repository:
waits for the async loads (rev-parse → log/ls-files/status → first diff),
moves the table cursor (diff pane follows), filters the log with '/',
Esc-clears, opens the full-diff overlay with Enter, Esc-closes it,
refreshes with 'r', quits with 'q'. Verifies the stream shows every stage.
"""
import fcntl
import os
import pty
import select
import struct
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CMD = ["./samples/QxTui.QxGit/bin/Release/net10.0/QxTui.QxGit"]

script = [
    (2.5, b"\x1b[B"),        # Down: select commit #2 → diff pane follows
    (2.8, b"\x1b[B"),        # Down again
    (3.2, b"a"),             # full file browser (qxtui.slnx appears)
    (3.7, b"a"),             # back to the changed-files status view
    (4.0, b"\t"),            # Tab: focus → diff list
    (4.15, b"\t"),           # Tab: focus → worktree tree
    (4.3, b"\x1b[F"),        # End: tree cursor to last row (the scratch
                              # leaf) — scrolls it into the 3-row viewport
                              # and loads its untracked-file diff
    (4.7, b"/"),             # focus the filter field
    (4.9, b"vocabulary"),    # live subject filter
    (6.2, b"\x1b"),          # Esc: clear filter, refocus table
    (6.5, b"\x1b[<0;35;2M"),   # click the panel's < chip: collapse to rail
    (6.6, b"\x1b[<0;35;2m"),
    (7.0, b"\x1b[<0;1;2M"),    # click the rail's > chip: expand again
    (7.1, b"\x1b[<0;1;2m"),
    (7.4, b"\x1b[<2;21;11M"),  # right-press the worktree panel…
    (7.6, b"\x1b[<34;31;11M"), # …drag sideways: horizontal-only resize
    (7.7, b"\x1b[<2;31;11m"),
    (8.2, b"\r"),            # Enter: full-diff overlay modal
    (9.5, b"\x1b"),          # Esc: close the modal
    (9.8, b"\x1b[<0;61;9M"),   # click a table row…
    (10.0, b"\x1b[<0;61;9m"),
    (10.1, b"\x1b[<0;61;9M"),  # …double-click: full diff opens again
    (10.2, b"\x1b[<0;61;9m"),
    (10.9, b"\x1b"),          # Esc: close it
    (11.6, b"r"),            # refresh log + files
    (12.6, b"q"),            # quit
]

# a scratch untracked file: the default tree is the STATUS view (changed
# files only), and the repo is clean at test time — this gives it a leaf
TMP = os.path.join(REPO, "qxgit-smoke-tmp.txt")
with open(TMP, "w") as f:
    f.write("smoke scratch\n")

captured = bytearray()
pid, fd = pty.fork()
if pid == 0:
    os.chdir(REPO)
    os.environ["TERM"] = "xterm-256color"
    os.execvp(CMD[0], CMD)

start = time.time()
fcntl.ioctl(fd, 0x5414, struct.pack("HHHH", 34, 110, 0, 0))  # TIOCSWINSZ 34x110
si = 0
status = 0
try:
    while time.time() - start < 22:
        r, _, _ = select.select([fd], [], [], 0.2)
        if r:
            try:
                data = os.read(fd, 65536)
            except OSError:
                break
            if not data:
                break
            captured += data
        elapsed = time.time() - start
        while si < len(script) and elapsed >= script[si][0]:
            os.write(fd, script[si][1])
            si += 1
        if si >= len(script):
            w, st = os.waitpid(pid, os.WNOHANG)
            if w:
                status = st
                break
finally:
    try:
        _, st = os.waitpid(pid, 0)
        status = st
    except ChildProcessError:
        pass
    os.close(fd)
    try:
        os.remove(TMP)
    except OSError:
        pass

text = captured.decode("utf-8", errors="replace")
with open("/tmp/opencode/qxgit.log", "w") as f:
    f.write(text)

checks = {
    "hud brand":       "qxgit" in text,
    # damage-isolated repaints only emit CHANGED cells, so the bracket
    # (unchanged since the placeholder) is not in the stream — "main]" is
    "branch shown":    "main]" in text,
    "worktree panel":  "worktree" in text,
    "tree rendered":   "\u25be" in text,          # ▾ expanded dir glyph
    "status markers":  "? qxgit-smoke-tmp" in text,  # untracked leaf, changed-only view
    "full tree toggle": "qxtui.slnx" in text,        # clean file, only in all-files mode
    "commits panel":   "commits (" in text,
    "table headers":   "hash" in text and "subject" in text,
    "commit rows":     "One component per file" in text,
    "first diff auto": "diff --git" in text,      # pane loads selection on LogMsg
    "filter applied":  "Widget vocabulary batch" in text,
    "overlay chips":   "close" in text,
    "help line":       "q: quit" in text,
    "dbl-click hint":  "dbl-click" in text,
    # collapse + expand + sideways resize each relayout the row, so the
    # commits panel title repaints at a new x every time (4 = initial + 3)
    "panel collapse+resize": text.count("commits (") >= 4,
    "collapse in help":  "chip: tree" in text,
    "clean exit":      "\x1b[?1049l" in text,
}

code = os.waitstatus_to_exitcode(status)
print(f"exit code: {code}, captured {len(captured)} bytes\n")
failed = 0
for name, ok in checks.items():
    print(f"  {'PASS' if ok else 'FAIL'}  {name}")
    failed += 0 if ok else 1

sys.exit(1 if failed or code != 0 else 0)
