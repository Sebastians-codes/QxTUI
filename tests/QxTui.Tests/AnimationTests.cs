using QxTui.Animation;

namespace QxTui.Tests;

public class EaseTests
{
    [Theory]
    [InlineData(Easing.Linear)]
    [InlineData(Easing.InQuad)]
    [InlineData(Easing.OutQuad)]
    [InlineData(Easing.InOutQuad)]
    [InlineData(Easing.OutCubic)]
    [InlineData(Easing.InOutCubic)]
    public void EndpointsAreExact(Easing easing)
    {
        Assert.Equal(0f, Ease.Apply(easing, 0f), precision: 5);
        Assert.Equal(1f, Ease.Apply(easing, 1f), precision: 5);
    }

    [Fact]
    public void LinearIsIdentity()
    {
        for (float t = 0f; t <= 1f; t += 0.1f)
            Assert.Equal(t, Ease.Apply(Easing.Linear, t), precision: 5);
    }

    [Fact]
    public void OutCubicIsMonotonic()
    {
        float prev = float.MinValue;
        for (float t = 0f; t <= 1f; t += 0.01f)
        {
            float v = Ease.Apply(Easing.OutCubic, t);
            Assert.True(v >= prev - 1e-6f, $"non-monotonic at t={t}: {v} < {prev}");
            prev = v;
        }
    }

    [Fact]
    public void OutQuadMidpoint()
    {
        // t(2-t) at 0.5 = 0.75
        Assert.Equal(0.75f, Ease.Apply(Easing.OutQuad, 0.5f), precision: 5);
    }
}

public class AnimationSchedulerTests
{
    private static (AnimationScheduler sched, VirtualTimeSource clock) MakeScheduler()
    {
        var clock = new VirtualTimeSource();
        return (new AnimationScheduler(clock), clock);
    }

    [Fact]
    public void LinearTransition_InterpolatesAndLandsExact()
    {
        var (sched, clock) = MakeScheduler();
        var signal = new QxTui.Reactive.Signal<float>(0f);

        sched.Animate(signal, 1f, new Transition(TimeSpan.FromSeconds(1), Easing.Linear));

        clock.Advance(TimeSpan.FromSeconds(0.5));
        sched.Tick();
        Assert.Equal(0.5f, signal.Value, precision: 5);

        clock.Advance(TimeSpan.FromSeconds(0.5));
        sched.Tick();
        Assert.Equal(1f, signal.Value); // EXACT final value, no drift (§2)
        Assert.Equal(0, sched.ActiveCount);
    }

    [Fact]
    public void EasedTransition_AppliesCurve()
    {
        var (sched, clock) = MakeScheduler();
        var values = new List<float>();

        sched.Animate(0f, 1f, new Transition(TimeSpan.FromSeconds(1), Easing.OutQuad), values.Add);

        clock.Advance(TimeSpan.FromSeconds(0.5));
        sched.Tick();

        Assert.Equal(0.75f, values[^1], precision: 5);
    }

    [Fact]
    public void Delay_HoldsUntilElapsed()
    {
        var (sched, clock) = MakeScheduler();
        var values = new List<float>();

        sched.Animate(0f, 1f,
            new Transition(TimeSpan.FromSeconds(1), Easing.Linear, Delay: TimeSpan.FromSeconds(2)),
            values.Add);

        clock.Advance(TimeSpan.FromSeconds(1));
        sched.Tick();
        Assert.Empty(values); // still in delay

        clock.Advance(TimeSpan.FromSeconds(1.5)); // 2.5 total = 0.5 into transition
        sched.Tick();
        Assert.Equal(0.5f, values[^1], precision: 5);
    }

    [Fact]
    public void LoopRepeat_RestartsFromBeginning()
    {
        var (sched, clock) = MakeScheduler();
        var values = new List<float>();

        sched.Animate(0f, 1f,
            new Transition(TimeSpan.FromSeconds(1), Easing.Linear, Repeat: RepeatMode.Loop),
            values.Add);

        clock.Advance(TimeSpan.FromSeconds(2.25)); // 2 full cycles + 0.25
        sched.Tick();

        Assert.Equal(0.25f, values[^1], precision: 5);
        Assert.Equal(1, sched.ActiveCount); // still running
    }

    [Fact]
    public void ReverseRepeat_PingPongs()
    {
        var (sched, clock) = MakeScheduler();
        var values = new List<float>();

        sched.Animate(0f, 1f,
            new Transition(TimeSpan.FromSeconds(1), Easing.Linear, Repeat: RepeatMode.Reverse),
            values.Add);

        clock.Advance(TimeSpan.FromSeconds(1.5)); // odd cycle → ping-pong back
        sched.Tick();
        Assert.Equal(0.5f, values[^1], precision: 5);

        clock.Advance(TimeSpan.FromSeconds(1.0)); // t=2.5 → even cycle, frac 0.5
        sched.Tick();
        Assert.Equal(0.5f, values[^1], precision: 5);
    }

    [Fact]
    public void Every_FiresOncePerInterval_OnSingleClock()
    {
        var (sched, clock) = MakeScheduler();
        long last = -1;
        int calls = 0;

        sched.Every(TimeSpan.FromMilliseconds(100), c => { last = c; calls++; });

        clock.Advance(TimeSpan.FromMilliseconds(50));
        sched.Tick();
        Assert.Equal(0, calls); // not yet

        clock.Advance(TimeSpan.FromMilliseconds(60)); // 110ms → cycle 1
        sched.Tick();
        Assert.Equal(1, calls);
        Assert.Equal(1, last);

        clock.Advance(TimeSpan.FromMilliseconds(250)); // 360ms → cycle 3
        sched.Tick();
        Assert.Equal(2, calls); // one call per tick, catching up to latest cycle
        Assert.Equal(3, last);
    }

    [Fact]
    public void Every_CancelStopsTicks()
    {
        var (sched, clock) = MakeScheduler();
        int calls = 0;
        var handle = sched.Every(TimeSpan.FromMilliseconds(10), _ => calls++);

        clock.Advance(TimeSpan.FromMilliseconds(50));
        sched.Tick();
        Assert.True(calls >= 1);

        handle.Dispose();
        int before = calls;
        clock.Advance(TimeSpan.FromMilliseconds(50));
        sched.Tick();
        Assert.Equal(before, calls);
    }

    [Fact]
    public void Cancel_StopsWrites()
    {
        var (sched, clock) = MakeScheduler();
        var values = new List<float>();

        var handle = sched.Animate(0f, 1f,
            new Transition(TimeSpan.FromSeconds(1), Easing.Linear), values.Add);

        clock.Advance(TimeSpan.FromSeconds(0.5));
        sched.Tick();
        Assert.Single(values);

        handle.Dispose();
        clock.Advance(TimeSpan.FromSeconds(0.5));
        sched.Tick();

        Assert.Single(values); // no further writes
        Assert.Equal(0, sched.ActiveCount);
    }

    [Fact]
    public void MultipleAnimations_SingleClockTicksAll()
    {
        var (sched, clock) = MakeScheduler();
        var a = new QxTui.Reactive.Signal<float>(0f);
        var b = new QxTui.Reactive.Signal<float>(10f);

        sched.Animate(a, 1f, new Transition(TimeSpan.FromSeconds(1), Easing.Linear));
        sched.Animate(b, 20f, new Transition(TimeSpan.FromSeconds(1), Easing.Linear));

        clock.Advance(TimeSpan.FromSeconds(0.5));
        int writes = sched.Tick();

        Assert.Equal(2, writes);
        Assert.Equal(0.5f, a.Value, precision: 5);
        Assert.Equal(15f, b.Value, precision: 5);
    }
}

public class TimeSourceTests
{
    [Fact]
    public void VirtualClock_AdvancesDeterministically()
    {
        var clock = new VirtualTimeSource();
        Assert.Equal(TimeSpan.Zero, clock.Now);
        clock.Advance(TimeSpan.FromMilliseconds(16));
        Assert.Equal(TimeSpan.FromMilliseconds(16), clock.Now);
    }
}
