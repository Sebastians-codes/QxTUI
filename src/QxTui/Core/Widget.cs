using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;

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

    /// <summary>Called by App exactly when Dirty. Draw within Rect bounds;
    /// CellBuffer clips anything outside.</summary>
    public abstract void Render(CellBuffer buffer);

    internal void RenderTracked(CellBuffer buffer)
    {
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
    }
}
