using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>Vertical one-of-N picker bound to a Signal&lt;int&gt; — the
/// Checkbox's sibling for exclusive choice: arrows move the selection
/// (radio semantics: moving IS choosing), a click on a row picks it.
/// Segmented is the horizontal compact form; this is the readable list.</summary>
public sealed class RadioGroup : Widget
{
    private readonly Signal<int> _selected;
    private readonly string[] _options;

    public RadioGroup(Signal<int> selected, params string[] options)
    {
        _selected = selected;
        _options = options;
        Clickable = true;
        Focusable = true;
        OnSignal(selected);
        Node.Height = Math.Max(1, options.Length);
    }

    public override void OnClick(Point local)
    {
        if (local.Y >= 0 && local.Y < _options.Length)
            _selected.Set(local.Y);
    }

    public override bool HandleKey(KeyEvent ev)
    {
        if (!Focused || _options.Length == 0) return false;
        switch (ev.Key)
        {
            case Key.Down:
            case Key.Right:
                _selected.Set(Math.Min(_options.Length - 1, _selected.Value + 1));
                return true;
            case Key.Up:
            case Key.Left:
                _selected.Set(Math.Max(0, _selected.Value - 1));
                return true;
            default:
                return false;
        }
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        for (int i = 0; i < _options.Length && i < r.H; i++)
        {
            bool sel = i == _selected.Value;
            int labelFg = Theme.Get(Hovered ? ThemeRole.Title : ThemeRole.Text);
            buffer.WriteTextClipped(Clip, r.X, r.Y + i, sel ? "(•) " : "( ) ",
                fg: sel ? Theme.Get(ThemeRole.Accent) : labelFg, bold: sel);
            buffer.WriteTextClipped(Clip, r.X + 4, r.Y + i,
                QxTui.Render.Text.Ellipsize(_options[i], Math.Max(0, r.W - 4)),
                fg: labelFg);
        }
    }
}
