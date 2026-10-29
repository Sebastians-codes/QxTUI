using QxTui.Layout;

namespace QxTui.Tests;

public class LayoutTests
{
    private static LayoutNode Leaf(int? w = null, int? h = null) =>
        new() { Width = w, Height = h };

    [Fact]
    public void ColumnStack_FixedHeights()
    {
        var a = Leaf(h: 3);
        var b = Leaf(h: 5);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(a).Add(b);

        LayoutNode.Compute(root, 20, 24);

        Assert.Equal(new Rect(0, 0, 20, 3), a.Rect);
        Assert.Equal(new Rect(0, 3, 20, 5), b.Rect);
    }

    [Fact]
    public void Row_FixedWidthsAndGap()
    {
        var a = Leaf(w: 10);
        var b = Leaf(w: 15);
        var root = new LayoutNode { Direction = FlexDirection.Row, Gap = 2, Height = 5 };
        root.Add(a).Add(b);

        LayoutNode.Compute(root, 80, 24);

        Assert.Equal(new Rect(0, 0, 10, 5), a.Rect);
        Assert.Equal(new Rect(12, 0, 15, 5), b.Rect); // 10 + gap 2
    }

    [Fact]
    public void Grow_DistributesFreeSpace_RemainderToEarliest()
    {
        var fixedChild = Leaf(h: 4);
        var growA = new LayoutNode { Grow = 1f };
        var growB = new LayoutNode { Grow = 2f };
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(fixedChild).Add(growA).Add(growB);

        // viewport 24 rows: free = 20, growSum = 3
        // A = floor(20/3) = 6, B = floor(40/3) = 13 → remainder 1 → earliest (A)
        LayoutNode.Compute(root, 20, 24);

        Assert.Equal(new Rect(0, 0, 20, 4), fixedChild.Rect);
        Assert.Equal(new Rect(0, 4, 20, 7), growA.Rect);
        Assert.Equal(new Rect(0, 11, 20, 13), growB.Rect);
    }

    [Fact]
    public void GrowRemainder_IsDeterministic()
    {
        // Invariant §3.5: same input → same rects, every run.
        static List<Rect> Once()
        {
            var children = Enumerable.Range(0, 3).Select(_ => new LayoutNode { Grow = 1f }).ToList();
            var root = new LayoutNode { Direction = FlexDirection.Column };
            foreach (var c in children) root.Add(c);
            LayoutNode.Compute(root, 17, 20); // 20 / 3 → remainder 2
            return children.Select(c => c.Rect).ToList();
        }

        var first = Once();
        for (int i = 0; i < 10; i++)
            Assert.Equal(first, Once());

        // and the remainder really was distributed: sizes 7,7,6
        Assert.Equal([7, 7, 6], first.Select(r => r.H));
    }

    [Fact]
    public void Percent_SizesAgainstParent()
    {
        var a = new LayoutNode { WidthPercent = 0.5f };
        var root = new LayoutNode { Direction = FlexDirection.Row, Height = 10 };
        root.Add(a);

        LayoutNode.Compute(root, 81, 24);

        Assert.Equal(40, a.Rect.W); // floor(81 * 0.5)
        Assert.Equal(10, a.Rect.H);
    }

    [Fact]
    public void NestedRowInColumn()
    {
        var left = Leaf(w: 12);
        var right = new LayoutNode { Grow = 1f };
        var row = new LayoutNode { Direction = FlexDirection.Row, Height = 5, Gap = 1 };
        row.Add(left).Add(right);

        var header = Leaf(h: 2);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(header).Add(row);

        LayoutNode.Compute(root, 40, 20);

        Assert.Equal(new Rect(0, 0, 40, 2), header.Rect);
        Assert.Equal(new Rect(0, 2, 12, 5), left.Rect);
        Assert.Equal(new Rect(13, 2, 27, 5), right.Rect); // 40 - 12 - gap 1
    }

    [Fact]
    public void AlignCenter_CrossAxis()
    {
        var child = Leaf(w: 10, h: 2);
        var root = new LayoutNode
        {
            Direction = FlexDirection.Row,
            Height = 10,
            Align = AlignItems.Center,
        };
        root.Add(child);

        LayoutNode.Compute(root, 40, 24);

        Assert.Equal(4, child.Rect.Y); // (10 - 2) / 2
        Assert.Equal(2, child.Rect.H); // fixed cross size respected
    }

    [Fact]
    public void Stretch_FillsCrossAxis()
    {
        var child = Leaf(w: 10); // no height
        var root = new LayoutNode
        {
            Direction = FlexDirection.Row,
            Height = 10,
            Align = AlignItems.Stretch,
        };
        root.Add(child);

        LayoutNode.Compute(root, 40, 24);

        Assert.Equal(10, child.Rect.H);
    }

    [Fact]
    public void MeasureLeaf_UsesIntrinsicSize()
    {
        var label = new LayoutNode { Measure = _ => new Size(7, 1) };
        var root = new LayoutNode { Direction = FlexDirection.Row, Height = 3 };
        root.Add(label);

        LayoutNode.Compute(root, 40, 24);

        Assert.Equal(7, label.Rect.W);
    }

    [Fact]
    public void RectChanged_FalseWhenRecomputedIdentically()
    {
        var child = Leaf(h: 3);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(child);

        LayoutNode.Compute(root, 20, 24);
        LayoutNode.Compute(root, 20, 24); // same viewport again

        Assert.False(child.RectChanged);
        Assert.False(root.RectChanged);
    }

    [Fact]
    public void RectChanged_HeightResize_LeavesFixedTopChildUnchanged()
    {
        // Invariant §3.4 foundation: content invalidation ≠ layout invalidation.
        var top = Leaf(h: 3);
        var bottom = new LayoutNode { Grow = 1f };
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(top).Add(bottom);

        LayoutNode.Compute(root, 20, 24);
        Assert.True(top.RectChanged); // first pass: everything "changed" from default

        LayoutNode.Compute(root, 20, 30); // taller window

        Assert.False(top.RectChanged);                    // (0,0,20,3) unchanged
        Assert.Equal(new Rect(0, 0, 20, 3), top.Rect);
        Assert.True(bottom.RectChanged);                  // grew from 21 to 27
        Assert.Equal(new Rect(0, 3, 20, 27), bottom.Rect);
    }

    [Fact]
    public void CollectChanged_ReturnsOnlyMovedNodes()
    {
        var top = Leaf(h: 3);
        var bottom = new LayoutNode { Grow = 1f };
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(top).Add(bottom);

        LayoutNode.Compute(root, 20, 24);
        LayoutNode.Compute(root, 20, 30);

        var changed = new List<LayoutNode>();
        root.CollectChanged(changed);

        Assert.Contains(bottom, changed);
        Assert.Contains(root, changed);      // root itself grew
        Assert.DoesNotContain(top, changed); // untouched
    }

    [Fact]
    public void ExtremeShrink_AllRectsStayInsideViewport()
    {
        // Regression: rapid-shrink crash. Overflowing children must clamp to
        // the parent edge; no rect may ever escape the viewport, because
        // widgets trust their rect for raw buffer writes.
        var row = new LayoutNode { Direction = FlexDirection.Row, Height = 9, Gap = 1 };
        var spinner = new LayoutNode { Width = 14 };
        var middle = new LayoutNode { Grow = 1f };
        var stats = new LayoutNode { WidthPercent = 0.25f };
        row.Add(spinner).Add(middle).Add(stats);

        var progress = new LayoutNode { Height = 1 };
        var particles = new LayoutNode { Grow = 1f };
        var help = new LayoutNode { Height = 1 };

        var root = new LayoutNode { Direction = FlexDirection.Column, Gap = 1 };
        root.Add(row).Add(progress).Add(particles).Add(help);

        foreach (var (w, h) in new[] { (3, 1), (5, 2), (8, 3), (20, 4), (1, 1) })
        {
            LayoutNode.Compute(root, w, h);

            foreach (var node in new[] { row, spinner, middle, stats, progress, particles, help, root })
            {
                Assert.True(node.Rect.X >= 0 && node.Rect.Y >= 0,
                    $"{w}x{h}: negative origin {node.Rect}");
                Assert.True(node.Rect.Right <= w && node.Rect.Bottom <= h,
                    $"{w}x{h}: rect {node.Rect} escapes viewport {w}x{h}");
            }
        }
    }

    [Fact]
    public void Overflow_ClampsToZero_NoNegativeRects()
    {
        var a = Leaf(h: 10);
        var b = Leaf(h: 10);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(a).Add(b);

        LayoutNode.Compute(root, 20, 12); // only room for 12 total

        Assert.True(a.Rect.H >= 0);
        Assert.True(b.Rect.H >= 0);
    }
}

public class LayoutConstraintTests
{
    private static LayoutNode Leaf(int? w = null, int? h = null) =>
        new() { Width = w, Height = h };

    [Fact]
    public void MinWidth_FloorsAPercentChild_AndGrowTakesTheRest()
    {
        // "20% but never under 30 cols": 20% of 100 = 20 → floored to 30
        var a = Leaf(); a.WidthPercent = 0.2f; a.MinWidth = 30;
        var b = Leaf(); b.Grow = 1f;
        var root = new LayoutNode { Direction = FlexDirection.Row };
        root.Add(a).Add(b);

        LayoutNode.Compute(root, 100, 10);

        Assert.Equal(30, a.Rect.W);
        Assert.Equal(70, b.Rect.W);
    }

    [Fact]
    public void MaxWidth_CapsAGrowingChild_SurplusGoesToSibling()
    {
        // two equal growers, one capped: the cap's surplus is redistributed
        var a = Leaf(); a.Grow = 1f; a.MaxWidth = 40;
        var b = Leaf(); b.Grow = 1f;
        var root = new LayoutNode { Direction = FlexDirection.Row };
        root.Add(a).Add(b);

        LayoutNode.Compute(root, 100, 10);

        Assert.Equal(40, a.Rect.W);
        Assert.Equal(60, b.Rect.W);
    }

    [Fact]
    public void MinHeight_FloorsCrossAxis_InRow()
    {
        var a = Leaf(); // no cap: stretch wins
        var root = new LayoutNode { Direction = FlexDirection.Row, Align = AlignItems.Stretch };
        root.Add(a);

        LayoutNode.Compute(root, 10, 8);
        Assert.Equal(8, a.Rect.H); // stretch, no min: full height

        var b = Leaf(); b.MaxHeight = 2;
        var root2 = new LayoutNode { Direction = FlexDirection.Row, Align = AlignItems.Stretch };
        root2.Add(b);
        LayoutNode.Compute(root2, 10, 8);
        Assert.Equal(2, b.Rect.H); // capped on the cross axis
    }

    [Fact]
    public void Justify_SpaceBetween_PacksEdges()
    {
        var a = Leaf(w: 4); var b = Leaf(w: 4);
        var root = new LayoutNode { Direction = FlexDirection.Row, Justify = JustifyContent.SpaceBetween };
        root.Add(a).Add(b);

        LayoutNode.Compute(root, 20, 5);

        Assert.Equal(new Rect(0, 0, 4, 5), a.Rect);
        Assert.Equal(new Rect(16, 0, 4, 5), b.Rect);
    }

    [Fact]
    public void Justify_Center_And_End_ExactRects()
    {
        var c = Leaf(w: 4); var d = Leaf(w: 4);
        var center = new LayoutNode { Direction = FlexDirection.Row, Justify = JustifyContent.Center };
        center.Add(c).Add(d);
        LayoutNode.Compute(center, 20, 5);
        Assert.Equal(6, c.Rect.X);   // free 12 → lead 6
        Assert.Equal(10, d.Rect.X);

        var e = Leaf(w: 4); var f = Leaf(w: 4);
        var end = new LayoutNode { Direction = FlexDirection.Row, Justify = JustifyContent.End };
        end.Add(e).Add(f);
        LayoutNode.Compute(end, 20, 5);
        Assert.Equal(12, e.Rect.X);
        Assert.Equal(16, f.Rect.X);
    }

    [Fact]
    public void Justify_SpaceAround_HalfEdges()
    {
        var a = Leaf(w: 4); var b = Leaf(w: 4);
        var root = new LayoutNode { Direction = FlexDirection.Row, Justify = JustifyContent.SpaceAround };
        root.Add(a).Add(b);

        LayoutNode.Compute(root, 20, 5);

        // free 12, n=2: between = 6, lead = (12-6)/2 = 3
        Assert.Equal(3, a.Rect.X);
        Assert.Equal(13, b.Rect.X);
    }

    [Fact]
    public void Justify_RemainderCells_GoToEarliestGaps_Deterministic()
    {
        var a = Leaf(w: 4); var b = Leaf(w: 4); var c = Leaf(w: 4);
        var root = new LayoutNode { Direction = FlexDirection.Row, Justify = JustifyContent.SpaceBetween };
        root.Add(a).Add(b).Add(c);

        LayoutNode.Compute(root, 21, 5); // free 9 over 2 gaps: 5 then 4

        Assert.Equal(0, a.Rect.X);
        Assert.Equal(9, b.Rect.X);   // 0 + 4 + gap 5
        Assert.Equal(17, c.Rect.X);  // 9 + 4 + gap 4
    }

    [Fact]
    public void Justify_IsIgnored_WhenGrowAbsorbsFreeSpace()
    {
        var a = Leaf(w: 4);
        var g = Leaf(); g.Grow = 1f;
        var root = new LayoutNode { Direction = FlexDirection.Row, Justify = JustifyContent.Center };
        root.Add(a).Add(g);

        LayoutNode.Compute(root, 20, 5);

        Assert.Equal(0, a.Rect.X);   // grow ate the free space: start packing
        Assert.Equal(16, g.Rect.W);
    }

    [Fact]
    public void Root_MinMax_ClampAgainstViewport()
    {
        var root = new LayoutNode { Direction = FlexDirection.Column, MaxWidth = 12 };
        root.Add(Leaf(h: 2));

        LayoutNode.Compute(root, 20, 10);
        Assert.Equal(12, root.Rect.W);
    }
}

public class ChildInsetTests
{
    [Fact]
    public void ChildInset_KeepsChildrenInsideTheBorderRing()
    {
        // the regression behind "the PROCESSES border renders wrong":
        // a framed parent whose children ignore the ring paint over it
        var child = new LayoutNode { Grow = 1f };
        var root = new LayoutNode { Direction = FlexDirection.Column, ChildInset = 1 };
        root.Add(child);

        LayoutNode.Compute(root, 20, 10);

        Assert.Equal(new Rect(1, 1, 18, 8), child.Rect);
    }

    [Fact]
    public void NoInset_ChildrenOwnTheWholeRect()
    {
        var child = new LayoutNode { Grow = 1f };
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(child);

        LayoutNode.Compute(root, 20, 10);

        Assert.Equal(new Rect(0, 0, 20, 10), child.Rect);
    }
}

public class CrossAxisIntrinsicTests
{
    [Fact]
    public void RowInsideColumn_ReportsHeight_NotItsChildrensWidths()
    {
        // regression: a Row nested in a Column reported its WIDTH as its
        // hypothetical height (SumIntrinsicMain used the node's own axis).
        // A wide child inflated the hypothetical past the viewport, the
        // column flipped into the shrink path, and every sibling after the
        // row — including fixed-height ones — was squeezed to zero.
        var root = new LayoutNode { Direction = FlexDirection.Column };
        var top = new LayoutNode { Height = 1 };
        var rowNode = new LayoutNode { Direction = FlexDirection.Row, Grow = 3f };
        rowNode.Add(new LayoutNode { Width = 200 }); // width must NOT leak into height
        var growAfter = new LayoutNode { Grow = 2f };
        var bottom = new LayoutNode { Height = 1 };
        root.Add(top);
        root.Add(rowNode);
        root.Add(growAfter);
        root.Add(bottom);

        LayoutNode.Compute(root, 100, 34);

        Assert.Equal(1, top.Rect.H);
        Assert.Equal(1, bottom.Rect.H);
        Assert.Equal(34, top.Rect.H + rowNode.Rect.H + growAfter.Rect.H + bottom.Rect.H);
        Assert.True(rowNode.Rect.H > growAfter.Rect.H); // 3:2 split of the free space
        Assert.Equal(34, bottom.Rect.Bottom);           // nothing overflows the viewport
    }

    [Fact]
    public void NestedContainerIntrinsic_IncludesChildInset()
    {
        // a panel-style container (ChildInset frame) contributes its ring
        // to the hypothetical size: a fixed sibling after it keeps its room
        var root = new LayoutNode { Direction = FlexDirection.Column };
        var panel = new LayoutNode { Direction = FlexDirection.Column, ChildInset = 1 };
        panel.Add(new LayoutNode { Height = 3 });
        var after = new LayoutNode { Height = 2 };
        root.Add(panel);
        root.Add(after);

        LayoutNode.Compute(root, 20, 20);

        Assert.Equal(5, panel.Rect.H); // 3 content + 2 ring
        Assert.Equal(2, after.Rect.H);
    }
}
