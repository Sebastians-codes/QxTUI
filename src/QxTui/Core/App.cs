using System.Buffers;
using QxTui.Animation;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Core;

/// <summary>Live frame statistics — surfaced on screen by the PoC demo
/// as proof of the damage-isolation claim (AGENT.md §2).</summary>
public sealed class FrameStats
{
    public int LastBytesWritten;
    public int LastDirtyCells;
    public int LastDamageRects;
    public double Fps;
    public double FrameWorkP50Ms;
    public double FrameWorkP99Ms;
    public int Relayouts;

    /// <summary>Frames the terminal actually received per second. Capped by
    /// the terminal emulator's drain rate — the honest "what you see" number,
    /// as opposed to <see cref="Fps"/> (frames produced).</summary>
    public double PresentedFps;
    /// <summary>Frames actually rendered+submitted per second (the number
    /// that matters for smoothness; 'produced' is only the pacer rate).</summary>
    public double RenderedFps;
    /// <summary>Mouse events routed per second (is the terminal sending
    /// motion at drag speed at all?).</summary>
    public double MouseEventsPerSec;
    /// <summary>Max tty write latency in the window (ms): high = the
    /// emulator/pty is not draining, i.e. present-path backpressure.</summary>
    public double WriterMaxMs;

    /// <summary>Frames dropped by the mailbox because a newer frame replaced
    /// them before the writer thread could hand them to the terminal.</summary>
    public long DroppedFrames;
}

/// <summary>
/// The frame loop. Order per frame (AGENT.md §2 pipeline):
///   input → resize check → animation tick → layout (if needed) →
///   dirty widgets render into back buffer → diff/damage → ONE write → pace.
/// Headless-testable: drive single frames with <see cref="RunFrame"/> using
/// an ITerminal fake; no real tty required.
/// </summary>
public sealed class App : IDisposable
{
    private readonly ITerminal _terminal;
    private LayoutNode _root;
    private readonly List<Widget> _roots = [];
    private readonly List<Widget> _widgets = []; // flat pre-order: parents before children
    private readonly HashSet<Widget> _registered = [];
    private readonly AnimationScheduler _scheduler;
    private readonly FramePacer _pacer;
    private readonly ArrayBufferWriter<byte> _output = new(1 << 16);
    private readonly List<LayoutNode> _changedNodes = [];

    private CellBuffer _back;
    private Renderer _renderer;
    private CellBuffer? _lastSubmitted;
    private readonly FramePresenter? _presenter;
    private bool _needsLayout = true;
    // true by default so RunFrame() works standalone (headless tests);
    // Quit() flips it, Run() re-arms it
    private bool _running = true;
    private long _lastPresentedCount;
    private long _droppedSeen;
    private int _presentedWindowFrames;
    private int _renderedWindowFrames;
    private int _mouseWindowEvents;
    private double _presentedWindowStart;

    public FrameStats Stats { get; } = new();

    /// <summary>Current back-buffer dimensions (viewport size in cells).</summary>
    public int BackWidth => _back.Width;

    /// <summary>The retained back buffer (test introspection).</summary>
    internal CellBuffer Back => _back;
    public int BackHeight => _back.Height;
    public int TargetFps => _pacer.TargetFps;

    /// <summary>The fps target as a signal — HUDs bind to it instead of
    /// mirroring SetTargetFps calls by hand.</summary>
    public Reactive.Signal<int> TargetFpsSignal { get; }

    /// <summary>The user's requested target (the pacer may run below it
    /// temporarily when the terminal can't keep up — see adaptation).</summary>
    public int RequestedFps => _userTargetFps;
    private int _userTargetFps;
    private int _effectiveFps;
    private int _slowWindows;
    private int _recoverWindows;

    /// <summary>Retarget the frame rate at runtime (e.g. from a pause menu).
    /// Takes effect from the next frame.</summary>
    public void SetTargetFps(int fps)
    {
        _userTargetFps = fps;
        _effectiveFps = fps;
        _slowWindows = 0;
        _recoverWindows = 0;
        _pacer.SetTargetFps(fps);
        TargetFpsSignal.Set(fps);
    }

    /// <summary>
    /// Backpressure adaptation: flooding a terminal that can only present
    /// 8fps with 120fps of 10KB diffs makes everything WORSE (emulator
    /// backlog, 4fps drags that look like teleports). When presented fps
    /// collapses below 60% of the effective rate for two windows, drop the
    /// effective rate toward what the terminal can actually show; when it
    /// keeps up again, climb back toward the user target (one doubling per
    /// two healthy windows — slow enough to avoid saw-toothing into a
    /// collapse, fast enough that returning to a heavy tab isn't syrup).
    /// </summary>
    internal int AdaptToPresented(double presented)
    {
        if (presented <= 0)
            return _effectiveFps;

        if (presented < _effectiveFps * 0.6)
        {
            _slowWindows++;
            _recoverWindows = 0;
        }
        else if (presented >= _effectiveFps * 0.9)
        {
            _slowWindows = 0;
            _recoverWindows++;
        }
        else
        {
            _slowWindows = 0;
            _recoverWindows = 0;
        }

        if (_slowWindows >= 2 && _effectiveFps > 24)
        {
            _effectiveFps = Math.Max(24, (int)(presented * 1.5));
            _pacer.SetTargetFps(_effectiveFps);
            _slowWindows = 0;
        }
        else if (_recoverWindows >= 2 && _effectiveFps < _userTargetFps)
        {
            _effectiveFps = Math.Min(_userTargetFps, _effectiveFps * 2);
            _pacer.SetTargetFps(_effectiveFps);
            _recoverWindows = 0;
        }

        return _effectiveFps;
    }

    /// <summary>Raw byte callback (legacy/simple apps). Return false to quit.</summary>
    public Func<byte, bool>? OnInput { get; set; }

    /// <summary>Parsed key callback (games/apps needing arrows etc.).
    /// Return false to quit.</summary>
    public Func<KeyEvent, bool>? OnKey { get; set; }

    private readonly KeyParser _keyParser = new();
    private readonly List<byte> _rawBatch = new(16);

    public AnimationScheduler Scheduler => _scheduler;

    /// <param name="asyncPresenting">When false, frames are written to the
    /// terminal inline (deterministic, for headless tests). Production keeps
    /// true so the frame loop never blocks on tty backpressure.</param>
    public App(ITerminal terminal, LayoutNode root, int targetFps = 60, ITimeSource? time = null, bool asyncPresenting = true)
    {
        _terminal = terminal;
        _root = root;
        _scheduler = new AnimationScheduler(time);
        _pacer = new FramePacer(targetFps);
        _back = new CellBuffer(Math.Max(1, terminal.Width), Math.Max(1, terminal.Height));
        _renderer = new Renderer(Math.Max(1, terminal.Width), Math.Max(1, terminal.Height));
        _presenter = asyncPresenting ? new FramePresenter(terminal.Write) : null;
        _userTargetFps = targetFps;
        _effectiveFps = targetFps;
        TargetFpsSignal = new Reactive.Signal<int>(targetFps);
        RegisterTree(_root); // DSL-composed widgets render without manual AddWidget
        _presentedWindowStart = NowMs() / 1000.0;
    }

    /// <summary>The palette widgets paint with (semantic roles, not ints).</summary>
    public Theme Theme { get; set; } = Theme.Default;

    /// <summary>Register a widget (attached to its Node) for the frame loop.
    /// Mounts it: declared Tick/OnSignal hooks bind to this app's scheduler
    /// and signals, and will be disposed with the app.</summary>
    public App AddWidget(Widget widget)
    {
        if (widget.Parent is not null)
            return this; // child: registered via its root's subtree

        if (_registered.Add(widget))
        {
            _roots.Add(widget);
            MountSubtree(widget);
            RebuildFlat();
        }
        return this;
    }

    private void MountSubtree(Widget widget)
    {
        widget.Mount(this);
        for (int i = 0; i < widget.Children.Count; i++)
            MountSubtree(widget.Children[i]);
    }

    internal void AttachSubtree(Widget child)
    {
        if (_registered.Add(child))
            MountSubtree(child);
        for (int i = 0; i < child.Children.Count; i++)
            AttachSubtree(child.Children[i]);
        RebuildFlat();
        _needsLayout = true; // new nodes need rects before they can paint
    }

    internal void DetachSubtree(Widget child, bool dispose = true)
    {
        // remember what the subtree painted BEFORE unregistering: those
        // pixels must be erased from the back buffer or they ghost forever
        // (new pages only paint their own rects; containers paint nothing)
        var rects = new List<Layout.Rect>();
        void Collect(Widget w)
        {
            rects.Add(w.Rect);
            for (int i = 0; i < w.Children.Count; i++)
                Collect(w.Children[i]);
        }
        Collect(child);

        void Un(Widget w)
        {
            _registered.Remove(w);
            for (int i = 0; i < w.Children.Count; i++)
                Un(w.Children[i]);
            if (dispose)
                w.Dispose();
        }

        Un(child);
        RebuildFlat();
        _needsLayout = true;

        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            _back.FillRect(r.X, r.Y, r.W, r.H, Render.Cell.Empty);
        }
    }

    private void RebuildFlat()
    {
        _widgets.Clear();
        void Walk(Widget w)
        {
            _widgets.Add(w);
            for (int i = 0; i < w.Children.Count; i++)
                Walk(w.Children[i]);
        }
        for (int i = 0; i < _roots.Count; i++)
            Walk(_roots[i]);
        for (int oi = 0; oi < _overlays.Count; oi++)
            if (!_widgets.Contains(_overlays[oi]))
                _widgets.Add(_overlays[oi]);
    }

    // ---- overlays (popups/modals) ------------------------------------------

    /// <summary>The modal widget currently on top of the stack, if any.</summary>
    public Widget? Overlay => _overlays.Count > 0 ? _overlays[^1] : null;
    private readonly List<Widget> _overlays = [];

    /// <summary>Show a modal on top of everything. It receives keys first
    /// (HandleKey) and all mouse events; render order puts it last = top.</summary>
    public void PushOverlay(Widget overlay)
    {
        _overlays.Add(overlay);
        if (_registered.Add(overlay))
        {
            overlay.Mount(this);
            _widgets.Add(overlay); // appended = top of the z-order
        }
        ResetHover();
        overlay.Invalidate();
    }

    /// <summary>Close the modal and repaint what it covered.</summary>
    public void PopOverlay()
    {
        if (_overlays.Count == 0) return;

        var ov = _overlays[^1];
        _overlays.RemoveAt(_overlays.Count - 1);
        _widgets.Remove(ov);
        _roots.Remove(ov);
        _registered.Remove(ov);
        ov.Dispose();
        ResetHover();

        // The dim covered gaps/uncovered regions no widget repaints. Clear
        // the back buffer but KEEP the renderer's prev frame holding the
        // dim: the diff then emits erases for every uncovered cell plus
        // repaints for widget rects. (Syncing prev to the cleared back —
        // the previous attempt — told the diff "nothing changed" and left
        // dim pixels on the terminal forever.)
        _back.Clear();
        _renderer.Resync(); // terminal still shows the dim: repaint EVERY cell
        InvalidateAll();
    }

    /// <summary>The widget receiving keyboard input, if any.</summary>
    public Widget? Focused { get; private set; }

    /// <summary>Move keyboard focus (null clears). Invalidates both ends so
    /// cursor/highlight states repaint.</summary>
    public void Focus(Widget? widget)
    {
        if (ReferenceEquals(Focused, widget))
            return;
        Focused?.Invalidate();
        Focused = widget;
        Focused?.Invalidate();
    }

    private void CycleFocus()
    {
        var focusable = _widgets.Where(w => w.Focusable).ToList();
        if (focusable.Count == 0)
            return;

        int idx = Focused is { } cur ? focusable.IndexOf(cur) : -1;
        Focus(focusable[(idx + 1) % focusable.Count]);
    }

    private Widget? _hovered;

    private Widget? TopMostAt(int x, int y)
    {
        // a dragging widget captures all mouse events until release
        if (_pressed is { CapturesMouse: true } cap)
            return cap;
        if (_overlays.Count > 0)
            return _overlays[^1];
        for (int i = _widgets.Count - 1; i >= 0; i--)
            if (_widgets[i].Rect.Contains(x, y))
                return _widgets[i];
        return null;
    }

    private void ResetHover()
    {
        if (_hovered is { } old)
        {
            old.Hovered = false;
            old.Invalidate();
        }
        _hovered = null;
    }

    private bool IsOverlay(Widget w) => _overlays.Contains(w);

    private int _cellPixW;
    private int _cellPixH;
    private bool _pixelMouse;

    /// <summary>Cell height in pixels (0 = unknown: pixel drag disabled).
    /// Widgets use it for sub-cell input math (scrollbar drags).</summary>
    public int CellPixelH => _cellPixH;
    public int CellPixelW => _cellPixW;

    /// <summary>Opt-in hardware scrolling (DECSTBM+SU/SD): repaints only
    /// exposed rows on scroll. Off by default — SU/SD semantics vary
    /// between emulators and a mismatch desyncs the front buffer.</summary>
    public bool HardwareScroll
    {
        get => _renderer.HardwareScroll;
        set => _renderer.HardwareScroll = value;
    }

    internal void SetCellPixelSize(int w, int h)
    {
        _cellPixW = w;
        _cellPixH = h;
        _pixelMouse = w > 0 && h > 0;
    }

    /// <summary>Route coordinates are cells; Px/Py survive for widgets that
    /// want sub-cell precision. Without a cell-size reply the raw coords
    /// ARE cells, so px is dropped to keep widgets from mis-scaling.</summary>
    private Terminal.MouseEvent ToCellCoords(Terminal.MouseEvent ev)
    {
        if (_pixelMouse && _cellPixH > 0)
            return new Terminal.MouseEvent(
                ev.Px / _cellPixW, ev.Py / _cellPixH, ev.Button, ev.Kind, ev.Px, ev.Py);
        return new Terminal.MouseEvent(ev.X, ev.Y, ev.Button, ev.Kind);
    }

    private void RouteMouse(MouseEvent ev)
    {
        _mouseWindowEvents++;
        Widget? target = TopMostAt(ev.X, ev.Y);

        // press/release bookkeeping FIRST: widgets may consume the event in
        // OnMouse (sliders do), and capture/Pressed/flash must still work
        Widget? clicked = null;
        if (ev.Kind == MouseKind.Press && target is { Clickable: true })
        {
            ReleasePressed();
            _pressed = target;
            target.Pressed = true;
            target.Invalidate();
        }

        if (ev.Kind == MouseKind.Press && target is { Focusable: true })
            Focus(target); // click-to-focus
        else if (ev.Kind == MouseKind.Release)
        {
            clicked = _pressed;
            ReleasePressed();
        }

        if (target is null) return;

        if (ev.Kind == MouseKind.Motion && !ReferenceEquals(target, _hovered))
        {
            ResetHover();
            _hovered = target;
            target.Hovered = true;
            target.Invalidate(); // border/color reacts to hover
        }

        if (target.OnMouse(ev)) return;

        if (ev.Kind == MouseKind.Release &&
            ReferenceEquals(clicked, target) && target.Clickable)
        {
            target.OnClick(new Point(ev.X - target.Rect.X, ev.Y - target.Rect.Y));
            target.FlashPressed(); // hold the pushed-down look ~120ms
            target.Invalidate();
        }
    }

    private Widget? _pressed;

    private void ReleasePressed()
    {
        if (_pressed is { } p)
        {
            p.Pressed = false;
            p.Invalidate();
            _pressed = null;
        }
    }

    /// <summary>Forward a hardware-scroll hint from a widget: the renderer
    /// emits a scroll-region SU/SD and repaints only exposed rows.</summary>
    public void ScrollHint(Layout.Rect rect, int delta) => _renderer.ScrollHint(rect, delta);

    /// <summary>Mark every widget dirty (theme switch, global style change).</summary>
    public void InvalidateAll()
    {
        for (int i = 0; i < _widgets.Count; i++)
            _widgets[i].Invalidate();
    }

    /// <summary>Install the layout root. Allows building widgets (which need
    /// the scheduler) before the tree that references their nodes exists.</summary>
    public void SetRoot(LayoutNode root)
    {
        _root = root;
        _needsLayout = true;
        RegisterTree(root); // DSL-composed widgets render without manual AddWidget
    }

    private void RegisterTree(LayoutNode node)
    {
        if (node.Tag is Widget w)
            AddWidget(w);
        for (int i = 0; i < node.Children.Count; i++)
            RegisterTree(node.Children[i]);
    }

    public void Run()
    {
        _terminal.Enter();
        _running = true;
        try
        {
            while (_running)
            {
                double start = NowMs();
                RunFrame(pace: false);
                if (!_running)
                    break;
                WaitBetweenFrames(NowMs() - start);
            }
        }
        finally
        {
            // Flush pending frame BEFORE exit sequences so ordering holds.
            _presenter?.Dispose();
            _terminal.Exit();
        }
    }

    /// <summary>
    /// On-demand rendering (AGENT.md §2): when no continuous animation is
    /// active and nothing is dirty, SLEEP until the next discrete scheduler
    /// tick instead of spin-pacing — a monitor with 1-20Hz panels must not
    /// burn a core running an empty 60fps loop. Continuous animations or
    /// pending damage fall back to the precision spin pacer.
    /// </summary>
    private void WaitBetweenFrames(double workMs)
    {
        if (_scheduler.HasContinuousActive || AnyWidgetDirty())
        {
            _pacer.PaceFrame(workMs);
            return;
        }

        double waitMs = MaxIdleSleepMs;
        if (_scheduler.NextDiscreteDeadline() is { } next)
        {
            double untilNext = (next - _scheduler.Time.Now).TotalMilliseconds;
            waitMs = Math.Clamp(untilNext, 1.0, MaxIdleSleepMs);
        }

        Thread.Sleep((int)waitMs);
    }

    /// <summary>Idle sleeps are capped so input/resize latency stays ≤16ms.</summary>
    private const double MaxIdleSleepMs = 16.0;

    private bool AnyWidgetDirty()
    {
        for (int i = 0; i < _widgets.Count; i++)
            if (_widgets[i].Dirty)
                return true;
        return false;
    }

    public void Quit() => _running = false;

    /// <summary>Execute exactly one frame. Public for headless tests.</summary>
    public RenderResult RunFrame(bool pace = false)
    {
        double startMs = NowMs();
        _pacer.MarkFpsSample();

        // 1) input: raw bytes → parsed key events (arrows, enter, chars)
        _rawBatch.Clear();
        while (_terminal.TryReadInput(out byte b))
        {
            _keyParser.Feed(b);
            _rawBatch.Add(b);
        }

        // a lone ESC left pending after the whole batch is a keypress,
        // not a sequence prefix (arrows arrive in one batch) — release it
        if (_keyParser.HasPending)
            _keyParser.Flush();

        while (_running && _keyParser.TryReadKey(out var keyEvent))
        {
            // overlay stack gets first chance at keys, top first
            bool consumed = false;
            for (int oi = _overlays.Count - 1; oi >= 0; oi--)
            {
                if (_overlays[oi].HandleKey(keyEvent))
                {
                    consumed = true;
                    break;
                }
            }
            if (consumed)
                continue;

            // Tab cycles focus among focusable widgets
            if (keyEvent.Key == Key.Tab)
            {
                CycleFocus();
                continue;
            }

            // focused widget gets keys before the app-level handler
            if (Focused is { } focused && focused.HandleKey(keyEvent))
                continue;

            if (OnKey is { } keyHandler && !keyHandler(keyEvent))
                _running = false;
        }

        if (_cellPixH == 0 && _keyParser.CellPixH > 0)
        {
            SetCellPixelSize(_keyParser.CellPixW, _keyParser.CellPixH);
            _terminal.Write("\x1b[?1016h"u8); // reply proved capability
        }
        else if (_cellPixH == 0 && _keyParser.WinPixH > 0 &&
                 _terminal.Height > 0 && _terminal.Width > 0)
        {
            // foot ignores 16 t but answers 14 t: derive the cell size
            int ch = _keyParser.WinPixH / _terminal.Height;
            int cw = _keyParser.WinPixW / _terminal.Width;
            if (ch >= 4 && cw >= 4) // sanity: a "cell" smaller than 4px is a lie
            {
                SetCellPixelSize(cw, ch);
                _terminal.Write("\x1b[?1016h"u8);
            }
        }

        while (_running && _keyParser.TryReadMouse(out var mouseEvent))
            RouteMouse(ToCellCoords(mouseEvent));

        for (int i = 0; i < _rawBatch.Count && _running; i++)
        {
            if (OnInput is { } handler && !handler(_rawBatch[i]))
                _running = false;
        }

        bool relayoutThisFrame = false;

        // 2) resize → preserve overlapping cells; only rect-changed widgets
        //    re-render (AGENT.md invariant: unchanged rect → cached cells kept)
        if (_terminal.ConsumeResize() ||
            _terminal.Width != _back.Width || _terminal.Height != _back.Height)
        {
            int w = Math.Max(1, _terminal.Width);
            int h = Math.Max(1, _terminal.Height);

            // RESIZE IS A COLD PATH: full clear + full repaint. Content-
            // preserving resizes freeze cross-contamination from frames
            // where moving widgets briefly painted over neighbours (ghost
            // panels, stale HUD fragments during window drags). One full
            // repaint per resize event is cheap; ghost chains are not.
            _back.Resize(w, h, preserve: false);
            _lastSubmitted?.Dispose();
            _lastSubmitted = null; // size changes; recreated on next submit
            _renderer.Resize(w, h); // fresh empty prev → diff = whole screen
            InvalidateAll();
            _needsLayout = true;
            relayoutThisFrame = true;
            Stats.Relayouts++;
        }

        // 3) animations tick → signals → widgets mark themselves dirty
        _scheduler.Tick();

        // widgets that asked for per-frame refresh follow the fps target
        for (int i = 0; i < _widgets.Count; i++)
            if (_widgets[i].PerFrame)
                _widgets[i].Invalidate();

        // overlays cover the viewport; positioned outside the layout tree
        for (int oi = 0; oi < _overlays.Count; oi++)
            _overlays[oi].Node.SetRect(new Rect(0, 0, _back.Width, _back.Height));

        // 4) layout pass (pure) when needed; rect-changed widgets go dirty.
        //    Their stale cells at the PREVIOUS rect must be cleared so the
        //    diff picks them up as damage (v1: no z-order compositing).
        if (_needsLayout)
        {
            LayoutNode.Compute(_root, _back.Width, _back.Height);
            _changedNodes.Clear();
            _root.CollectChanged(_changedNodes);
            for (int i = 0; i < _widgets.Count; i++)
            {
                var widget = _widgets[i];
                if (!_changedNodes.Contains(widget.Node))
                    continue;

                var prev = widget.Node.PreviousRect;
                if (!prev.Equals(default(Rect)) && !prev.Equals(widget.Rect))
                    _back.FillRect(prev.X, prev.Y, prev.W, prev.H, Cell.Empty);
                widget.Invalidate();
            }

            // Clear everything no widget covers anymore (gap rows, vacated
            // regions after resize). Without this, stale pixels survive
            // forever because nothing is dirty there. Cold path: fine.
            ClearUncovered();

            _needsLayout = false;
            relayoutThisFrame = true;
        }

        // 5) retained render: only dirty widgets touch the back buffer
        bool modalOpen = false;
        for (int oi = 0; oi < _overlays.Count; oi++)
            if (_overlays[oi].BlocksBaseRendering) modalOpen = true;

        // z-order: overlays are the top layers — repaint them LAST EVERY
        // frame, not only when dirty. Otherwise base widgets that render
        // on top of their stale pixels glitch through. The diff keeps the
        // wire cost at real changes.
        for (int oi = 0; oi < _overlays.Count; oi++)
            _overlays[oi].Invalidate();

        bool anyRendered = false;
        for (int i = 0; i < _widgets.Count; i++)
        {
            var widget = _widgets[i];
            // a DIMMED modal blocks the base: frozen base keeps the dim
            // layer clean (otherwise ticking widgets repaint over the dim
            // patchwork-style). Floating popups let the background live.
            if (modalOpen && !IsOverlay(widget))
                continue;
            if (widget.Dirty)
            {
                widget.RenderTracked(_back);
                anyRendered = true;

                // a parent repainting its full rect erases its children's
                // pixels: children must repaint in the same frame (they come
                // later in pre-order), or they'd blink/vanish
                for (int c = 0; c < widget.Children.Count; c++)
                    widget.Children[c].Invalidate();
            }
        }

        // 6) diff → damage → serialize → ONE submit. Idle wakeups (nothing
        //    rendered, no relayout) skip the diff and put ZERO bytes on the
        //    wire — a monitor between ticks costs nothing (AGENT.md §2).
        RenderResult result = default;
        if (anyRendered || relayoutThisFrame)
        {
            // dropped delta frames desync the terminal (it never applied
            // those diffs): the next frame after any drop must be FULL.
            // Capture the count BEFORE submitting: a drop caused by this
            // frame's own submit must trigger the NEXT frame's resync.
            bool dropped = _presenter is not null &&
                           _presenter.DroppedCount != _droppedSeen;
            if (_presenter is not null)
                _droppedSeen = _presenter.DroppedCount;

            // a drop means the terminal never saw those diffs: re-base the
            // front buffer on the last SUBMITTED content and repair with a
            // minimal delta (a full repaint here would flood a terminal
            // that is already drowning: drop → 40KB → more drops)
            if (dropped && _lastSubmitted is not null)
                _renderer.AdoptPrev(_lastSubmitted);

            _output.Clear();
            result = _renderer.Render(_back, _output, forceFull: false);
            if (_presenter is { } presenter)
            {
                presenter.Submit(_output.WrittenSpan);
            }
            else
            {
                _terminal.Write(_output.WrittenSpan); // synchronous test mode
            }
            _renderedWindowFrames++;

            _lastSubmitted ??= new CellBuffer(_back.Width, _back.Height);
            if (_lastSubmitted.Width == _back.Width && _lastSubmitted.Height == _back.Height)
                _back.Cells.CopyTo(_lastSubmitted.Cells);

            Stats.LastBytesWritten = result.BytesWritten;
            Stats.LastDirtyCells = result.DirtyCells;
            Stats.LastDamageRects = result.DamageRects;
        }


        // 7) stats + pace
        double workMs = NowMs() - startMs;
        Stats.Fps = _pacer.Fps;
        Stats.FrameWorkP50Ms = _pacer.FrameTimeP50Ms;
        Stats.FrameWorkP99Ms = _pacer.FrameTimeP99Ms;
        Stats.DroppedFrames = _presenter?.DroppedCount ?? 0;
        UpdatePresentedFps();

        if (pace)
            _pacer.PaceFrame(workMs);

        return result;
    }

    /// <summary>
    /// Cold-path cleanup after a relayout: any cell not covered by at least
    /// one widget's CURRENT rect is stale (gap rows, vacated regions after a
    /// resize) and gets cleared so the diff erases it on the terminal.
    /// O(cells × widgets) — only runs on layout frames.
    /// </summary>
    private void ClearUncovered()
    {
        if (_widgets.Count == 0)
        {
            _back.Clear();
            return;
        }

        int w = _back.Width, h = _back.Height;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool covered = false;
                for (int i = 0; i < _widgets.Count; i++)
                {
                    var w2 = _widgets[i];
                    if (IsOverlay(w2)) continue; // full-screen: would hide gaps
                    if (w2.Rect.Contains(x, y))
                    {
                        covered = true;
                        break;
                    }
                }
                if (!covered)
                    _back[x, y] = Cell.Empty;
            }
        }
    }

    /// <summary>Rolling presented-fps estimate over ~0.5s windows, measured
    /// from the writer thread's hand-off count (terminal-limited).</summary>
    private void UpdatePresentedFps()
    {
        long presented = _presenter?.PresentedCount ?? 0;
        _presentedWindowFrames += (int)(presented - _lastPresentedCount);
        _lastPresentedCount = presented;

        double nowS = NowMs() / 1000.0;
        double span = nowS - _presentedWindowStart;
        if (span >= 0.5)
        {
            Stats.PresentedFps = _presentedWindowFrames / span;
            Stats.RenderedFps = _renderedWindowFrames / span;
            Stats.MouseEventsPerSec = _mouseWindowEvents / span;
            Stats.WriterMaxMs = _presenter?.WindowMaxWriteMs ?? 0;
            _renderedWindowFrames = 0;
            _mouseWindowEvents = 0;
            _presentedWindowFrames = 0;
            _presentedWindowStart = nowS;

            // adapt ONLY on evidence of flooding: we submitted clearly more
            // than the terminal showed. An idle scene renders ~4fps because
            // nothing changes — treating that as a slow terminal demoted
            // user targets while the app was merely quiet.
            bool saturated = Stats.RenderedFps > Stats.PresentedFps * 1.2;
            bool busy = Stats.RenderedFps > 10;
            if (saturated)
                AdaptToPresented(Stats.PresentedFps);
            else if (busy)
                AdaptToPresented(Math.Max(Stats.PresentedFps, _effectiveFps * 0.95));
        }
    }

    private static double NowMs() =>
        System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public void Dispose()
    {
        foreach (var w in _roots)
            w.Dispose(); // cascades to children
        for (int oi = 0; oi < _overlays.Count; oi++)
            _overlays[oi].Dispose();
        _presenter?.Dispose();
        _back.Dispose();
        _lastSubmitted?.Dispose();
        _renderer.Dispose();
        _terminal.Dispose();
    }
}
