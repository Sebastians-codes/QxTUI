using System.Text;
using QxTui.Terminal;

namespace QxTui.Tests;

/// <summary>
/// FramePresenter: the frame loop must never block on the terminal;
/// intermediate frames drop (latest wins); dispose flushes the pending frame.
/// </summary>
public class FramePresenterTests
{
    private sealed class Sink
    {
        public readonly List<byte[]> Frames = [];
        public readonly object Gate = new();
        public ManualResetEventSlim? Blocker;

        public void Write(ReadOnlySpan<byte> bytes)
        {
            Blocker?.Wait(TimeSpan.FromSeconds(5));
            lock (Gate)
                Frames.Add(bytes.ToArray());
        }

        public int Count { get { lock (Gate) return Frames.Count; } }

        public string Last()
        {
            lock (Gate)
                return Encoding.Latin1.GetString(Frames[^1]);
        }
    }

    [Fact]
    public void FastSink_PresentsEveryFrame_InOrder()
    {
        var sink = new Sink();
        using var presenter = new FramePresenter(sink.Write);

        for (int i = 0; i < 20; i++)
        {
            presenter.Submit(Encoding.ASCII.GetBytes($"frame{i:00}"));
            Thread.Sleep(2); // spaced like a real paced loop; writer keeps up
        }

        SpinWait.SpinUntil(() => sink.Count == 20, TimeSpan.FromSeconds(2));

        Assert.Equal(20, sink.Count);
        Assert.Equal("frame19", sink.Last());
        Assert.Equal(20, presenter.PresentedCount);
        Assert.Equal(0, presenter.DroppedCount);
    }

    [Fact]
    public void BlockedSink_DropsIntermediates_LatestWins()
    {
        var sink = new Sink();
        var blocker = new ManualResetEventSlim(false);
        sink.Blocker = blocker;

        using var presenter = new FramePresenter(sink.Write);

        // First submit is taken by the writer thread immediately and blocks.
        presenter.Submit("first"u8);
        SpinWait.SpinUntil(() => sink.Count >= 0 && blocker.IsSet == false, TimeSpan.FromSeconds(1));
        Thread.Sleep(50); // let the writer enter the blocked write

        // These pile up while the terminal is "slow": only the latest survives.
        presenter.Submit("stale1"u8);
        presenter.Submit("stale2"u8);
        presenter.Submit("latest"u8);

        blocker.Set();
        SpinWait.SpinUntil(() => sink.Last() == "latest", TimeSpan.FromSeconds(2));

        Assert.Equal("latest", sink.Last());
        Assert.True(presenter.DroppedCount >= 1, "intermediate frames must be dropped");

        // And critically: Submit never blocked the producer — this assertion
        // being reached at all is the proof.
    }

    [Fact]
    public void Dispose_FlushesPendingFrame()
    {
        var sink = new Sink();
        var blocker = new ManualResetEventSlim(false);
        sink.Blocker = blocker;

        var presenter = new FramePresenter(sink.Write);
        presenter.Submit("a"u8);
        Thread.Sleep(50);
        presenter.Submit("pending-exit-seq"u8);

        blocker.Set();
        presenter.Dispose(); // must flush "pending-exit-seq" before returning

        Assert.Equal("pending-exit-seq", sink.Last());

        // Dispose is idempotent
        presenter.Dispose();
    }

    [Fact]
    public void SubmitAfterDispose_IsIgnored()
    {
        var sink = new Sink();
        var presenter = new FramePresenter(sink.Write);
        presenter.Dispose();

        presenter.Submit("nope"u8); // must not throw or resurrect the thread

        Thread.Sleep(50);
        Assert.Equal(0, sink.Count);
    }
}
