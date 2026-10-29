using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;

namespace QxTui.Tests;

file sealed class DslProbe : Widget
{
    public override void Render(CellBuffer buffer) { }
}

public class DslTests
{
    [Fact]
    public void ColumnRow_BuildEquivalentTrees()
    {
        var a = new DslProbe();
        var b = new DslProbe();
        var c = new DslProbe();

        var dsl = UI.Column(1,
            a.Height(3),
            UI.Row(2, b.Grow(1), c.WidthPercent(0.25f)),
            new LayoutNode { Grow = 1f });

        // manual equivalent
        var row = new LayoutNode { Direction = FlexDirection.Row, Gap = 2 };
        var bNode = new LayoutNode { Grow = 1f };
        var cNode = new LayoutNode { WidthPercent = 0.25f };
        row.Add(bNode).Add(cNode);
        var manual = new LayoutNode { Direction = FlexDirection.Column, Gap = 1 };
        var aNode = new LayoutNode { Height = 3 };
        manual.Add(aNode).Add(row).Add(new LayoutNode { Grow = 1f });

        LayoutNode.Compute(dsl, 40, 20);
        LayoutNode.Compute(manual, 40, 20);

        Assert.Equal(aNode.Rect, a.Rect);
        Assert.Equal(bNode.Rect, b.Rect);
        Assert.Equal(cNode.Rect, c.Rect);
        // row's intrinsic HEIGHT is 0: neither child carries a height and
        // the row's gap is horizontal. (It used to report the gap as its
        // height — the crossed-axis confusion SumIntrinsicMain had, where
        // a row-in-a-column summed its own main axis instead of the
        // parent's.) The grow child therefore starts one gap later.
        Assert.Equal(new Rect(0, 4, 40, 0), dsl.Children[1].Rect);
        Assert.Equal(new Rect(0, 5, 40, 15), dsl.Children[2].Rect);
    }

    [Fact]
    public void FluentChain_SetsAllProperties()
    {
        var n = new LayoutNode()
            .Grow(2).Shrink(0).Basis(10).Width(5).Height(7)
            .WidthPercent(0.5f).HeightPercent(0.25f)
            .Align(AlignItems.Center).Gap(3);

        Assert.Equal(2f, n.Grow);
        Assert.Equal(0f, n.Shrink);
        Assert.Equal(10f, n.Basis);
        Assert.Equal(5, n.Width);
        Assert.Equal(7, n.Height);
        Assert.Equal(0.5f, n.WidthPercent);
        Assert.Equal(0.25f, n.HeightPercent);
        Assert.Equal(AlignItems.Center, n.Align);
        Assert.Equal(3, n.Gap);
    }

    [Fact]
    public void WidgetsConvertImplicitly_InDsl()
    {
        var w1 = new DslProbe();
        var w2 = new DslProbe();

        var root = UI.Column(gap: 1, w1.Height(2), w2.Grow(1));

        Assert.Same(w1.Node, root.Children[0]);
        Assert.Same(w2.Node, root.Children[1]);

        LayoutNode.Compute(root, 10, 10);
        Assert.Equal(new Rect(0, 0, 10, 2), w1.Rect);
        Assert.Equal(new Rect(0, 3, 10, 7), w2.Rect);
    }
}
