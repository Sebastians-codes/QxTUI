using System.Diagnostics;

namespace QxTui.Animation;

/// <summary>
/// Clock abstraction. Production uses the high-resolution stopwatch;
/// tests inject a virtual clock (AGENT.md §3: never real sleeps in tests).
/// </summary>
public interface ITimeSource
{
    TimeSpan Now { get; }
}

public sealed class StopwatchTimeSource : ITimeSource
{
    private static readonly long StartTicks = Stopwatch.GetTimestamp();

    public TimeSpan Now =>
        TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - StartTicks) / Stopwatch.Frequency);
}

public sealed class VirtualTimeSource(TimeSpan? start = null) : ITimeSource
{
    public TimeSpan Now { get; private set; } = start ?? TimeSpan.Zero;

    public void Advance(TimeSpan delta) => Now += delta;
}
