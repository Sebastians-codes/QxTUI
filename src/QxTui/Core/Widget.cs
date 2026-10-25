using QxTui.Layout;
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

    /// <summary>Hook for cleanup (cancel animations, dispose subscriptions).</summary>
    public virtual void Dispose() { }
}
