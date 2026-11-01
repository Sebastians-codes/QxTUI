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
///
/// Set <see cref="ContentWidth"/> for rows wider than the list: it scrolls
/// sideways too (wheel, shift/alt+wheel, ←/→, bottom scrollbar). Rows are
/// handed a rect shifted left by <see cref="OffsetX"/> and a clip of the
/// viewport, so painting at rect.X just works. Set <see cref="ViewerMode"/>
/// for read-only content: no cursor, and the arrows move the view.
/// </summary>
public sealed class ListView : Widget
{
    /// <summary>Row count provider (read every render — cheap).</summary>
    public Func<int> Count { get; }

    /// <summary>Paints one row at its rect. index is the data index.</summary>
    public Action<CellBuffer, RenderClip, Rect, int, bool> RenderRow { get; set; } =
        (_, _, _, _, _) => { };

    public int RowHeight { get; set; } = 1;

    /// <summary>Width of the widest row in cells. 0 = no sideways scrolling
    /// and ←/→ stay free for the app.</summary>
    public int ContentWidth
    {
        get => _contentWidth;
        set
        {
            if (_contentWidth == value) return;
            _contentWidth = value;
            Invalidate();
        }
    }
    private int _contentWidth;

    /// <summary>Read-only content: nothing is selected and the arrows scroll
    /// the view at once, instead of moving an invisible cursor.</summary>
    public bool ViewerMode { get; set; }

    /// <summary>First visible column.</summary>
    public int OffsetX { get; private set; }

    private const int KeyStepX = 8;
    private const int WheelStepX = 4;
    private const int WheelStepY = 3;

    /// <summary>Trackpads leak the other axis during a swipe. The axis that
    /// started a gesture keeps the wheel until the gesture pauses this long.</summary>
    private static readonly TimeSpan AxisLock = TimeSpan.FromMilliseconds(150);
    private enum WheelAxis { None, Vertical, Horizontal }
    private WheelAxis _wheelAxis;
    private TimeSpan _wheelAt;

    public void ScrollXTo(int x)
    {
        OffsetX = Math.Clamp(x, 0, MaxOffsetX);
        Invalidate();
    }

    public void ScrollXBy(int delta) => ScrollXTo(OffsetX + delta);
    public bool ShowScrollbar { get; set; } = true;

    private bool _dragThumb;
    private bool _dragThumbX;
    private int _grabDeltaX;
    private int _grabDelta;      // cursor offset inside the thumb at grab
    private int _grabDeltaPx;    // same, in pixels (sub-cell drags)
    private int _lastRenderedOffset; // for hardware-scroll hints

    /// <summary>Index of the first visible row.</summary>
    public int Offset { get; private set; }

    /// <summary>Focused/selected row (highlighted in RenderRow's last arg).</summary>
    public int Selected { get; set; }

    /// <summary>Report `selected` to RenderRow even while unfocused, for
    /// apps whose selection must stay visible after focus moves on (dashboards
    /// where a button or field takes focus but the cursor row must not
    /// vanish). Default false = highlight only while focused.</summary>
    public bool SelectionVisibleUnfocused { get; set; }

    /// <summary>Fired with the row index on double-click (the "open this"
    /// gesture). Element identity is the data row, so fast clicks on two
    /// different rows are two singles, never an accidental activation.</summary>
    public event Action<int>? Activated;

    public override void OnDoubleClick(Point local)
    {
        if (ClickElementAt(local) is int row) Activated?.Invoke(row);
    }

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

    private int ViewWidth => Math.Max(1, Rect.W - (ShowScrollbar ? 1 : 0));
    private int MaxOffsetX => Math.Max(0, ContentWidth - ViewWidth);
    private bool HasBottomBar => ShowScrollbar && MaxOffsetX > 0;

    /// <summary>Rect.H minus the bottom scrollbar's row.</summary>
    private int TrackH => Math.Max(0, Rect.H - (HasBottomBar ? 1 : 0));

    private int VisibleRows() =>
        RowHeight <= 0 ? 0 : Math.Max(0, TrackH / RowHeight);

    private int ThumbW() => Math.Clamp(ViewWidth * ViewWidth / Math.Max(1, ContentWidth), 2, ViewWidth);

    private int ThumbX() => Rect.X + OffsetX * (ViewWidth - ThumbW()) / Math.Max(1, MaxOffsetX);

    public override bool HandleKey(KeyEvent ev)
    {
        if (!Focused) return false;
        int rows = VisibleRows();
        int total = Count();
        // consumed at the edges too: mid-read they must not reach the app
        if (ContentWidth > 0 && ev.Key is Key.Left or Key.Right)
        {
            ScrollXBy(ev.Key == Key.Left ? -KeyStepX : KeyStepX);
            return true;
        }
        if (ViewerMode)
        {
            switch (ev.Key)
            {
                case Key.Down: ScrollBy(1); return true;
                case Key.Up: ScrollBy(-1); return true;
                case Key.PageDown: ScrollTo(Offset + rows); return true;
                case Key.PageUp: ScrollTo(Offset - rows); return true;
                case Key.Home: ScrollTo(0); ScrollXTo(0); return true;
                case Key.End: ScrollTo(total); return true;
                default: return false;
            }
        }
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
            case MouseKind.WheelUp or MouseKind.WheelDown:
                int dir = ev.Kind == MouseKind.WheelUp ? -1 : 1;
                if (MaxOffsetX > 0 && (ev.Shift || ev.Alt))
                    WheelX(dir);
                else if (WheelAllowed(WheelAxis.Vertical))
                    ScrollBy(dir * WheelStepY);
                return true;

            case MouseKind.WheelLeft or MouseKind.WheelRight:
                if (ContentWidth <= 0) return false;
                WheelX(ev.Kind == MouseKind.WheelLeft ? -1 : 1);
                return true;

            case MouseKind.Press:
                if (HasBottomBar && ev.Y >= r.Bottom - 1 && ev.X < r.Right - 1)
                {
                    int thumbX = ThumbX();
                    int thumbW = ThumbW();
                    // on the thumb: no teleport. Off it: jump, center-grabbed
                    _grabDeltaX = ev.X >= thumbX && ev.X < thumbX + thumbW ? ev.X - thumbX : thumbW / 2;
                    _dragThumbX = true;
                    DragXTo(ev);
                    return true;
                }
                if (ShowScrollbar && ev.X >= r.Right - 1 && ev.Y < r.Y + TrackH)
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
                if (ViewerMode) return false;
                // press on content: select that row
                Selected = Math.Clamp(Offset + (ev.Y - r.Y) / Math.Max(1, RowHeight),
                    0, Math.Max(0, Count() - 1));
                Invalidate();
                return false;

            case MouseKind.Motion:
                if (_dragThumbX)
                {
                    DragXTo(ev);
                    return true;
                }
                if (_dragThumb)
                {
                    DragTo(ev);
                    return true;
                }
                return false;

            default: // release
                _dragThumb = false;
                _dragThumbX = false;
                return false;
        }
    }

    private void WheelX(int dir)
    {
        if (WheelAllowed(WheelAxis.Horizontal))
            ScrollXBy(dir * WheelStepX);
    }

    private bool WheelAllowed(WheelAxis axis)
    {
        if (MaxOffsetX == 0) return true;
        var now = App?.Scheduler.Time.Now ?? TimeSpan.Zero;
        if (_wheelAxis != WheelAxis.None && axis != _wheelAxis && now - _wheelAt < AxisLock)
            return false;
        _wheelAxis = axis;
        _wheelAt = now;
        return true;
    }

    private void DragXTo(MouseEvent ev)
    {
        double track = Math.Max(1, ViewWidth - ThumbW());
        ScrollXTo((int)Math.Round(Math.Clamp((ev.X - Rect.X - _grabDeltaX) / track, 0, 1) * MaxOffsetX));
    }

    /// <summary>The data row under the point is the element identity for
    /// double-click: adjacent rows are distinct elements.</summary>
    internal override object? ClickElementAt(Point local) =>
        Offset + local.Y / Math.Max(1, RowHeight);

    private int ThumbH()
    {
        int total = Count();
        int rows = VisibleRows();
        if (total <= rows) return TrackH;
        return Math.Max(1, rows * TrackH / total);
    }

    private int ThumbY()
    {
        int total = Count();
        int rows = VisibleRows();
        if (total <= rows) return Rect.Y;
        return Rect.Y + Offset * (TrackH - ThumbH()) / Math.Max(1, total - rows);
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
            trackH = Math.Max(1, (TrackH - ThumbH()) * cellH);
            thumbTop = ev.Py - r.Y * cellH - _grabDeltaPx;
        }
        else
        {
            trackH = Math.Max(1, TrackH - ThumbH());
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
        if (r.W <= 0 || r.H <= 0) return; // collapsed: keep the scroll position
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);

        int total = Count();
        int rows = VisibleRows();
        int trackW = ShowScrollbar ? 1 : 0;
        int contentW = Math.Max(1, r.W - trackW);
        int trackH = TrackH;

        Offset = Math.Clamp(Offset, 0, Math.Max(0, total - rows));
        OffsetX = Math.Clamp(OffsetX, 0, MaxOffsetX);

        // hardware scroll: window shifted vertically → repaint exposed
        // only. The rect MUST be the full widget width: SU/SD shift whole
        // terminal rows (every column), so a content-only rect leaves the
        // scrollbar column shifted on the terminal but not in the front
        // buffer — its exposed rows blank out and never repaint (the
        // vanishing scrollbar).
        int deltaCells = (Offset - _lastRenderedOffset) * RowHeight;
        _lastRenderedOffset = Offset;
        if (deltaCells != 0)
            App?.ScrollHint(new Rect(r.X, r.Y, r.W, trackH), deltaCells);

        // scrolled rows run past both edges of their rect: only a clip of
        // the viewport keeps them off the scrollbars
        var rowClip = ContentWidth > 0 ? new RenderClip(r.X, r.Y, contentW, trackH) : Clip;
        int last = Math.Min(total, Offset + rows);
        for (int i = Offset; i < last; i++)
        {
            var rowRect = new Rect(r.X - OffsetX, r.Y + (i - Offset) * RowHeight, contentW + OffsetX, RowHeight);
            RenderRow(buffer, rowClip, rowRect, i,
                !ViewerMode && i == Selected && (Focused || SelectionVisibleUnfocused));
        }

        if (HasBottomBar) DrawBottomBar(buffer, r);

        if (!ShowScrollbar || total <= rows) return;

        int sx = r.Right - 1;
        int trackBottom = r.Y + trackH;
        for (int y = r.Y; y < trackBottom; y++)
            buffer.WriteTextClipped(Clip, sx, y, "│", fg: Theme.Get(ThemeRole.Faint));

        int thumbH = Math.Max(1, rows * rows / total);
        // drag is 1:1 and immediate, so Offset IS the cursor position
        double thumbOff = Offset;
        // QUARTER-ROW precision via braille dot-rows (4 dot-rows per cell):
        // the indicator creeps in quarter-cursor steps instead of hopping
        // whole rows, and keeps one visual texture at every position
        double posExact = thumbOff * (trackH - thumbH) / Math.Max(1, total - rows);
        int quarter = (int)Math.Round(posExact * 4);
        int topRow = r.Y + quarter / 4;
        int rem = quarter % 4;

        int accent = Theme.Get(ThemeRole.Accent);
        // ALL-braille thumb: one visual language at every position — the
        // mixed solid/dotted render flickered between two textures as it
        // moved. Dot-row bands give quarter-row translation uniformly.
        if (rem == 0)
        {
            for (int y = topRow; y < Math.Min(trackBottom, topRow + thumbH); y++)
                buffer.WriteTextClipped(Clip, sx, y, BrailleBand(0, 3).ToString(), fg: accent);
        }
        else
        {
            if (topRow < trackBottom)
                buffer.WriteTextClipped(Clip, sx, topRow, BrailleBand(rem, 3).ToString(), fg: accent);
            for (int y = topRow + 1; y < Math.Min(trackBottom, topRow + thumbH); y++)
                buffer.WriteTextClipped(Clip, sx, y, BrailleBand(0, 3).ToString(), fg: accent);
            int lastRow = topRow + thumbH;
            if (lastRow < trackBottom)
                buffer.WriteTextClipped(Clip, sx, lastRow, BrailleBand(0, rem - 1).ToString(), fg: accent);
        }
    }

    /// <summary>The vertical thumb's language turned sideways: braille at
    /// half-cell precision (a cell has two dot columns) on a thin track,
    /// two dots high so it sits on the line instead of blocking the row.</summary>
    private void DrawBottomBar(CellBuffer buffer, Rect r)
    {
        const string Full = "\u2836", RightHalf = "\u2830", LeftHalf = "\u2806";
        int y = r.Bottom - 1;
        int view = ViewWidth;
        for (int x = r.X; x < r.X + view; x++)
            buffer.WriteTextClipped(Clip, x, y, "─", fg: Theme.Get(ThemeRole.Faint));

        int thumbW = ThumbW();
        int halves = (int)Math.Round(OffsetX * (double)(view - thumbW) / Math.Max(1, MaxOffsetX) * 2);
        int left = r.X + halves / 2;
        bool half = halves % 2 != 0;
        int accent = Theme.Get(ThemeRole.Accent);
        int end = r.X + view;
        for (int i = 0; i < thumbW && left + i < end; i++)
            buffer.WriteTextClipped(Clip, left + i, y, half && i == 0 ? RightHalf : Full, fg: accent);
        if (half && left + thumbW < end)
            buffer.WriteTextClipped(Clip, left + thumbW, y, LeftHalf, fg: accent);
    }
}
