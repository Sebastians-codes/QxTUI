using System.Diagnostics;

namespace QxTui.Render;

/// <summary>
/// Stopwatch-precision frame pacer + frame-time statistics.
/// Renders the LATEST state at the target rate; never queues frames
/// (AGENT.md §2). Stats back the on-screen fps / p99 proof.
/// </summary>
public sealed class FramePacer
{
    private const int HistorySize = 240;

    private readonly double[] _frameTimesMs = new double[HistorySize];
    private readonly double[] _sorted = new double[HistorySize];
    private int _count;
    private long _frameIndex;
    private long _lastFrameTimestamp;
    private readonly double _frameBudgetTicks;

    public int TargetFps { get; }
    public long FrameCount => _frameIndex;

    public FramePacer(int targetFps)
    {
        if (targetFps <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetFps));

        TargetFps = targetFps;
        _frameBudgetTicks = Stopwatch.Frequency / (double)targetFps;
        _lastFrameTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Record a completed frame's work duration and wait out the
    /// remainder of the frame budget. Call once per frame, after rendering.
    ///
    /// Pacing strategy: Thread.Sleep on Linux wakes 1-4ms late under desktop
    /// load (scheduler latency, not just timer slack). At a 240fps budget of
    /// 4.17ms that overshoot alone caps the loop near ~200fps, so for high
    /// rate targets (budget ≤ SpinOnlyBudgetMs) we skip sleeping entirely and
    /// spin-wait the deadline. The burned core is deliberate and documented:
    /// holding the frame target IS the product (AGENT.md §2). Low targets
    /// (e.g. 60fps) still sleep with a spin margin to stay CPU-friendly.</summary>
    public void PaceFrame(double workMs)
    {
        _frameTimesMs[_frameIndex % HistorySize] = workMs;
        if (_count < HistorySize) _count++;
        _frameIndex++;

        double budgetMs = _frameBudgetTicks / Stopwatch.Frequency * 1000.0;
        double elapsedMs = (Stopwatch.GetTimestamp() - _lastFrameTimestamp)
                           / (double)Stopwatch.Frequency * 1000.0;
        double remainingMs = budgetMs - elapsedMs;

        // Spin margin scales with budget: a 60fps loop must not burn 2ms of
        // core per frame (that alone was ~12% CPU); high-rate targets keep
        // the full margin because precision is the product there.
        double margin = Math.Min(SpinMarginMs, budgetMs * 0.15);
        if (budgetMs > SpinOnlyBudgetMs && remainingMs > margin + 1.0)
            Thread.Sleep((int)(remainingMs - margin));

        // TRUE spin to the deadline. Do NOT use SpinWait.SpinUntil here:
        // after a short spin it degenerates into Thread.Sleep(1) internally,
        // and that 1-4ms overshoot is precisely what capped 240fps at ~208.
        long deadline = _lastFrameTimestamp + (long)_frameBudgetTicks;
        long now = Stopwatch.GetTimestamp();
        while (now < deadline)
        {
            Thread.SpinWait(1);
            now = Stopwatch.GetTimestamp();
        }

        _lastFrameTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Frame budgets at or below this are paced by pure spin-wait.</summary>
    private const double SpinOnlyBudgetMs = 8.0;

    /// <summary>How many ms before the deadline we stop sleeping and spin
    /// (only used for budgets above SpinOnlyBudgetMs).</summary>
    private const double SpinMarginMs = 2.0;

    public double Fps { get; private set; }
    private double _fpsWindowStart;
    private int _fpsWindowFrames;

    public double FrameTimeP50Ms => Percentile(0.50);
    public double FrameTimeP99Ms => Percentile(0.99);
    public double ActualFps => _fpsWindowFrames / Math.Max(1e-9, Now() - _fpsWindowStart);

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Call at frame start boundary to maintain the rolling fps estimate.</summary>
    public void MarkFpsSample()
    {
        _fpsWindowFrames++;
        double span = Now() - _fpsWindowStart;
        if (span >= 0.5)
        {
            Fps = _fpsWindowFrames / span;
            _fpsWindowFrames = 0;
            _fpsWindowStart = Now();
        }
    }

    private double Percentile(double p)
    {
        if (_count == 0) return 0;
        Array.Copy(_frameTimesMs, _sorted, _count);
        Array.Sort(_sorted, 0, _count);
        int idx = Math.Min(_count - 1, (int)(p * _count));
        return _sorted[idx];
    }
}
