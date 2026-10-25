using QxTui.Core;
using QxTui.Render;

namespace QxTui.Widgets;

/// <summary>Single-line clipped label. Text is a provider so it can be
/// live without extra invalidation plumbing.</summary>
public sealed class LabelWidget(Func<string> text, int? fg = null, int? bg = null) : Widget
{
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        if (bg is int b)
            buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = ' ', Fg = -1, Bg = b });
        buffer.WriteTextClipped(Clip, r.X + 1, r.Y, text(), fg: fg ?? Theme.Text, bg: bg ?? -1);
    }
}

/// <summary>Horizontal fraction bar bound to a provider.</summary>
public sealed class BarWidget(Func<double> fraction, int? fg = null) : Widget
{
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        Bar.Draw(buffer, Clip, r.X, r.Y, r.W, fraction(), fg: fg ?? Theme.Accent);
    }
}

/// <summary>Sparkline bound to a RingHistory the app owns and pushes to.</summary>
public sealed class SparklineWidget(
    RingHistory history,
    int? fg = null,
    float scaleMax = 0f,
    float autoFloor = 1f,
    Func<float>? phase = null) : Widget
{
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        Sparkline.Draw(buffer, Clip, r.X, r.Y, r.W, history,
            fg: fg ?? Theme.Accent, scaleMax: scaleMax, autoFloor: autoFloor,
            phase: phase?.Invoke() ?? 0f);
    }
}
