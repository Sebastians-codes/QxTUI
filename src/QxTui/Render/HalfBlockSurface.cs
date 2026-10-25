namespace QxTui.Render;

/// <summary>
/// Square-pixel canvas over a CellBuffer using half-blocks (▀ ▄ █).
///
/// Geometry: a terminal cell is ~2:1 (twice as tall as wide). The square
/// pixel on such a cell is therefore FULL width × HALF height — one cell
/// holds 1x2 square pixels. (Quadrant 2x2 splits are a trap: they halve
/// both axes and keep the 1:2 aspect, making vertical motion look 2x
/// faster — exactly the bug this replaces.)
///
/// Coloring is exact in every case: fg = top pixel, bg = bottom pixel when
/// both are on and differ; ▀/▄ with the field background otherwise.
/// </summary>
public sealed class HalfBlockSurface
{
    private readonly CellBuffer _buffer;
    private readonly int[] _top;    // per cell: top pixel color, -1 off
    private readonly int[] _bottom; // per cell: bottom pixel color, -1 off
    private readonly Cell _background;

    public int OriginX { get; }
    public int OriginY { get; }

    /// <summary>Square-pixel dimensions: one column per cell, two rows.</summary>
    public int PixelWidth { get; }
    public int PixelHeight { get; }

    public int CellWidth => PixelWidth;
    public int CellHeight => (PixelHeight + 1) / 2;

    public HalfBlockSurface(CellBuffer buffer, int originX, int originY,
        int pixelWidth, int pixelHeight, Cell background)
    {
        _buffer = buffer;
        OriginX = originX;
        OriginY = originY;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        _background = background;
        _top = new int[CellWidth * CellHeight];
        _bottom = new int[CellWidth * CellHeight];
        Clear();
    }

    public void Clear()
    {
        Array.Fill(_top, -1);
        Array.Fill(_bottom, -1);
    }

    public void SetPixel(int x, int y, int fg)
    {
        if ((uint)x >= (uint)PixelWidth || (uint)y >= (uint)PixelHeight)
            return;

        int ci = (y / 2) * CellWidth + x;
        if ((y & 1) == 0) _top[ci] = fg;
        else _bottom[ci] = fg;
    }

    /// <summary>Compose pixels into half-block cells and write them out.</summary>
    public void Draw()
    {
        _buffer.FillRect(OriginX, OriginY, CellWidth, CellHeight, _background);

        for (int cy = 0; cy < CellHeight; cy++)
        {
            for (int cx = 0; cx < CellWidth; cx++)
            {
                int ci = cy * CellWidth + cx;
                int t = _top[ci];
                int b = _bottom[ci];
                if (t < 0 && b < 0)
                    continue;

                char glyph;
                int fg;
                int bg = _background.Bg;

                if (t >= 0 && b >= 0)
                {
                    if (t == b)
                    {
                        glyph = '█';
                        fg = t;
                    }
                    else
                    {
                        glyph = '▀';
                        fg = t;
                        bg = b; // exact two-color cell
                    }
                }
                else if (t >= 0)
                {
                    glyph = '▀';
                    fg = t;
                }
                else
                {
                    glyph = '▄';
                    fg = b;
                }

                _buffer[OriginX + cx, OriginY + cy] =
                    new Cell { Char = glyph, Fg = fg, Bg = bg };
            }
        }
    }
}
