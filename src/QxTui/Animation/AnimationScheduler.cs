namespace QxTui.Animation;

public enum RepeatMode
{
    None,
    Loop,     // restart from beginning
    Reverse,  // ping-pong
}

/// <summary>
/// Declarative transition description. Immutable value object.
/// </summary>
public readonly record struct Transition(
    TimeSpan Duration,
    Easing Easing = Easing.Linear,
    RepeatMode Repeat = RepeatMode.None,
    TimeSpan? Delay = null);

/// <summary>Handle for a running animation. Dispose to cancel.</summary>
public interface IAnimationHandle : IDisposable
{
    bool IsCompleted { get; }
}

/// <summary>
/// Single global animation clock (AGENT.md §2: no per-component timers).
/// Tick() advances every active transition and writes interpolated values
/// into their targets — typically Signal&lt;float&gt; — which in turn mark only
/// the subscribing widgets dirty.
///
/// Hot path: Tick() must not allocate. Active animations live in a pooled
/// List; completed entries are removed by swap-with-last.
/// </summary>
public sealed class AnimationScheduler(ITimeSource? time = null)
{
    private readonly List<Running> _running = new(16);
    private readonly ITimeSource _time = time ?? new StopwatchTimeSource();

    public ITimeSource Time => _time;

    public int ActiveCount => _running.Count;

    /// <summary>
    /// Animate a float from <paramref name="from"/> to <paramref name="to"/>,
    /// writing each interpolated value through <paramref name="write"/>.
    /// On completion the EXACT final value is written (no fp drift).
    /// </summary>
    public IAnimationHandle Animate(float from, float to, Transition transition, Action<float> write)
    {
        var run = new Running
        {
            From = from,
            To = to,
            Duration = transition.Duration,
            Delay = transition.Delay ?? TimeSpan.Zero,
            Easing = transition.Easing,
            Repeat = transition.Repeat,
            Start = _time.Now,
            Write = write,
        };
        _running.Add(run);
        return run;
    }

    /// <summary>Convenience: animate a Signal&lt;float&gt;.</summary>
    public IAnimationHandle Animate(
        Reactive.Signal<float> target, float to, Transition transition)
    {
        float from = target.Value;
        return Animate(from, to, transition, v => target.Set(v));
    }

    /// <summary>
    /// Advance all active animations to the current clock. Returns the number
    /// of animations that produced a write this tick. Allocation-free.
    /// </summary>
    public int Tick()
    {
        var now = _time.Now;
        int writes = 0;

        for (int i = _running.Count - 1; i >= 0; i--)
        {
            var run = _running[i];
            if (run.Cancelled)
            {
                RemoveAt(i);
                continue;
            }

            var elapsed = now - run.Start;
            if (elapsed < run.Delay)
                continue;

            elapsed -= run.Delay;
            double total = run.Duration.TotalSeconds;
            if (total <= 0)
                total = double.Epsilon;

            double t = elapsed.TotalSeconds / total;

            if (t >= 1.0)
            {
                switch (run.Repeat)
                {
                    case RepeatMode.None:
                        run.Write(run.To); // exact final value
                        writes++;
                        run.Completed = true;
                        RemoveAt(i);
                        continue;

                    case RepeatMode.Loop:
                        t -= Math.Floor(t);
                        break;

                    case RepeatMode.Reverse:
                    {
                        double cycle = Math.Floor(t);
                        double frac = t - cycle;
                        t = (long)cycle % 2 == 0 ? frac : 1.0 - frac;
                        break;
                    }
                }
            }

            float eased = Ease.Apply(run.Easing, (float)t);
            run.Write(run.From + (run.To - run.From) * eased);
            writes++;
        }

        return writes;
    }

    private void RemoveAt(int i)
    {
        int last = _running.Count - 1;
        _running[i] = _running[last];
        _running.RemoveAt(last);
    }

    private sealed class Running : IAnimationHandle
    {
        public required Action<float> Write { get; init; }
        public required float From { get; init; }
        public required float To { get; init; }
        public required TimeSpan Duration { get; init; }
        public required TimeSpan Delay { get; init; }
        public required Easing Easing { get; init; }
        public required RepeatMode Repeat { get; init; }
        public required TimeSpan Start { get; init; }

        public bool Cancelled;
        public bool Completed;
        public bool IsCompleted => Completed || Cancelled;

        public void Dispose() => Cancelled = true;
    }
}
