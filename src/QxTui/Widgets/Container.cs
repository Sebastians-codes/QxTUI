using QxTui.Core;
using QxTui.Render;

namespace QxTui.Widgets;

/// <summary>Composition-only widget: paints nothing, exists to hold
/// children in the widget tree (panels, groups, rows of controls).</summary>
public sealed class Container : Widget
{
    public override void Render(CellBuffer buffer) { }
}
