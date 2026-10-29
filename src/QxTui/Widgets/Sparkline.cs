using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;

namespace QxTui.Widgets;

/// <summary>Sparkline bound to a RingHistory the app owns and pushes to.</summary>
public sealed class Sparkline : Widget
{
    private readonly RingHistory _history;
    private readonly ThemeRole _fg;
    private readonly float _scaleMax;
    private readonly float _autoFloor;
    private readonly Func<float>? _phase;

    public Sparkline(RingHistory history, ThemeRole fg = ThemeRole.Accent,
        float scaleMax = 0f, float autoFloor = 1f, Func<float>? phase = null)
    {
        _history = history;
        _fg = fg;
        _scaleMax = scaleMax;
        _autoFloor = autoFloor;
        _phase = phase;
        OnSignal(history.Pushed); // repaint whenever new data lands
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty); // no stale trail on reflow
        QxTui.Render.SparklinePainter.Draw(buffer, Clip, r.X, r.Y, r.W, _history,
            fg: Theme.Get(_fg), scaleMax: _scaleMax, autoFloor: _autoFloor,
            phase: _phase?.Invoke() ?? 0f);
    }
}
