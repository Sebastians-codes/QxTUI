using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>Compact one-of-many chooser: a closed one-row control that
/// opens a floating option list (an overlay layer) anchored under itself.
/// The popup closes on outside press, Escape, or a choice — all via
/// features the overlay stack already has.</summary>
public sealed class Dropdown : Widget
{
    private readonly Signal<int> _selected;
    private readonly Func<string[]> _options;

    public Dropdown(Signal<int> selected, Func<string[]> options)
    {
        _selected = selected;
        _options = options;
        Clickable = true;
        Focusable = true;
        OnSignal(selected);
        Tick(TimeSpan.FromMilliseconds(250)); // reactive option lists
        Node.Height = 1;
    }

    public Dropdown(Signal<int> selected, params string[] options)
        : this(selected, () => options) { }

    public string Current =>
        _options()[Math.Clamp(_selected.Value, 0, Math.Max(0, _options().Length - 1))];

    public override void OnClick(Point local) => Toggle();

    public override bool HandleKey(KeyEvent ev)
    {
        if (!Focused) return false;
        if (ev.Key is Key.Enter or Key.Space or Key.Down)
        {
            Toggle();
            return true;
        }
        return false;
    }

    private void Toggle()
    {
        if (App is null) return;
        if (App.Overlay is DropdownPopup p && ReferenceEquals(p.Owner, this))
        {
            App.PopOverlay(p);
            return;
        }
        App.PushOverlay(new DropdownPopup(this, _selected, _options()));
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        int fg = Theme.Get(Hovered || Focused ? ThemeRole.Title : ThemeRole.Text);
        string label = QxTui.Render.Text.Ellipsize($" {Current} ▾", Math.Max(0, r.W - 2));
        buffer.WriteTextClipped(Clip, r.X, r.Y, label, fg: fg,
            bg: Theme.Get(ThemeRole.PanelBg), bold: Hovered);
        buffer.WriteTextClipped(Clip, r.X + r.W - 1, r.Y, " ",
            bg: Theme.Get(ThemeRole.PanelBg));
    }
}
