using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;

namespace QxTui.Tests;

file sealed class TickWidget : Widget
{
    public TickWidget(TimeSpan interval) => Tick(interval);
    public override void Render(CellBuffer buffer) { }
}

file sealed class SignalWidget2 : Widget
{
    public SignalWidget2(Signal<int> source) => OnSignal(source);
    public override void Render(CellBuffer buffer) { }
}

file sealed class CallbackWidget : Widget
{
    public int Callbacks;
    public CallbackWidget(Signal<int> source) =>
        OnSignal(source, v => Callbacks += v);
    public override void Render(CellBuffer buffer) { }
}

public class WidgetLifecycleTests
{
    private static (App app, FakeTerminal term, VirtualTimeSource clock) MakeApp(
        LayoutNode root, int w = 20, int h = 10)
    {
        var term = new FakeTerminal(w, h);
        var clock = new VirtualTimeSource();
        var app = new App(term, root, targetFps: 240, time: clock, asyncPresenting: false);
        return (app, term, clock);
    }

    [Fact]
    public void Tick_MaterializesAtMount_NotBefore()
    {
        var widget = new TickWidget(TimeSpan.FromMilliseconds(100));
        Assert.False(widget.Mounted);

        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(widget.Node);
        widget.Node.Grow = 1f;

        var (app, _, clock) = MakeApp(root);
        app.AddWidget(widget);
        Assert.True(widget.Mounted);
        Assert.Same(app, widget.App);

        app.RunFrame(); // first paint
        Assert.Equal(1, widget.RenderCount);

        for (int i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            app.RunFrame();
        }

        Assert.True(widget.RenderCount >= 5,
            $"declared tick must invalidate per interval, got {widget.RenderCount} renders");
    }

    [Fact]
    public void OnSignal_InvalidatesOnChange()
    {
        var signal = new Signal<int>(0);
        var widget = new SignalWidget2(signal);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(widget.Node);
        widget.Node.Grow = 1f;

        var (app, _, _) = MakeApp(root);
        app.AddWidget(widget);
        app.RunFrame();
        Assert.Equal(1, widget.RenderCount);

        signal.Set(1);
        app.RunFrame();
        Assert.Equal(2, widget.RenderCount);

        signal.Set(1); // equal write: no invalidation
        app.RunFrame();
        Assert.Equal(2, widget.RenderCount);
    }

    [Fact]
    public void OnSignal_CustomCallback_ReceivesValues()
    {
        var signal = new Signal<int>(0);
        var widget = new CallbackWidget(signal);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(widget.Node);

        var (app, _, _) = MakeApp(root);
        app.AddWidget(widget);

        signal.Set(3);
        signal.Set(4);
        Assert.Equal(7, widget.Callbacks);
    }

    [Fact]
    public void Dispose_UnsubscribesEverything()
    {
        var signal = new Signal<int>(0);
        var widget = new SignalWidget2(signal);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(widget.Node);
        widget.Node.Grow = 1f;

        var (app, _, _) = MakeApp(root);
        app.AddWidget(widget);
        app.RunFrame();

        app.Dispose();

        signal.Set(99); // must not touch the disposed widget
        Assert.False(widget.Dirty);
    }

    [Fact]
    public void Theme_ComesFromApp_WithDefaultBeforeMount()
    {
        var widget = new TickWidget(TimeSpan.FromSeconds(1));
        Assert.Same(Theme.Default, widget.Theme);

        var root = new LayoutNode();
        root.Add(widget.Node);
        var (app, _, _) = MakeApp(root);
        app.Theme = Theme.Mono;
        app.AddWidget(widget);

        Assert.Same(Theme.Mono, widget.Theme);
    }
}
