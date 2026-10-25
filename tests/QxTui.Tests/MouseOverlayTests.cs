using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;
using QxTui.Widgets;

namespace QxTui.Tests;

public class MouseParserTests
{
    private static List<MouseEvent> Feed(string s)
    {
        var parser = new KeyParser();
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes(s))
            parser.Feed(b);
        parser.Flush();

        var events = new List<MouseEvent>();
        while (parser.TryReadMouse(out var ev))
            events.Add(ev);
        return events;
    }

    [Fact]
    public void SgrPress_Release_ZeroBasedCoords()
    {
        var events = Feed("\x1b[<0;15;7M\x1b[<0;15;7m");

        Assert.Equal(2, events.Count);
        Assert.Equal(new MouseEvent(14, 6, 0, MouseKind.Press), events[0]);
        Assert.Equal(new MouseEvent(14, 6, 0, MouseKind.Release), events[1]);
    }

    [Fact]
    public void MotionBit_Detected()
    {
        var events = Feed("\x1b[<35;3;4M"); // 32 = motion + 3 = release-ish btn bits
        Assert.Equal(MouseKind.Motion, events[0].Kind);
        Assert.Equal(2, events[0].X);
        Assert.Equal(3, events[0].Y);
    }

    [Fact]
    public void MouseAndKeys_Interleave()
    {
        var parser = new KeyParser();
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("a\x1b[<0;2;2Mb\x1b[C"))
            parser.Feed(b);

        Assert.True(parser.TryReadKey(out var k1));
        Assert.Equal('a', k1.Char);
        Assert.True(parser.TryReadMouse(out var m));
        Assert.Equal(new MouseEvent(1, 1, 0, MouseKind.Press), m);
        Assert.True(parser.TryReadKey(out var k2));
        Assert.Equal('b', k2.Char);
        Assert.True(parser.TryReadKey(out var k3));
        Assert.Equal(Key.Right, k3.Key);
    }
}

file sealed class ClickTarget : Widget
{
    public Point? Clicked;
    public ClickTarget() => Clickable = true;
    public override void OnClick(Point local) => Clicked = local;
    public override void Render(CellBuffer buffer) =>
        buffer.FillRect(Rect.X, Rect.Y, Rect.W, Rect.H, Cell.Empty);
}

file sealed class SilentWidget : Widget
{
    public int Clicks;
    public override void OnClick(Point local) => Clicks++;
    public override void Render(CellBuffer buffer) =>
        buffer.FillRect(Rect.X, Rect.Y, Rect.W, Rect.H, Cell.Empty);
}

public class ClickRoutingTests
{
    [Fact]
    public void Release_InsideClickable_WidgetGetsLocalCoords()
    {
        var target = new ClickTarget();
        var root = UI.Column(0, target.Grow(1));

        var term = new FakeTerminal(20, 10);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        app.RunFrame(); // layout pass first: rects exist for hit-testing

        // click at (5,3)
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;6;4M\x1b[<0;6;4m"))
            term.QueueInput(b);

        app.RunFrame();

        Assert.NotNull(target.Clicked);
        Assert.Equal(new Point(5, 3), target.Clicked!.Value);
    }

    [Fact]
    public void NonClickable_IgnoresRelease()
    {
        var silent = new SilentWidget(); // Clickable=false
        var root = UI.Column(0, silent.Grow(1));

        var term = new FakeTerminal(20, 10);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;6;4M\x1b[<0;6;4m"))
            term.QueueInput(b);
        app.RunFrame();

        Assert.Equal(0, silent.Clicks);
    }
}

public class OverlayTests
{
    [Fact]
    public void Modal_SwallowsKeys_UntilPopped()
    {
        var seen = new List<Key>();
        var root = UI.Column(0, new ClickTarget().Grow(1));
        var term = new FakeTerminal(30, 12);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.OnKey = ev => { seen.Add(ev.Key); return true; };

        var modal = new ModalWidget("t", () => ["line"]);
        app.PushOverlay(modal);
        Assert.Same(modal, app.Overlay);

        term.QueueInput((byte)'x');
        app.RunFrame();
        Assert.Empty(seen); // swallowed by modal

        term.QueueInput((byte)0x1b); // escape closes
        app.RunFrame();
        Assert.Null(app.Overlay);

        term.QueueInput((byte)'x');
        app.RunFrame();
        Assert.Equal([Key.Char], seen); // app handler works again
    }

    [Fact]
    public void Modal_PaintsOnTop_AndPopRestores()
    {
        var target = new ClickTarget();
        var root = UI.Column(0, target.Grow(1));
        var term = new FakeTerminal(30, 12);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        app.RunFrame(); // base paint

        var modal = new ModalWidget("title", () => ["hello modal"]);
        app.PushOverlay(modal);
        app.RunFrame();

        // modal dim + box own the screen center
        Assert.Equal(236, app.Back[15, 0].Bg);   // dim row above the box
        app.PopOverlay();
        app.RunFrame();
        Assert.Null(app.Overlay);
        Assert.Equal(-1, app.Back[15, 0].Bg);    // base content restored
    }
}
