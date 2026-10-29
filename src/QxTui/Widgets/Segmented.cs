using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>Radio group as a segmented control: exactly one of N options,
/// bound to a Signal&lt;int&gt;. Click a segment or use arrows while
/// focused.</summary>
public sealed class Segmented : Widget
{
    private readonly Signal<int> _selected;
    private readonly string[] _options;

    public Segmented(Signal<int> selected, params string[] options)
    {
        _selected = selected;
        _options = options;
        Clickable = true;
        Focusable = true;
        OnSignal(selected);
        Node.Height = 1;
    }

    /// <summary>Segment index under a widget-local x, or -1. Layout source
    /// for both render and hit-testing: ' lab ' + 1-col separators.</summary>
    private int SegmentAt(int localX)
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

    public override void OnClick(Point local)
    {
        int i = SegmentAt(local.X);
        if (i >= 0) _selected.Set(i);
    }

    public override bool HandleKey(KeyEvent ev)
    {
        if (!Focused) return false;
        if (ev.Key == Key.Right)
        {
            _selected.Set(Math.Min(_options.Length - 1, _selected.Value + 1));
            return true;
        }
        if (ev.Key == Key.Left)
        {
            _selected.Set(Math.Max(0, _selected.Value - 1));
            return true;
        }
        return false;
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        int x = r.X;
        for (int i = 0; i < _options.Length; i++)
        {
            bool sel = i == _selected.Value;
            int fg = sel ? 232 : Theme.Get(Hovered ? ThemeRole.Title : ThemeRole.Text);
            int bg = sel ? Theme.Get(ThemeRole.Accent) : -1;
            buffer.WriteTextClipped(Clip, x, r.Y, $" {_options[i]} ", fg: fg, bg: bg, bold: sel);
            x += _options[i].Length + 2;
            if (i < _options.Length - 1)
            {
                buffer.WriteTextClipped(Clip, x, r.Y, "│", fg: Theme.Get(ThemeRole.Dim));
                x += 1;
            }
        }
    }
}
