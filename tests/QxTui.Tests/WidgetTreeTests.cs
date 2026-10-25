using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Widgets;

namespace QxTui.Tests;

file sealed class OrderProbe(string name, List<string> log) : Widget
{
    public OrderProbe(string name, List<string> log, Signal<int>? sig = null) : this(name, log)
    {
        if (sig is not null) OnSignal(sig);
    }

    private readonly string _name = name;
    private readonly List<string> _log = log;

    public override void Render(CellBuffer buffer)
    {
        _log.Add(_name);
        buffer.FillRect(Rect.X, Rect.Y, Rect.W, Rect.H, Cell.Empty);
    }
}

file sealed class ClickProbe : Widget
{
    public int Clicks;
    public ClickProbe() => Clickable = true;
    public override void OnClick(Point local) => Clicks++;
    public override void Render(CellBuffer buffer) =>
        buffer.FillRect(Rect.X, Rect.Y, Rect.W, Rect.H, Cell.Empty);
}

public class WidgetTreeTests
{
    private static (App app, FakeTerminal term) MakeApp(Widget root, int w = 20, int h = 10)
    {
        var term = new FakeTerminal(w, h);
        var app = new App(term, root.Node, targetFps: 60, asyncPresenting: false);
        return (app, term);
    }

    [Fact]
    public void AddWidget_MountsWholeSubtree()
    {
        var log = new List<string>();
        var grand = new OrderProbe("grand", log);
        var child = new OrderProbe("child", log);
        var root = new OrderProbe("root", log);

        root.AddChild(child);
        child.AddChild(grand);

        var (app, _) = MakeApp(root);
        app.AddWidget(root);

        Assert.True(root.Mounted);
        Assert.True(child.Mounted);
        Assert.True(grand.Mounted);
        Assert.Same(root, child.Parent);
        Assert.Same(child, grand.Parent);
    }

    [Fact]
    public void AddChild_AfterMount_MountsImmediately()
    {
        var log = new List<string>();
        var root = new OrderProbe("root", log);
        var (app, _) = MakeApp(root);
        app.AddWidget(root);

        var late = new OrderProbe("late", log);
        root.AddChild(late);

        Assert.True(late.Mounted);
        Assert.Same(app, late.App);
    }

    [Fact]
    public void RenderOrder_IsPreOrder_ParentsBeforeChildren()
    {
        var log = new List<string>();
        var child = new OrderProbe("child", log);
        var root = new OrderProbe("root", log);
        root.AddChild(child);

        var (app, _) = MakeApp(root);
        app.AddWidget(root);
        app.RunFrame();

        Assert.Equal(["root", "child"], log);
    }

    [Fact]
    public void ChildRect_NestsInsideParent()
    {
        var log = new List<string>();
        var child = new OrderProbe("child", log);
        var root = new OrderProbe("root", log);
        root.AddChild(child);
        child.Node.Height = 3;

        var (app, _) = MakeApp(root, 20, 10);
        app.AddWidget(root);
        app.RunFrame();

        Assert.Equal(new Rect(0, 0, 20, 10), root.Rect);
        Assert.Equal(new Rect(0, 0, 20, 3), child.Rect); // nested at parent origin
    }

    [Fact]
    public void RemoveChild_UnmountsAndDisposesSubtree()
    {
        var sig = new Signal<int>(0);
        var log = new List<string>();
        var child = new OrderProbe("child", log, sig);
        var root = new OrderProbe("root", log);
        root.AddChild(child);

        var (app, _) = MakeApp(root);
        app.AddWidget(root);
        app.RunFrame();

        sig.Set(1);
        app.RunFrame();
        Assert.Equal(2, child.RenderCount); // initial + signal

        Assert.True(root.RemoveChild(child));
        Assert.Null(child.Parent);

        long before = child.RenderCount;
        sig.Set(2); // subscription must be dead
        app.RunFrame();
        Assert.Equal(before, child.RenderCount);
    }

    [Fact]
    public void AppDispose_CascadesToChildren()
    {
        var sig = new Signal<int>(0);
        var log = new List<string>();
        var child = new OrderProbe("child", log, sig);
        var root = new OrderProbe("root", log);
        root.AddChild(child);

        var (app, _) = MakeApp(root);
        app.AddWidget(root);
        app.RunFrame();
        app.Dispose();

        long versionBefore = sig.Version;
        sig.Set(42); // must not throw or touch disposed child
        Assert.True(sig.Version > versionBefore); // signal itself fine
        Assert.False(child.Dirty);
    }

    [Fact]
    public void Click_HitsChildBeforeParent()
    {
        var parent = new ClickProbe();
        var child = new ClickProbe();
        parent.AddChild(child);
        child.Node.Height = 4; // top 4 rows belong to the child

        var (app, term) = MakeApp(parent, 20, 10);
        app.AddWidget(parent);
        app.RunFrame(); // layout

        // click inside the child's area
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;5;2M\x1b[<0;5;2m"))
            term.QueueInput(b);
        app.RunFrame();

        Assert.Equal(1, child.Clicks);
        Assert.Equal(0, parent.Clicks); // child consumed it (topmost)

        // click below the child: parent's area
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;5;8M\x1b[<0;5;8m"))
            term.QueueInput(b);
        app.RunFrame();

        Assert.Equal(1, child.Clicks);
        Assert.Equal(1, parent.Clicks);
    }

    [Fact]
    public void ContainerWidget_HoldsChildren_AndPaintsNothing()
    {
        var label = new LabelWidget(() => "hi");
        var container = new ContainerWidget();
        container.AddChild(label);
        label.Node.Height = 1;

        var term = new FakeTerminal(20, 6);
        using var app = new App(term, container.Node, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        Assert.True(label.Mounted);
        Assert.Equal('h', app.Back[1, 0].Char); // child painted; container painted nothing
    }
}

file sealed class TreeFill(char c) : Widget
{
    public override void Render(CellBuffer buffer) =>
        buffer.FillRect(Rect.X, Rect.Y, Rect.W, Rect.H, new Cell { Char = c, Fg = 7, Bg = -1 });
}

file sealed class FeederProbe : Widget
{
    private readonly RingHistory _hist;
    private float _v;

    public FeederProbe(RingHistory hist)
    {
        _hist = hist;
        Tick(TimeSpan.FromMilliseconds(100), _ =>
        {
            _v += 1;
            _hist.Push(_v);
        });
    }

    public override void Render(CellBuffer buffer) { }
}

public class DataOnlyWidgetTests
{
    [Fact]
    public void ZeroHeightTreeSlot_StillMountsAndTicks()
    {
        // regression: a data-only widget left OUT of the layout tree never
        // mounted once the manual AddWidget chains were removed — its Tick
        // never ran and the sparklines it fed went empty
        var hist = new RingHistory(60);
        var feeder = new FeederProbe(hist);
        var visible = new TreeFill('T');

        var root = UI.Column(0, visible.Grow(1), feeder.Height(0));
        var term = new FakeTerminal(20, 8);
        var clock = new VirtualTimeSource();
        using var app = new App(term, root, targetFps: 60, time: clock, asyncPresenting: false);

        app.RunFrame();
        Assert.True(feeder.Mounted);
        Assert.Equal(0, hist.Count);

        for (int i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            app.RunFrame();
        }

        Assert.Equal(5, hist.Count); // tick ran via the zero-height slot
    }
}
