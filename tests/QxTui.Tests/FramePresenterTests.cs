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
        app.RunFrame(); // initial paint
        SpinWait.SpinUntil(() =>
        {
            lock (sizes) return sizes.Count >= 1;
        }, TimeSpan.FromSeconds(2)); // writer actually consumed it

        gate.Reset(); // writer stalls from here on
        // three submits: the writer grabs the first and blocks in Write,
        // the mailbox holds the second, the third is the latest-wins drop
        widget.Char = 'b'; widget.Invalidate(); app.RunFrame(pace: false);
        widget.Char = 'c'; widget.Invalidate(); app.RunFrame(pace: false);
        widget.Char = 'd'; widget.Invalidate(); app.RunFrame(pace: false);

        gate.Set();
        widget.Char = 'z'; widget.Invalidate(); app.RunFrame(pace: false);
        // wait on CONDITIONS, not wall clock: suite load makes fixed sleeps flaky
        SpinWait.SpinUntil(() =>
        {
            lock (writes) return writes.Any(w => w.Contains('z'));
        }, TimeSpan.FromSeconds(3));

        Assert.True(app.Stats.DroppedFrames > 0, "test setup must actually drop frames");
        // terminal converges on the truth: some write carries the 'z' block
        Assert.Contains(writes, w => w.Contains('z'));
        // repair frame is a delta: no whole-screen sweep (the old repair
        // opened with ESC[H and serialized every row, empty margins too)
        string lastWrite;
        lock (writes) lastWrite = writes.Count > 0 ? writes[^1] : "";
        Assert.True(sizes.Count >= 2, "expected initial + post-stall writes");
        Assert.DoesNotContain("\x1b[H", lastWrite);
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

public class WrittenIdTests
{
    private sealed class GateSink(List<string> seen, ManualResetEventSlim gate)
    {
        public void Write(ReadOnlySpan<byte> b)
        {
            gate.Wait(TimeSpan.FromSeconds(2));
            lock (seen) seen.Add(System.Text.Encoding.UTF8.GetString(b));
        }
    }

    [Fact]
    public void WrittenId_SkipsDroppedSubmits()
    {
        var seen = new List<string>();
        var gate = new ManualResetEventSlim(true);
        using var p = new FramePresenter(new GateSink(seen, gate).Write);

        p.Submit("frame1"u8, 1);
        // WrittenId is published AFTER the sink returns: spin on it, not
        // on the sink's list
        SpinWait.SpinUntil(() => p.WrittenId == 1, TimeSpan.FromSeconds(2));

        gate.Reset();                 // writer blocks inside the next write
        p.Submit("frame2"u8, 2);      // writer grabs it, stalls
        Thread.Sleep(50);             // let the wake land before submitting
        p.Submit("frame3"u8, 3);      // mailbox
        p.Submit("frame4"u8, 4);      // replaces 3 -> 3 is DROPPED
        Assert.Equal(1, p.WrittenId); // nothing new reached the terminal

        gate.Set();
        SpinWait.SpinUntil(() => { lock (seen) return seen.Count == 3; }, TimeSpan.FromSeconds(2));

        lock (seen)
        {
            // the dropped frame's content never reaches the terminal, so
            // it must never be a repair base
            Assert.Equal(["frame1", "frame2", "frame4"], seen);
        }
        Assert.Equal(4, p.WrittenId);
        Assert.Equal(1, p.DroppedCount);
    }
}

public class SnapshotSlotTests
{
    [Fact]
    public void UseSlotA_ProtectsTheWrittenSnapshot()
    {
        // the written frame's slot must never be recycled, or drop-repair
        // loses the terminal's true base and freezes holes
        Assert.False(App.UseSlotA(5, 6, 5)); // A is on screen -> write B
        Assert.True(App.UseSlotA(5, 6, 6));  // B is on screen -> write A
        Assert.True(App.UseSlotA(5, 6, 9));  // neither -> recycle older (A)
        Assert.False(App.UseSlotA(8, 6, 9)); // neither -> recycle older (B)
    }
}
