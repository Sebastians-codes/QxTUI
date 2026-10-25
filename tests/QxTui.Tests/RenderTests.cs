using QxTui.Layout;
using QxTui.Render;

namespace QxTui.Tests;

public class DamageRectTests
{
    [Fact]
    public void Coalesce_MergesOverlapping()
    {
        var rects = new List<DamageRect>
        {
            new(0, 0, 10, 5),
            new(5, 2, 10, 5),
        };

        DamageCoalescer.Coalesce(rects);

        Assert.Single(rects);
        Assert.Equal(new DamageRect(0, 0, 15, 7), rects[0]);
    }

    [Fact]
    public void Coalesce_MergesTouching()
    {
        var rects = new List<DamageRect>
        {
            new(0, 0, 10, 5),
            new(10, 0, 10, 5), // shares an edge
        };

        DamageCoalescer.Coalesce(rects);

        Assert.Single(rects);
        Assert.Equal(new DamageRect(0, 0, 20, 5), rects[0]);
    }

    [Fact]
    public void Coalesce_KeepsDisjointSeparate()
    {
        var rects = new List<DamageRect>
        {
            new(0, 0, 5, 5),
            new(20, 20, 5, 5),
        };

        DamageCoalescer.Coalesce(rects);

        Assert.Equal(2, rects.Count);
    }

    [Fact]
    public void Coalesce_SortsRasterOrder()
    {
        var rects = new List<DamageRect>
        {
            new(30, 10, 2, 2),
            new(0, 10, 2, 2),
            new(0, 0, 2, 2),
        };

        DamageCoalescer.Coalesce(rects);

        Assert.Equal(new Rect(0, 0, 2, 2), new Rect(rects[0].X, rects[0].Y, rects[0].W, rects[0].H));
        Assert.True(rects[1].Y <= rects[2].Y);
        for (int i = 1; i < rects.Count; i++)
            Assert.True(rects[i - 1].Y < rects[i].Y ||
                        (rects[i - 1].Y == rects[i].Y && rects[i - 1].X <= rects[i].X));
    }

    [Fact]
    public void Coalesce_ChainMerge()
    {
        // A touches B, B touches C → all one rect
        var rects = new List<DamageRect>
        {
            new(0, 0, 5, 5),
            new(5, 0, 5, 5),
            new(10, 0, 5, 5),
        };

        DamageCoalescer.Coalesce(rects);

        Assert.Single(rects);
        Assert.Equal(new DamageRect(0, 0, 15, 5), rects[0]);
    }
}

public class CellBufferTests
{
    [Fact]
    public void Indexer_SetGet()
    {
        using var buf = new CellBuffer(10, 5);
        buf[3, 2] = new Cell { Char = 'X', Fg = 9, Bg = -1 };

        Assert.Equal('X', buf[3, 2].Char);
        Assert.Equal(9, buf[3, 2].Fg);
    }

    [Fact]
    public void Indexer_OutOfBounds_Throws()
    {
        using var buf = new CellBuffer(10, 5);
        Assert.Throws<IndexOutOfRangeException>(() => buf[10, 0].Char);
        Assert.Throws<IndexOutOfRangeException>(() => buf[0, 5].Char);
    }

    [Fact]
    public void StartsEmpty()
    {
        using var buf = new CellBuffer(4, 4);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                Assert.Equal(Cell.Empty, buf[x, y]);
    }

    [Fact]
    public void WriteText_ClipsToBounds()
    {
        using var buf = new CellBuffer(5, 2);

        int written = buf.WriteText(3, 0, "hello"); // only "he" fits
        Assert.Equal(2, written);
        Assert.Equal('h', buf[3, 0].Char);
        Assert.Equal('e', buf[4, 0].Char);

        Assert.Equal(0, buf.WriteText(0, 5, "offscreen"));
        Assert.Equal(0, buf.WriteText(0, -1, "offscreen"));
    }

    [Fact]
    public void WriteText_NegativeX_ClipsLeft()
    {
        using var buf = new CellBuffer(5, 1);
        int written = buf.WriteText(-2, 0, "abcd"); // "cd" visible
        Assert.Equal(2, written);
        Assert.Equal('c', buf[0, 0].Char);
    }

    [Fact]
    public void FillRect_ClipsAndFills()
    {
        using var buf = new CellBuffer(6, 4);
        var cell = new Cell { Char = '#', Fg = 1, Bg = 2 };

        buf.FillRect(4, 2, 10, 10, cell); // overflows right/bottom

        Assert.Equal('#', buf[5, 3].Char);
        Assert.Equal('#', buf[4, 2].Char);
        Assert.Equal(Cell.Empty, buf[3, 2]); // outside
        Assert.Equal(Cell.Empty, buf[4, 1]); // above
    }

    [Fact]
    public void Resize_Preserve_KeepsOverlap()
    {
        using var buf = new CellBuffer(4, 4);
        buf[1, 1] = new Cell { Char = 'K', Fg = 5, Bg = -1 };
        buf[3, 3] = new Cell { Char = 'L', Fg = 6, Bg = -1 };

        buf.Resize(6, 3, preserve: true);

        Assert.Equal('K', buf[1, 1].Char);   // inside overlap → kept
        Assert.Equal(Cell.Empty, buf[4, 0]); // new area → empty
        Assert.Equal(Cell.Empty, buf[5, 2]); // new area → empty
    }

    [Fact]
    public void FillRect_StaleRectBeyondShrunkBuffer_DegeneratesToNoOp()
    {
        // Regression: rapid shrink crash. A rect from the old (larger)
        // layout may start beyond the new buffer edge; clipping must be
        // two-sided so it becomes a no-op instead of throwing.
        using var buf = new CellBuffer(40, 10);
        buf.FillRect(75, 2, 25, 5, new Cell { Char = 'X', Fg = 1, Bg = -1 }); // starts past right edge

        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 40; x++)
                Assert.Equal(Cell.Empty, buf[x, y]);

        // partially overlapping stale rect: only the in-bounds part fills
        buf.FillRect(35, 0, 20, 3, new Cell { Char = 'Y', Fg = 1, Bg = -1 });
        Assert.Equal('Y', buf[39, 2].Char);
        Assert.Equal(Cell.Empty, buf[34, 2]);
    }

    [Fact]
    public void Resize_NoPreserve_Clears()
    {
        using var buf = new CellBuffer(4, 4);
        buf[1, 1] = new Cell { Char = 'K', Fg = 5, Bg = -1 };

        buf.Resize(4, 4, preserve: false);

        Assert.Equal(Cell.Empty, buf[1, 1]);
    }
}
