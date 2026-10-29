using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>Clickable tab strip bound to a Router. Active tab is a filled
/// chip; hover brightens; keys are up to the app (see TabBar.Keys).</summary>
public sealed class TabBar : Widget
{
    private readonly Router _router;
    private readonly string[] _tabs;
    private int _hover = -1;

    public TabBar(Router router, string[] tabs)
    {
        _router = router;
        _tabs = tabs;
        Clickable = true;
        OnSignal(router.Changed); // repaint on ANY switch, keyboard or click
    }

    public TabBar() : this(new Router(), []) { }

    public IReadOnlyList<string> Tabs => _tabs;

    public override bool OnMouse(MouseEvent ev)
    {
        int idx = TabAt(ev.X);
        if (ev.Kind == MouseKind.Motion && idx != _hover)
        {
            _hover = idx;
            Invalidate();
        }
        return false;
    }

    public override void OnClick(Point local)
    {
        int idx = TabAt(local.X + Rect.X);
        if (idx >= 0)
            _router.Switch(_tabs[idx]);
    }

    private int TabAt(int screenX)
    {
        int x = Rect.X;
        for (int i = 0; i < _tabs.Length; i++)
        {
            int w = _tabs[i].Length + 2; // " tab " label width
            if (screenX >= x && screenX < x + w)
                return i;
            x += w + 1; // label + separator gap, matches Render
        }
        return -1;
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = ' ', Fg = -1, Bg = Theme.Get(ThemeRole.Faint) });

        int x = r.X;
        for (int i = 0; i < _tabs.Length; i++)
        {
            string label = $" {_tabs[i]} ";
            bool active = _tabs[i] == _router.Current;
            int bg = active ? Theme.Get(ThemeRole.Accent)
                   : _hover == i ? Theme.Get(ThemeRole.PanelBg)
                   : Theme.Get(ThemeRole.Faint);
            int fg = active ? 232 : Theme.Get(_hover == i ? ThemeRole.Title : ThemeRole.Dim);

            buffer.WriteTextClipped(Clip, x, r.Y, label, fg: fg, bg: bg, bold: active);
            x += label.Length + 1;
        }
    }
}
