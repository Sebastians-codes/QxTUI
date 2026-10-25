namespace QxTui.Render;

/// <summary>Box-drawing glyph set.</summary>
public readonly record struct BoxStyle(
    char Horizontal,
    char Vertical,
    char TopLeft,
    char TopRight,
    char BottomLeft,
    char BottomRight);

public static class BoxStyles
{
    public static readonly BoxStyle Single = new('─', '│', '┌', '┐', '└', '┘');
    public static readonly BoxStyle Double = new('═', '║', '╔', '╗', '╚', '╝');
    public static readonly BoxStyle Rounded = new('─', '│', '╭', '╮', '╰', '╯');
    public static readonly BoxStyle Heavy = new('━', '┃', '┏', '┓', '┗', '┛');
}

/// <summary>
/// Framework-level box drawing (borders were hand-rolled in every sample
/// until this existed). Clips to buffer bounds, degenerates to a no-op
/// below 2x2, and optionally insets a title into the top edge.
/// Pure buffer operation — no layout, no I/O (AGENT.md §2 layer rules).
/// </summary>
public static class Box
{
    public static void Draw(CellBuffer buf, int x, int y, int w, int h,
        in BoxStyle style, int fg = 238,
        string? title = null, int titleFg = -1, bool titleBold = true)
    {
        if (w < 2 || h < 2) return;

        var hCell = new Cell { Char = style.Horizontal, Fg = fg, Bg = -1 };
        var vCell = new Cell { Char = style.Vertical, Fg = fg, Bg = -1 };

        for (int cx = x; cx < x + w; cx++)
        {
            Set(buf, cx, y, hCell);
            Set(buf, cx, y + h - 1, hCell);
        }
        for (int cy = y; cy < y + h; cy++)
        {
            Set(buf, x, cy, vCell);
            Set(buf, x + w - 1, cy, vCell);
        }

        Set(buf, x, y, new Cell { Char = style.TopLeft, Fg = fg, Bg = -1 });
        Set(buf, x + w - 1, y, new Cell { Char = style.TopRight, Fg = fg, Bg = -1 });
        Set(buf, x, y + h - 1, new Cell { Char = style.BottomLeft, Fg = fg, Bg = -1 });
        Set(buf, x + w - 1, y + h - 1, new Cell { Char = style.BottomRight, Fg = fg, Bg = -1 });

        if (title is { Length: > 0 } && w > title.Length + 4)
        {
            buf.WriteTextClipped(new RenderClip(x, y, w, 1), x + 2, y,
                $" {title} ", fg: titleFg >= 0 ? titleFg : fg, bg: -1, bold: titleBold);
        }
    }

    private static void Set(CellBuffer buf, int x, int y, Cell cell)
    {
        if (buf.InBounds(x, y))
            buf[x, y] = cell;
    }
}
