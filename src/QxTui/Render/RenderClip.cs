namespace QxTui.Render;

/// <summary>
/// Clip region for widget painting. Render-layer primitive (no Layout
/// dependency — AGENT.md §2 layer rule). Widgets convert their layout Rect
/// into a RenderClip before painting text.
/// </summary>
public readonly record struct RenderClip(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
}
