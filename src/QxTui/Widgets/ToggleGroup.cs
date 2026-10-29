using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>Horizontal any-of-N toggle chips bound to a
/// Signal&lt;HashSet&lt;int&gt;&gt; — the Segmented's sibling for
/// multi-select: chips light independently, Space/Enter toggles the
/// keyboard cursor's chip, a click toggles the clicked one. The signal
/// carries a fresh set per write (Signal notifies on reference change).</summary>
public sealed class ToggleGroup : Widget
{
    private readonly Signal<HashSet<int>> _on;
    private readonly string[] _options;
    private int _cursor;

    public ToggleGroup(Signal<HashSet<int>> on, params string[] options)
    {
        _on = on;
        _options = options;
        Clickable = true;
        Focusable = true;
        OnSignal(on);
        Node.Height = 1;
    }

    /// <summary>Chip index under a widget-local x, or -1. Layout source
    /// for both render and hit-testing: ' lab ' + 1-col separators
    /// (same geometry as Segmented).</summary>
    private int ChipAt(int localX)
    {
        int x = 0;
        for (int i = 0; i < _options.Length; i++)
        {
            int w = _options[i].Length + 2;
            if (localX >= x && localX < x + w) return i;
            x += w + 1;
        }
        return -1;
    }

    private void Toggle(int i)
    {
        var next = new HashSet<int>(_on.Value);
        if (!next.Add(i)) next.Remove(i);
        _on.Set(next);
    }

    public bool IsOn(int i) => _on.Value.Contains(i);

    public override void OnClick(Point local)
    {
        int i = ChipAt(local.X);
        if (i >= 0)
        {
            _cursor = i;
            Toggle(i);
        }
    }

    public override bool HandleKey(KeyEvent ev)
    {
        if (!Focused || _options.Length == 0) return false;
        switch (ev.Key)
        {
            case Key.Right:
                _cursor = Math.Min(_options.Length - 1, _cursor + 1);
                Invalidate();
                return true;
            case Key.Left:
                _cursor = Math.Max(0, _cursor - 1);
                Invalidate();
                return true;
            case Key.Enter:
            case Key.Space:
                Toggle(_cursor);
                return true;
            default:
                return false;
        }
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        int x = r.X;
        for (int i = 0; i < _options.Length; i++)
        {
            bool on = _on.Value.Contains(i);
            bool cur = i == _cursor && Focused;
            int fg = on ? 232 : Theme.Get(cur ? ThemeRole.Title
                : Hovered ? ThemeRole.Title : ThemeRole.Text);
            int bg = on ? Theme.Get(ThemeRole.Accent) : -1;
            buffer.WriteTextClipped(Clip, x, r.Y, $" {_options[i]} ",
                fg: fg, bg: bg, bold: on || cur);
            x += _options[i].Length + 2;
            if (i < _options.Length - 1)
            {
                buffer.WriteTextClipped(Clip, x, r.Y, "│", fg: Theme.Get(ThemeRole.Dim));
                x += 1;
            }
        }
    }
}
