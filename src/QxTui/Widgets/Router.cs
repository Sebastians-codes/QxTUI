using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>
/// SPA-style view routing: register named pages, Switch() between them.
/// Pages are instantiated lazily and CACHED — switching away detaches
/// without disposing, so a page's state (signals, scroll, game progress)
/// survives round-trips. Mount/dispose cascades handle lifecycle; only the
/// active page is in the widget tree, so inactive pages cost zero per frame.
/// </summary>
public sealed class Router : Widget
{
    private readonly Dictionary<string, Func<Widget>> _factories = new();
    private readonly Dictionary<string, Widget> _cache = new();
    private string _current = "";

    public string Current => _current;
    public IReadOnlyCollection<string> Keys => _factories.Keys;

    public Router Register(string key, Func<Widget> factory)
    {
        _factories[key] = factory;
        return this;
    }

    public void Switch(string key)
    {
        if (key == _current || !_factories.ContainsKey(key))
            return;

        if (_cache.TryGetValue(_current, out var old))
            RemoveChild(old, dispose: false); // cache: keep state alive

        if (!_cache.TryGetValue(key, out var page))
        {
            page = _factories[key]();
            _cache[key] = page;
        }

        _current = key;
        page.Node.Grow = 1f; // pages fill the router slot by default
        AddChild(page);
        Invalidate();
    }

    public override void Render(CellBuffer buffer) { } // pure container

    public override void Dispose()
    {
        foreach (var page in _cache.Values)
            if (page.Parent is null) // detached cached pages: we own them
                page.Dispose();
        base.Dispose();
    }
}

/// <summary>Clickable tab strip bound to a Router. Active tab is a filled
/// chip; hover brightens; keys are up to the app (see TabBar.Keys).</summary>
public sealed class TabBarWidget : Widget
{
    private readonly Router _router;
    private readonly string[] _tabs;
    private int _hover = -1;

    public TabBarWidget(Router router, string[] tabs)
    {
        _router = router;
        _tabs = tabs;
        Clickable = true;
    }

    public TabBarWidget() : this(new Router(), []) { }

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
