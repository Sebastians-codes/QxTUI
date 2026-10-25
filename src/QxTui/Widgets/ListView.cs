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
    private double _target;      // drag stream target (thumb = cursor)
    private long _lastGlideTs;
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
        Tick(TimeSpan.FromMilliseconds(8), _ => Glide());
    }

    public void ScrollTo(int index)
    {
        int rows = VisibleRows();
        Offset = Math.Clamp(index, 0, Math.Max(0, Count() - rows));
        _target = Offset;
        Invalidate();
    }

    /// <summary>Constant-velocity catch-up for drags: the terminal mouse
    /// quantizes to whole cells — one cell is hundreds of rows on a 10k
    /// list — so the CONTENT glides to the drag target while the thumb
    /// itself stays glued to the cursor. Linear (not eased): continuous
    /// drags track without rubber-band lag; discrete jumps animate.</summary>
    /// <summary>Drag catch-up stream: the thumb is glued to the cursor while
    /// the content flows to it at a velocity that ADAPTS to what the
    /// terminal actually presents (cheap hardware-scroll frames raise
    /// presented fps → the stream accelerates: a feedback loop that
    /// converges on smooth). Large lags ramp the rate so catch-up after a
    /// fast flick stays short.</summary>
    private void Glide()
    {
        int target = (int)Math.Round(_target);
        int diff = target - Offset;
        if (diff == 0) return;
        long now = Environment.TickCount64;
        double dt = Math.Min(0.25, Math.Max(0.001, (now - _lastGlideTs) / 1000.0));
        _lastGlideTs = now;

        double presented = App is { } host && host.Stats.PresentedFps > 0
            ? host.Stats.PresentedFps : 60;
        double window = Math.Max(1, VisibleRows());
        double ramp = 1 + Math.Abs(diff) / (window * 4);
        double v = Math.Clamp(presented * window * 0.5 * ramp, 300, 6000);
        int maxStep = Math.Max(1, (int)(v * dt));
        int pos = Math.Clamp(Offset + Math.Clamp(diff, -maxStep, maxStep),
            0, Math.Max(0, Count() - VisibleRows()));
        if (pos != Offset)
        {
            Offset = pos;
            Invalidate();
        }
    }

    private void Retarget(double target)
    {
        _lastGlideTs = Environment.TickCount64;
        _target = Math.Clamp(target, 0, Math.Max(0, Count() - VisibleRows()));
        Invalidate();
    }

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
        _target = Offset;
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
                    if (ev.Y >= thumbY && ev.Y < thumbY + thumbH)
                        _grabDelta = ev.Y - thumbY;
                    else
                    {
                        _grabDelta = thumbH / 2;
                        ScrollTo(ev.Y < thumbY ? Offset - VisibleRows() : Offset + VisibleRows());
                    }
                    _dragThumb = true;
                    DragTo(ev.Y, smooth: false); // press lands instantly
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
                    DragTo(ev.Y, smooth: true); // content glides to target
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

    /// <summary>Maps cursor Y to a scroll target (thumb follows the cursor
    /// 1:1 minus the grab delta). Presses land instantly; motion glides.</summary>
    private void DragTo(int y, bool smooth)
    {
        var r = Rect;
        int total = Count();
        int rows = VisibleRows();
        if (total <= rows) return;

        int trackH = Math.Max(1, r.H - ThumbH());
        double thumbTop = y - r.Y - _grabDelta;
        double frac = Math.Clamp(thumbTop / trackH, 0, 1);
        int target = Math.Clamp((int)Math.Round(frac * (total - rows)), 0, total - rows);

        if (smooth)
        {
            if (target != (int)Math.Round(_target))
                Retarget(target);
        }
        else if (target != Offset)
        {
            Offset = target;
            _target = target;
            Invalidate();
        }
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
        _target = Math.Clamp(_target, 0, Math.Max(0, total - rows));

        // hardware scroll: window shifted vertically → repaint exposed only
        int deltaCells = (Offset - _lastRenderedOffset) * RowHeight;
        _lastRenderedOffset = Offset;
        if (deltaCells != 0)
            App?.ScrollHint(new Rect(r.X, r.Y, contentW, r.H), deltaCells);

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
        // during a drag the thumb tracks the cursor exactly (target-based);
        // the content glides to catch up — no rubber-band thumb
        int thumbOff = _dragThumb ? (int)Math.Round(_target) : Offset;
        int thumbY = r.Y + (int)((long)thumbOff * (r.H - thumbH) / Math.Max(1, total - rows));
        for (int y = thumbY; y < Math.Min(r.Bottom, thumbY + thumbH); y++)
            buffer.WriteTextClipped(Clip, sx, y, "█", fg: Theme.Get(ThemeRole.Accent));
    }
}
