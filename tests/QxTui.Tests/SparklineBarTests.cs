using QxTui.Render;

namespace QxTui.Tests;

public class RingHistoryTests
{
    [Fact]
    public void Push_Index_Wrap()
    {
        var h = new RingHistory(3);
        h.Push(1); h.Push(2); h.Push(3); h.Push(4);

        Assert.Equal(3, h.Count);
        Assert.Equal([2f, 3f, 4f], [h[0], h[1], h[2]]);
    }

    [Fact]
    public void AtInterp_LerpBetweenSamples()
    {
        var h = new RingHistory(4);
        h.Push(0); h.Push(10);

        Assert.Equal(5f, h.AtInterp(0.5f), precision: 4);
        Assert.Equal(0f, h.AtInterp(-1f));   // clamped
        Assert.Equal(10f, h.AtInterp(99f));  // clamped
    }

    [Fact]
    public void Max_RespectsFloor()
    {
        var h = new RingHistory(4);
        h.Push(3);
        Assert.Equal(100f, h.Max(100f));
        Assert.Equal(3f, h.Max(1f));
    }
}

public class SparklineTests
{
    private static readonly RenderClip Full = new(0, 0, 40, 10);

    [Fact]
    public void FixedScale_MaxValueIsFullBlock()
    {
        using var buf = new CellBuffer(10, 3);
        var h = new RingHistory(4);
        h.Push(100);

        SparklinePainter.Draw(buf, Full, 0, 0, 4, h, fg: 7, scaleMax: 100f);

        Assert.Equal('█', buf[3, 0].Char); // right-aligned single sample
    }

    [Fact]
    public void HalfScale_IsMiddleLevel()
    {
        using var buf = new CellBuffer(10, 3);
        var h = new RingHistory(4);
        h.Push(50);

        SparklinePainter.Draw(buf, Full, 0, 0, 4, h, fg: 7, scaleMax: 100f);

        Assert.Equal('▄', buf[3, 0].Char); // 50/100 → level 3-4
    }

    [Fact]
    public void Phase_ShiftsSampling()
    {
        using var bufA = new CellBuffer(10, 3);
        using var bufB = new CellBuffer(10, 3);
        var h = new RingHistory(4);
        h.Push(0); h.Push(100);

        SparklinePainter.Draw(bufA, Full, 0, 0, 2, h, fg: 7, scaleMax: 100f, phase: 0f);
        SparklinePainter.Draw(bufB, Full, 0, 0, 2, h, fg: 7, scaleMax: 100f, phase: 0.5f);

        // left column lerps between the two samples as phase advances;
        // the right column clamps to the newest sample in both cases
        Assert.Equal('▁', bufA[0, 0].Char);
        Assert.Equal('▄', bufB[0, 0].Char);
        Assert.Equal('█', bufA[1, 0].Char);
        Assert.Equal('█', bufB[1, 0].Char);
    }
}

public class BarTests
{
    private static readonly RenderClip Full = new(0, 0, 40, 10);

    [Fact]
    public void Fraction_FillsProportionally()
    {
        using var buf = new CellBuffer(10, 3);
        BarPainter.Draw(buf, Full, 0, 0, 10, 0.5, fg: 7);

        Assert.Equal('█', buf[4, 0].Char);
        Assert.Equal('░', buf[5, 0].Char);
    }

    [Fact]
    public void Fraction_Clamped()
    {
        using var buf = new CellBuffer(10, 3);
        BarPainter.Draw(buf, Full, 0, 0, 10, 5.0, fg: 7);
        Assert.Equal('█', buf[9, 0].Char);

        BarPainter.Draw(buf, Full, 0, 1, 10, -1.0, fg: 7);
        Assert.Equal('░', buf[0, 1].Char);
    }
}
