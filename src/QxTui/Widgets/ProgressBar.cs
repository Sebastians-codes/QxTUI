using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;

namespace QxTui.Widgets;

/// <summary>Determinate progress bar bound to a Signal&lt;float&gt; in
/// [0,1]: filled blocks plus a right-aligned percent when it fits.</summary>
public sealed class ProgressBar : Widget
{
    private readonly Signal<float> _value;
    public ThemeRole Role = ThemeRole.Success;

    public ProgressBar(Signal<float> value)
    {
        _value = value;
        OnSignal(value);
        Node.Height = 1;
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        float v = Math.Clamp(_value.Value, 0f, 1f);
        string pct = $"{(int)(v * 100)}%";
        int barW = r.W >= pct.Length + 1 ? r.W - pct.Length - 1 : r.W;
        int filled = (int)(v * barW);
        for (int x = 0; x < barW; x++)
            buffer.WriteTextClipped(Clip, r.X + x, r.Y, x < filled ? "█" : "░",
                fg: Theme.Get(x < filled ? Role : ThemeRole.Dim));
        if (barW < r.W)
            buffer.WriteTextClipped(Clip, r.X + barW + 1, r.Y, pct,
                fg: Theme.Get(ThemeRole.Text));
    }
}
