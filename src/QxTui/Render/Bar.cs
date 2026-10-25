namespace QxTui.Render;

/// <summary>Horizontal fraction bar (progress, utilization, ...).</summary>
public static class Bar
{
    public static void Draw(CellBuffer buf, RenderClip clip, int x, int y, int width,
        double fraction, int fg, char fill = '█', char empty = '░', int emptyFg = 236)
    {
        int filled = Math.Clamp((int)(fraction * width), 0, width);
        for (int i = 0; i < width; i++)
        {
            char c = i < filled ? fill : empty;
            buf.WriteTextClipped(clip, x + i, y,
                System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref c, 1),
                fg: i < filled ? fg : emptyFg);
        }
    }
}
