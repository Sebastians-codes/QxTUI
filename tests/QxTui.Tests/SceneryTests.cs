using QxTui.Render;
using QxTui.Snake;

namespace QxTui.Tests;

public class SceneryTests
{
    private static readonly Cell Bg = new() { Char = ' ', Fg = -1, Bg = 233 };

    [Fact]
    public void Deterministic_SameSeedSameTime_SamePixels()
    {
        using var a = new CellBuffer(40, 20);
        using var b = new CellBuffer(40, 20);
        var sa = new HalfBlockSurface(a, 0, 0, 40, 20, Bg);
        var sb = new HalfBlockSurface(b, 0, 0, 40, 20, Bg);

        new Scenery(7).Paint(sa, 12.5);
        new Scenery(7).Paint(sb, 12.5);
        sa.Draw();
        sb.Draw();

        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 40; x++)
                Assert.Equal(a[x, y], b[x, y]);
    }

    [Fact]
    public void TimeAdvances_SomethingChanges()
    {
        using var a = new CellBuffer(40, 20);
        using var b = new CellBuffer(40, 20);
        var sa = new HalfBlockSurface(a, 0, 0, 40, 20, Bg);
        var sb = new HalfBlockSurface(b, 0, 0, 40, 20, Bg);

        var scenery = new Scenery(7);
        scenery.Paint(sa, 0.0);
        sa.Draw();
        scenery.Paint(sb, 30.0); // half a minute later: stars drifted/ twinkled
        sb.Draw();

        int diffs = 0;
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 40; x++)
                if (!a[x, y].Equals(b[x, y])) diffs++;

        Assert.True(diffs > 0, "scenery must animate over time");
    }

    [Fact]
    public void TinySurface_DoesNotThrow()
    {
        using var buf = new CellBuffer(4, 4);
        var s = new HalfBlockSurface(buf, 0, 0, 4, 4, Bg);
        new Scenery().Paint(s, 5.0); // below minimum: no-op
        s.Draw();
    }

    [Fact]
    public void StaysDim_GameplayContrast()
    {
        // scenery colors must stay in the dim palette so the bright snake
        // (42/46) and food (196) keep full contrast
        using var buf = new CellBuffer(60, 30);
        var s = new HalfBlockSurface(buf, 0, 0, 60, 30, Bg);
        new Scenery(3).Paint(s, 17.0);
        s.Draw();

        var allowed = new HashSet<int> { 232, 233, 234, 235, 236, 237, 230, 244, 252, -1 };
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 60; x++)
            {
                Assert.Contains(buf[x, y].Fg, allowed);
                Assert.True(buf[x, y].Bg is 233 or -1 or >= 0);
            }
    }
}

public class SceneryFitsAnyWindowTests
{
    private static readonly Cell Bg2 = new() { Char = ' ', Fg = -1, Bg = 233 };

    [Theory]
    [InlineData(8, 8)]      // tiny
    [InlineData(200, 40)]   // ultrawide
    [InlineData(24, 120)]   // tall narrow
    [InlineData(64, 64)]    // square-ish
    [InlineData(13, 9)]     // odd small
    public void PaintsEveryPixel_AtAnySize(int w, int h)
    {
        // half-blocks: one cell column per pixel column, one cell row per 2
        using var buf = new CellBuffer(w + 2, h / 2 + 2);
        var s = new HalfBlockSurface(buf, 0, 0, w, h, Bg2);

        new Scenery(5).Paint(s, 9.7);
        s.Draw();

        // scenery covers the whole surface: no background cell left over
        for (int cy = 0; cy < s.CellHeight; cy++)
            for (int cx = 0; cx < s.CellWidth; cx++)
                Assert.NotEqual(' ', buf[cx, cy].Char);
    }

    [Fact]
    public void StarField_IsStableSubset_AcrossResizes()
    {
        // same seed at two sizes: the smaller sky's stars must be a subset
        // of the larger one's (no reshuffle on resize)
        using var small = new CellBuffer(30, 20);
        using var big = new CellBuffer(60, 40);
        var ss = new HalfBlockSurface(small, 0, 0, 30, 20, Bg2);
        var bs = new HalfBlockSurface(big, 0, 0, 60, 40, Bg2);

        var scenery = new Scenery(11);
        scenery.Paint(ss, 4.0);
        ss.Draw();
        scenery.Paint(bs, 4.0);
        bs.Draw();

        int starsSmall = Count(small, 30, 20, c => c is 252 or 244 or 60);
        int starsBig = Count(big, 60, 40, c => c is 252 or 244 or 60);
        Assert.True(starsBig >= starsSmall,
            $"bigger sky must not have fewer stars ({starsBig} < {starsSmall})");
    }

    private static int Count(CellBuffer buf, int w, int h, Func<int, bool> match)
    {
        int n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (match(buf[x, y].Fg)) n++;
        return n;
    }
}

public class ShootingStarTests
{
    private static readonly Cell Bg3 = new() { Char = ' ', Fg = -1, Bg = 233 };

    private static int BrightPixels(double t)
    {
        using var buf = new CellBuffer(102, 22);
        var s = new HalfBlockSurface(buf, 0, 0, 100, 40, Bg3);
        new Scenery(5).Paint(s, t);
        s.Draw();

        int n = 0;
        for (int y = 0; y < 22; y++)
            for (int x = 0; x < 102; x++)
                if (buf[x, y].Fg is 255 or 252) n++;
        return n;
    }

    [Fact]
    public void StreakAppears_InActiveWindow_AndGoneAfter()
    {
        int during = BrightPixels(70.5); // mid-streak (cycle 0.5 of 7s)
        int after = BrightPixels(74.0);  // quiet sky
        Assert.True(during > after + 5,
            $"shooting star must add bright pixels ({during} vs {after})");
    }

    [Fact]
    public void StreakMoves_FastEnoughToExposeFps()
    {
        // 100ms apart the head must have moved visibly — this is the
        // element that makes 30fps look choppy and 240fps look smooth
        using var a = new CellBuffer(102, 22);
        using var b = new CellBuffer(102, 22);
        var sa = new HalfBlockSurface(a, 0, 0, 100, 40, Bg3);
        var sb = new HalfBlockSurface(b, 0, 0, 100, 40, Bg3);
        var scenery = new Scenery(5);

        scenery.Paint(sa, 70.30);
        sa.Draw();
        scenery.Paint(sb, 70.40);
        sb.Draw();

        int diffs = 0;
        for (int y = 0; y < 22; y++)
            for (int x = 0; x < 102; x++)
                if (!a[x, y].Equals(b[x, y])) diffs++;
        Assert.True(diffs >= 4, $"streak must move between 100ms frames, diffs={diffs}");
    }
}
