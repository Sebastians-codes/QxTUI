using System.Buffers;
using QxTui.Animation;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Core;

/// <summary>Posted by <see cref="App.Effect(Func{CancellationToken,Task{object}})"/>
/// when the off-thread work throws: faults arrive as intents, so apps log
/// or retry them in the same update function instead of losing the thread.</summary>
public sealed record EffectFault(Exception Error);

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

    /// <summary>Intents nobody handled (no typed handler, catch-all returned
    /// false, effect faults nobody listened to). Should stay 0: a growing
    /// counter means a component speaks a message the app forgot to learn.</summary>
    public long UnhandledMessages;
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
    private CellBuffer _base;
    private readonly List<CellBuffer> _overlayBufs = []; // parallel to _overlays
    private bool _layersChanged;                         // push/pop/reorder
    private readonly List<Layout.Rect> _blitRects = [];  // compose scratch
    private Renderer _renderer;
    private CellBuffer? _lastSubmitted;
    private CellBuffer? _snapA;
    private CellBuffer? _snapB;
    private long _snapIdA = long.MinValue;
    private long _snapIdB = long.MinValue;
    private long _submitId;
    private int _snapW = -1;
    private int _snapH = -1;
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

    /// <summary>Parsed key callback (games/apps needing arrows etc.).
    /// Return false to quit. The ONLY input hook: intents enter as keys,
    /// mouse events, or posted messages — never as raw bytes.</summary>
    public Func<KeyEvent, bool>? OnKey { get; set; }

    private readonly KeyParser _keyParser = new();

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
        _base = new CellBuffer(Math.Max(1, terminal.Width), Math.Max(1, terminal.Height));
        _snapW = _back.Width;
        _snapH = _back.Height;
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

        // a re-attached subtree (tab round-trip) was ERASED on detach but
        // its widgets may still consider themselves clean — static panels
        // render exactly once, idle buttons only on state change — so
        // without this their erased holes persist forever
        void Dirty(Widget w)
        {
            w.Invalidate();
            for (int i = 0; i < w.Children.Count; i++)
                Dirty(w.Children[i]);
        }
        Dirty(child);
    }

    internal void DetachSubtree(Widget child, bool dispose = true,
        bool erase = true)
    {
        // remember what the subtree painted BEFORE unregistering: those
        // pixels must be erased from the back buffer or they ghost forever
        // (new pages only paint their own rects; containers paint nothing)
        var rects = new List<Layout.Rect>();
        void Collect(Widget w)
        {
            w.CollectPaintedRects(rects);
            for (int i = 0; i < w.Children.Count; i++)
                Collect(w.Children[i]);
        }
        Collect(child);

        void Un(Widget w)
        {
            _registered.Remove(w);
            for (int i = 0; i < w.Children.Count; i++)
                Un(w.Children[i]);
            w.OnDetach();          // lifecycle first: still fully wired here
            if (dispose)
                w.Dispose();
        }

        // focus must not survive detach: keys routed into an off-screen
        // widget get swallowed or mis-handled (the 'press 4, land on
        // another tab' class of weird). Same for an in-flight mouse
        // gesture: a press/resize/hover on a disposed widget would keep
        // routing events into dead state (pop mid-drag).
        bool focusedDetached = false;
        void CheckFocus(Widget w)
        {
            if (ReferenceEquals(Focused, w)) focusedDetached = true;
            if (ReferenceEquals(_pressed, w)) _pressed = null;
            if (ReferenceEquals(_resizing, w)) _resizing = null;
            if (ReferenceEquals(_hovered, w)) _hovered = null;
            for (int i = 0; i < w.Children.Count; i++) CheckFocus(w.Children[i]);
        }
        CheckFocus(child);

        Un(child);
        RebuildFlat();
        _needsLayout = true;
        if (focusedDetached)
            Focus(null);

        if (erase)
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                _base.FillRect(r.X, r.Y, r.W, r.H, Render.Cell.Empty);
            }

        // erasing also destroys pixels of widgets that STAY registered
        // (anything the detached subtree overlapped): they must repaint or
        // the intersection stays a hole
        if (!erase) return;
        for (int i = 0; i < _widgets.Count; i++)
        {
            var w = _widgets[i];
            var wr = w.Rect;
            for (int j = 0; j < rects.Count; j++)
            {
                var r = rects[j];
                if (wr.X < r.Right && r.X < wr.Right && wr.Y < r.Bottom && r.Y < wr.Bottom)
                {
                    w.Invalidate();
                    break;
                }
            }
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
    /// (HandleKey) and all mouse events; render order puts it last = top.
    /// Compositing: the overlay paints into its OWN layer buffer, so the
    /// base scene is never touched and popping is just dropping a layer.</summary>
    public void PushOverlay(Widget overlay)
    {
        _overlays.Add(overlay);
        if (!_roots.Contains(overlay))
            _roots.Add(overlay);   // appended = top of the z-order
        AttachSubtree(overlay);    // subtree too: hosted bodies must mount
        _overlayBufs.Add(new CellBuffer(_back.Width, _back.Height));
        _layersChanged = true;
        ResetHover();
        overlay.Invalidate();
    }

    /// <summary>Close the top modal and repaint what it covered.</summary>
    public void PopOverlay() =>
        PopOverlay(_overlays.Count > 0 ? _overlays[^1] : null!);

    /// <summary>Close a SPECIFIC modal (its own [x] chip): a lower window's
    /// close button must not pop the window above it.</summary>
    public void PopOverlay(Widget ov)
    {
        if (_overlays.Count == 0 || ov is null || !_overlays.Contains(ov))
            return;

        int i = _overlays.IndexOf(ov);
        _overlays.RemoveAt(i);
        _overlayBufs[i].Dispose();
        _overlayBufs.RemoveAt(i);
        _roots.Remove(ov);                // or RebuildFlat walks it back in
        _layersChanged = true;

        // the layer's pixels lived in the layer buffer: dropping the layer
        // IS the undo — no erase, no base repaint, no resync blink, and
        // out-of-order pops cost exactly the same as top pops
        DetachSubtree(ov, dispose: true, erase: false);
        ResetHover();
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
        // top-down, hit-test aware: floating popups only own their box, so
        // a click beside the top window reaches the window underneath
        // (the X of a lower modal closes THAT modal, not the top one)
        for (int i = _overlays.Count - 1; i >= 0; i--)
            if (_overlays[i].HitTest(x, y))
                return _overlays[i];
        for (int i = _widgets.Count - 1; i >= 0; i--)
            if (_widgets[i].HitTest(x, y))
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

    /// <summary>Move the overlay owning `w` (or `w` itself) to top of the
    /// z-stack. The layer buffer travels with it and the next compose
    /// picks up the new order.</summary>
    private void RaiseOverlayOf(Widget w)
    {
        Widget? ov = w;
        while (ov is not null && !IsOverlay(ov))
            ov = ov.Parent;
        if (ov is null || _overlays.Count < 2 || _overlays[^1] == ov)
            return;
        int i = _overlays.IndexOf(ov);
        _overlays.RemoveAt(i);
        _overlays.Add(ov);
        var buf = _overlayBufs[i];
        _overlayBufs.RemoveAt(i);
        _overlayBufs.Add(buf);
        _roots.Remove(ov);
        _roots.Add(ov);
        RebuildFlat();
        _layersChanged = true;
    }

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
    /// <summary>Terminal identity (XTVERSION), null until it answers.</summary>
    public string? TerminalName => _termName;
    private string? _termName;
    private bool _hwScrollUserSet;

    public bool HardwareScroll
    {
        get => _renderer.HardwareScroll;
        set
        {
            _renderer.HardwareScroll = value;
            _hwScrollUserSet = true; // manual toggle beats the caps table
        }
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

        // opt-in outside-press dismissal (OutsideClickCloses, default off):
        // the first press no overlay above claims AND that lands outside an
        // opt-in overlay's box closes it — and is consumed: while a modal
        // is open it owns the screen, the dismissing press is not a click
        // on what sits underneath. A press inside a higher overlay's box
        // belongs to that overlay and dismisses nothing.
        if (ev.Kind == MouseKind.Press)
        {
            for (int i = _overlays.Count - 1; i >= 0; i--)
            {
                var ov = _overlays[i];
                bool owns = ov.OwnsPoint(ev.X, ev.Y);
                if (ov.OutsideClickCloses && !owns)
                {
                    PopOverlay(ov);
                    return;
                }
                if (owns) break;
            }
        }

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

        // right-drag on an element's edge/corner = resize gesture
        if (target is not null && ev.Kind == MouseKind.Press && ev.Button == 2 &&
            target.Resizable && target.ResizeEdgeAt(ev.X, ev.Y) != 0)
        {
            // right-drag anywhere on a component = resize (dx->width,
            // dy->height); overlays qualify too (they are parentless by
            // design, and modals resize their box via ForceW/ForceH)
            _resizing = target;
            target.BeginResize(ev, 3, _cellPixW, _cellPixH);
            return;
        }
        if (_resizing is { } rw)
        {
            if (ev.Kind == MouseKind.Motion)
            {
                rw.ApplyResize(ev, _cellPixW, _cellPixH, () => _needsLayout = true);
                return;
            }
            if (ev.Kind == MouseKind.Release)
            {
                rw.EndResize();
                _resizing = null;
                return;
            }
        }

        // click-to-raise: clicking anywhere in a floating window (or its
        // hosted body) brings it to the top of the stack, like a WM —
        // except presses that close it (the [x] chip): raising on the way
        // out blinks the dying window in front of its neighbours
        if (ev.Kind == MouseKind.Press && target is not null && target.RaisesOnPress(ev))
            RaiseOverlayOf(target);

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
    private Widget? _resizing;

    /// <summary>The box a resize edge visually belongs to: the OUTERMOST
    /// widget whose edge is at the point (stretched children share their
    /// container's edge — grabbing it must resize the container, not the
    /// child). Root is excluded (it is the viewport).</summary>
    private static Widget? ResizeOwnerAt(Widget target, int x, int y, out int edge)
    {
        edge = 0;
        Widget? owner = null;
        for (var w = target; w is not null; w = w.Parent)
        {
            if (!w.Resizable || w.Parent is null) continue;
            int e = w.ResizeEdgeAt(x, y);
            if (e == 0) break;      // edge chain ends here
            owner = w;
            edge = e;
        }
        return owner;
    }

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

    // ---- authoring API: the message channel ---------------------------------

    /// <summary>Register the update function for a message type: the single
    /// place intents become state (Elm-style). One handler per type;
    /// re-registering replaces. Runs on the frame thread, in the input
    /// phase that emitted the message. The returned subscription removes
    /// the handler again — components register in OnMount and dispose in
    /// OnDetach so a detached widget never receives intents.</summary>
    public IDisposable OnMessage<TMsg>(Action<TMsg> update)
    {
        _handlers ??= new Dictionary<Type, Action<object>>();
        Action<object> wrapped = m => update((TMsg)m); // wrap once: sends stay alloc-free
        _handlers[typeof(TMsg)] = wrapped;
        return new HandlerSubscription(() =>
        {
            // remove only if still ours: a later registration replaced it
            if (_handlers is not null &&
                _handlers.TryGetValue(typeof(TMsg), out var current) &&
                ReferenceEquals(current, wrapped))
                _handlers.Remove(typeof(TMsg));
        });
    }

    private sealed class HandlerSubscription(Action remove) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) remove();
        }
    }

    /// <summary>Catch-all update for switch-style apps: runs only when no
    /// typed handler matched. Return false to leave the intent unhandled
    /// (it lands in <see cref="FrameStats.UnhandledMessages"/>).</summary>
    public void Update(Func<object, bool> update) => _update = update;

    /// <summary>Emit an intent from the composition root (key handlers,
    /// startup sequencing). Widgets use the protected Widget.Send.
    /// Returns whether some handler consumed it.</summary>
    public bool Send<TMsg>(TMsg msg) => Dispatch(msg);

    /// <summary>Thread-safe ingress: queue an intent from ANY thread
    /// (background effects, OS callbacks). Drained in the input phase of
    /// the next frame — the only safe way into frame-thread state from
    /// off-thread. This is what effects post their results to.</summary>
    public void Post<TMsg>(TMsg msg) => _inbox.Enqueue(msg!);

    /// <summary>Run work OFF the frame thread (AGENT.md §2: the loop must
    /// never block). The returned message — if any — is posted back
    /// through the thread-safe inbox and arrives like any other intent.
    /// Exceptions become <see cref="EffectFault"/> messages instead of
    /// killing the loop.</summary>
    public void Effect(Func<CancellationToken, Task<object?>> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var msg = await work(_effectCts.Token);
                if (msg is not null) Post(msg);
            }
            catch (OperationCanceledException) { } // app shutting down
            catch (Exception ex)
            {
                Post(new EffectFault(ex));
            }
        });
    }

    /// <summary>Fire-and-forget variant for effects with no result message.</summary>
    public void Effect(Func<CancellationToken, Task> work) =>
        Effect(async ct => { await work(ct); return null; });

    internal bool Dispatch(object? msg)
    {
        if (msg is null) return false;
        bool handled = false;
        if (_handlers is not null && _handlers.TryGetValue(msg.GetType(), out var update))
        {
            update(msg);
            handled = true;
        }
        else if (_update is not null)
            handled = _update(msg);
        if (!handled)
        {
            Stats.UnhandledMessages++;
            TraceLine($"unhandled intent: {msg.GetType().Name}");
        }
        return handled;
    }

    private Dictionary<Type, Action<object>>? _handlers;
    private Func<object, bool>? _update;
    private readonly System.Collections.Concurrent.ConcurrentQueue<object> _inbox = new();
    private readonly CancellationTokenSource _effectCts = new();

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
        while (_terminal.TryReadInput(out byte b))
            _keyParser.Feed(b);

        // 1b) thread-safe ingress: intents posted from effects / background
        //     threads join the same vocabulary as keys and clicks, in the
        //     same phase — frame-thread state stays single-threaded
        while (_inbox.TryDequeue(out var posted))
            Dispatch(posted);

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

        if (_termName is null && _keyParser.TerminalName is { } name)
        {
            _termName = name;
            if (!_hwScrollUserSet)
                _renderer.HardwareScroll = Terminal.TerminalCaps.HardwareScrollSafe(name);
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
            _base.Resize(w, h, preserve: false);
            for (int i = 0; i < _overlayBufs.Count; i++)
                _overlayBufs[i].Resize(w, h, preserve: false);
            _lastSubmitted?.Dispose();
            _lastSubmitted = null; // size changes; recreated on next submit
            _snapA?.Dispose(); _snapA = null;
            _snapB?.Dispose(); _snapB = null;
            _snapIdA = _snapIdB = long.MinValue;
            _snapW = w; _snapH = h;
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

                var prevRect = widget.Node.PreviousRect;
                if (!prevRect.Equals(default(Rect)) && !prevRect.Equals(widget.Rect))
                {
                    // vacated cells MUST be erased from the BASE layer:
                    // they can sit inside a clean neighbour whose last
                    // paint predates the write that ghosted them. Erasing
                    // + dirtying the touched widgets keeps erase and
                    // repaint in the same frame (no blink)
                    _base.FillRect(prevRect.X, prevRect.Y, prevRect.W, prevRect.H, Cell.Empty);
                    if (Trace)
                        _traceErases.Add($"{widget.GetType().Name}:{prevRect}");
                    for (int k = 0; k < _widgets.Count; k++)
                    {
                        var wk = _widgets[k];
                        var wr = wk.Rect;
                        if (wr.X < prevRect.Right && prevRect.X < wr.Right &&
                            wr.Y < prevRect.Bottom && prevRect.Y < wr.Bottom)
                        {
                            wk.Invalidate();
                            wk.ForceRenderNext(); // no cap-interval blank
                        }
                    }
                }
                widget.Invalidate();
                widget.ForceRenderNext(); // moved widgets repaint NOW, cap or not
            }

            // Clear everything no widget covers anymore (gap rows, vacated
            // regions after resize). Without this, stale pixels survive
            // forever because nothing is dirty there. Cold path: fine.
            ClearUncovered();

            _needsLayout = false;
            relayoutThisFrame = true;
        }

        // 5) retained render: dirty widgets paint into THEIR LAYER's buffer
        //    (base scene, or the owning overlay's own buffer). Layers never
        //    share pixels, so nothing any widget does can ghost another —
        //    z-order is resolved once, at compose time, below.
        bool modalOpen = false;
        for (int oi = 0; oi < _overlays.Count; oi++)
            if (_overlays[oi].BlocksBaseRendering) modalOpen = true;

        bool anyRendered = false;
        CellBuffer layer = _base;
        for (int i = 0; i < _widgets.Count; i++)
        {
            var widget = _widgets[i];
            // pre-order: a root switches the layer, children inherit it
            if (widget.Parent is null)
            {
                int z = _overlays.IndexOf(widget);
                layer = z >= 0 ? _overlayBufs[z] : _base;
            }
            // a DIMMED modal freezes the base layer: its ticks would cost
            // renders nobody can see. Floating popups let the base live.
            if (modalOpen && ReferenceEquals(layer, _base))
                continue;
            if (widget.Dirty)
            {
                if (widget.RenderTracked(layer))
                {
                    anyRendered = true;
                    if (Trace)
                        _traceRendered.Add($"{widget.GetType().Name}:{widget.Rect}");

                    // a parent repainting its full rect erases its children's
                    // pixels: children must repaint in the same frame (they come
                    // later in pre-order), or they'd blink/vanish
                    for (int c = 0; c < widget.Children.Count; c++)
                        if (!widget.Children[c].SurvivesParentRepaint)
                            widget.Children[c].Invalidate();
                }
            }
        }

        // 5b) compose: base first, then each overlay blitted over the rect
        //     it actually paints (its box, or the screen when dimming).
        //     A moved/resized/popped overlay needs no erase and no base
        //     repaint — the next compose simply blits the new state.
        bool composed = anyRendered || _layersChanged || relayoutThisFrame;
        if (composed)
        {
            _base.Cells.CopyTo(_back.Cells);
            for (int oi = 0; oi < _overlays.Count; oi++)
            {
                _blitRects.Clear();
                _overlays[oi].CollectPaintedRects(_blitRects);
                var src = _overlayBufs[oi];
                for (int r = 0; r < _blitRects.Count; r++)
                {
                    var br = _blitRects[r];
                    _back.BlitFrom(src, br.X, br.Y, br.W, br.H);
                }
            }
            _layersChanged = false;
        }

        // 5c) lifecycle: report rect changes (terminal resize, relayout,
        //     modal body relayout). Fires only on frames where a rect
        //     actually moved — discrete, like the message channel.
        for (int i = 0; i < _widgets.Count; i++)
            _widgets[i].FireRectHook();

        // 6) diff → damage → serialize → ONE submit. Idle wakeups (nothing
        //    rendered, no relayout, no layer change) skip the diff AND the
        //    compose and put ZERO bytes on the wire (AGENT.md §2).
        RenderResult result = default;
        if (composed)
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
            if (dropped && _presenter is not null)
            {
                // rebase on the frame the writer ACTUALLY wrote: a dropped
                // submit's snapshot is content the terminal never saw, and
                // diffing against it freezes holes forever
                var written = _presenter.WrittenId;
                var snap = written == _snapIdA ? _snapA
                           : written == _snapIdB ? _snapB
                           : _lastSubmitted;
                if (snap is not null)
                    _renderer.AdoptPrev(snap);
            }

            _output.Clear();
            result = _renderer.Render(_back, _output, forceFull: false);
            long frameId = ++_submitId;
            if (_presenter is { } presenter)
            {
                presenter.Submit(_output.WrittenSpan, frameId);
            }
            else
            {
                _terminal.Write(_output.WrittenSpan); // synchronous test mode
            }
            _renderedWindowFrames++;

            if (_back.Width == _snapW && _back.Height == _snapH)
            {
                // two-slot ring: {the written frame, the latest frame}.
                // The written slot is PROTECTED — overwriting it would
                // leave the drop-repair without the terminal's true base
                // and freeze holes again (the id%2 version did exactly
                // that once submits outran writes).
                bool useA = UseSlotA(_snapIdA, _snapIdB,
                                     _presenter?.WrittenId ?? long.MinValue);
                if (useA)
                {
                    _snapA ??= new CellBuffer(_back.Width, _back.Height);
                    _back.Cells.CopyTo(_snapA.Cells);
                    _snapIdA = frameId;
                    _lastSubmitted = _snapA;
                }
                else
                {
                    _snapB ??= new CellBuffer(_back.Width, _back.Height);
                    _back.Cells.CopyTo(_snapB.Cells);
                    _snapIdB = frameId;
                    _lastSubmitted = _snapB;
                }
            }

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
        TraceFrame(relayoutThisFrame);
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
    // ---- debug ledger (QXTUI_TRACE=1): one line per frame with relayout,
    // prev-rect erases, uncovered clears, rendered widgets, and whether the
    // back buffer still holds a probe string. For hunting bake-in frames.
    internal static readonly bool Trace =
        Environment.GetEnvironmentVariable("QXTUI_TRACE") == "1";
    private static System.IO.StreamWriter? _traceWriter;
    private static System.IO.StreamWriter TraceWriter =>
        _traceWriter ??= new System.IO.StreamWriter(
            Environment.GetEnvironmentVariable("QXTUI_TRACE_FILE")
                ?? "/tmp/qxtui_trace.log", append: false);
    private int _traceFrame;
    private readonly List<string> _traceErases = new();
    private readonly List<string> _traceRendered = new();
    private int _traceUncovered;

    private void TraceFrame(bool relayout)
    {
        if (!Trace) return;
        bool probe = BackContains("damage isolation");
        var w = TraceWriter;
        w.WriteLine($"f{_traceFrame} relayout={relayout} uncovered={_traceUncovered} " +
                    $"probe={probe} erases=[{string.Join(",", _traceErases)}] " +
                    $"rendered=[{string.Join(",", _traceRendered)}]");
        w.Flush();
        _traceErases.Clear();
        _traceRendered.Clear();
        _traceUncovered = 0;
        _traceFrame++;
    }

    /// <summary>Slot choice for the next snapshot: never overwrite the
    /// snapshot the terminal is actually showing; otherwise the older one.</summary>
    internal static bool UseSlotA(long idA, long idB, long written)
    {
        if (idA == written) return false;
        if (idB == written) return true;
        return idA <= idB; // neither protected: recycle the older slot
    }

    internal static void TraceLine(string line)
    {
        if (!Trace) return;
        TraceWriter.WriteLine(line);
        TraceWriter.Flush();
    }

    private bool BackContains(string text)
    {
        var cells = _back.Cells;
        int ni = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            ni = cells[i].Char == text[ni] ? ni + 1 : 0;
            if (ni == text.Length) return true;
        }
        return false;
    }

    private void ClearUncovered()
    {
        if (_widgets.Count == 0)
        {
            _base.Clear();
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
                {
                    _base[x, y] = Cell.Empty;
                    _traceUncovered++;
                }
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
        _effectCts.Cancel();
        _effectCts.Dispose();
        foreach (var w in _roots)
            w.Dispose(); // cascades to children
        for (int oi = 0; oi < _overlays.Count; oi++)
            _overlays[oi].Dispose();
        _presenter?.Dispose();
        _back.Dispose();
        _base.Dispose();
        for (int i = 0; i < _overlayBufs.Count; i++)
            _overlayBufs[i].Dispose();
        _overlayBufs.Clear();
        _lastSubmitted?.Dispose();
        _snapA?.Dispose();
        _snapB?.Dispose();
        _renderer.Dispose();
        _terminal.Dispose();
    }
}
