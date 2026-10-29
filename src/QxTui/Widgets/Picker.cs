using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>Floating single-choice list: arrows/mouse move the cursor,
/// Enter commits, Esc or an outside press closes. Lines are a LIVE
/// provider — a background refresh repopulates an open picker without
/// reopening it.
///
/// Opt-in search (<paramref name="search"/>): a query line appears under
/// the title, printable keys edit it (Backspace deletes), and the visible
/// lines become <c>search(query)</c>. Enter with NO match calls
/// <paramref name="onCreate"/> with the query — the fzf-style "create
/// what you typed" gesture (qxgit creates branches this way).
///
/// The commit callback receives (index, query): index into the CURRENT
/// filtered lines, so callers map through the same pure filter function
/// they handed to <paramref name="search"/>.</summary>
public sealed class Picker : Widget
{
    private readonly string _title;
    private readonly Func<IReadOnlyList<string>> _lines;
    private readonly Action<int, string> _commit;
    private readonly Action<char, int, string>? _extraKey;
    private readonly Func<string, IReadOnlyList<string>>? _search;
    private readonly Action<string>? _onCreate;
    private string _query = "";
    private int _cursor;

    public Picker(string title, Func<IReadOnlyList<string>> lines,
        Action<int, string> commit, Action<char, int, string>? extraKey = null,
        Func<string, IReadOnlyList<string>>? search = null,
        Action<string>? onCreate = null)
    {
        _title = title;
        _lines = lines;
        _commit = commit;
        _extraKey = extraKey;
        _search = search;
        _onCreate = onCreate;
        OutsideClickCloses = true;
    }

    private IReadOnlyList<string> Lines() =>
        _search is null ? _lines() : _search(_query);

    private int SearchRows => _search is null ? 0 : 1;

    private Rect BoxRect(Rect screen)
    {
        var lines = Lines();
        int w = Math.Max(12, _title.Length + 6);
        for (int i = 0; i < lines.Count; i++)
            w = Math.Max(w, lines[i].Length + 4);
        w = Math.Min(w, screen.W - 4);
        int h = Math.Min(lines.Count + 2 + SearchRows, Math.Max(5, screen.H - 6));
        return new Rect((screen.W - w) / 2, (screen.H - h) / 2, w, h);
    }

    public override bool HitTest(int x, int y) => BoxRect(Rect).Contains(x, y);
    internal override bool OwnsPoint(int x, int y) => BoxRect(Rect).Contains(x, y);

    public override bool HandleKey(KeyEvent ev)
    {
        int n = Lines().Count;
        switch (ev.Key)
        {
            case Key.Escape:
                App?.PopOverlay(this);
                return true;
            case Key.Up:
                if (n > 0) _cursor = (_cursor - 1 + n) % n;
                Invalidate();
                return true;
            case Key.Down:
                if (n > 0) _cursor = (_cursor + 1) % n;
                Invalidate();
                return true;
            case Key.Backspace when _search is not null:
                if (_query.Length > 0)
                {
                    _query = _query[..^1];
                    _cursor = 0;
                }
                Invalidate();
                return true;
            case Key.Enter:
                if (n > 0)
                    _commit(Math.Clamp(_cursor, 0, n - 1), _query);
                else if (_search is not null && _onCreate is not null && _query.Trim().Length > 0)
                    _onCreate(_query.Trim()); // typed what doesn't exist → create it
                return true;
            // control chars (ctrl-d = \x04, …) are COMMANDS even while
            // searching — printable chars type into the query instead
            case Key.Char when _extraKey is not null &&
                               (_search is null || ev.Char < ' '):
                _extraKey(ev.Char, Math.Clamp(_cursor, 0, Math.Max(0, n - 1)), _query);
                return true;
            case Key.Char when _search is not null && ev.Char >= ' ':
                _query += ev.Char;   // search mode: typing edits the query
                _cursor = 0;
                Invalidate();
                return true;
            default:
                return true; // swallow: the picker owns the keyboard
        }
    }

    public override bool OnMouse(MouseEvent ev)
    {
        var b = BoxRect(Rect);
        int i = ev.Y - b.Y - 1 - SearchRows;
        int n = Lines().Count;
        if (ev.Kind == MouseKind.Motion && i >= 0 && i < n && i != _cursor)
        {
            _cursor = i;
            Invalidate();
            return true;
        }
        if (ev.Kind == MouseKind.Release && i >= 0 && i < n)
        {
            _commit(i, _query);
            return true;
        }
        return ev.Kind != MouseKind.Press || b.Contains(ev.X, ev.Y);
    }

    public override void Render(CellBuffer buffer)
    {
        var b = BoxRect(Rect);
        var lines = Lines();
        buffer.FillRect(b.X, b.Y, b.W, b.H,
            new Cell { Char = ' ', Fg = Theme.Get(ThemeRole.Text), Bg = Theme.Get(ThemeRole.PanelBg) });
        Box.Draw(buffer, b.X, b.Y, b.W, b.H, BoxStyles.Single,
            Theme.Get(ThemeRole.Border), _title, Theme.Get(ThemeRole.Title));

        int firstLine = b.Y + 1;
        if (_search is not null)
        {
            // one run for prompt+query so the stream keeps "> query"
            buffer.WriteTextClipped(Clip, b.X + 1, b.Y + 1,
                "> " + Text.Ellipsize(_query, b.W - 5),
                fg: Theme.Get(ThemeRole.Accent), bg: Theme.Get(ThemeRole.PanelBg));
            int cx = b.X + 3 + Math.Min(_query.Length, b.W - 5);
            buffer.WriteTextClipped(Clip, cx, b.Y + 1, " ",
                fg: 232, bg: Theme.Get(ThemeRole.Accent));
            firstLine = b.Y + 2;
        }

        int rows = b.H - 2 - SearchRows;
        int first = Math.Max(0, Math.Min(_cursor - (rows - 1), lines.Count - rows));
        for (int i = 0; i < rows && first + i < lines.Count; i++)
        {
            bool cur = first + i == _cursor;
            buffer.WriteTextClipped(Clip, b.X + 1, firstLine + i,
                " " + Text.Ellipsize(lines[first + i], b.W - 3) + " ",
                fg: cur ? 232 : Theme.Get(ThemeRole.Text),
                bg: cur ? Theme.Get(ThemeRole.Accent) : Theme.Get(ThemeRole.PanelBg),
                bold: cur);
        }
    }

    internal override void CollectPaintedRects(List<Rect> dst) => dst.Add(BoxRect(Rect));
}
