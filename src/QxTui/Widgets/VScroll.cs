using QxTui.Render;

namespace QxTui.Widgets;

/// <summary>Braille-thumb scrollbar painting shared by the data widgets
/// (Table/TreeView): the same quarter-row visual language as ListView —
/// one texture at every position, sub-cell creep where the terminal
/// reports pixels.</summary>
internal static class VScroll
{
    public static void Draw(CellBuffer buffer, RenderClip clip, int x, int yTop, int trackH,
        int offset, int visible, int total, int accent, int faint)
    {
        if (total <= visible || trackH <= 0 || visible <= 0) return;
        for (int y = yTop; y < yTop + trackH; y++)
            buffer.WriteTextClipped(clip, x, y, "│", fg: faint);

        int thumbH = Math.Max(1, visible * trackH / total);
        double posExact = (double)offset * (trackH - thumbH) / Math.Max(1, total - visible);
        int quarter = (int)Math.Round(posExact * 4);
        int topRow = yTop + quarter / 4;
        int rem = quarter % 4;
        int bottom = yTop + trackH;

        if (rem == 0)
        {
            for (int y = topRow; y < Math.Min(bottom, topRow + thumbH); y++)
                buffer.WriteTextClipped(clip, x, y, ListView.BrailleBand(0, 3).ToString(), fg: accent);
        }
        else
        {
            if (topRow < bottom)
                buffer.WriteTextClipped(clip, x, topRow, ListView.BrailleBand(rem, 3).ToString(), fg: accent);
            for (int y = topRow + 1; y < Math.Min(bottom, topRow + thumbH); y++)
                buffer.WriteTextClipped(clip, x, y, ListView.BrailleBand(0, 3).ToString(), fg: accent);
            int lastRow = topRow + thumbH;
            if (lastRow < bottom)
                buffer.WriteTextClipped(clip, x, lastRow, ListView.BrailleBand(0, rem - 1).ToString(), fg: accent);
        }
    }
}
