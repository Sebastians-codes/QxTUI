using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;

namespace QxTui.Widgets;

/// <summary>Horizontal rule. Height 1 by convention; taller rects get one
/// line per row (use as a column divider by giving it Width 1 and the
/// vertical style renders instead).</summary>
public sealed class Separator : Widget
{
    public ThemeRole Role = ThemeRole.Faint;

    public Separator() => Node.Height = 1;

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        bool vertical = r.H > r.W;
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
                buffer.WriteTextClipped(Clip, x, y, vertical ? "│" : "─",
                    fg: Theme.Get(Role));
    }
}
