using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;

namespace QxTui.Widgets;

/// <summary>Multi-line word-wrapped paragraph, unicode-width aware.
/// Invalidate (or Tick/OnSignal) when the text provider changes.</summary>
public sealed class Paragraph(Func<string> text, ThemeRole fg = ThemeRole.Text) : Widget
{
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        buffer.WriteTextWrapped(Clip, r.X + 1, r.Y, text(), fg: Theme.Get(fg));
    }
}
