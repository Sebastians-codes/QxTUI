using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;

namespace QxTui.Tests;

/// <summary>Minimal test widget: fills its rect with a char, counts renders.</summary>
file sealed class BlockWidget(char c, int fg) : Widget
{
    public char C = c;

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = C, Fg = fg, Bg = -1 });
    }
}

/// <summary>Widget driven by a signal — the animation fast path.</summary>
file sealed class SignalWidget : Widget
{
    private readonly Signal<float> _source;
    private readonly IDisposable _sub;

    public SignalWidget(Signal<float> source)
    {
        _source = source;
        _sub = source.Subscribe(_ => Invalidate());
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        int pos = (int)(_source.Value * (r.W - 1));
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        if (r.W > 0 && pos >= 0 && pos < r.W)
            buffer[r.X + pos, r.Y] = new Cell { Char = '@', Fg = 9, Bg = -1 };
    }

    public override void Dispose() => _sub.Dispose();
}

public class AppTests
{
    private static (App app, FakeTerminal term, VirtualTimeSource clock) MakeApp(
        LayoutNode root, IEnumerable<Widget> widgets, int w = 40, int h = 20)
    {
        var term = new FakeTerminal(w, h);
        var clock = new VirtualTimeSource();
        var app = new App(term, root, targetFps: 240, time: clock, asyncPresenting: false);
        foreach (var widget in widgets)
            app.AddWidget(widget);
        return (app, term, clock);
    }

    [Fact]
    public void Invariant3_AnimatingWidget_IsolatedFromStaticWidget()
    {
        // Static block on top (fixed height), signal-driven widget below (grow).
        var staticWidget = new BlockWidget('#', 7);
        var signal = new Signal<float>(0f);
        var animWidget = new SignalWidget(signal);

        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(staticWidget.Node).Add(animWidget.Node);
        staticWidget.Node.Height = 3;
        animWidget.Node.Grow = 1f;

        var (app, term, clock) = MakeApp(root, [staticWidget, animWidget]);
        app.Scheduler.Animate(signal, 1f,
            new Transition(TimeSpan.FromSeconds(1), Easing.Linear, Repeat: RepeatMode.Loop));

        // First frame: everything renders once.
        app.RunFrame();
        Assert.Equal(1, staticWidget.RenderCount);
        Assert.Equal(1, animWidget.RenderCount);

        // 60 animation frames: signal changes every frame → anim widget dirty.
        for (int i = 0; i < 60; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(16));
            app.RunFrame();
        }

        // THE isolation proof:
        Assert.True(animWidget.RenderCount >= 30,
            $"anim widget should re-render as it animates, got {animWidget.RenderCount}");
        Assert.Equal(1, staticWidget.RenderCount); // untouched: rendered exactly once

        // And the wire proof: last frame bytes are tiny compared to a full
        // repaint of 40x20 (~2400+ bytes). Only the animating row cells move.
        Assert.True(app.Stats.LastBytesWritten < 200,
            $"bytes/frame should stay tiny, got {app.Stats.LastBytesWritten}");
    }

    [Fact]
    public void Invariant4_Resize_HeightOnly_DoesNotReRenderUnchangedTopWidget()
    {
        var top = new BlockWidget('T', 7);
        var bottom = new BlockWidget('B', 8);

        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(top.Node).Add(bottom.Node);
        top.Node.Height = 3;
        bottom.Node.Grow = 1f;

        var (app, term, _) = MakeApp(root, [top, bottom], w: 40, h: 20);

        app.RunFrame();
        Assert.Equal(1, top.RenderCount);
        Assert.Equal(1, bottom.RenderCount);

        // Grow the window vertically: top's rect (0,0,40,3) is unchanged.
        term.SimulateResize(40, 26);
        app.RunFrame();

        Assert.Equal(1, app.Stats.Relayouts);
        Assert.Equal(1, top.RenderCount);    // cached cells kept — NOT re-rendered
        Assert.Equal(2, bottom.RenderCount); // grew → re-rendered
    }

    [Fact]
    public void Resize_WidthChange_ReRendersMovedWidgets()
    {
        var left = new BlockWidget('L', 7);
        var right = new BlockWidget('R', 8);

        var root = new LayoutNode { Direction = FlexDirection.Row, Height = 5 };
        root.Add(left.Node).Add(right.Node);
        left.Node.Width = 10;
        right.Node.Grow = 1f;

        var (app, term, _) = MakeApp(root, [left, right], w: 40, h: 20);
        app.RunFrame();

        term.SimulateResize(50, 20); // wider: left stays 10 wide at (0,0)
        app.RunFrame();

        Assert.Equal(1, left.RenderCount); // fixed size, same position
        Assert.Equal(2, right.RenderCount); // grew wider
    }

    [Fact]
    public void IdleFrames_WriteOnlySyncBytes()
    {
        var widget = new BlockWidget('.', 7);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(widget.Node);
        widget.Node.Grow = 1f; // fill viewport (leaf without grow/measure = 0 main size)

        var (app, term, _) = MakeApp(root, [widget]);

        app.RunFrame(); // initial paint
        long firstFrameBytes = app.Stats.LastBytesWritten;
        Assert.True(firstFrameBytes > 100); // real content went out

        app.RunFrame(); // idle
        app.RunFrame(); // idle

        Assert.Equal(0, app.Stats.LastDirtyCells);
        Assert.True(app.Stats.LastBytesWritten <= 16); // sync wrapper only
        Assert.Equal(1, widget.RenderCount);
    }

    [Fact]
    public void Resize_ClearsVacatedGapRegions_NoStalePixels()
    {
        // Regression: gap rows / vacated regions must be erased after a
        // relayout, otherwise stale content accumulates across resizes.
        var top = new BlockWidget('T', 7);
        var bottom = new BlockWidget('B', 8);

        var root = new LayoutNode { Direction = FlexDirection.Column, Gap = 1 };
        root.Add(top.Node).Add(bottom.Node);
        top.Node.Height = 3;
        bottom.Node.Grow = 1f;

        var (app, term, _) = MakeApp(root, [top, bottom], w: 20, h: 12);
        app.RunFrame();
        Assert.Equal(1, top.RenderCount);
        Assert.Equal(1, bottom.RenderCount);

        // Shrink so bottom collapses to zero height: its old rows (4..11)
        // and the gap row (3) become uncovered → must be erased.
        term.SimulateResize(20, 4); // top 3 + gap 1 + bottom 0
        app.RunFrame();

        Assert.Equal(1, top.RenderCount); // (0,0,20,3) unchanged → cached
        Assert.Equal(2, bottom.RenderCount); // collapsed → re-rendered (no-op draw)

        // The erase happened in the resize frame; an idle frame must now be
        // perfectly clean (no stale pixels left to discover later).
        app.RunFrame();
        Assert.Equal(0, app.Stats.LastDirtyCells);
        Assert.True(app.Stats.LastBytesWritten <= 16);
    }

    [Fact]
    public void Input_QQuits()
    {
        var widget = new BlockWidget('.', 7);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(widget.Node);

        var (app, term, _) = MakeApp(root, [widget]);
        bool sawQ = false;
        app.OnInput = b => { sawQ = b == (byte)'q'; return !sawQ; };

        term.QueueInput((byte)'q');
        app.Run(); // must return, not hang

        Assert.True(sawQ);
        Assert.True(term.Entered);
        Assert.True(term.Exited);
    }

    [Fact]
    public void FrameLoop_PacesToOneWritePerFrame()
    {
        var widget = new BlockWidget('.', 7);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(widget.Node);

        var (app, term, _) = MakeApp(root, [widget]);

        app.RunFrame();
        app.RunFrame();
        app.RunFrame();

        Assert.Equal(3, term.WriteCount); // exactly one write per frame
    }

    [Fact]
    public void DeclarativeAnimation_WritesExactFinalValue()
    {
        var signal = new Signal<float>(0f);
        var widget = new SignalWidget(signal);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(widget.Node);
        widget.Node.Height = 1;

        var (app, _, clock) = MakeApp(root, [widget], w: 40, h: 10);
        app.Scheduler.Animate(signal, 1f,
            new Transition(TimeSpan.FromSeconds(1), Easing.OutCubic));

        app.RunFrame();
        clock.Advance(TimeSpan.FromSeconds(1.5));
        app.RunFrame();

        Assert.Equal(1f, signal.Value); // exact — no drift (§2)
        Assert.Equal(0, app.Scheduler.ActiveCount);
    }
}
