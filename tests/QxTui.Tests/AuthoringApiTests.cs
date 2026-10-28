using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Widgets;

namespace QxTui.Tests;

// intents used by these tests: plain records, no app reference anywhere
file record Bump(int By);
file record Ping;

file sealed class LifecycleProbe : Widget
{
    public int Mounts, Detaches, Disposes;
    public App? MountedApp;
    public readonly List<(Rect Old, Rect New)> RectChanges = [];
    public override void OnMount(App app) { Mounts++; MountedApp = app; }
    public override void OnDetach() => Detaches++;
    public override void OnRectChanged(Rect oldRect, Rect newRect) =>
        RectChanges.Add((oldRect, newRect));
    public override void Dispose() { Disposes++; base.Dispose(); }
    public override void Render(CellBuffer buffer) { }
}

public class MessageChannelTests
{
    private static (App app, FakeTerminal term) MakeApp(LayoutNode root,
        int w = 20, int h = 10)
    {
        var term = new FakeTerminal(w, h);
        var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        return (app, term);
    }

    private static void Mouse(FakeTerminal term, string seq)
    {
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes(seq))
            term.QueueInput(b);
    }

    [Fact]
    public void Update_Runs_OnSend_WithoutAnyInputOrFrame()
    {
        // the doctrine payoff: app logic is testable headless — feed an
        // intent, assert the state, no terminal bytes and no frames needed
        var level = new Signal<int>(0);
        var (app, _) = MakeApp(new LayoutNode());
        app.OnMessage<Bump>(m => level.Set(level.Value + m.By));

        app.Send(new Bump(5));
        app.Send(new Bump(2));

        Assert.Equal(7, level.Value);
    }

    [Fact]
    public void UnhandledMessage_IsInert()
    {
        var (app, _) = MakeApp(new LayoutNode());
        app.Send(new Ping()); // no handler registered: nothing happens
        app.OnMessage<Bump>(_ => throw new InvalidOperationException("wrong type"));
        app.Send(new Ping()); // still no handler for Ping
    }

    [Fact]
    public void Button_EmitsMessage_OnClick_ComponentHasNoAppReference()
    {
        var level = new Signal<int>(0);
        var btn = new ButtonWidget(() => " bump ", style: ButtonStyle.Rounded)
            { EmitOnClick = new Bump(3) };
        var root = UI.Column(0, btn.Height(1));
        var (app, term) = MakeApp(root, 20, 4);
        app.OnMessage<Bump>(m => level.Set(level.Value + m.By));
        app.RunFrame();

        Mouse(term, "\x1b[<0;11;1M\x1b[<0;11;1m"); // press+release on the button
        app.RunFrame();

        Assert.Equal(3, level.Value);
    }

    [Fact]
    public void ModalChip_EmitsMessage_OnClick_AndOnKey()
    {
        var hits = 0;
        var modal = new ModalWidget("t", () => ["x"], dim: false,
            chips: [new ChipAction("go", Msg: new Bump(1))]);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        var (app, term) = MakeApp(root, 40, 12);
        app.OnMessage<Bump>(_ => hits++);
        app.RunFrame();
        app.PushOverlay(modal);
        app.RunFrame();

        Mouse(term, "\x1b[<0;14;8M\x1b[<0;14;8m"); // the chip at (12,7)
        app.RunFrame();
        Assert.Equal(1, hits);

        term.QueueInput((byte)'1'); // same chip, bound to key 1
        app.RunFrame();
        Assert.Equal(2, hits);
        Assert.Same(modal, app.Overlay); // emitting does not close the modal
    }

    [Fact]
    public void ClosureChips_StillWork_BesideMessageChips()
    {
        // migration is additive: app-specific policy chips coexist with
        // pure-intent chips in the same modal
        int closed = 0;
        var modal = new ModalWidget("t", () => ["x"], dim: false,
            chips:
            [
                new ChipAction("intent", Msg: new Ping()),
                new ChipAction("policy", Act: () => closed++),
            ]);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        var (app, term) = MakeApp(root, 40, 12);
        int pings = 0;
        app.OnMessage<Ping>(_ => pings++);
        app.RunFrame();
        app.PushOverlay(modal);
        app.RunFrame();

        term.QueueInput((byte)'1');
        app.RunFrame();
        term.QueueInput((byte)'2');
        app.RunFrame();

        Assert.Equal(1, pings);
        Assert.Equal(1, closed);
    }
}

public class LifecycleHookTests
{
    [Fact]
    public void OnMount_FiresAtAttach_NotAtConstruction()
    {
        var probe = new LifecycleProbe();
        Assert.Equal(0, probe.Mounts);

        var parent = new ContainerWidget();
        parent.Node.Grow = 1;
        var root = UI.Column(0, parent);
        var term = new FakeTerminal(20, 10);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        parent.AddChild(probe); // attaches into a mounted parent
        Assert.Equal(1, probe.Mounts);
        Assert.Same(app, probe.MountedApp);
    }

    [Fact]
    public void OnDetach_FiresOnRemove_DisposeOnlyWhenAsked()
    {
        var probe = new LifecycleProbe();
        var parent = new ContainerWidget();
        parent.Node.Grow = 1;
        var root = UI.Column(0, parent);
        var term = new FakeTerminal(20, 10);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        parent.AddChild(probe);

        parent.RemoveChild(probe, dispose: false); // cached-page style detach
        Assert.Equal(1, probe.Detaches);
        Assert.Equal(0, probe.Disposes);

        parent.AddChild(probe); // re-attach: mounted again
        Assert.Equal(2, probe.Mounts);

        parent.RemoveChild(probe, dispose: true);
        Assert.Equal(2, probe.Detaches);
        Assert.Equal(1, probe.Disposes);
    }

    [Fact]
    public void OnRectChanged_FiresOnResize_WithOldAndNew_OncePerMove()
    {
        var probe = new LifecycleProbe();
        probe.Node.Grow = 1;
        var root = UI.Column(0, probe);
        var term = new FakeTerminal(20, 10);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame(); // first rect is recorded, not reported
        Assert.Empty(probe.RectChanges);

        app.RunFrame(); // idle: no spurious fires
        Assert.Empty(probe.RectChanges);

        term.SimulateResize(30, 10);
        app.RunFrame();

        Assert.Single(probe.RectChanges);
        Assert.Equal(new Rect(0, 0, 20, 10), probe.RectChanges[0].Old);
        Assert.Equal(new Rect(0, 0, 30, 10), probe.RectChanges[0].New);
    }
}
