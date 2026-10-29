using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>Bound boolean toggle: [x]/[ ] plus label. State lives in a
/// Signal (the state layer), so several widgets can share one checkbox's
/// truth and settings screens stay testable headless.</summary>
public sealed class Checkbox : Widget
{
    private readonly Signal<bool> _value;
    private readonly Func<string> _label;

    public Checkbox(Signal<bool> value, Func<string> label)
    {
        _value = value;
        _label = label;
        Clickable = true;
        Focusable = true;
        OnSignal(value);
        Node.Height = 1;
    }

    public Checkbox(Signal<bool> value, string label) : this(value, () => label) { }

    public override void OnClick(Point local) => _value.Set(!_value.Value);

    public override bool HandleKey(KeyEvent ev)
    {
        if (Focused && ev.Key == Key.Space)
        {
            _value.Set(!_value.Value);
            return true;
        }
        return false;
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        int fg = Theme.Get(Hovered ? ThemeRole.Title
            : Focused ? ThemeRole.Accent : ThemeRole.Text);
        buffer.WriteTextClipped(Clip, r.X, r.Y, _value.Value ? "[x] " : "[ ] ",
            fg: fg, bold: Hovered || Pressed);
        buffer.WriteTextClipped(Clip, r.X + 4, r.Y,
            QxTui.Render.Text.Ellipsize(_label(), Math.Max(0, r.W - 4)), fg: fg);
    }
}
