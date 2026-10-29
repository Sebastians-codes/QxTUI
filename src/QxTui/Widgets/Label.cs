using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;

namespace QxTui.Widgets;

/// <summary>Single-line clipped label. Text is a provider so it can be
/// live without extra invalidation plumbing.</summary>
public sealed class Label : Widget
{
    private readonly Func<string> _text;
    private readonly ThemeRole _fg;
    private readonly ThemeRole? _bg;

    private Label(Func<string> text, ThemeRole fg, ThemeRole? bg, bool live)
    {
        _text = text;
        _fg = fg;
        _bg = bg;
        // a provider is only evaluated WHEN we render: without a heartbeat
        // a Func label paints once and its text is stale pixels forever
        // (the "counters don't increase until I hover it" bug)
        if (live)
            Tick(TimeSpan.FromMilliseconds(250), _ => Invalidate());
    }

    public Label(Func<string> text, ThemeRole fg = ThemeRole.Text, ThemeRole? bg = null)
        : this(text, fg, bg, live: true)
    {
    }

    /// <summary>Signal-bound: re-renders whenever the source changes —
    /// event-driven, no heartbeat needed.</summary>
    public Label(Signal<float> source, Func<float, string> format,
        ThemeRole fg = ThemeRole.Text, ThemeRole? bg = null)
        : this(() => format(source.Value), fg, bg, live: false)
    {
        OnSignal(source);
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        // always clear our rect: a moved/resized widget must not leave
        // stale text behind (only writing text ghosts on reflow)
        buffer.FillRect(r.X, r.Y, r.W, r.H,
            new Cell { Char = ' ', Fg = -1, Bg = _bg is { } r3 ? Theme.Get(r3) : -1 });
        buffer.WriteTextClipped(Clip, r.X + 1, r.Y,
            QxTui.Render.Text.Ellipsize(_text(), r.W - 2), fg: Theme.Get(_fg),
            bg: _bg is { } r2 ? Theme.Get(r2) : -1);
    }
}
