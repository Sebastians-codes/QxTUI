# qxtui

A terminal UI framework for .NET (net10.0), inspired by Go's Bubble Tea and
Ratatui — but built around an architecture they don't have:
**fine-grained reactivity with damage isolation**.

A component animating at 240fps re-renders *only its own cells*. The rest of
the UI costs zero bytes and zero CPU per frame.

## The four core claims

1. **Isolated updates** — an animating child invalidates only its own cells.
   Untouched UI costs 0 bytes/frame.
2. **Declarative animation** — components declare transitions
   (`Transition(duration, easing, repeat)`); one global scheduler drives all
   of them from a single clock and writes into signals.
3. **200fps-class rendering in pure C#** — zero-alloc hot path: retained
   scene tree → per-cell diff → damage coalescing → minimal ANSI → one
   synchronized-output write per frame.
4. **Flexbox from day one** — row/column, grow, shrink, basis, percent, gap,
   align. Terminal resizes re-flow the tree; nodes whose rect didn't change
   keep their cached cells.

## Proof, not promises

```bash
dotnet test                                  # 77 tests, incl. the invariants below
dotnet run -c Release --project bench/QxTui.Bench        # headless fps/bytes proof
dotnet run -c Release --project samples/QxTui.PoC        # the demo (needs a TTY)
dotnet run -c Release --project samples/QxTui.SysMon     # qxtop: a real app (needs a TTY)
python3 samples/pty_smoke_test.py            # demo under a pty, asserts on-screen proof
python3 samples/sysmon_smoke_test.py         # qxtop under a pty, asserts every panel
python3 samples/resize_stress_test.py        # rapid shrink/grow stress, asserts no crash
```

### qxtop — the framework eating its own dogfood

A btop-style system monitor: per-core CPU bars + sparklines, memory/swap,
network rx/tx rates, load average, live process table. Each panel samples
`/proc` at its own rate (20/10/4/2/1 Hz) off the single scheduler clock and
invalidates only itself — damage isolation doing real work, not demo work.
Keys: `q` quit, `p` pause the process table.

The demo shows the proof live on screen: bytes/frame vs. full-repaint cost,
dirty cells/damage rects, fps vs. target, frame-work p50/p99, relayout
count — plus a static panel whose render count stays at 1 while a spinner,
a declarative progress transition and 500 bouncing particles animate.

Benchmark (200×60 grid, renderer side):

| scene                        | p99 frame time | bytes/frame | sustainable fps |
|------------------------------|---------------:|------------:|----------------:|
| idle                         |      0.043 ms  |          16 |          23 250 |
| one animating cell           |      0.040 ms  |          40 |          24 915 |
| 500 bouncing particles       |      1.210 ms  |      31 291 |             826 |

(The terminal emulator and monitor cap what you can *see*; the framework
side has the headroom above.)

## Architecture

```
Reactive   Signal<T> — versioned values; equal writes are no-ops
Animation  Transition, Easing, AnimationScheduler (one clock, injectable time)
Layout     flexbox tree, pure integer cell math, rect-change tracking
Render     Cell/CellBuffer, damage rects + coalescing, ANSI serializer, pacer
Core       Widget (retained, dirty-tracked), App (the frame loop)
Terminal   POSIX raw mode, alt screen, SIGWINCH, background input reader
```

Layer rule: no upward dependencies; `Render` and `Layout` are pure and fully
headless-testable. All I/O goes through `IBufferWriter<byte>` / `ITerminal`.

Frame loop order: input → resize → animation tick → layout (if needed) →
dirty widgets paint the retained back buffer → diff/damage/coalesce →
ONE submit to the FramePresenter (a dedicated writer thread owns the
blocking tty write; the frame loop never stalls on terminal backpressure,
intermediate frames drop latest-wins) → pace.

The demo reports produced fps (pacer) and presented fps (terminal-capped)
separately, plus dropped frames — so "the framework held 240" and "the
terminal showed N" are never conflated.

## Read this before contributing

**[AGENT.md](AGENT.md)** is the constitution: architecture invariants,
performance budgets, testing doctrine (every module ships with tests;
the four core claims are encoded as regression invariants), and the
definition of done.

## Status

Phase 0 (PoC): Linux/POSIX only. No text wrapping, no mouse, no Windows,
no component-kit beyond the demo widgets. See AGENT.md §5 roadmap.
