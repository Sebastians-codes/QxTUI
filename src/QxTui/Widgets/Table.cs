using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>
/// Virtualized data grid: header row, column separators, a cell cursor
/// (row + column), wheel/keys/thumb-drag scrolling with the ListView feel
/// (1:1 drag, braille quarter-row thumb, hardware-scroll hints), and an
/// <see cref="Activated"/> event on Enter.
///
/// Cell content is a callback — the table owns scrolling, column geometry,
/// clipping and selection; you own the data.
/// </summary>
public sealed class Table : Widget
{
    /// <summary>Row count provider (read every render — cheap).</summary>
    public Func<int> RowCount { get; }

    /// <summary>Column specs (fixed widths + weight shares).</summary>
    public IReadOnlyList<TableColumn> Columns { get; }

    /// <summary>Cell text provider: (rowIndex, colIndex) → string.</summary>
    public Func<int, int, string> Cell { get; set; } = (_, _) => "";

    public bool ShowHeader { get; set; } = true;
    public bool ShowScrollbar { get; set; } = true;

    /// <summary>Index of the first visible body row.</summary>
    public int Offset { get; private set; }

    /// <summary>Cursor cell (clamped on render/use).</summary>
    public int SelectedRow { get; set; }
    public int SelectedCol { get; set; }

    /// <summary>Fires on Enter over the cursor cell: (row, col). Events
    /// become messages — apps turn this into an intent.</summary>
    public event Action<int, int>? Activated;

    /// <summary>Fires when the cursor cell actually MOVES via user
    /// navigation (keys or mouse). Setting SelectedRow/SelectedCol
    /// directly does not fire — this reports the user, which is what
    /// detail panes follow.</summary>
    public event Action<int, int>? SelectionChanged;

    /// <summary>Column the rows are sorted by (-1 = natural/data order).
    /// Double-click a header to sort it; double-click again to flip
    /// direction. The TABLE owns the state and the indicator; the APP
    /// owns the ordering (it owns the data) via <see cref="SortChanged"/>.</summary>
    public int SortColumn { get; private set; } = -1;
    public bool SortDescending { get; private set; }

    /// <summary>(column, descending) after every header double-click or
    /// <see cref="SetSort"/>. Reorder your data and Invalidate().</summary>
    public event Action<int, bool>? SortChanged;

    /// <summary>Programmatic sort (restore saved view state, menu items).
    /// col &lt; 0 clears back to natural order.</summary>
    public void SetSort(int col, bool descending)
    {
        if (col == SortColumn && descending == SortDescending) return;
        SortColumn = col;
        SortDescending = descending;
        SortChanged?.Invoke(col, descending);
        Invalidate();
    }

    private bool _dragThumb;
    private int _grabDelta;         // cursor offset inside the thumb at grab
    private int _grabDeltaPx;       // same, in pixels (sub-cell drags)
    private int _lastRenderedOffset; // for hardware-scroll hints
    private int[] _starts = [], _widths = [];

    public Table(Func<int> rowCount, params TableColumn[] columns)
    {
        RowCount = rowCount;
        Columns = columns;
        Focusable = true;
        Clickable = true;
        CapturesMouse = true; // thumb drags survive leaving the widget
    }

    private int HeaderRows => ShowHeader ? 2 : 0; // titles + ─ separator
    private int TrackW => ShowScrollbar ? 1 : 0;
    private int BodyRows => Math.Max(0, Rect.H - HeaderRows);

    public void ScrollTo(int index)
    {
        Offset = Math.Clamp(index, 0, Math.Max(0, RowCount() - BodyRows));
        Invalidate();
    }

    /// <summary>Wheel/keys: immediate steps — smoothness comes from the
    /// terminal presenting frames fast (same doctrine as ListView).</summary>
    public void ScrollBy(int delta) => ScrollTo(Offset + delta);

    /// <summary>Column geometry for a content width: fixed widths first,
    /// leftover split by weight; the last weight column eats the rounding
    /// remainder so the grid always spans exactly the content width.</summary>
    private void LayoutColumns(int contentW)
    {
        int n = Columns.Count;
        if (_widths.Length != n) { _widths = new int[n]; _starts = new int[n]; }
        int seps = Math.Max(0, n - 1);
        int fixedSum = 0;
        float weightSum = 0;
        foreach (var c in Columns)
        {
            if (c.Width is int w) fixedSum += Math.Max(0, w);
            else weightSum += Math.Max(0.01f, c.Weight);
        }

        // narrow rects: squeeze fixed columns proportionally instead of
        // letting them overflow (every column keeps ≥1 cell) — a real app
        // meets narrow terminals, and clipped-away columns are unreadable
        float scale = 1f;
        if (fixedSum + seps > contentW && fixedSum > 0)
            scale = Math.Max(0.05f, (float)Math.Max(n, contentW - seps) / fixedSum);

        int flex = Math.Max(0, contentW - seps - (int)(fixedSum * scale));
        int lastWeight = -1, flexUsed = 0;
        for (int i = 0; i < n; i++)
        {
            var c = Columns[i];
            if (c.Width is int w) _widths[i] = Math.Max(1, (int)(Math.Max(0, w) * scale));
            else
            {
                _widths[i] = weightSum <= 0 ? 1 : Math.Max(1, (int)(flex * Math.Max(0.01f, c.Weight) / weightSum));
                flexUsed += _widths[i];
                lastWeight = i;
            }
        }
        if (lastWeight >= 0)
            _widths[lastWeight] = Math.Max(1, _widths[lastWeight] + flex - flexUsed);

        int x = 0;
        for (int i = 0; i < n; i++)
        {
            _starts[i] = x;
            x += _widths[i] + 1; // + separator column
        }
    }

    private int ColumnAt(int localX)
    {
        for (int i = 0; i < _widths.Length; i++)
            if (localX >= _starts[i] && localX < _starts[i] + _widths[i]) return i;
        return -1;
    }

    private void EnsureVisible()
    {
        int rows = BodyRows;
        if (SelectedRow < Offset) Offset = SelectedRow;
        else if (SelectedRow >= Offset + rows) Offset = SelectedRow - rows + 1;
        Invalidate();
    }

    /// <summary>Clamp → dedupe → notify: the single funnel for user
    /// navigation so SelectionChanged fires exactly on real moves.</summary>
    private void MoveSelection(int row, int col)
    {
        row = Math.Clamp(row, 0, Math.Max(0, RowCount() - 1));
        col = Math.Clamp(col, 0, Math.Max(0, Columns.Count - 1));
        if (row == SelectedRow && col == SelectedCol) return;
        SelectedRow = row;
        SelectedCol = col;
        SelectionChanged?.Invoke(row, col);
        Invalidate();
    }

    public override bool HandleKey(KeyEvent ev)
    {
        if (!Focused) return false;
        int total = RowCount(), rows = BodyRows;
        switch (ev.Key)
        {
            case Key.Down:
                // selection outside the window jumps into it first
                MoveSelection(SelectedRow < Offset ? Offset : SelectedRow + 1, SelectedCol);
                EnsureVisible();
                return true;
            case Key.Up:
                MoveSelection(SelectedRow >= Offset + rows ? Offset + rows - 1 : SelectedRow - 1, SelectedCol);
                EnsureVisible();
                return true;
            case Key.Left:
                MoveSelection(SelectedRow, SelectedCol - 1);
                return true;
            case Key.Right:
                MoveSelection(SelectedRow, SelectedCol + 1);
                return true;
            case Key.PageDown: ScrollTo(Offset + rows); return true;
            case Key.PageUp: ScrollTo(Offset - rows); return true;
            case Key.Home: ScrollTo(0); MoveSelection(0, SelectedCol); return true;
            case Key.End: ScrollTo(total); MoveSelection(total - 1, SelectedCol); return true;
            case Key.Enter:
                if (total > 0)
                    Activated?.Invoke(Math.Clamp(SelectedRow, 0, total - 1),
                        Math.Clamp(SelectedCol, 0, Math.Max(0, Columns.Count - 1)));
                return true;
            default: return false;
        }
    }

    /// <summary>The data cell under the point (header clicks identity as
    /// their column) — two fast clicks on different rows stay singles.</summary>
    internal override object? ClickElementAt(Point local)
    {
        LayoutColumns(Math.Max(1, Rect.W - TrackW));
        int col = ColumnAt(local.X);
        if (local.Y < HeaderRows) return ("header", col);
        return (Offset + local.Y - HeaderRows, col);
    }

    /// <summary>Double-click on a HEADER sorts (asc → desc → asc…);
    /// anywhere else it activates the cursor cell, like Enter.</summary>
    public override void OnDoubleClick(Point local)
    {
        if (local.Y < HeaderRows)
        {
            LayoutColumns(Math.Max(1, Rect.W - TrackW));
            int col = ColumnAt(local.X);
            if (col < 0) return;
            SetSort(col, col == SortColumn ? !SortDescending : false);
            return;
        }
        int total = RowCount();
        if (total > 0)
            Activated?.Invoke(Math.Clamp(SelectedRow, 0, total - 1),
                Math.Clamp(SelectedCol, 0, Math.Max(0, Columns.Count - 1)));
    }

    public override bool OnMouse(MouseEvent ev)
    {
        var r = Rect;
        int trackTop = r.Y + HeaderRows;
        int trackH = Math.Max(0, r.Bottom - trackTop);
        switch (ev.Kind)
        {
            case MouseKind.WheelUp:
                ScrollBy(-3);
                return true;

            case MouseKind.WheelDown:
                ScrollBy(3);
                return true;

            case MouseKind.Press:
                if (ShowScrollbar && ev.X >= r.Right - 1 && ev.Y >= trackTop)
                {
                    // grab the thumb where the cursor is (no teleport), or
                    // jump the view when pressing the track outside it
                    int thumbY = ThumbY(trackTop, trackH);
                    int thumbH = ThumbH(trackH);
                    int cellH = App?.CellPixelH ?? 0;
                    if (ev.Y >= thumbY && ev.Y < thumbY + thumbH)
                    {
                        _grabDelta = ev.Y - thumbY;
                        _grabDeltaPx = ev.Py >= 0 && cellH > 0
                            ? ev.Py - thumbY * cellH
                            : _grabDelta * cellH;
                    }
                    else
                    {
                        _grabDelta = thumbH / 2;
                        _grabDeltaPx = thumbH * cellH / 2;
                    }
                    _dragThumb = true;
                    DragTo(ev, trackTop, trackH); // 1:1: view goes where the cursor is
                    return true;
                }
                if (ev.Y < trackTop)
                {
                    // header press: pick the column, keep the row
                    LayoutColumns(Math.Max(1, r.W - TrackW));
                    int c = ColumnAt(ev.X - r.X);
                    if (c >= 0) MoveSelection(SelectedRow, c);
                    return false;
                }
                // body press: select the cell under the cursor
                LayoutColumns(Math.Max(1, r.W - TrackW));
                int row = Offset + (ev.Y - trackTop);
                if (row < RowCount())
                {
                    int col = ColumnAt(ev.X - r.X);
                    MoveSelection(row, col >= 0 ? col : SelectedCol);
                }
                return false; // let the App focus us (Clickable path)

            case MouseKind.Motion:
                if (_dragThumb)
                {
                    DragTo(ev, trackTop, trackH);
                    return true;
                }
                return false;

            default: // release
                _dragThumb = false;
                return false;
        }
    }

    private int ThumbH(int trackH)
    {
        int total = RowCount();
        int rows = BodyRows;
        if (total <= rows || trackH <= 0) return trackH;
        return Math.Max(1, rows * trackH / total);
    }

    private int ThumbY(int trackTop, int trackH)
    {
        int total = RowCount();
        int rows = BodyRows;
        if (total <= rows) return trackTop;
        return trackTop + Offset * (trackH - ThumbH(trackH)) / Math.Max(1, total - rows);
    }

    /// <summary>Cursor → offset, 1:1 and immediate (ListView doctrine),
    /// pixel-fine where the terminal reports sub-cell coordinates.</summary>
    private void DragTo(MouseEvent ev, int trackTop, int trackH)
    {
        int total = RowCount();
        int rows = BodyRows;
        if (total <= rows) return;

        int cellH = App?.CellPixelH ?? 0;
        double trackPx, thumbTop;
        int usable = Math.Max(1, trackH - ThumbH(trackH));
        if (ev.Py >= 0 && cellH > 0)
        {
            trackPx = Math.Max(1, usable * cellH);
            thumbTop = ev.Py - trackTop * cellH - _grabDeltaPx;
        }
        else
        {
            trackPx = usable;
            thumbTop = ev.Y - trackTop - _grabDelta;
        }
        double frac = Math.Clamp(thumbTop / trackPx, 0, 1);
        ScrollTo((int)Math.Round(frac * (total - rows)));
    }

    private static string Align(string s, int w, ColumnAlign a)
    {
        if (s.Length >= w) return s;
        int pad = w - s.Length;
        return a switch
        {
            ColumnAlign.Right => new string(' ', pad) + s,
            ColumnAlign.Center => new string(' ', pad / 2) + s + new string(' ', pad - pad / 2),
            _ => s + new string(' ', pad),
        };
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, QxTui.Render.Cell.Empty);

        int total = RowCount();
        int contentW = Math.Max(1, r.W - TrackW);
        LayoutColumns(contentW);
        int rows = BodyRows;
        Offset = Math.Clamp(Offset, 0, Math.Max(0, total - rows));
        SelectedRow = Math.Clamp(SelectedRow, 0, Math.Max(0, total - 1));
        SelectedCol = Math.Clamp(SelectedCol, 0, Math.Max(0, Columns.Count - 1));
        int bodyY = r.Y + HeaderRows;

        // hardware scroll: the hint rect is the BODY only — the header must
        // not travel with the shifted region
        int deltaCells = Offset - _lastRenderedOffset;
        _lastRenderedOffset = Offset;
        if (deltaCells != 0 && rows > 0)
            App?.ScrollHint(new Rect(r.X, bodyY, contentW, Math.Max(0, r.Bottom - bodyY)),
                deltaCells);

        int accent = Theme.Get(ThemeRole.Accent);
        int dim = Theme.Get(ThemeRole.Dim);
        int faint = Theme.Get(ThemeRole.Faint);
        int panelBg = Theme.Get(ThemeRole.PanelBg);

        if (ShowHeader)
        {
            for (int i = 0; i < Columns.Count; i++)
            {
                var c = Columns[i];
                // sort indicator lives inside the header cell
                string mark = i == SortColumn ? (SortDescending ? " ▼" : " ▲") : "";
                buffer.WriteTextClipped(Clip, r.X + _starts[i], r.Y,
                    Align(Text.Ellipsize(c.Header + mark, _widths[i]), _widths[i], c.Align),
                    fg: i == SelectedCol ? accent : Theme.Get(ThemeRole.Title), bold: true);
                if (i < Columns.Count - 1)
                    buffer.WriteTextClipped(Clip, r.X + _starts[i] + _widths[i], r.Y, "│", fg: dim);
            }
            for (int x = 0; x < contentW; x++)
                buffer.WriteTextClipped(Clip, r.X + x, r.Y + 1, "─", fg: faint);
        }

        int last = Math.Min(total, Offset + rows);
        for (int row = Offset; row < last; row++)
        {
            int y = bodyY + (row - Offset);
            bool selRow = row == SelectedRow && Focused;
            if (selRow)
                buffer.FillRect(r.X, y, contentW, 1,
                    new Cell { Char = ' ', Fg = -1, Bg = panelBg });
            for (int ci = 0; ci < Columns.Count; ci++)
            {
                var col = Columns[ci];
                bool selCell = selRow && ci == SelectedCol;
                string text = Cell(row, ci) ?? "";
                int fg = selCell ? 232 : selRow ? Theme.Get(ThemeRole.Title) : Theme.Get(ThemeRole.Text);
                int bg = selCell ? accent : selRow ? panelBg : -1;
                buffer.WriteTextClipped(Clip, r.X + _starts[ci], y,
                    Align(Text.Ellipsize(text, _widths[ci]), _widths[ci], col.Align),
                    fg: fg, bg: bg);
                if (ci < Columns.Count - 1)
                    buffer.WriteTextClipped(Clip, r.X + _starts[ci] + _widths[ci], y, "│",
                        fg: dim, bg: selRow ? panelBg : -1);
            }
        }

        if (!ShowScrollbar) return;
        VScroll.Draw(buffer, Clip, r.Right - 1, bodyY, Math.Max(0, r.Bottom - bodyY),
            Offset, rows, total, accent, faint);
    }
}
