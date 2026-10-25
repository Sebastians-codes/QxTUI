#!/usr/bin/env python3
"""Reconstruct the final screen from a captured qxtui ANSI stream.

Mini VT emulator: tracks cursor position (CUP/H), writes printable text,
ignores SGR/mode sequences. Good enough to verify qxtui frame output.
"""
import re
import sys

def emulate(data: str, fallback_w=120, fallback_h=40):
    w, h = fallback_w, fallback_h
    grid = [[' '] * w for _ in range(h)]
    row, col = 0, 0

    token = re.compile(r'\x1b\[([0-9;?]*)([a-zA-Z])|(\x1b[=>])|(.)', re.S)
    for m in token.finditer(data):
        params, final, _simple, ch = m.groups()
        if final:
            if final == 'H':
                parts = params.split(';')
                row = (int(parts[0]) - 1) if parts and parts[0] else 0
                col = (int(parts[1]) - 1) if len(parts) > 1 and parts[1] else 0
            elif final == 'J':
                grid = [[' '] * w for _ in range(h)]
            # m (SGR), h/l (modes), etc: ignore
        elif ch:
            if ch in '\r\n\x07':
                continue
            if 0 <= row < h and 0 <= col < w:
                grid[row][col] = ch
            col += 1
            if col >= w:
                col = 0
                row = min(row + 1, h - 1)
    return '\n'.join(''.join(r).rstrip() for r in grid)

if __name__ == '__main__':
    path = sys.argv[1] if len(sys.argv) > 1 else '/tmp/opencode/qxtui-demo.log'
    data = open(path, encoding='utf-8', errors='replace').read()
    print(emulate(data))
