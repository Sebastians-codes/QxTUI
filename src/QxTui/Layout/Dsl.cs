namespace QxTui.Layout;

/// <summary>
/// Declarative layout DSL: build flexbox trees as expressions instead of
/// statements.
///
///     var root = UI.Column(gap: 1,
///         header,                       // widgets convert implicitly
///         UI.Row(gap: 1,
///             cpu.Grow(3),
///             mem.Grow(2)),
///         net.Height(5),
///         procs.Grow(1).Basis(8),
///         footer.Height(1));
/// </summary>
public static class UI
{
    public static LayoutNode Column(params LayoutNode[] children) => Column(0, children);

    public static LayoutNode Column(int gap, params LayoutNode[] children)
    {
        var node = new LayoutNode { Direction = FlexDirection.Column, Gap = gap };
        foreach (var c in children) node.Add(c);
        return node;
    }

    public static LayoutNode Row(params LayoutNode[] children) => Row(0, children);

    public static LayoutNode Row(int gap, params LayoutNode[] children)
    {
        var node = new LayoutNode { Direction = FlexDirection.Row, Gap = gap };
        foreach (var c in children) node.Add(c);
        return node;
    }
}

/// <summary>Fluent sizing/alignment setters — every call returns the node
/// so trees compose in a single expression.</summary>
public static class NodeFluent
{
    public static LayoutNode Grow(this LayoutNode n, float weight = 1f) { n.Grow = weight; return n; }
    public static LayoutNode Shrink(this LayoutNode n, float weight) { n.Shrink = weight; return n; }
    public static LayoutNode Basis(this LayoutNode n, float cells) { n.Basis = cells; return n; }
    public static LayoutNode Width(this LayoutNode n, int cells) { n.Width = cells; return n; }
    public static LayoutNode Height(this LayoutNode n, int cells) { n.Height = cells; return n; }
    public static LayoutNode WidthPercent(this LayoutNode n, float fraction) { n.WidthPercent = fraction; return n; }
    public static LayoutNode HeightPercent(this LayoutNode n, float fraction) { n.HeightPercent = fraction; return n; }
    public static LayoutNode Align(this LayoutNode n, AlignItems align) { n.Align = align; return n; }
    public static LayoutNode Gap(this LayoutNode n, int cells) { n.Gap = cells; return n; }

    // Widget overloads: implicit conversion does NOT apply to extension
    // method receivers, so widgets get their own fluent entry points.
    public static LayoutNode Grow(this Core.Widget w, float weight = 1f) => w.Node.Grow(weight);
    public static LayoutNode Shrink(this Core.Widget w, float weight) => w.Node.Shrink(weight);
    public static LayoutNode Basis(this Core.Widget w, float cells) => w.Node.Basis(cells);
    public static LayoutNode Width(this Core.Widget w, int cells) => w.Node.Width(cells);
    public static LayoutNode Height(this Core.Widget w, int cells) => w.Node.Height(cells);
    public static LayoutNode WidthPercent(this Core.Widget w, float fraction) => w.Node.WidthPercent(fraction);
    public static LayoutNode HeightPercent(this Core.Widget w, float fraction) => w.Node.HeightPercent(fraction);
    public static LayoutNode Align(this Core.Widget w, AlignItems align) => w.Node.Align(align);
    public static LayoutNode Gap(this Core.Widget w, int cells) => w.Node.Gap(cells);
}
