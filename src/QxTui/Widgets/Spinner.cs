using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;

namespace QxTui.Widgets;

/// <summary>Self-driving activity indicator (braille frames). For work
/// whose progress is unknown — pair with App.Effect.</summary>
public sealed class Spinner : Widget
{
    private static readonly string[] Frames =
        ["\u280b", "\u2819", "\u2839", "\u2838", "\u283c", "\u2834", "\u2826", "\u2827", "\u2807", "\u280f"];

    private int _frame;
    public ThemeRole Role = ThemeRole.Accent;

    public Spinner(int hz = 12)
    {
        Node.Height = 1;
        Node.Width = 1;
        Tick(TimeSpan.FromMilliseconds(1000.0 / hz),
            _ => { _frame = (_frame + 1) % Frames.Length; Invalidate(); });
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        buffer.WriteTextClipped(Clip, r.X, r.Y, Frames[_frame], fg: Theme.Get(Role));
    }
}
