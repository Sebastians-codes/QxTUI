using System.Text;
using System.Buffers;

namespace QxTui.Render;

/// <summary>
/// Persistent grid of cells — the retained back buffer of the scene
/// (AGENT.md §2 rendering pipeline). Backed by an ArrayPool-rented buffer;
/// resize is a cold path and may re-rent.
/// </summary>
public sealed class CellBuffer : IDisposable
{
    private Cell[] _cells;
    private bool _rented;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public CellBuffer(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Buffer dimensions must be positive.");

        Width = width;
        Height = height;
        _cells = ArrayPool<Cell>.Shared.Rent(width * height);
        _rented = true;
        Array.Fill(_cells, Cell.Empty, 0, Width * Height);
    }

    public int CellCount => Width * Height;

    public ref Cell this[int x, int y]
    {
        get
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                throw new IndexOutOfRangeException($"({x},{y}) outside {Width}x{Height}");
            return ref _cells[y * Width + x];
        }
    }

    public ref Cell AtUnchecked(int index) => ref _cells[index];

    /// <summary>Live cell span (length == Width*Height). Used by the renderer
    /// to copy back → prev each frame.</summary>
    public Span<Cell> Cells => _cells.AsSpan(0, Width * Height);

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    public void Clear() => Array.Fill(_cells, Cell.Empty, 0, Width * Height);

    /// <summary>Copy a rect of cells from a same-width buffer (layer
    /// compositing: blit an overlay's painted rect over the scene).
    /// Row-wise span copies — zero alloc, no per-cell loop.</summary>
    public void BlitFrom(CellBuffer src, int x, int y, int w, int h)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Max(x0, Math.Min(Math.Min(Width, src.Width), x + w));
        int y1 = Math.Max(y0, Math.Min(Math.Min(Height, src.Height), y + h));
        if (x1 <= x0 || y1 <= y0) return;
        for (int row = y0; row < y1; row++)
            src._cells.AsSpan(row * src.Width + x0, x1 - x0)
               .CopyTo(_cells.AsSpan(row * Width + x0, x1 - x0));
    }

    /// <summary>Fill a rect (clipped to bounds) with a value. Used by widgets.
    /// Clamping is two-sided: after a shrink, stale rects may start beyond
    /// the new buffer edge and must degenerate to a no-op, not throw.</summary>
    public void FillRect(int x, int y, int w, int h, Cell cell)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Max(x0, Math.Min(Width, x + w));
        int y1 = Math.Max(y0, Math.Min(Height, y + h));

        // Stale rects from a larger layout can start entirely beyond the
        // shrunk buffer: degenerate to a no-op (AsSpan would throw even for
        // a zero-length span whose start exceeds the array).
        if (x1 <= x0 || y1 <= y0 || x0 >= Width || y0 >= Height)
            return;

        for (int row = y0; row < y1; row++)
        {
            var span = _cells.AsSpan(row * Width + x0, x1 - x0);
            span.Fill(cell);
        }
    }

    /// <summary>Write text at (x,y) with attributes, clipped to bounds. Returns cells written.</summary>
    public int WriteText(int x, int y, ReadOnlySpan<char> text, int fg = -1, int bg = -1, bool bold = false)
        => WriteTextClipped(new RenderClip(0, 0, Width, Height), x, y, text, fg, bg, bold);

    /// <summary>
    /// Write text clipped to BOTH the buffer bounds and <paramref name="clip"/>
    /// (a widget's rect). Widgets must never paint outside their own region.
    /// </summary>
    /// <summary>Rune-aware clipped write: 2-column runes occupy two cells
    /// (second marked WidePad), combining marks are skipped, and a wide rune
    /// never straddles the clip edge.</summary>
    public int WriteTextClipped(RenderClip clip, int x, int y, ReadOnlySpan<char> text,
        int fg = -1, int bg = -1, bool bold = false)
    {
        if ((uint)y >= (uint)Height || y < clip.Y || y >= clip.Bottom)
            return 0;

        int written = 0;
        int cx = x;
        int i = 0;
        while (i < text.Length)
        {
            if (Rune.DecodeFromUtf16(text[i..], out var rune, out int consumed)
                != System.Buffers.OperationStatus.Done)
            {
                rune = new Rune('?');
                consumed = 1;
            }

            // TAB: expand to the next 8-col stop as real space cells. A
            // raw tab in the stream would make the TERMINAL jump stops and
            // desync it from the front buffer — the classic garbled-diff
            // glitch on tab-indented sources (Godot C#, Makefiles, …).
            if (rune.Value == '\t')
            {
                i += consumed;
                int stop = (cx / 8 + 1) * 8;
                if (cx < clip.X) cx = Math.Min(stop, clip.X);
                int limit = Math.Min(Width, clip.Right);
                while (cx < stop && cx < limit)
                {
                    ref var tcell = ref _cells[y * Width + cx];
                    tcell.Char = ' ';
                    tcell.WidePad = false;
                    tcell.Fg = fg;
                    tcell.Bg = bg;
                    tcell.Bold = bold;
                    cx++;
                    written++;
                }
                if (cx >= limit) break;
                continue;
            }

            // other C0 controls + DEL: paint a space, NEVER the raw byte —
            // a stray \r (CRLF diffs) would carriage-return the terminal
            // mid-row and scramble everything after it
            if (rune.Value < 0x20 || rune.Value == 0x7f)
            {
                i += consumed;
                if (cx >= clip.X && cx + 1 <= Width && cx + 1 <= clip.Right)
                {
                    ref var ccell = ref _cells[y * Width + cx];
                    ccell.Char = ' ';
                    ccell.WidePad = false;
                    ccell.Fg = fg;
                    ccell.Bg = bg;
                    ccell.Bold = bold;
                    written++;
                }
                cx++;
                continue;
            }

            int w = UnicodeWidth.Of(rune);
            i += consumed;
            if (w == 0) continue; // combining/zwj: attaches, no column

            if (cx < 0 || cx < clip.X) { cx += w; continue; }
            if (cx + w > Width || cx + w > clip.Right) break; // never split a wide rune

            ref var cell = ref _cells[y * Width + cx];
            cell.Char = consumed == 2 ? text[i - 2] : (char)rune.Value; // high surrogate or BMP
            cell.WidePad = false;
            cell.Fg = fg;
            cell.Bg = bg;
            cell.Bold = bold;

            if (w == 2)
            {
                ref var pad = ref _cells[y * Width + cx + 1];
                pad.Char = consumed == 2 ? text[i - 1] : '\0'; // low surrogate or marker
                pad.WidePad = true;
                pad.Fg = fg;
                pad.Bg = bg;
                pad.Bold = bold;
            }

            cx += w;
            written += w;
        }
        return written;
    }

    /// <summary>Word-wrapped write inside the clip; returns lines written.</summary>
    public int WriteTextWrapped(RenderClip clip, int x, int y, ReadOnlySpan<char> text,
        int fg = -1, int bg = -1, bool bold = false, int maxLines = int.MaxValue)
    {
        int right = Math.Min(clip.Right, Width);
        int width = right - Math.Max(x, clip.X);
        if (width <= 0) return 0;

        var lines = TextWrap.Wrap(text, width);
        int n = 0;
        foreach (var line in lines)
        {
            if (n >= maxLines || y + n >= clip.Bottom || y + n >= Height) break;
            WriteTextClipped(clip, x, y + n, line.Span, fg, bg, bold);
            n++;
        }
        return n;
    }

    /// <summary>Resize. With preserve:true, overlapping content is kept —
    /// this is what makes partial repaint on terminal resize possible
    /// (AGENT.md §2: content invalidation ≠ layout invalidation).</summary>
    public void Resize(int width, int height, bool preserve = false)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        var old = _cells;
        int oldW = Width, oldH = Height;

        Width = width;
        Height = height;
        _cells = ArrayPool<Cell>.Shared.Rent(width * height);
        Array.Fill(_cells, Cell.Empty, 0, Width * Height);

        if (preserve)
        {
            int copyW = Math.Min(oldW, width);
            int copyH = Math.Min(oldH, height);
            for (int y = 0; y < copyH; y++)
                old.AsSpan(y * oldW, copyW).CopyTo(_cells.AsSpan(y * Width, copyW));
        }

        if (_rented)
            ArrayPool<Cell>.Shared.Return(old);
        _rented = true;
    }

    private void ReturnBuffer()
    {
        if (_rented)
        {
            ArrayPool<Cell>.Shared.Return(_cells);
            _rented = false;
        }
    }

    public void Dispose() => ReturnBuffer();
}
