using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Widgets;

namespace QxTui.Tests;

file sealed class PageWidget(string tag) : Widget
{
    public string Tag { get; } = tag;
    public int Renders;

    public void EveryFrame() => TickPerFrame();

    public override void Render(CellBuffer buffer)
    {
        Renders++;
        buffer.FillRect(Rect.X, Rect.Y, Rect.W, Rect.H, new Cell { Char = Tag[0], Fg = 7, Bg = -1 });
    }
}

public class RouterTests
{
    private static (App app, FakeTerminal term, Router router) MakeApp(int w = 20, int h = 8)
    {
        var router = new Router();
        var term = new FakeTerminal(w, h);
        var app = new App(term, UI.Column(0, router.Grow(1)), targetFps: 60, asyncPresenting: false);
        return (app, term, router);
    }

    [Fact]
    public void Switch_MountsNewPage_UnmountsOld_WithoutDisposing()
    {
        var a = new PageWidget("aaaa");
        var b = new PageWidget("bbbb");
        var (app, _, router) = MakeApp();
        router.Register("a", () => a).Register("b", () => b);
        router.Switch("a");
        app.RunFrame();

        Assert.Equal('a', app.Back[5, 4].Char);
        int aRenders = a.Renders;

        router.Switch("b");
        app.RunFrame();
        Assert.Equal('b', app.Back[5, 4].Char);
        Assert.Equal("b", router.Current);

        app.RunFrame();
        Assert.Equal(aRenders, a.Renders); // unmounted: no more renders, not disposed
        Assert.Null(a.Parent);
    }

    [Fact]
    public void CachedPage_KeepsState_AcrossRoundTrip()
    {
        var a = new PageWidget("aaaa");
        var b = new PageWidget("bbbb");
        var (app, _, router) = MakeApp();
        router.Register("a", () => a).Register("b", () => b);
        router.Switch("a");
        app.RunFrame();

        router.Switch("b");
        app.RunFrame();
        router.Switch("a");
        app.RunFrame();

        Assert.Same(a, router.Children[0]); // same instance: state preserved
    }

    [Fact]
    public void InactivePage_CostsNothing()
    {
        var a = new PageWidget("aaaa");
        var b = new PageWidget("bbbb");
        var (app, _, router) = MakeApp();
        router.Register("a", () => a).Register("b", () => b);
        router.Switch("a");
        app.RunFrame();
        router.Switch("b");
        app.RunFrame();

        int before = a.Renders;
        for (int i = 0; i < 5; i++) app.RunFrame();
        Assert.Equal(before, a.Renders); // inactive pages never tick/render
    }

    [Fact]
    public void TabBar_ClickSwitchesPage()
    {
        var a = new PageWidget("aaaa");
        var b = new PageWidget("bbbb");
        var router = new Router();
        router.Register("one", () => a).Register("two", () => b);
        router.Switch("one");

        var tabs = new TabBarWidget(router, ["one", "two"]);
        var term = new FakeTerminal(30, 8);
        using var app = new App(term, UI.Column(0, tabs.Height(1), router.Grow(1)),
            targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        // " two " chip starts at x = 0 + (3+4+1) = 8..12; click x=9,y=0
        foreach (byte ch in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;10;1M\x1b[<0;10;1m"))
            term.QueueInput(ch);
        app.RunFrame();

        Assert.Equal("two", router.Current);
        Assert.Equal('b', app.Back[5, 4].Char);
    }
}

public class ChildRepaintTests
{
    [Fact]
    public void PerFrameParent_DoesNotEraseChild()
    {
        // regression: a parent repainting every frame wiped undirty children
        // (the ball panel erased its slider row; it blinked only on
        // full-invalidation events like button presses)
        var parent = new PageWidget("pppp"); // renders full rect every call
        var child = new LabelWidget(() => "CHILDROW");
        parent.AddChild(child);
        child.Node.Height = 1;
        parent.EveryFrame();

        var term = new FakeTerminal(20, 6);
        using var app = new App(term, UI.Column(0, parent.Grow(1)),
            targetFps: 60, asyncPresenting: false);

        for (int i = 0; i < 5; i++)
            app.RunFrame();

        // child row (y=0) must still show its text, not the parent's fill
        Assert.Equal('C', app.Back[1, 0].Char);
        Assert.True(child.RenderCount >= 5, "child must repaint with its parent");
    }
}

public class PageSwitchCleanupTests
{
    [Fact]
    public void SwitchingPages_ErasesOldPagePixels()
    {
        var a = new PageWidget("aaaa"); // fills its whole rect with 'a'
        var b = new ContainerWidget();  // paints nothing
        var bChild = new LabelWidget(() => "top");
        b.AddChild(bChild);
        bChild.Node.Height = 1;

        var router = new Router();
        router.Register("a", () => a).Register("b", () => b);
        router.Switch("a");

        var term = new FakeTerminal(20, 10);
        using var app = new App(term, UI.Column(0, router.Grow(1)),
            targetFps: 60, asyncPresenting: false);
        app.RunFrame();
        Assert.Equal('a', app.Back[10, 8].Char);

        router.Switch("b");
        app.RunFrame();

        // the region page A owned must be cleared, not ghosted
        Assert.Equal(' ', app.Back[10, 8].Char);
        Assert.Equal(-1, app.Back[10, 8].Bg);
        Assert.Equal('t', app.Back[1, 0].Char); // new page's child painted
    }
}

public class TabBarFollowsRouterTests
{
    [Fact]
    public void KeyboardSwitch_MovesActiveMarker()
    {
        var a = new PageWidget("aaaa");
        var b = new PageWidget("bbbb");
        var router = new Router();
        router.Register("one", () => a).Register("two", () => b);
        router.Switch("one");

        var tabs = new TabBarWidget(router, ["one", "two"]);
        var term = new FakeTerminal(30, 8);
        using var app = new App(term, UI.Column(0, tabs.Height(1), router.Grow(1)),
            targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        int accent = Theme.Night.Accent;
        Assert.Equal(accent, app.Back[1, 0].Bg);   // "one" chip active
        Assert.NotEqual(accent, app.Back[8, 0].Bg);

        // switch via the router directly (what a key handler does)
        router.Switch("two");
        app.RunFrame();

        Assert.NotEqual(accent, app.Back[1, 0].Bg); // old marker gone
        Assert.Equal(accent, app.Back[8, 0].Bg);    // new marker follows
    }
}
