namespace QxTui.Render;

/// <summary>
/// 8-level block sparkline (▁▂▃▄▅▆▇█), right-aligned in its box.
/// Fixed scale (scaleMax) for percent-like series; auto-scale with a floor
/// for unbounded series; phase for sub-cell scrolling.
/// </summary>
public static class SparklinePainter
{
    private const string Levels = "▁▂▃▄▅▆▇█";

    /// <param name="scaleMax">Fixed full-scale value (e.g. 100). 0 = auto.</param>
    /// <param name="autoFloor">Minimum full-scale when auto-scaling.</param>
    /// <param name="phase">0..1 sub-cell scroll offset.</param>
    public static void Draw(CellBuffer buf, RenderClip clip, int x, int y, int width,
        RingHistory history, int fg, float scaleMax = 0f, float autoFloor = 1f, float phase = 0f)
    {
        int n = Math.Min(width, history.Count);
        if (n <= 0) return;

        float max = scaleMax > 0 ? scaleMax : history.Max(autoFloor);
        int start = x + width - n;

        for (int i = 0; i < n; i++)
        {
            float idx = history.Count - n + i + phase;
            float v = history.AtInterp(Math.Min(idx, history.Count - 1));
            int level = Math.Clamp((int)(v / max * 7.999f), 0, 7);
            buf.WriteTextClipped(clip, start + i, y, Levels.AsSpan(level, 1), fg: fg);
        }
    }
}
