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

    /// <summary>Clip region for painting: widgets must never draw outside it.</summary>
    public RenderClip Clip => new(Rect.X, Rect.Y, Rect.W, Rect.H);

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

    internal void RenderTracked(CellBuffer buffer)
    {
        _lastBuffer = buffer;
        Render(buffer);
        RenderCount++;
        Dirty = false;
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
