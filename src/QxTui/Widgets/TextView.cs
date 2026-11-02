using System.Text;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>
/// Read-only, scrollable, selectable text: logs, command output, diffs. Lines come from
/// two delegates (<c>count</c> and <c>line</c>), so a million-line buffer costs only what
/// is on screen.
///
/// <para>Long lines either wrap at the view width (<see cref="Wrap"/>, the default) or
/// scroll sideways (←/→, shift+wheel). Vertical scrolling counts wrapped rows, with the
/// same braille scrollbar as <see cref="ListView"/>. With <see cref="FollowTail"/> the
/// view stays pinned to the newest line until the user scrolls up; End (or scrolling to
/// the bottom) pins it again.</para>
///
/// <para>Drag with the mouse to select, double-click to select a line, Ctrl+A for
/// everything. <see cref="Widget.SelectedText"/> is what Ctrl+C copies (the original
/// lines, never wrap breaks), and the right-click menu offers copy, select all and jump
/// to end. Selection survives lines being trimmed from the front when
/// <c>trimmed</c> reports how many have been dropped so far.</para>
/// </summary>
public sealed class TextView : Widget
{
    private const int WheelStep = 3;
    private const int KeyStepX = 8;
    private const int ToEndOfLine = int.MaxValue / 2;
    private const int SelectedFg = 232; // dark text on the accent background, like Button

    private readonly Func<int> _count;
    private readonly Func<int, string> _line;
    private readonly Func<long>? _trimmed;

    private int _offset;      // first visible wrapped row
    private int _offsetX;     // first visible column (no-wrap mode)
    private bool _pinned = true;
    private bool _dragging;
    private bool _hasSelection;
    private bool _wrap = true;

    // Selection ends as (line id, cell column within the whole line). A line id is its
    // index plus the lines trimmed so far, so a selection stays on its text when the
    // oldest lines are dropped.
    private (long Line, int Col) _anchor;
    private (long Line, int Col) _head;

    // Wrap layout: first wrapped row of each line, valid for the width/mode it was built at.
    private int[] _starts = [];
    private int _lineCount;
    private int _totalRows;
    private int _maxLineWidth;
    private int _layoutWidth = -1;
    private bool _layoutWrap;
    private long _layoutTrimmed = -1;

    /// <summary>Creates a view over <paramref name="count"/> lines.</summary>
    /// <param name="count">Current number of lines (read every render).</param>
    /// <param name="line">The text of line <c>i</c>. Must not contain newlines.</param>
    /// <param name="trimmed">Lines dropped from the front so far, if the source trims.</param>
    public TextView(Func<int> count, Func<int, string> line, Func<long>? trimmed = null)
    {
        _count = count;
        _line = line;
        _trimmed = trimmed;
        Focusable = true;
        Clickable = true;
        CapturesMouse = true; // drags keep selecting after leaving the widget
        MenuAt = _ => [.. CopyMenu(), new MenuItem("jump to end", Act: ScrollToEnd, Hint: "end")];
    }

    /// <summary>Wrap long lines at the view width (default) instead of scrolling sideways.</summary>
    public bool Wrap
    {
        get => _wrap;
        set
        {
            if (_wrap == value) return;
            _wrap = value;
            _offsetX = 0;
            Invalidate();
        }
    }

    /// <summary>Stay on the newest line while the user has not scrolled up (logs). Off for static text.</summary>
    public bool FollowTail { get; set; }

    /// <summary>True while the view is pinned to the tail.</summary>
    public bool Following => FollowTail && _pinned;

    /// <summary>Optional per-line look: the theme role and boldness of a whole line.</summary>
    public Func<string, (ThemeRole Role, bool Bold)>? LineStyle { get; set; }

    /// <summary>First visible wrapped row.</summary>
    public int Offset => _offset;

    /// <summary>First visible column (only moves when <see cref="Wrap"/> is off).</summary>
    public int OffsetX => _offsetX;

    /// <summary>Total wrapped rows at the current width.</summary>
    public int TotalRows => EnsureLayout();

    /// <summary>Whether any text is selected.</summary>
    public bool HasSelection => _hasSelection;

    private int TextX => Rect.X + 1;
    private int Rows => Math.Max(1, Rect.H);
    private int TextWidth => Math.Max(1, Rect.W - 2); // one column of padding left, one for the scrollbar

    /// <summary>Back to the top (or the tail with <see cref="FollowTail"/>) with no selection.
    /// Call when the content is replaced.</summary>
    public void Reset()
    {
        _offset = 0;
        _offsetX = 0;
        _pinned = true;
        _dragging = false;
        SetSelected(false);
        Invalidate();
    }

    /// <summary>Scrolls by wrapped rows (negative = up).</summary>
    public void ScrollBy(int rows)
    {
        int max = MaxOffset();
        _offset = Math.Clamp((Following ? max : _offset) + rows, 0, max);
        _pinned = _offset >= max;
        Invalidate();
    }

    /// <summary>Scrolls sideways by columns (no-wrap mode).</summary>
    public void ScrollXBy(int columns)
    {
        if (_wrap) return;
        _offsetX = Math.Clamp(_offsetX + columns, 0, Math.Max(0, _maxLineWidth - TextWidth));
        Invalidate();
    }

    /// <summary>Jumps to the last line (and pins to it with <see cref="FollowTail"/>).</summary>
    public void ScrollToEnd() => ScrollBy(int.MaxValue / 2);

    /// <summary>Jumps to the first row.</summary>
    public void ScrollToTop() => ScrollBy(-(int.MaxValue / 2));

    /// <summary>Drops the selection.</summary>
    public void ClearSelection()
    {
        SetSelected(false);
        Invalidate();
    }

    /// <inheritdoc />
    public override bool SelectAll()
    {
        int count = _count();
        if (count == 0) return false;
        long first = Trimmed();
        _anchor = (first, 0);
        _head = (first + count - 1, ToEndOfLine);
        SetSelected(true);
        Invalidate();
        return true;
    }

    /// <inheritdoc />
    public override string? SelectedText
    {
        get
        {
            if (!_hasSelection) return null;
            var (start, end) = Ordered();
            long trimmed = Trimmed();
            int count = _count();
            var text = new StringBuilder();
            for (long id = Math.Max(start.Line, trimmed); id <= end.Line && id - trimmed < count; id++)
            {
                string line = _line((int)(id - trimmed));
                if (TryRange(id, line, out int from, out int to))
                    text.Append(line, from, to - from);
                if (id < end.Line)
                    text.Append('\n');
            }
            return text.ToString();
        }
    }

    #region Input

    /// <inheritdoc />
    public override bool OnMouse(MouseEvent ev)
    {
        switch (ev.Kind)
        {
            case MouseKind.WheelUp when ev.Shift:
                ScrollXBy(-KeyStepX / 2);
                return true;
            case MouseKind.WheelDown when ev.Shift:
                ScrollXBy(KeyStepX / 2);
                return true;
            case MouseKind.WheelUp:
                ScrollBy(-WheelStep);
                return true;
            case MouseKind.WheelDown:
                ScrollBy(WheelStep);
                return true;
            case MouseKind.WheelLeft:
                ScrollXBy(-KeyStepX / 2);
                return true;
            case MouseKind.WheelRight:
                ScrollXBy(KeyStepX / 2);
                return true;

            case MouseKind.Press when ev.Button == 0 && _count() > 0:
                _anchor = _head = PointAt(ev.X, ev.Y);
                _dragging = true;
                SetSelected(false);
                Invalidate();
                return false; // let the app focus us

            case MouseKind.Motion when _dragging:
                if (ev.Y < Rect.Y) ScrollBy(-1);
                else if (ev.Y >= Rect.Bottom) ScrollBy(1);
                _head = PointAt(ev.X, ev.Y);
                SetSelected(_head != _anchor);
                Invalidate();
                return true;

            case MouseKind.Release:
                _dragging = false;
                return false;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    public override void OnDoubleClick(Point local)
    {
        if (_count() == 0) return;
        var (line, _) = PointAt(Rect.X, Rect.Y + local.Y);
        _anchor = (line, 0);
        _head = (line, ToEndOfLine);
        _dragging = false;
        SetSelected(true);
        Invalidate();
    }

    /// <inheritdoc />
    public override bool HandleKey(KeyEvent ev)
    {
        switch (ev.Key)
        {
            case Key.Up: ScrollBy(-1); return true;
            case Key.Down: ScrollBy(1); return true;
            case Key.PageUp: ScrollBy(-Rows); return true;
            case Key.PageDown: ScrollBy(Rows); return true;
            case Key.Home: ScrollToTop(); return true;
            case Key.End: ScrollToEnd(); return true;
            case Key.Left when !_wrap: ScrollXBy(-KeyStepX); return true;
            case Key.Right when !_wrap: ScrollXBy(KeyStepX); return true;
            case Key.Escape when _hasSelection:
                ClearSelection();
                return true;
            default: return false;
        }
    }

    private void SetSelected(bool selected) => _hasSelection = selected;

    private (long Line, int Col) PointAt(int screenX, int screenY)
    {
        int total = EnsureLayout();
        long trimmed = Trimmed();
        if (total == 0) return (trimmed, 0);

        int row = Math.Clamp(_offset + Math.Clamp(screenY - Rect.Y, 0, Rows - 1), 0, total - 1);
        int index = LineAt(row);
        string text = _line(index);
        var segments = Segments(text, EffectiveWidth());
        var segment = segments[Math.Min(row - _starts[index], segments.Count - 1)];
        int column = UnicodeWidth.OfText(text.AsSpan(0, segment.Start)) + Math.Max(0, screenX - TextX) + (_wrap ? 0 : _offsetX);
        return (index + trimmed, column);
    }

    #endregion

    #region Selection geometry

    private ((long Line, int Col) Start, (long Line, int Col) End) Ordered() =>
        _anchor.CompareTo(_head) <= 0 ? (_anchor, _head) : (_head, _anchor);

    /// <summary>The characters of <paramref name="text"/> inside the selection, if the line is part of it.</summary>
    private bool TryRange(long lineId, string text, out int from, out int to)
    {
        from = to = 0;
        if (!_hasSelection) return false;

        var (start, end) = Ordered();
        if (lineId < start.Line || lineId > end.Line) return false;

        from = lineId == start.Line ? IndexAtColumn(text, start.Col, inclusiveEnd: false) : 0;
        to = lineId == end.Line ? IndexAtColumn(text, end.Col, inclusiveEnd: true) : text.Length;
        to = Math.Max(from, to);
        return true;
    }

    /// <summary>Cell column to char index, wide characters included. With
    /// <paramref name="inclusiveEnd"/> the result is just past the character under
    /// the column, so a selection includes the cell it ends on.</summary>
    private static int IndexAtColumn(string text, int column, bool inclusiveEnd)
    {
        int used = 0;
        for (int i = 0; i < text.Length;)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out int length);
            int width = UnicodeWidth.Of(rune);
            if (column < used + width)
                return inclusiveEnd ? i + length : i;
            used += width;
            i += length;
        }
        return text.Length;
    }

    #endregion

    #region Rendering

    /// <inheritdoc />
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        var theme = Theme;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);

        int total = EnsureLayout();
        int rows = Rows;
        int max = Math.Max(0, total - rows);
        _offset = Following ? max : Math.Clamp(_offset, 0, max);
        _offsetX = _wrap ? 0 : Math.Clamp(_offsetX, 0, Math.Max(0, _maxLineWidth - TextWidth));
        if (total == 0) return;

        long trimmed = Trimmed();
        int width = EffectiveWidth();
        var clip = new RenderClip(r.X, r.Y, Math.Max(0, r.W - 1), r.H); // last column is the scrollbar
        int index = LineAt(_offset);
        int sub = _offset - _starts[index];
        for (int y = 0; y < rows && index < _lineCount; index++, sub = 0)
        {
            string text = _line(index);
            var segments = Segments(text, width);
            for (; sub < segments.Count && y < rows; sub++, y++)
                PaintSegment(buffer, clip, theme, r.Y + y, index + trimmed, text, segments[sub], sub == segments.Count - 1);
        }

        if (total > rows)
            PaintScrollbar(buffer, theme, total, rows, max);
    }

    private void PaintSegment(CellBuffer buffer, RenderClip clip, Theme theme, int y, long lineId,
        string text, (int Start, int End) segment, bool lastOfLine)
    {
        var (role, bold) = LineStyle?.Invoke(text) ?? (ThemeRole.Text, false);
        int fg = theme.Get(role);
        var (start, end) = segment;
        int x0 = TextX - (_wrap ? 0 : _offsetX);

        if (!TryRange(lineId, text, out int from, out int to))
        {
            buffer.WriteTextClipped(clip, x0, y, text.AsSpan(start, end - start), fg: fg, bold: bold);
            return;
        }

        int selectedFrom = Math.Max(from, start);
        int selectedTo = Math.Min(to, end);
        int selectedBg = theme.Get(ThemeRole.Accent);
        int x = x0;

        if (selectedFrom < selectedTo)
        {
            buffer.WriteTextClipped(clip, x, y, text.AsSpan(start, selectedFrom - start), fg: fg, bold: bold);
            x += UnicodeWidth.OfText(text.AsSpan(start, selectedFrom - start));
            buffer.WriteTextClipped(clip, x, y, text.AsSpan(selectedFrom, selectedTo - selectedFrom),
                fg: SelectedFg, bg: selectedBg, bold: bold);
            x += UnicodeWidth.OfText(text.AsSpan(selectedFrom, selectedTo - selectedFrom));
            buffer.WriteTextClipped(clip, x, y, text.AsSpan(selectedTo, end - selectedTo), fg: fg, bold: bold);
            x += UnicodeWidth.OfText(text.AsSpan(selectedTo, end - selectedTo));
        }
        else
        {
            buffer.WriteTextClipped(clip, x, y, text.AsSpan(start, end - start), fg: fg, bold: bold);
            x += UnicodeWidth.OfText(text.AsSpan(start, end - start));
        }

        // a selection running on to the next line also covers this line's newline
        if (lastOfLine && lineId < Ordered().End.Line)
            buffer.WriteTextClipped(clip, x, y, " ", bg: selectedBg);
    }

    /// <summary>Same scrollbar as <see cref="ListView"/>: a faint track and an all-braille
    /// thumb that moves in quarter-row steps.</summary>
    private void PaintScrollbar(CellBuffer buffer, Theme theme, int total, int rows, int max)
    {
        var r = Rect;
        int x = r.Right - 1;
        var full = new RenderClip(r.X, r.Y, r.W, r.H);
        int accent = theme.Get(ThemeRole.Accent);

        for (int y = r.Y; y < r.Bottom; y++)
            buffer.WriteTextClipped(full, x, y, "│", fg: theme.Get(ThemeRole.Faint));

        int thumbHeight = Math.Max(1, rows * rows / total);
        int quarter = (int)Math.Round((double)_offset * (r.H - thumbHeight) / Math.Max(1, max) * 4);
        int top = r.Y + quarter / 4;
        int remainder = quarter % 4;

        if (remainder == 0)
        {
            for (int y = top; y < Math.Min(r.Bottom, top + thumbHeight); y++)
                buffer.WriteTextClipped(full, x, y, BrailleBand(0, 3), fg: accent);
            return;
        }

        if (top < r.Bottom)
            buffer.WriteTextClipped(full, x, top, BrailleBand(remainder, 3), fg: accent);
        for (int y = top + 1; y < Math.Min(r.Bottom, top + thumbHeight); y++)
            buffer.WriteTextClipped(full, x, y, BrailleBand(0, 3), fg: accent);
        int last = top + thumbHeight;
        if (last < r.Bottom)
            buffer.WriteTextClipped(full, x, last, BrailleBand(0, remainder - 1), fg: accent);
    }

    /// <summary>Braille glyph with both dot columns filled over dot-rows from..to (0..3, top-down).</summary>
    private static string BrailleBand(int from, int to)
    {
        ReadOnlySpan<int> left = [0x01, 0x02, 0x04, 0x40];
        ReadOnlySpan<int> right = [0x08, 0x10, 0x20, 0x80];
        int bits = 0;
        for (int row = from; row <= to; row++)
            bits |= left[row] | right[row];
        return ((char)(0x2800 + bits)).ToString();
    }

    #endregion

    #region Wrap layout

    private long Trimmed() => _trimmed?.Invoke() ?? 0;
    private int EffectiveWidth() => _wrap ? TextWidth : int.MaxValue;
    private int MaxOffset() => Math.Max(0, EnsureLayout() - Rows);

    /// <summary>Builds (or extends) the row layout and returns the total wrapped rows.</summary>
    private int EnsureLayout()
    {
        int count = _count();
        long trimmed = Trimmed();
        int width = TextWidth;

        if (width != _layoutWidth || _wrap != _layoutWrap || trimmed != _layoutTrimmed || count < _lineCount)
        {
            (_layoutWidth, _layoutWrap, _layoutTrimmed) = (width, _wrap, trimmed);
            _lineCount = 0;
            _totalRows = 0;
            _maxLineWidth = 0;
        }

        if (_starts.Length < count)
            Array.Resize(ref _starts, Math.Max(count, _starts.Length * 2));

        int effective = EffectiveWidth();
        for (; _lineCount < count; _lineCount++)
        {
            string text = _line(_lineCount);
            _starts[_lineCount] = _totalRows;
            _totalRows += CountRows(text, effective);
            if (!_wrap)
                _maxLineWidth = Math.Max(_maxLineWidth, UnicodeWidth.OfText(text));
        }
        return _totalRows;
    }

    /// <summary>Index of the line containing wrapped row <paramref name="row"/>.</summary>
    private int LineAt(int row)
    {
        int index = Array.BinarySearch(_starts, 0, _lineCount, row);
        return Math.Max(0, index >= 0 ? index : ~index - 1);
    }

    private static int CountRows(string text, int width)
    {
        if (width == int.MaxValue) return 1;
        int rows = 1, used = 0;
        for (int i = 0; i < text.Length;)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out int length);
            int w = UnicodeWidth.Of(rune);
            if (used + w > width && used > 0)
            {
                rows++;
                used = 0;
            }
            used += w;
            i += length;
        }
        return rows;
    }

    /// <summary>Char ranges of a line's wrapped rows (at least one, even when empty).</summary>
    private static List<(int Start, int End)> Segments(string text, int width)
    {
        var segments = new List<(int, int)>(1);
        if (width == int.MaxValue)
        {
            segments.Add((0, text.Length));
            return segments;
        }

        int start = 0, used = 0;
        for (int i = 0; i < text.Length;)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out int length);
            int w = UnicodeWidth.Of(rune);
            if (used + w > width && used > 0)
            {
                segments.Add((start, i));
                start = i;
                used = 0;
            }
            used += w;
            i += length;
        }
        segments.Add((start, text.Length));
        return segments;
    }

    #endregion
}
