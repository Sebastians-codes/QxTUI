using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>
/// Virtualized list: only the visible rows are ever rendered, so 10k+
/// rows cost the same as 10. Wheel scrolls; PageUp/Down and Home/End via
/// HandleKey when focused; optional scrollbar thumb.
///
/// Row content is a callback — the list owns scrolling, clipping and the
/// scrollbar; you own painting one row.
/// </summary>
public sealed class ListView : Widget
{
    /// <summary>Row count provider (read every render — cheap).</summary>
    public Func<int> Count { get; }

    /// <summary>Paints one row at its rect. index is the data index.</summary>
    public Action<CellBuffer, RenderClip, Rect, int, bool> RenderRow { get; set; } =
        (_, _, _, _, _) => { };

    public int RowHeight { get; set; } = 1;
    public bool ShowScrollbar { get; set; } = true;

    private bool _dragThumb;
    private int _grabDelta;      // cursor offset inside the thumb at grab
    private int _grabDeltaPx;    // same, in pixels (sub-cell drags)
    private int _lastRenderedOffset; // for hardware-scroll hints

    /// <summary>Index of the first visible row.</summary>
    public int Offset { get; private set; }

    /// <summary>Focused/selected row (highlighted in RenderRow's last arg).</summary>
    public int Selected { get; set; }

    public ListView(Func<int> count)
    {
        Count = count;
        Focusable = true;
        Clickable = true;
        CapturesMouse = true; // thumb drags survive leaving the widget
    }

    public void ScrollTo(int index)
    {
        int rows = VisibleRows();
        Offset = Math.Clamp(index, 0, Math.Max(0, Count() - rows));
        Invalidate();
    }

    /// <summary>Constant-velocity catch-up for drags: the terminal mouse
    /// quantizes to whole cells — one cell is hundreds of rows on a 10k
    /// list — so the CONTENT glides to the drag target while the thumb
    /// itself stays glued to the cursor. Linear (not eased): continuous
    /// drags track without rubber-band lag; discrete jumps animate.</summary>
    /// <summary>Wheel: immediate steps. Smoothness comes from the terminal
    /// presenting frames fast, not from client-side delay — hardware-scroll
    /// hints keep those frames ~1KB so presented fps stays healthy.</summary>
    public void ScrollBy(int delta) => ScrollTo(Offset + delta);

    private int VisibleRows() =>
        RowHeight <= 0 ? 0 : Math.Max(0, Rect.H / RowHeight);

    public override bool HandleKey(KeyEvent ev)
    {
        if (!Focused) return false;
        int rows = VisibleRows();
        int total = Count();
        switch (ev.Key)
        {
            case Key.Down:
                // selection outside the window jumps into it first
                Selected = Selected < Offset ? Offset : Math.Min(total - 1, Selected + 1);
                EnsureVisible();
                return true;
            case Key.Up:
                Selected = Selected >= Offset + rows ? Offset + rows - 1 : Math.Max(0, Selected - 1);
                EnsureVisible();
                return true;
            case Key.PageDown: ScrollTo(Offset + rows); return true;   // keys jump now
            case Key.PageUp: ScrollTo(Offset - rows); return true;
            case Key.Home: ScrollTo(0); Selected = 0; return true;
            case Key.End: ScrollTo(total); Selected = total - 1; return true;
            default: return false;
        }
    }

    private void EnsureVisible()
    {
        int rows = VisibleRows();
        if (Selected < Offset) Offset = Selected;
        else if (Selected >= Offset + rows) Offset = Selected - rows + 1;
        Invalidate();
    }

    public override bool OnMouse(MouseEvent ev)
    {
        var r = Rect;
        switch (ev.Kind)
        {
            case MouseKind.WheelUp:
                ScrollBy(-3);
                return true;

            case MouseKind.WheelDown:
                ScrollBy(3);
                return true;

            case MouseKind.Press:
                if (ShowScrollbar && ev.X >= r.Right - 1)
                {
                    // grab the thumb where the cursor is (no teleport), or
                    // jump-page when pressing the track outside the thumb
                    int thumbY = ThumbY();
                    int thumbH = ThumbH();
                    int cellH = App?.CellPixelH ?? 0;
                    if (ev.Y >= thumbY && ev.Y < thumbY + thumbH)
                    {
                        // grab the thumb where the cursor is (no teleport)
                        _grabDelta = ev.Y - thumbY;
                        _grabDeltaPx = ev.Py >= 0 && cellH > 0
                            ? ev.Py - thumbY * cellH
                            : _grabDelta * cellH;
                    }
                    else
                    {
                        // press ANYWHERE on the track: jump the view to the
                        // pressed spot (center-grab so the drag continues
                        // from there). The pixel branch must use the same
                        // center grab — deriving it from the cursor's
                        // distance to the thumb cancelled the cursor out
                        // and snapped presses to the top.
                        _grabDelta = thumbH / 2;
                        _grabDeltaPx = thumbH * cellH / 2;
                    }
                    _dragThumb = true;
                    DragTo(ev); // drag is 1:1: view goes where the cursor is
                    return true;
                }
                // press on content: select that row
                Selected = Math.Clamp(Offset + (ev.Y - r.Y) / Math.Max(1, RowHeight),
                    0, Math.Max(0, Count() - 1));
                Invalidate();
                return false;

            case MouseKind.Motion:
                if (_dragThumb)
                {
                    DragTo(ev);
                    return true;
                }
                return false;

            default: // release
                _dragThumb = false;
                return false;
        }
    }

    private int ThumbH()
    {
        int total = Count();
        int rows = VisibleRows();
        if (total <= rows) return Rect.H;
        return Math.Max(1, rows * Rect.H / total);
    }

    private int ThumbY()
    {
        int total = Count();
        int rows = VisibleRows();
        if (total <= rows) return Rect.Y;
        return Rect.Y + Offset * (Rect.H - ThumbH()) / Math.Max(1, total - rows);
    }

    /// <summary>Cursor → offset, 1:1 and immediate. No catch-up stream: a
    /// capped-velocity stream made fast drags lag, and on release the thumb
    /// hopped backwards and oozed to catch up. Renders coalesce to one frame
    /// per loop tick at the latest offset, so fast motion can't flood.</summary>
    private void DragTo(MouseEvent ev)
    {
        var r = Rect;
        int total = Count();
        int rows = VisibleRows();
        if (total <= rows) return;

        // pixel coords when available: one mouse pixel moves a handful of
        // rows instead of a whole cell's worth (hundreds on big lists)
        int cellH = App?.CellPixelH ?? 0;
        double trackH, thumbTop;
        if (ev.Py >= 0 && cellH > 0)
        {
            trackH = Math.Max(1, (r.H - ThumbH()) * cellH);
            thumbTop = ev.Py - r.Y * cellH - _grabDeltaPx;
        }
        else
        {
            trackH = Math.Max(1, r.H - ThumbH());
            thumbTop = ev.Y - r.Y - _grabDelta;
        }
        double frac = Math.Clamp(thumbTop / trackH, 0, 1);
        ScrollTo((int)Math.Round(frac * (total - rows)));
    }

    /// <summary>Braille glyph with both dot columns filled over dot-rows
    /// [from..to] (0..3, top-down): quarter-row vertical bands for
    /// sub-half scrollbar precision.</summary>
    internal static char BrailleBand(int from, int to)
    {
        int[] left = [0x01, 0x02, 0x04, 0x40];
        int[] right = [0x08, 0x10, 0x20, 0x80];
        int bits = 0;
        for (int row = from; row <= to; row++)
            bits |= left[row] | right[row];
        return (char)(0x2800 + bits);
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);

        int total = Count();
        int rows = VisibleRows();
        int trackW = ShowScrollbar ? 1 : 0;
        int contentW = Math.Max(1, r.W - trackW);

        Offset = Math.Clamp(Offset, 0, Math.Max(0, total - rows));

        // hardware scroll: window shifted vertically → repaint exposed
        // only. The rect MUST be the full widget width: SU/SD shift whole
        // terminal rows (every column), so a content-only rect leaves the
        // scrollbar column shifted on the terminal but not in the front
        // buffer — its exposed rows blank out and never repaint (the
        // vanishing scrollbar).
        int deltaCells = (Offset - _lastRenderedOffset) * RowHeight;
        _lastRenderedOffset = Offset;
        if (deltaCells != 0)
            App?.ScrollHint(r, deltaCells);

        int last = Math.Min(total, Offset + rows);
        for (int i = Offset; i < last; i++)
        {
            var rowRect = new Rect(r.X, r.Y + (i - Offset) * RowHeight, contentW, RowHeight);
            RenderRow(buffer, Clip, rowRect, i, i == Selected && Focused);
        }

        if (!ShowScrollbar || total <= rows) return;

        int sx = r.Right - 1;
        for (int y = r.Y; y < r.Bottom; y++)
            buffer.WriteTextClipped(Clip, sx, y, "│", fg: Theme.Get(ThemeRole.Faint));

        int thumbH = Math.Max(1, rows * rows / total);
        // drag is 1:1 and immediate, so Offset IS the cursor position
        double thumbOff = Offset;
        // QUARTER-ROW precision via braille dot-rows (4 dot-rows per cell):
        // the indicator creeps in quarter-cursor steps instead of hopping
        // whole rows, and keeps one visual texture at every position
        double posExact = thumbOff * (r.H - thumbH) / Math.Max(1, total - rows);
        int quarter = (int)Math.Round(posExact * 4);
        int topRow = r.Y + quarter / 4;
        int rem = quarter % 4;

        int accent = Theme.Get(ThemeRole.Accent);
        // ALL-braille thumb: one visual language at every position — the
        // mixed solid/dotted render flickered between two textures as it
        // moved. Dot-row bands give quarter-row translation uniformly.
        if (rem == 0)
        {
            for (int y = topRow; y < Math.Min(r.Bottom, topRow + thumbH); y++)
                buffer.WriteTextClipped(Clip, sx, y, BrailleBand(0, 3).ToString(), fg: accent);
        }
        else
        {
            if (topRow < r.Bottom)
                buffer.WriteTextClipped(Clip, sx, topRow, BrailleBand(rem, 3).ToString(), fg: accent);
            for (int y = topRow + 1; y < Math.Min(r.Bottom, topRow + thumbH); y++)
                buffer.WriteTextClipped(Clip, sx, y, BrailleBand(0, 3).ToString(), fg: accent);
            int lastRow = topRow + thumbH;
            if (lastRow < r.Bottom)
                buffer.WriteTextClipped(Clip, sx, lastRow, BrailleBand(0, rem - 1).ToString(), fg: accent);
        }
    }
}
