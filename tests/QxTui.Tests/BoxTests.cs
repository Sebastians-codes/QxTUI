using QxTui.Render;

namespace QxTui.Tests;

public class BoxTests
{
    [Fact]
    public void SingleStyle_DrawsEdgesAndCorners()
    {
        using var buf = new CellBuffer(12, 6);
        Box.Draw(buf, 1, 1, 8, 4, BoxStyles.Single, fg: 7);

        Assert.Equal('┌', buf[1, 1].Char);
        Assert.Equal('┐', buf[8, 1].Char);
        Assert.Equal('└', buf[1, 4].Char);
        Assert.Equal('┘', buf[8, 4].Char);
        Assert.Equal('─', buf[4, 1].Char);
        Assert.Equal('│', buf[1, 2].Char);
        Assert.Equal(' ', buf[4, 2].Char); // interior untouched
    }

    [Fact]
    public void Title_InsetsIntoTopEdge()
    {
        using var buf = new CellBuffer(20, 6);
        Box.Draw(buf, 0, 0, 16, 4, BoxStyles.Single, fg: 7, title: "HI", titleFg: 9);

        Assert.Equal('H', buf[3, 0].Char);
        Assert.Equal('I', buf[4, 0].Char);
        Assert.Equal(9, buf[3, 0].Fg);
        Assert.Equal(' ', buf[2, 0].Char); // padding before title
    }

    [Fact]
    public void TooSmall_DegeneratesToNoOp()
    {
        using var buf = new CellBuffer(6, 6);
        Box.Draw(buf, 0, 0, 1, 6, BoxStyles.Single);
        Box.Draw(buf, 0, 0, 6, 1, BoxStyles.Single);

        for (int y = 0; y < 6; y++)
            for (int x = 0; x < 6; x++)
                Assert.Equal(' ', buf[x, y].Char);
    }

    [Fact]
    public void OutOfBounds_ClipsWithoutThrowing()
    {
        using var buf = new CellBuffer(6, 4);
        Box.Draw(buf, 3, 2, 10, 10, BoxStyles.Double, fg: 7); // overflows right/bottom

        Assert.Equal('╔', buf[3, 2].Char);
        Assert.Equal('═', buf[5, 2].Char);
        Assert.Equal('║', buf[3, 3].Char);
    }

    [Fact]
    public void AllStyles_HaveDistinctCorners()
    {
        BoxStyle[] styles = [BoxStyles.Single, BoxStyles.Double, BoxStyles.Rounded, BoxStyles.Heavy];
        foreach (var st in styles)
        {
            Assert.NotEqual('\0', st.TopLeft);
            Assert.NotEqual('\0', st.TopRight);
            Assert.NotEqual('\0', st.BottomLeft);
            Assert.NotEqual('\0', st.BottomRight);
        }
    }
}
