using QxTui.Core;
using QxTui.Render;
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

        // warm the writer thread first: under parallel-suite load thread
        // startup can exceed the submit spacing, and the first burst frame
        // would be a legitimate latest-wins drop (not what this tests)
        presenter.Submit(Encoding.ASCII.GetBytes("warm"));
        SpinWait.SpinUntil(() => sink.Count == 1, TimeSpan.FromSeconds(2));

        for (int i = 0; i < 20; i++)
        {
            presenter.Submit(Encoding.ASCII.GetBytes($"frame{i:00}"));
            Thread.Sleep(2); // spaced like a real paced loop; writer keeps up
        }

        SpinWait.SpinUntil(() => sink.Count == 21, TimeSpan.FromSeconds(2));

        Assert.Equal(21, sink.Count);
        Assert.Equal("frame19", sink.Last());
        Assert.Equal(21, presenter.PresentedCount);
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

public class DropResyncTests
{
    [Fact]
    public void ForceFull_EmitsWholeScreen()
    {
        using var back = new CellBuffer(40, 10);
        using var renderer = new QxTui.Render.Renderer(40, 10, synchronizedOutput: false);
        var output = new System.Buffers.ArrayBufferWriter<byte>();

        back.WriteText(5, 5, "x", fg: 7);
        var delta = renderer.Render(back, output);
        int deltaBytes = delta.BytesWritten;

        output.Clear();
        var full = renderer.Render(back, output, forceFull: true);

        Assert.True(full.FullRepaint);
        Assert.True(full.BytesWritten > deltaBytes * 5,
            $"forceFull ({full.BytesWritten}) must dwarf the delta ({deltaBytes})");
    }

    [Fact]
    public void DroppedFrames_RepairConverges_WithoutFullRepaintFlood()
    {
        // a sink that stalls: submits pile up and get dropped. The repair
        // must re-base on the last SUBMITTED content and converge with a
        // DELTA — the old full-repaint repair flooded exactly the
        // terminals that were already drowning (drop → 40KB → more drops)
        var sizes = new List<int>();
        var writes = new List<string>();
        var gate = new ManualResetEventSlim(true);
        var sinkTerm = new SlowTerminal(sizes, gate, writes);

        var widget = new BlockWidgetPublic('a', 7);
        var root = new QxTui.Layout.LayoutNode { Direction = QxTui.Layout.FlexDirection.Column };
        root.Add(widget.Node);
        widget.Node.Grow = 0.5f;
        var spacer = new QxTui.Widgets.ContainerWidget();
        root.Add(spacer.Node);
        spacer.Node.Grow = 0.5f;

        using var app = new App(sinkTerm, root, targetFps: 1000);
        app.RunFrame(); // initial paint (writer consumes it)

        gate.Reset(); // writer stalls from here on
        widget.Char = 'b'; widget.Invalidate(); app.RunFrame(pace: false);
        widget.Char = 'c'; widget.Invalidate(); app.RunFrame(pace: false);

        gate.Set();
        widget.Char = 'z'; widget.Invalidate(); app.RunFrame(pace: false);
        Thread.Sleep(150);          // let the writer drain

        Assert.True(app.Stats.DroppedFrames > 0, "test setup must actually drop frames");
        // terminal converges on the truth: some write carries the 'z' block
        Assert.Contains(writes, w => w.Contains('z'));
        // repair frame is a delta: no whole-screen sweep (the old repair
        // opened with ESC[H and serialized every row, empty margins too)
        Assert.True(sizes.Count >= 2, "expected initial + post-stall writes");
        Assert.DoesNotContain("\x1b[H", sizes.Count > 0 ? writes[^1] : "");
    }

    private sealed class SlowTerminal(List<int> sizes, ManualResetEventSlim gate,
        List<string>? writes = null) : ITerminal
    {
        public int Width { get; } = 40;
        public int Height { get; } = 10;
        public long Dropped;

        public bool ConsumeResize() => false;
        public bool TryReadInput(out byte b) { b = 0; return false; }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            gate.Wait(TimeSpan.FromSeconds(2));
            lock (sizes)
            {
                sizes.Add(bytes.Length);
                writes?.Add(System.Text.Encoding.UTF8.GetString(bytes));
            }
        }

        public void Enter() { }
        public void Exit() { }
        public void Dispose() { }
    }
}
