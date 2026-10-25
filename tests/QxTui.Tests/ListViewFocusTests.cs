using QxTui.Reactive;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;
using QxTui.Widgets;

namespace QxTui.Tests;

internal static class Settle
{
    public static void Run(App app, int ms = 150)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(10);
            app.RunFrame();
        }
    }
}

public class ListViewTests
{
    private const int Total = 10_000;

    private static (App app, FakeTerminal term, ListView list, List<int> painted) Make()
    {
        var painted = new List<int>();
        var list = new ListView(() => Total)
        {
            RenderRow = (buffer, clip, rect, index, selected) =>
            {
                painted.Add(index);
                buffer.WriteTextClipped(clip, rect.X, rect.Y, $"row {index}", fg: 7);
            },
        };
        var root = UI.Column(0, list.Grow(1));
        var term = new FakeTerminal(30, 12);
        var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        return (app, term, list, painted);
    }

    [Fact]
    public void OnlyVisibleRows_ArePainted()
    {
        var (app, _, list, painted) = Make();
        app.RunFrame();

        Assert.Equal(12, painted.Count);           // viewport height = 12 rows
        Assert.Equal(Enumerable.Range(0, 12), painted);
        Assert.True(painted.Count < Total / 100);  // virtualized, not 10k
    }

    [Fact]
    public void Wheel_ScrollsWindow()
    {
        var (app, term, list, painted) = Make();
        app.RunFrame();

        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<65;5;5M")) // wheel down
            term.QueueInput(b);
        app.RunFrame();

        Assert.Equal(3, list.Offset); // wheel steps are immediate
        // the wheel frame painted exactly the new visible window
        Assert.Equal(Enumerable.Range(3, 12), painted.TakeLast(12));
    }

    [Fact]
    public void ScrollClamps_AtEnds()
    {
        var (app, term, list, _) = Make();
        app.RunFrame();

        list.ScrollBy(-50);
        Assert.Equal(0, list.Offset);

        list.ScrollTo(int.MaxValue);
        Assert.Equal(Total - 12, list.Offset);
    }

    [Fact]
    public void FocusedKeys_PageAndSelect()
    {
        var (app, term, list, painted) = Make();
        app.Focus(list);
        app.RunFrame();

        void Key(string seq)
        {
            foreach (byte b in System.Text.Encoding.ASCII.GetBytes(seq))
                term.QueueInput(b);
            app.RunFrame();
        }

        Key("\x1b[6~"); // PageDown
        Assert.Equal(12, list.Offset);

        Key("\x1b[B");  // Down: selects within view
        Assert.True(list.Selected >= 12);

        Key("\x1b[1~"); // Home
        Assert.Equal(0, list.Offset);
        Assert.Equal(0, list.Selected);
    }
}

public class FocusTests
{
    private sealed class FocusProbe : Widget
    {
        public readonly List<Key> Keys = [];
        public FocusProbe() => Focusable = true;
        public override bool HandleKey(KeyEvent ev) { Keys.Add(ev.Key); return true; }
        public override void Render(CellBuffer buffer) { }
    }

    [Fact]
    public void Tab_CyclesFocus_InTreeOrder()
    {
        var a = new FocusProbe();
        var b = new FocusProbe();
        var root = UI.Column(0, a.Height(1), b.Height(1));
        var term = new FakeTerminal(20, 6);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        Assert.Null(app.Focused);
        term.QueueInput((byte)'\t');
        app.RunFrame();
        Assert.Same(a, app.Focused);

        term.QueueInput((byte)'\t');
        app.RunFrame();
        Assert.Same(b, app.Focused);

        term.QueueInput((byte)'\t');
        app.RunFrame();
        Assert.Same(a, app.Focused); // wraps
    }

    [Fact]
    public void FocusedWidget_ReceivesKeys_BeforeAppHandler()
    {
        var probe = new FocusProbe();
        var root = UI.Column(0, probe.Height(1));
        var term = new FakeTerminal(20, 6);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        bool appSaw = false;
        app.OnKey = _ => { appSaw = true; return true; };

        app.Focus(probe);
        term.QueueInput((byte)'x');
        app.RunFrame();

        Assert.Equal([Key.Char], probe.Keys);
        Assert.False(appSaw); // consumed by the focused widget
    }

    [Fact]
    public void Click_FocusesFocusableWidget()
    {
        var probe = new FocusProbe();
        var root = UI.Column(0, probe.Height(2));
        var term = new FakeTerminal(20, 6);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;5;1M"))
            term.QueueInput(b);
        app.RunFrame();

        Assert.Same(probe, app.Focused);
    }
}

public class ListViewMouseTests
{
    private static (App app, FakeTerminal term, ListView list) Make()
    {
        var list = new ListView(() => 1000)
        {
            RenderRow = (b, c, r, i, sel) =>
                b.WriteTextClipped(c, r.X, r.Y, $"row {i}", fg: 7),
        };
        var root = UI.Column(0, list.Grow(1));
        var term = new FakeTerminal(30, 10);
        var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();
        return (app, term, list);
    }

    private static void Mouse(FakeTerminal term, string seq)
    {
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes(seq))
            term.QueueInput(b);
    }

    [Fact]
    public void ThumbDrag_Scrolls_AndCapturesOutside()
    {
        var (app, term, list) = Make();

        // press on scrollbar column (x=29), then drag upward outside widget
        Mouse(term, "\x1b[<0;30;8M");   // press near bottom of track
        app.RunFrame();
        int afterPress = list.Offset;
        Assert.True(afterPress > 0, $"press on track should jump, got {afterPress}");

        Mouse(term, "\x1b[<35;30;2M");   // drag up (still on track col)
        app.RunFrame();
        Assert.True(list.Offset < afterPress); // thumb follows cursor 1:1

        Mouse(term, "\x1b[<35;5;1M");    // drag OUTSIDE horizontally: capture holds
        app.RunFrame();

        Mouse(term, "\x1b[<0;30;2m");    // release
        app.RunFrame();
    }

    [Fact]
    public void ContentPress_SelectsRow()
    {
        var (app, term, list) = Make();
        Mouse(term, "\x1b[<0;5;6M\x1b[<0;5;6m"); // press row y=5
        app.RunFrame();
        Assert.Equal(5, list.Selected);
    }
}

public class OpaqueStripTests
{
    [Fact]
    public void Slider_OwnsItsBackground_NoTransparentHoles()
    {
        var sig = new Signal<float>(0.5f);
        var slider = new SliderWidget(sig);
        var root = UI.Column(0, slider.Height(1));
        var term = new FakeTerminal(20, 3);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        for (int x = 0; x < 20; x++)
            Assert.Equal(Theme.Night.PanelBg, app.Back[x, 0].Bg); // fully opaque
    }
}

public class BallStripCollisionTests
{
    [Fact]
    public void Surface_StopsAboveChildStrip()
    {
        // the ball's pixel canvas must exclude the bottom strip row so the
        // ball reads as colliding with the UI instead of passing under it
        var speed = new Signal<float>(0.5f);
        var ball = new QxTui.Playground.BallWidget(speed);
        var row = new ContainerWidget();
        row.Node.Height = 1;
        ball.AddChild(row);

        var root = UI.Column(0, ball.Grow(1));
        var term = new FakeTerminal(30, 12);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        // rect h=12 → inner rows 10 → surface rows must be 9 (strip excluded)
        Assert.Equal(9 * 2, ball.SurfacePixelHeight);
    }
}
