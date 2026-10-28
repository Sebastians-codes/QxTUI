# AGENT.md — qxtui

Strict instructions for any agent (or human) working on this repository.
Read this fully before touching code. Violations of MUST rules require
explicit discussion before proceeding.

## 1. What qxtui is

qxtui is a terminal UI framework for .NET, inspired by Go's Bubble Tea and
Ratatui, but built around an architecture those do NOT have:

**Fine-grained reactivity with damage isolation.**
A component animating at 240fps MUST NOT cause any other part of the UI to
re-render, re-layout, or re-serialize. Only the dirty cells of the animating
component are written to the terminal.

### Core claims (the reason this project exists)

1. **Isolated updates** — an animating child component invalidates ONLY its
   own cells. Untouched UI costs 0 bytes/frame and 0 render time.
2. **Declarative animation** — components declare transitions
   (`AnimateTo(value, Transition(duration, easing))`); a single global
   scheduler drives all animations from one clock and writes into signals.
   No per-component timers.
3. **High frame rate** — the renderer targets 200+ fps capability in pure
   C# (zero-alloc hot path). The terminal emulator/monitor is the limit,
   never the framework.
4. **Flexbox from day one** — layout is flexbox-style (row/column, grow,
   shrink, basis, gap, align). Terminal resizes re-flow the tree.
   **Content invalidation ≠ layout invalidation**: if a node's rect does not
   change during re-layout, its cached cells are kept.

## 2. Non-negotiable architecture rules

### Layer dependency rule (MUST)

```
Reactive  → (depends on nothing)
Animation → Reactive
Layout    → (depends on nothing)
Render    → (depends on nothing; pure buffer/diff/serialize)
Core      → Reactive, Animation, Layout, Render
Terminal  → platform interop ONLY; no framework logic
```

- No upward dependencies. No cycles. `Render` and `Layout` are pure data
  transforms and MUST stay unit-testable without a terminal.
- All terminal I/O goes through an output abstraction
  (`IBufferWriter<byte>`), so the renderer is fully testable headless.

### Rendering pipeline (MUST follow exactly)

```
intent (messages)  → ONE update fn per app (App.OnMessage<T>) → signal writes
signals/animations → mark widgets dirty
dirty widgets      → render into persistent back CellBuffer (retained scene)
back vs prev       → per-cell diff → damage rects → coalesce
damage rects       → serialize ANSI (raster order, minimal cursor moves)
one submit         → FramePresenter mailbox (single slot, latest wins)
writer thread      → the ONLY place a blocking tty write may happen
```

- The scene is **retained**, not immediate-mode. Widgets render only when
  dirty; the back buffer persists between frames.
- One frame = at most ONE submitted frame; the writer thread performs the
  terminal write. The frame loop MUST NEVER block on tty backpressure —
  the pty queue is a few KB and a blocking write couples our fps to the
  terminal emulator's drain rate (which scales with window size). If the
  writer is behind, intermediate frames are DROPPED (latest wins): render
  the latest state, never queue frames.
- Stats must report produced fps (pacer) AND presented fps (writer
  hand-offs) separately; presented is terminal-capped and that is expected.
- Frame pacer renders the LATEST state; if behind, skip ahead (drop
  intermediate frames). For budgets ≤ 8ms the pacer spin-waits (sleep
  overshoot on Linux alone caps a 240fps loop near ~200fps).
- **On-demand rendering**: when no continuous animation is active and
  nothing is dirty, the loop SLEEPS until the next discrete scheduler tick
  (capped at 16ms for input latency) instead of pacing — idle wakeups put
  ZERO bytes on the wire and skip the diff. A monitor with 1-20Hz panels
  must cost ~0% CPU between ticks; spin-pacing an empty loop is a bug
  (it burned ~12-18% CPU in qxtop before this rule existed).

### Hot-path performance budget (MUST)

- **Zero heap allocations** in the per-frame path: diff, damage coalescing,
  serialization, animation ticking. Use `Span<T>`, pooled arrays
  (`ArrayPool<T>`), preallocated buffers. (Exception: cold paths like
  resize, mount/unmount, layout-tree mutation may allocate.)
- Renderer frame time budget: < 5 ms for a 200×60 grid including
  serialization, so 200fps headroom exists at the source.
- No LINQ, no closures capturing per frame, no boxing in the hot path.
- The PoC sample MUST display live proof on screen: bytes/frame, fps,
  p99 frame time, relayout count.

### Reactivity rules (MUST)

- `Signal<T>` is versioned; writing an equal value MUST NOT bump the version
  or fire subscribers (this is what keeps clean UI clean).
- Widgets subscribe to the signals they render from. A signal write marks
  ONLY subscribing widgets dirty.
- Animations write into signals. Animation completion MUST set the exact
  final value (no floating point drift).
- **Intent flows on the message channel, state flows on signals.** Widgets
  emit intents with `Send(msg)` WITHOUT referencing the app (reusable by
  construction); the app's single update function (`App.OnMessage<T>`) is
  the only place intents become state. Closures survive only as
  app-specific policy at the composition root (`ChipAction.Act`).
- **Off-thread work enters ONLY as posted intents.** `App.Effect` runs I/O
  on the pool and posts its result (or an `EffectFault`) through the
  thread-safe inbox (`App.Post`); the frame thread stays single-threaded
  and never blocks (see §2 pipeline and samples/QxTui.SysMon Procs).
- **Lifecycle is hooks, not plumbing:** `OnMount(App)` / `OnDetach()` /
  `OnRectChanged(old, new)` are the supported extension points; the
  mount-action materialization stays internal.

### Layout rules (MUST)

- Pure function of (tree, constraints) → rects. No I/O, no global state.
- After a resize, the layout pass MUST compare new rects against previous
  rects: nodes whose rect is unchanged keep their cached cells; only
  moved/resized nodes are marked dirty.
- Integer cell math: grow distributes leftover cells by weight; remainder
  cells go to the earliest children. Deterministic — same input, same rects.
- Min/Max sizes are HARD clamps applied after flex distribution (a cap beats
  grow, a floor beats shrink); space a clamped child gives up is redistributed
  once to unconstrained siblings. `Justify` packs only what grow and the
  clamps left over; justify remainder cells go to the earliest gaps.

## 3. Testing doctrine (MUST — "test everything from the start")

- Every module ships with unit tests in the same PR/commit that adds it.
  No test = not done.
- Tests are headless: renderer tests assert on exact ANSI bytes written to
  an in-memory buffer; layout tests assert exact rects; animation tests use
  a **virtual clock** (injected `ITimeSource`), never real sleeps.
- Required regression invariants (these encode the core claims — if a test
  here fails, the architecture is broken):
  1. `Identical frame → only sync begin/end bytes written` (~8 bytes).
  2. `One cell changed → output contains exactly one cursor-move to that
     cell and the new char; total bytes < 64`.
  3. `Anim signal write → only subscribing widget dirty` (dirty tracking
     observable in tests).
  4. `Resize is a cold full repaint` (no preserve: preserving freezes
     cross-widget contamination from drag frames). Layout RectChanged
     tracking still reports exactly which nodes moved.
  5. `Grow distribution remainder is deterministic`.
- Benchmark project (`bench/`) tracks frame-time p50/p99 and bytes/frame
  for the standard scenes (1 animating cell on full screen; 500 particles).

## 4. Code conventions

- C# 14 / net10.0. `nullable enable`, warnings-as-errors for the library.
- Files: one primary type per file, folder-per-layer (see §2).
- Naming: `PascalCase` types/members, `_camelCase` private fields.
- Public API surface stays minimal and intentional; PoC internals may be
  `internal` + `InternalsVisibleTo` for tests.
- No external NuGet dependencies in `src/QxTui` — BCL only.
- Platform interop (stty, ioctl, signals) is isolated in `Terminal/` and
  behind interfaces; PoC targets Linux/POSIX first, Windows later.

## 5. Roadmap

- **Phase 0 (current): PoC** — proves the four core claims on screen:
  spinner + declarative progress transition + 500 bouncing particles +
  live stats (bytes/frame, fps, p99, relayouts) + live flexbox resize.
  Excluded: full component API, text wrapping, mouse, Windows, kitty
  keyboard protocol.
- Phase 1: component API design (mount/unmount lifecycle), input routing
  (Elm-style message channel for app events), text wrapping.
- Phase 2: full flexbox (shrink with min-content, justify), virtualized
  lists, borders, component kit.
- Phase 3: Windows terminal support, caps detection, synchronized-output
  fallbacks, NuGet packaging.
- Measure before optimizing: any talk of native (Rust) extraction requires
  benchmark evidence that managed code is the bottleneck. Default answer:
  it isn't.

## 6. Commands

```
dotnet build                      # build everything
dotnet test                       # run all tests
dotnet run --project samples/QxTui.PoC   # run the demo (needs a real TTY)
```

## 7. Definition of done for any change

1. `dotnet build` clean (zero warnings in src/QxTui).
2. `dotnet test` green, including the §3 invariant tests.
3. No allocations introduced in the hot path (justify in review if needed).
4. Demo still shows: bytes/frame stays ~1–2 KB while animations run
   (vs. ~20+ KB for a full repaint of the same screen).
