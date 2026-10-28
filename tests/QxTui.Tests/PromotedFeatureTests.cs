using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Widgets;

namespace QxTui.Tests;

public class SmoothedSamplerTests
{
    [Fact]
    public void SamplesAtInterval_InterpolatesBetween()
    {
        double source = 0;
        var sampler = new SmoothedSampler(() => source, intervalMs: 100, alpha: 1.0);

        Assert.True(sampler.Update(0));      // first sample immediate
        Assert.Equal(0, sampler.Value);

        source = 10;
        Assert.False(sampler.Update(50));    // inside window: still easing 0→0
        Assert.Equal(0, sampler.Value);

        Assert.True(sampler.Update(100));    // window elapsed: sample 10
        Assert.Equal(10, sampler.Current);

        Assert.False(sampler.Update(150));   // easing 0→10 between samples
        Assert.Equal(5, sampler.Value, precision: 3);
        Assert.Equal(0.5f, sampler.Phase, precision: 3);
    }

    [Fact]
    public void Alpha_SmoothsJitter()
    {
        int call = 0;
        double[] seq = [100, 0, 100, 0];
        var sampler = new SmoothedSampler(() => seq[Math.Min(call, 3)], 10, alpha: 0.5);
        call++;

        sampler.Update(0);
        for (long t = 10; t <= 40; t += 10)
        {
            sampler.Update(t);
            call++;
        }

        // EMA of 100,0,100,0 with alpha .5 never reaches the extremes
        Assert.True(sampler.Current > 0 && sampler.Current < 100);
    }
}

public class RingHistorySignalTests
{
    [Fact]
    public void Push_BumpsPushedSignal()
    {
        var h = new RingHistory(8);
        long v0 = h.Pushed.Value;
        h.Push(1);
        h.Push(2);
        Assert.Equal(v0 + 2, h.Pushed.Value);
    }

    [Fact]
    public void SparklineWidget_AutoInvalidatesOnPush()
    {
        var h = new RingHistory(8);
        var spark = new Sparkline(h);
        var root = UI.Column(0, spark.Height(2));
        var term = new FakeTerminal(20, 6);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        app.RunFrame();
        Assert.Equal(1, spark.RenderCount);

        h.Push(42); // no manual Invalidate anywhere
        app.RunFrame();
        Assert.Equal(2, spark.RenderCount);
    }
}

public class TargetFpsSignalTests
{
    [Fact]
    public void SetTargetFps_UpdatesSignal()
    {
        var term = new FakeTerminal(10, 5);
        using var app = new App(term, new LayoutNode(), targetFps: 60, asyncPresenting: false);
        Assert.Equal(60, app.TargetFpsSignal.Value);

        app.SetTargetFps(240);
        Assert.Equal(240, app.TargetFpsSignal.Value);
        Assert.Equal(240, app.TargetFps);
    }
}

public class PixelCanvasTests
{
    private sealed class CanvasProbe : Widget
    {
        public bool LastRebuilt;
        public override void Render(CellBuffer buffer)
        {
            var (_, rebuilt) = Pixels(Rect, new Cell { Char = ' ', Fg = -1, Bg = 233 });
            LastRebuilt = rebuilt;
        }
    }

    [Fact]
    public void Canvas_RebuildsOnlyOnRectChange()
    {
        var probe = new CanvasProbe();
        var root = UI.Column(0, probe.Grow(1));
        var term = new FakeTerminal(20, 8);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        app.RunFrame();
        Assert.True(probe.LastRebuilt); // first build

        probe.Invalidate();
        app.RunFrame();
        Assert.False(probe.LastRebuilt); // same rect: cached

        term.SimulateResize(30, 8);
        app.RunFrame();
        Assert.True(probe.LastRebuilt); // new rect: rebuilt
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(512, "512B")]
    [InlineData(2048, "2.0KiB")]
    [InlineData(737280, "720KiB")]
    [InlineData(8598323200.0, "8.0GiB")]
    public void HumanBytes(double bytes, string expected) =>
        Assert.Equal(expected, Format.HumanBytes(bytes));

    [Theory]
    [InlineData(512, "512K")]
    [InlineData(8192, "8.0M")]
    [InlineData(737280, "720M")]
    [InlineData(14680064, "14.0G")]
    public void CompactKiB(long kb, string expected) =>
        Assert.Equal(expected, Format.CompactKiB(kb));
}
