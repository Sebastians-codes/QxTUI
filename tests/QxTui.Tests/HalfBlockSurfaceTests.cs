using QxTui.Render;

namespace QxTui.Tests;

/// <summary>
/// HalfBlockSurface: square pixels (full width x half height per cell),
/// exact two-color cells via fg=top / bg=bottom.
/// </summary>
public class HalfBlockSurfaceTests
{
    private static readonly Cell Bg = new() { Char = ' ', Fg = -1, Bg = 233 };

    [Fact]
    public void TopPixel_IsUpperHalfBlock()
    {
        using var buf = new CellBuffer(6, 4);
        var s = new HalfBlockSurface(buf, 1, 1, 4, 4, Bg);

        s.SetPixel(2, 0, 42);
        s.Draw();

        Assert.Equal('▀', buf[3, 1].Char);
        Assert.Equal(42, buf[3, 1].Fg);
        Assert.Equal(233, buf[3, 1].Bg); // bottom stays field background
    }

    [Fact]
    public void BottomPixel_IsLowerHalfBlock()
    {
        using var buf = new CellBuffer(6, 4);
        var s = new HalfBlockSurface(buf, 1, 1, 4, 4, Bg);

        s.SetPixel(2, 1, 196);
        s.Draw();

        Assert.Equal('▄', buf[3, 1].Char);
        Assert.Equal(196, buf[3, 1].Fg);
    }

    [Fact]
    public void BothPixelsSameColor_IsFullBlock()
    {
        using var buf = new CellBuffer(6, 4);
        var s = new HalfBlockSurface(buf, 1, 1, 4, 4, Bg);

        s.SetPixel(0, 0, 42);
        s.SetPixel(0, 1, 42);
        s.Draw();

        Assert.Equal('█', buf[1, 1].Char);
        Assert.Equal(42, buf[1, 1].Fg);
    }

    [Fact]
    public void BothPixelsDifferentColors_ExactTwoColorCell()
    {
        using var buf = new CellBuffer(6, 4);
        var s = new HalfBlockSurface(buf, 1, 1, 4, 4, Bg);

        s.SetPixel(0, 0, 46);  // head green on top
        s.SetPixel(0, 1, 196); // food red below
        s.Draw();

        Assert.Equal('▀', buf[1, 1].Char);
        Assert.Equal(46, buf[1, 1].Fg);
        Assert.Equal(196, buf[1, 1].Bg);
    }

    [Fact]
    public void Geometry_OneColumnTwoRowsPerCell()
    {
        using var buf = new CellBuffer(20, 12);
        var s = new HalfBlockSurface(buf, 0, 0, 17, 9, Bg);

        Assert.Equal(17, s.CellWidth);
        Assert.Equal(5, s.CellHeight); // ceil(9/2)
    }

    [Fact]
    public void Clear_ResetsToBackground()
    {
        using var buf = new CellBuffer(6, 4);
        var s = new HalfBlockSurface(buf, 1, 1, 4, 4, Bg);

        s.SetPixel(1, 1, 9);
        s.Draw();
        Assert.NotEqual(' ', buf[2, 1].Char);

        s.Clear();
        s.Draw();
        Assert.Equal(' ', buf[2, 1].Char);
    }

    [Fact]
    public void OutOfBoundsPixels_AreIgnored()
    {
        using var buf = new CellBuffer(6, 4);
        var s = new HalfBlockSurface(buf, 1, 1, 4, 4, Bg);

        s.SetPixel(-1, 0, 9);
        s.SetPixel(0, -1, 9);
        s.SetPixel(99, 0, 9);
        s.Draw(); // must not throw

        Assert.Equal(' ', buf[1, 1].Char);
    }
}
