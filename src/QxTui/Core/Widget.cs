using QxTui.Animation;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Core;

/// <summary>
/// A retained UI component (AGENT.md §1: retained scene tree, NOT immediate
/// mode). Owns a layout node, a dirty flag and a render counter.
///
/// Update isolation contract: a widget re-renders ONLY when
///  - it was explicitly invalidated (typically via a Signal subscription), or
///  - its layout rect changed during a re-layout.
/// RenderCount exists so tests can PROVE the isolation invariants (§3).
///
/// Ergonomics: declare <see cref="Tick"/> and <see cref="OnSignal"/> in your
/// constructor; they are materialized at mount time (App.AddWidget) against
/// the app's single scheduler, and disposed automatically with the widget.
/// No more manual subscription bookkeeping in samples.
/// </summary>
public abstract class Widget
{
    public LayoutNode Node { get; } = new();

    public Widget()
    {
        // lets App.SetRoot discover widgets straight from the layout tree —
        // composing a widget into the DSL is enough to render it
        Node.Tag = this;
    }

    /// <summary>Widgets drop straight into the layout DSL:
    /// UI.Row(gap: 1, cpuWidget, memWidget) — no .Node needed.</summary>
    public static implicit operator LayoutNode(Widget widget) => widget.Node;

    public bool Dirty { get; private set; } = true;

    /// <summary>Number of times Render was actually invoked. Test observable.</summary>
    public long RenderCount { get; private set; }

    public Rect Rect => Node.Rect;

    /// <summary>Clip region for painting: widgets must never draw outside it.
    /// Overridable: floating overlays clip to their box so lower windows
    /// can't paint into a higher window's territory (z-correctness without
    /// full-scene recomposition).</summary>
    public virtual RenderClip Clip => new(Rect.X, Rect.Y, Rect.W, Rect.H);

    /// <summary>Rects this widget actually PAINTS (for erase-on-detach).
    /// Default: the whole rect; floating modals paint only their box.</summary>
    internal virtual void CollectPaintedRects(List<Rect> dst) => dst.Add(Rect);

    /// <summary>The app this widget is mounted in; null before AddWidget.</summary>
    public App? App { get; private set; }

    /// <summary>Theme of the mounted app (or the default before mount).</summary>
    public Theme Theme => App?.Theme ?? Core.Theme.Default;

    public bool Mounted => App is not null;

    private List<Action<App>>? _mountActions;
    private List<IDisposable>? _lifetime;
    private List<Widget>? _children;

    /// <summary>Parent in the widget tree; null for roots.</summary>
    public Widget? Parent { get; private set; }

    public IReadOnlyList<Widget> Children => _children ??= [];

    /// <summary>
    /// Attach a child: its layout node nests under this widget's node, it
    /// mounts immediately if this widget is mounted, and it renders AFTER
    /// this widget (children paint on top of their parent).
    /// </summary>
    public Widget AddChild(Widget child)
    {
        if (child.Parent is not null)
            throw new InvalidOperationException("Widget already has a parent.");

        (_children ??= []).Add(child);
        child.Parent = this;
        Node.Add(child.Node);
        App?.AttachSubtree(child);
        return child;
    }

    /// <summary>Detach a subtree. dispose:false keeps it alive for
    /// re-attachment (router pages cache their state).</summary>
    public bool RemoveChild(Widget child, bool dispose = true)
    {
        if (_children is null || !_children.Remove(child))
            return false;

        child.Parent = null;
        Node.Remove(child.Node);
        App?.DetachSubtree(child, dispose);
        return true;
    }

    /// <summary>
    /// Declare a periodic invalidation (or callback) on the app's single
    /// scheduler clock. Safe to call in the constructor — materializes at
    /// mount, auto-disposes with the widget.
    /// </summary>
    protected void Tick(TimeSpan interval, Action<long>? callback = null)
    {
        Action<App> mount = app =>
            AddLifetime(app.Scheduler.Every(interval, callback ?? (_ => Invalidate())));

        if (App is { } live) mount(live);
        else (_mountActions ??= []).Add(mount);
    }

    /// <summary>
    /// Declare a signal subscription that invalidates (or runs your callback)
    /// on change. Materializes at mount, auto-disposes with the widget.
    /// </summary>
    protected void OnSignal<T>(Signal<T> signal, Action<T>? callback = null)
    {
        Action<App> mount = _ =>
            AddLifetime(signal.Subscribe(callback ?? (_ => Invalidate())));

        if (App is { } live) mount(live);
        else (_mountActions ??= []).Add(mount);
    }

    private void AddLifetime(IDisposable d) => (_lifetime ??= []).Add(d);

    internal void Mount(App app)
    {
        App = app;
        if (_mountActions is { Count: > 0 })
        {
            foreach (var mount in _mountActions)
                mount(app);
            _mountActions = null;
        }
    }

    /// <summary>Mark for re-render on the next frame. Called by signal
    /// subscriptions, animations, layout changes.</summary>
    public void Invalidate() => Dirty = true;

    // ---- interaction -------------------------------------------------------

    /// <summary>Receive click callbacks (OnMouse release inside Rect).</summary>
    public bool Clickable { get; set; }

    /// <summary>Can receive keyboard focus (Tab cycle, click-to-focus,
    /// App.Focus). Focused widgets get keys before App.OnKey.</summary>
    public bool Focusable { get; set; }

    public bool Focused => App?.Focused == this;

    /// <summary>True while the pointer is over this widget (motion events
    /// must be enabled; the App maintains this and invalidates on change).</summary>
    public bool Hovered { get; internal set; }

    /// <summary>True between mouse press and release on this widget —
    /// the momentary "pushed down" state. Maintained by the App.</summary>
    public bool Pressed { get; internal set; }

    /// <summary>While pressed, receive ALL mouse events even outside the
    /// rect (slider drags).</summary>
    public bool CapturesMouse { get; set; }

    /// <summary>Widget asked to invalidate on EVERY produced frame — its
    /// refresh rate follows the app's fps target instead of a fixed timer.</summary>
    internal bool PerFrame { get; private set; }

    /// <summary>Refresh in lockstep with the frame loop (whatever the target
    /// fps is). For sim-coupled widgets like game balls or particles.</summary>
    protected void TickPerFrame() => PerFrame = true;

    private TimeSpan _flashUntil;

    /// <summary>True while a press flash is held. Real clicks press+release
    /// within one frame batch, so without a held flash the pushed-down look
    /// would never be seen — only a label blip.</summary>
    public bool FlashActive => App is not null && App.Scheduler.Time.Now < _flashUntil;

    /// <summary>Hold the pressed look for <paramref name="ms"/> after a
    /// click, then repaint back to idle (one-shot animation on the app
    /// clock — virtual-time testable).</summary>
    public void FlashPressed(int ms = 120)
    {
        if (App is null) return;
        _flashUntil = App.Scheduler.Time.Now + TimeSpan.FromMilliseconds(ms);
        Invalidate();
        App.Scheduler.Animate(0f, 1f,
            new Transition(TimeSpan.FromMilliseconds(ms)), _ => Invalidate());
    }

    /// <summary>While this widget is the open overlay: freeze base paints
    /// (dimmed modals need it to keep the dim clean). Floating popups set
    /// false so the background stays live behind them.</summary>
    public bool BlocksBaseRendering { get; set; }

    /// <summary>Opt-in dismissal (default FALSE): while this overlay is
    /// open, any mouse press that lands outside its box — and outside
    /// every overlay above it — closes it, and that press is consumed
    /// (while a modal is open it owns the screen: the dismissing press is
    /// not a click on what sits underneath). Authors opt in per window.</summary>
    public bool OutsideClickCloses { get; set; }

    /// <summary>The region a press must land in to count as "inside" this
    /// widget for <see cref="OutsideClickCloses"/>: modals use their BOX in
    /// both modes — a press on the dim layer is OUTSIDE.</summary>
    internal virtual bool OwnsPoint(int x, int y) => Rect.Contains(x, y);

    /// <summary>Raw mouse events hitting this widget's rect. Return true to
    /// consume (stop routing to widgets below).</summary>
    public virtual bool OnMouse(MouseEvent ev) => false;

    /// <summary>Click at widget-local coordinates (Clickable must be true).</summary>
    public virtual void OnClick(Point local) { }

    /// <summary>First chance at key input (overlays use this for modality).
    /// Return true to consume.</summary>
    public virtual bool HandleKey(KeyEvent ev) => false;

    private HalfBlockSurface? _pixels;
    private Rect _pixelsRect;

    /// <summary>Cached square-pixel canvas for this widget, rebuilt when the
    /// rect changes (the rebuild flag is your cue to clear trails/state in
    /// the old coordinate space).</summary>
    protected (HalfBlockSurface Surface, bool Rebuilt) Pixels(Rect r, Cell background)
    {
        int pw = Math.Max(1, r.W - 2);
        int ph = Math.Max(2, (r.H - 2) * 2);
        bool rebuilt = _pixels is null || _pixelsRect != r;
        if (rebuilt)
        {
            _pixels = new HalfBlockSurface(_lastBuffer!, r.X + 1, r.Y + 1, pw, ph, background);
            _pixelsRect = r;
        }
        return (_pixels!, rebuilt);
    }

    private CellBuffer? _lastBuffer;

    /// <summary>Called by App exactly when Dirty. Draw within Rect bounds;
    /// CellBuffer clips anything outside.</summary>
    public abstract void Render(CellBuffer buffer);

    /// <summary>Per-component repaint cap in Hz (0 = uncapped). A dirty
    /// widget whose last paint is newer than 1/Hz stays dirty and paints
    /// on the next eligible frame — deferred, never dropped. Lets a HUD
    /// tick at 5Hz while a ball animates at 120Hz in the same scene.</summary>
    /// <summary>OPT-IN: right-click-drag resizes the element (grab-side
    /// edge follows the cursor; flow siblings absorb the delta). Default
    /// false — a component is resizable only when its author says so.
    /// ModalWidget opts in at class level (windows are resizable).</summary>
    public bool Resizable { get; set; }

    private bool _resizing;
    private bool _rsApplied;
    private int _rsEdges;           // 1 = right, 2 = bottom, 3 = corner
    private int _rsStartX, _rsStartY, _rsStartW, _rsStartH;
    private int _rsCurW, _rsCurH;
    private int _rsPrevX, _rsPrevY;
    private int _rsSignX = 1, _rsSignY = 1;
    private int _rsCenterX, _rsCenterY;
    private double _rsCorrW, _rsCorrH;

    /// <summary>Bypass MaxRenderHz for the next frame: a relayout that
    /// moved this widget must not wait out a cap interval (blink).</summary>
    internal void ForceRenderNext() => _lastRenderTs = 0;

    /// <summary>Resize grab zones: ANYWHERE inside the element (right-drag
    /// resizes both axes by the cursor delta — thin edges are ungrabbable
    /// and users think in components, not border pixels).</summary>
    internal virtual int ResizeEdgeAt(int x, int y) =>
        Rect.Contains(x, y) ? 3 : 0;

    internal void BeginResize(MouseEvent ev, int edges, int cellW, int cellH)
    {
        _resizing = true;
        _rsEdges = edges;
        _rsStartX = ev.Px >= 0 && cellW > 0 ? ev.Px : ev.X;
        _rsStartY = ev.Py >= 0 && cellH > 0 ? ev.Py : ev.Y;
        (_rsStartW, _rsStartH) = ResizeBaseSize();
        _rsCurW = _rsStartW;
        _rsCurH = _rsStartH;
        _rsApplied = false;
        int gx = ev.Px >= 0 && cellW > 0 ? ev.Px / cellW : ev.X;
        int gy = ev.Py >= 0 && cellH > 0 ? ev.Py / cellH : ev.Y;
        _rsPrevX = gx;
        _rsPrevY = gy;
        if (CenterMirroredResize)
        {
            // center-mirrored absolute (free-floating windows): size =
            // 2 x distance(cursor, center) with a grab correction so the
            // size at grab time is unchanged; the edge on the cursor's
            // side tracks it 1:1, the opposite edge mirrors
            var (ccx, ccy) = ResizeCenter();
            _rsCenterX = ccx;
            _rsCenterY = ccy;
            _rsCorrW = _rsStartW / 2.0 - Math.Abs(gx - ccx);
            _rsCorrH = _rsStartH / 2.0 - Math.Abs(gy - ccy);
        }
        // pin the CURRENT size before dropping grow: zeroing grow alone
        // collapsed the element to its intrinsic size on press and made
        // every sibling jump — the 'surroundings move too much' feel
        Node.Width = Rect.W;
        Node.Height = Rect.H;
        Node.Grow = 0f; // manual size wins from here on
        // flow widgets: the edge nearest the grab follows the cursor —
        // grab the top half and drag down = push that edge down (shrink)
        if (!CenterMirroredResize)
        {
            var (ccx, ccy) = ResizeCenter();
            _rsSignX = gx >= ccx ? 1 : -1;
            _rsSignY = gy >= ccy ? 1 : -1;
        }
    }

    /// <summary>Resize math: flow-layout components use plain cursor
    /// deltas (drag right = wider, left = narrower); free-floating
    /// windows (modals) use the center-mirrored absolute form.</summary>
    internal virtual bool CenterMirroredResize => false;

    /// <summary>Mirror axis for resize: the element's center (modals use
    /// their box center — their Rect is the fullscreen overlay).</summary>
    internal virtual (int X, int Y) ResizeCenter() =>
        (Rect.X + Rect.W / 2, Rect.Y + Rect.H / 2);


    /// <summary>The size a resize grab starts from: the rect for normal
    /// widgets, the box for modals.</summary>
    internal virtual (int W, int H) ResizeBaseSize() => (Rect.W, Rect.H);

    /// <summary>Where a resized size lands: flex sizes for normal widgets,
    /// forced box sizes for modals.</summary>
    internal virtual void ApplyResizeSize(int w, int h)
    {
        int dW = w - Rect.W;
        int dH = h - Rect.H;
        Node.Width = w;
        Node.Height = h;
        AbsorbDelta(Node, dW, dH);
    }

    /// <summary>Split-pane absorption: a resize moves the grabbed element
    /// and as little else as possible — the immediate fixed sibling takes
    /// the main-axis delta (grow siblings let flex handle it), and fixed
    /// ancestors grow ONLY when the child no longer fits.</summary>
    private static void AbsorbDelta(LayoutNode node, int dW, int dH)
    {
        var p = node.Parent;
        if (p is null) return;
        bool row = p.Direction == FlexDirection.Row;
        int dMain = row ? dW : dH;
        int dCross = row ? dH : dW;

        if (dMain != 0)
        {
            var sib = p.NextSiblingOf(node) ?? p.PrevSiblingOf(node);
            if (sib is not null)
            {
                if (row && sib.Width is int sw) sib.Width = Math.Max(1, sw - dMain);
                else if (!row && sib.Height is int sh) sib.Height = Math.Max(1, sh - dMain);
            }
        }

        if (dCross != 0)
        {
            int? cp = row ? p.Height : p.Width;
            if (cp is int cv)
            {
                if (row) p.Height = Math.Max(1, cv + dCross);
                else p.Width = Math.Max(1, cv + dCross);
                AbsorbDelta(p, row ? 0 : dCross, row ? dCross : 0);
            }
        }
    }

    internal bool ApplyResize(MouseEvent ev, int cellW, int cellH,
        Action layoutDirty)
    {
        if (!_resizing) return false;
        int cx = ev.Px >= 0 && cellW > 0 ? ev.Px / cellW : ev.X;
        int cy = ev.Py >= 0 && cellH > 0 ? ev.Py / cellH : ev.Y;
        int w, h;
        if (CenterMirroredResize)
        {
            double halfW = Math.Abs(cx - _rsCenterX) + _rsCorrW;
            double halfH = Math.Abs(cy - _rsCenterY) + _rsCorrH;
            w = Math.Max(1, (int)Math.Round(2 * halfW));
            h = Math.Max(1, (int)Math.Round(2 * halfH));
        }
        else
        {
            w = Math.Max(1, _rsCurW + _rsSignX * (cx - _rsPrevX));
            h = Math.Max(1, _rsCurH + _rsSignY * (cy - _rsPrevY));
        }
        _rsPrevX = cx;
        _rsPrevY = cy;
        if (w == _rsCurW && h == _rsCurH && _rsApplied) return false;
        _rsCurW = w;
        _rsCurH = h;
        _rsApplied = true;
        ApplyResizeSize(w, h);
        layoutDirty();
        return true;
    }

    internal void EndResize() => _resizing = false;

    /// <summary>Mouse hit-test. Overlays override this: a floating popup
    /// only owns clicks inside its box, so clicks beside it reach the
    /// window underneath instead of being swallowed by the top layer.</summary>
    public virtual bool HitTest(int x, int y) => Rect.Contains(x, y);

    /// <summary>Click-to-raise gate (WM feel). A press that will CLOSE the
    /// window must not raise it first: the raise shows as a one-frame
    /// blink where the dying window jumps in front of its neighbours on
    /// its way out.</summary>
    internal virtual bool RaisesOnPress(MouseEvent ev) => true;

    /// <summary>Set when this widget's pixels SURVIVE its parent's repaint
    /// (the parent paints around them): the parent-repaint cascade then
    /// leaves it alone and its own tick rate governs its refresh —
    /// independent refresh windows inside a live parent.</summary>
    public bool SurvivesParentRepaint { get; set; }

    public double MaxRenderHz { get; set; }
    private long _lastRenderTs;

    internal bool RenderTracked(CellBuffer buffer)
    {
        if (MaxRenderHz > 0)
        {
            long now = Environment.TickCount64;
            // double math: long elapsed * Hz overflowed on the first check
            // (elapsed ~ 4.6e18 from a MinValue sentinel) and deferred
            // capped widgets FOREVER
            if ((now - _lastRenderTs) * MaxRenderHz < 1000.0)
                return false; // too soon: stay dirty, skip this frame
            _lastRenderTs = now;
        }
        _lastBuffer = buffer;
        Render(buffer);
        RenderCount++;
        Dirty = false;
        return true;
    }

    /// <summary>Releases ticks/subscriptions declared via Tick/OnSignal.
    /// Override for extra cleanup, calling base.</summary>
    public virtual void Dispose()
    {
        if (_lifetime is not null)
        {
            foreach (var d in _lifetime)
                d.Dispose();
            _lifetime = null;
        }

        if (_children is not null)
        {
            foreach (var child in _children)
                child.Dispose();
        }
    }
}
