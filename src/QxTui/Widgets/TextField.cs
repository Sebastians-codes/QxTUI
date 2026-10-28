using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>
/// Single-line text input with cursor, editing keys and blink.
/// Focusable: receives keys via HandleKey while focused. Tab is NOT
/// consumed (it moves focus); Enter fires <see cref="Submitted"/>.
/// Long content scrolls a window around the cursor.
/// </summary>
public sealed class TextField : Widget
{
    private readonly List<char> _chars = [];
    private int _cursor;
    private bool _blinkOn = true;

    public string Text => new([.. _chars]);
    public int Cursor => _cursor;
    public string? Placeholder { get; set; }

    public event Action<string>? Changed;
    public event Action<string>? Submitted;

    public TextField(string initial = "")
    {
        _chars.AddRange(initial);
        _cursor = _chars.Count;
        Focusable = true;
        Clickable = true;
        Tick(TimeSpan.FromMilliseconds(500), _ =>
        {
            _blinkOn = !_blinkOn;
            if (Focused) Invalidate();
        });
    }

    public void SetText(string value)
    {
        _chars.Clear();
        _chars.AddRange(value);
        _cursor = _chars.Count;
        Invalidate();
    }

    public override void OnClick(Point local)
    {
        // click positions the cursor (approximate: 1 char per column)
        _cursor = Math.Clamp(local.X - 1, 0, _chars.Count);
        Invalidate();
    }

    public override bool HandleKey(KeyEvent ev)
    {
        if (!Focused)
            return false;

        switch (ev.Key)
        {
            case Key.Backspace:
                if (_cursor > 0)
                {
                    _chars.RemoveAt(_cursor - 1);
                    _cursor--;
                    Emit();
                }
                return true;

            case Key.Left:
                _cursor = Math.Max(0, _cursor - 1);
                Invalidate();
                return true;

            case Key.Right:
                _cursor = Math.Min(_chars.Count, _cursor + 1);
                Invalidate();
                return true;

            case Key.Home:
                _cursor = 0;
                Invalidate();
                return true;

            case Key.End:
                _cursor = _chars.Count;
                Invalidate();
                return true;

            case Key.Enter:
                Submitted?.Invoke(Text);
                App?.Focus(null); // confirm and hand focus back to the app
                return true;

            case Key.Space:
                _chars.Insert(_cursor, ' ');
                _cursor++;
                Emit();
                return true;

            case Key.Char when ev.Char >= ' ':
                _chars.Insert(_cursor, ev.Char);
                _cursor++;
                Emit();
                return true;

            default:
                return false; // Tab etc. fall through to app/focus handling
        }
    }

    private void Emit()
    {
        Changed?.Invoke(Text);
        Invalidate();
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H,
            new Cell { Char = ' ', Fg = -1, Bg = Theme.Get(ThemeRole.PanelBg) });

        int innerW = Math.Max(1, r.W - 2);
        int viewStart = 0;
        if (_cursor >= viewStart + innerW)
            viewStart = _cursor - innerW + 1;

        if (_chars.Count == 0 && Placeholder is { } ph && !Focused)
            buffer.WriteTextClipped(Clip, r.X + 1, r.Y, ph, fg: Theme.Get(ThemeRole.Faint));

        var slice = _chars.GetRange(viewStart, Math.Min(innerW, _chars.Count - viewStart));
        buffer.WriteTextClipped(Clip, r.X + 1, r.Y, new string([.. slice]),
            fg: Theme.Get(ThemeRole.Text), bg: Theme.Get(ThemeRole.PanelBg));

        if (Focused)
        {
            int cx = r.X + 1 + (_cursor - viewStart);
            char under = _cursor < _chars.Count ? _chars[_cursor] : ' ';
            if (_blinkOn)
                buffer.WriteTextClipped(Clip, cx, r.Y, under.ToString(),
                    fg: 232, bg: Theme.Get(ThemeRole.Title), bold: true);
            else
                buffer.WriteTextClipped(Clip, cx, r.Y, under.ToString(),
                    fg: Theme.Get(ThemeRole.Title), bg: Theme.Get(ThemeRole.PanelBg));
        }
    }
}
