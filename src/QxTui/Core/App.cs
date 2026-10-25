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
    private readonly List<Widget> _widgets = [];
    private readonly HashSet<Widget> _registered = [];
    private readonly AnimationScheduler _scheduler;
    private readonly FramePacer _pacer;
    private readonly ArrayBufferWriter<byte> _output = new(1 << 16);
    private readonly List<LayoutNode> _changedNodes = [];

    private CellBuffer _back;
    private Renderer _renderer;
    private readonly FramePresenter? _presenter;
    private bool _needsLayout = true;
    // true by default so RunFrame() works standalone (headless tests);
    // Quit() flips it, Run() re-arms it
    private bool _running = true;
    private long _lastPresentedCount;
    private int _presentedWindowFrames;
    private double _presentedWindowStart;

    public FrameStats Stats { get; } = new();

    /// <summary>Current back-buffer dimensions (viewport size in cells).</summary>
    public int BackWidth => _back.Width;

    /// <summary>The retained back buffer (test introspection).</summary>
    internal CellBuffer Back => _back;
    public int BackHeight => _back.Height;
    public int TargetFps => _pacer.TargetFps;

    /// <summary>Retarget the frame rate at runtime (e.g. from a pause menu).
    /// Takes effect from the next frame.</summary>
    public void SetTargetFps(int fps) => _pacer.SetTargetFps(fps);

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
        if (_registered.Add(widget))
        {
            widget.Mount(this);
            _widgets.Add(widget);
        }
        return this;
    }

    // ---- overlays (popups/modals) ------------------------------------------

    /// <summary>The modal widget currently on top, if any.</summary>
    public Widget? Overlay => _overlay;
    private Widget? _overlay;

    /// <summary>Show a modal on top of everything. It receives keys first
    /// (HandleKey) and all mouse events; render order puts it last = top.</summary>
    public void PushOverlay(Widget overlay)
    {
        if (_overlay is not null)
            PopOverlay(); // one modal at a time (v1)

        _overlay = overlay;
        AddWidget(overlay);
        ResetHover();
        overlay.Invalidate();
    }

    /// <summary>Close the modal and repaint what it covered.</summary>
    public void PopOverlay()
    {
        if (_overlay is null) return;

        var ov = _overlay;
        _overlay = null;
        _widgets.Remove(ov);
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
        InvalidateAll();
    }

    private Widget? _hovered;

    private Widget? TopMostAt(int x, int y)
    {
        if (_overlay is not null)
            return _overlay;
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

    private void RouteMouse(MouseEvent ev)
    {
        Widget? target = TopMostAt(ev.X, ev.Y);

        if (ev.Kind == MouseKind.Motion && !ReferenceEquals(target, _hovered))
        {
            ResetHover();
            _hovered = target;
            if (target is not null)
            {
                target.Hovered = true;
                target.Invalidate(); // border/color reacts to hover
            }
        }

        if (target is null)
        {
            ReleasePressed();
            return;
        }
        if (target.OnMouse(ev)) return;

        if (ev.Kind == MouseKind.Press && target.Clickable)
        {
            ReleasePressed();
            _pressed = target;
            target.Pressed = true;
            target.Invalidate();
            return;
        }

        if (ev.Kind == MouseKind.Release)
        {
            var clicked = _pressed;
            ReleasePressed();
            if (target.Clickable && ReferenceEquals(clicked, target))
            {
                target.OnClick(new Point(ev.X - target.Rect.X, ev.Y - target.Rect.Y));
                // a click almost always mutates rendered state: repaint the
                // clicked widget immediately, don't wait for hover to change
                target.Invalidate();
            }
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
            // modal overlay gets first chance at keys
            if (_overlay is { } overlayKeys && overlayKeys.HandleKey(keyEvent))
                continue;
            if (OnKey is { } keyHandler && !keyHandler(keyEvent))
                _running = false;
        }

        while (_running && _keyParser.TryReadMouse(out var mouseEvent))
            RouteMouse(mouseEvent);

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
            _back.Resize(w, h, preserve: true);
            _renderer.ResizeToContent(_back); // prev = what the terminal shows

            // NOW erase the overlay's OLD rect: back differs from prev here,
            // so the diff emits real erase bytes. (Clearing before the
            // prev-sync hid the change from the diff → ghost popups.)
            if (_overlay is { } ghost)
                _back.FillRect(ghost.Rect.X, ghost.Rect.Y, ghost.Rect.W, ghost.Rect.H, Cell.Empty);
            _needsLayout = true;
            relayoutThisFrame = true;
            Stats.Relayouts++;
        }

        // 3) animations tick → signals → widgets mark themselves dirty
        _scheduler.Tick();

        // overlay covers the viewport; positioned outside the layout tree
        if (_overlay is { } ov)
            ov.Node.SetRect(new Rect(0, 0, _back.Width, _back.Height));

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
        bool modalOpen = _overlay is not null;

        // z-order: the overlay is the top layer — repaint it LAST EVERY
        // frame, not only when dirty. Otherwise base widgets that render
        // on top of its stale pixels (floating popups over live content)
        // glitch through it. The diff keeps the wire cost at real changes.
        if (_overlay is not null)
            _overlay.Invalidate();

        bool anyRendered = false;
        for (int i = 0; i < _widgets.Count; i++)
        {
            var widget = _widgets[i];
            // a DIMMED modal blocks the base: frozen base keeps the dim
            // layer clean (otherwise ticking widgets repaint over the dim
            // patchwork-style). Floating popups let the background live.
            if (_overlay is { BlocksBaseRendering: true } &&
                !ReferenceEquals(widget, _overlay))
                continue;
            if (widget.Dirty)
            {
                widget.RenderTracked(_back);
                anyRendered = true;
            }
        }

        // 6) diff → damage → serialize → ONE submit. Idle wakeups (nothing
        //    rendered, no relayout) skip the diff and put ZERO bytes on the
        //    wire — a monitor between ticks costs nothing (AGENT.md §2).
        RenderResult result = default;
        if (anyRendered || relayoutThisFrame)
        {
            _output.Clear();
            result = _renderer.Render(_back, _output);
            if (_presenter is { } presenter)
                presenter.Submit(_output.WrittenSpan);
            else
                _terminal.Write(_output.WrittenSpan); // synchronous test mode

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
                    if (ReferenceEquals(w2, _overlay)) continue; // full-screen: would hide gaps
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
            _presentedWindowFrames = 0;
            _presentedWindowStart = nowS;
        }
    }

    private static double NowMs() =>
        System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public void Dispose()
    {
        foreach (var w in _widgets)
            w.Dispose();
        _presenter?.Dispose();
        _back.Dispose();
        _renderer.Dispose();
        _terminal.Dispose();
    }
}
