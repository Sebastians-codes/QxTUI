using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;
using QxTui.Widgets;

namespace QxTui.Tests;

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
        painted.Clear();
        app.RunFrame();

        Assert.Equal(3, list.Offset);
        Assert.Equal(Enumerable.Range(3, 12), painted);
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
