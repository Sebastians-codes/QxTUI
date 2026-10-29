using QxTui.Core;
using QxTui.Render;

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

    /// <summary>Fires on every Switch — tab bars and breadcrumbs subscribe
    /// so keyboard-driven switches update them too.</summary>
    public Reactive.Signal<string> Changed { get; } = new("");
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

        if (Core.App.Trace)
        {
            string? caller = null;
            try
            {
                caller = new System.Diagnostics.StackTrace(2, false)
                    .GetFrame(0)?.GetMethod()?.DeclaringType?.Name;
            }
            catch { }
            Core.App.TraceLine($"switch {key} from {caller}");
        }

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
        Changed.Set(key);
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
