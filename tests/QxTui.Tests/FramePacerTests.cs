using System.Diagnostics;
using QxTui.Core;
using QxTui.Render;

namespace QxTui.Tests;

/// <summary>
/// Real-time pacing tests. These deliberately measure wall-clock time:
/// the frame target is a product requirement (AGENT.md §2), so CI must
/// catch pacing regressions. Tolerances are wide enough for loaded CI
/// runners but tight enough to catch sleep-overshoot class bugs
/// (207fps against a 240 target = -14%, outside tolerance).
/// </summary>
public class FramePacerTests
{
    [Fact]
    public void HighTarget_HoldsRate_WithinTolerance()
    {
        var pacer = new FramePacer(240);
        const int frames = 240; // exactly one second at target

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
            pacer.PaceFrame(0.1);
        sw.Stop();

        double elapsedMs = sw.Elapsed.TotalMilliseconds;
        double expectedMs = frames * 1000.0 / 240;

        // ±10%: spin-pacing should land within ~1-2%; the slack absorbs CI noise.
        Assert.True(Math.Abs(elapsedMs - expectedMs) <= expectedMs * 0.10,
            $"240fps x {frames} frames took {elapsedMs:F1}ms, expected ~{expectedMs:F1}ms ±10%");
    }

    [Fact]
    public void LowTarget_SleepsAndStillHoldsRate()
    {
        var pacer = new FramePacer(60);
        const int frames = 60; // one second at target

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
            pacer.PaceFrame(0.1);
        sw.Stop();

        double elapsedMs = sw.Elapsed.TotalMilliseconds;
        Assert.True(Math.Abs(elapsedMs - 1000.0) <= 100.0,
            $"60fps x {frames} frames took {elapsedMs:F1}ms, expected ~1000ms ±10%");
    }

    [Fact]
    public void OverrunFrames_NeverQueue_NextFrameStartsImmediately()
    {
        // If work exceeds the budget, the pacer must not try to "catch up"
        // by sleeping negative time or accumulating debt.
        var pacer = new FramePacer(240);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
            pacer.PaceFrame(20.0); // pretend each frame took 20ms
        sw.Stop();

        // 10 frames with no waiting at all beyond the (already past) deadline
        Assert.True(sw.Elapsed.TotalMilliseconds < 100,
            $"overrun frames should not add waits, took {sw.Elapsed.TotalMilliseconds:F1}ms");
    }

    [Fact]
    public void Stats_PercentilesTrackRecordedWork()
    {
        var pacer = new FramePacer(1000); // tiny budget → pure spin, fast test
        for (int i = 0; i < 100; i++)
            pacer.PaceFrame(i < 99 ? 1.0 : 50.0);

        Assert.Equal(1.0, pacer.FrameTimeP50Ms, precision: 3);
        Assert.Equal(50.0, pacer.FrameTimeP99Ms, precision: 3);
    }
}

public class RuntimeFpsControlTests
{
    [Fact]
    public void SetTargetFps_ChangesPaceImmediately()
    {
        var pacer = new FramePacer(240);
        Assert.Equal(240, pacer.TargetFps);

        pacer.SetTargetFps(60);
        Assert.Equal(60, pacer.TargetFps);

        // 30 frames at 60fps ≈ 500ms
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 30; i++)
            pacer.PaceFrame(0.1);
        sw.Stop();

        Assert.True(Math.Abs(sw.Elapsed.TotalMilliseconds - 500) <= 75,
            $"30 frames at retargeted 60fps took {sw.Elapsed.TotalMilliseconds:F0}ms, expected ~500ms");
    }

    [Fact]
    public void SetTargetFps_RejectsNonPositive()
    {
        var pacer = new FramePacer(60);
        Assert.Throws<ArgumentOutOfRangeException>(() => pacer.SetTargetFps(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pacer.SetTargetFps(-5));
        Assert.Equal(60, pacer.TargetFps); // unchanged
    }

    [Fact]
    public void App_ExposesRuntimeRetarget()
    {
        var term = new FakeTerminal(20, 10);
        var root = new QxTui.Layout.LayoutNode();
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        Assert.Equal(60, app.TargetFps);
        app.SetTargetFps(144);
        Assert.Equal(144, app.TargetFps);
    }
}

public class AdaptivePacingTests
{
    private static App MakeApp(int target)
    {
        var term = new FakeTerminal(20, 10);
        return new App(term, new QxTui.Layout.LayoutNode(), targetFps: target,
            asyncPresenting: false);
    }

    [Fact]
    public void CollapsingPresented_LowersEffectiveRate()
    {
        using var app = MakeApp(1000);

        // two slow windows in a row (terminal presenting 8fps)
        app.AdaptToPresented(8);
        Assert.Equal(1000, app.TargetFps); // user target untouched
        int eff1 = app.AdaptToPresented(8);

        Assert.True(eff1 <= 60, $"effective must collapse toward presented, got {eff1}");
        Assert.True(eff1 >= 12, $"but not below a usable floor, got {eff1}");
    }

    [Fact]
    public void HealthyPresented_ClumbsBackToUserTarget()
    {
        using var app = MakeApp(240);

        int eff = app.AdaptToPresented(10);
        eff = app.AdaptToPresented(10); // collapses
        Assert.True(eff < 240);

        for (int i = 0; i < 20; i++)
            eff = app.AdaptToPresented(eff); // keeps up → climb

        Assert.Equal(240, eff); // fully recovered to the user target
    }

    [Fact]
    public void SteadyTerminal_NeverAdapts()
    {
        using var app = MakeApp(120);
        for (int i = 0; i < 10; i++)
            Assert.Equal(120, app.AdaptToPresented(118)); // keeping up: no change
    }
}
