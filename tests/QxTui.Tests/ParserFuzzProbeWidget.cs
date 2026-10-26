using QxTui.Core;
using QxTui.Render;

namespace QxTui.Tests;

/// <summary>Minimal concrete widget for input-gesture tests.</summary>
public sealed class ParserFuzzProbeWidget : Widget
{
    public override void Render(CellBuffer buffer) =>
        buffer.FillRect(Rect.X, Rect.Y, Rect.W, Rect.H, new Cell { Char = '#', Fg = 7 });
}
