using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;

namespace QxTui.Widgets;

/// <summary>Horizontal fraction bar bound to a provider.</summary>
public sealed class Bar : Widget
{
    private readonly Func<double> _fraction;
    private readonly ThemeRole _fg;

    public Bar(Func<double> fraction, ThemeRole fg = ThemeRole.Accent)
    {
        _fraction = fraction;
        _fg = fg;
    }

    /// <summary>Signal-bound: re-renders automatically whenever the source
    /// signal changes — the invalidation wiring you'd otherwise forget.</summary>
    public Bar(Signal<float> source, ThemeRole fg = ThemeRole.Accent)
        : this(() => source.Value, fg)
    {
        OnSignal(source);
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        QxTui.Render.BarPainter.Draw(buffer, Clip, r.X, r.Y, r.W, _fraction(), fg: Theme.Get(_fg));
    }
}
