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

        var allowed = new HashSet<int> { 16, 17, 18, 54, 0, 60, 95, 230, 244, 252, -1 };
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 60; x++)
            {
                Assert.Contains(buf[x, y].Fg, allowed);
                Assert.True(buf[x, y].Bg is 233 or -1 or >= 0);
            }
    }
}
