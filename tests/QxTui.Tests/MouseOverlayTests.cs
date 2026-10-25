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

file sealed class FillWidget(char c) : Widget
{
    public override void Render(CellBuffer buffer) =>
        buffer.FillRect(Rect.X, Rect.Y, Rect.W, Rect.H, new Cell { Char = c, Fg = 7, Bg = -1 });
}

public class ModalSemanticsTests
{
    private static (App app, FakeTerminal term) MakeApp(LayoutNode root, int w = 30, int h = 12)
    {
        var term = new FakeTerminal(w, h);
        var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        return (app, term);
    }

    [Fact]
    public void Modal_FreezesBasePaints_WhileOpen()
    {
        var baseWidget = new FillWidget('T');
        var root = UI.Column(0, baseWidget.Grow(1));
        var (app, _) = MakeApp(root);

        app.RunFrame();
        Assert.Equal(1, baseWidget.RenderCount);

        app.PushOverlay(new ModalWidget("t", () => ["x"]));

        baseWidget.Invalidate(); // base wants to repaint (a tick would do this)
        app.RunFrame();
        baseWidget.Invalidate();
        app.RunFrame();

        // modal blocks it: no patchwork over the dim layer
        Assert.Equal(1, baseWidget.RenderCount);

        app.PopOverlay();
        app.RunFrame();
        Assert.Equal(2, baseWidget.RenderCount); // restored after pop
    }

    [Fact]
    public void Pop_RestoresEvenUncoveredRegions()
    {
        var baseWidget = new FillWidget('T');
        // trailing empty row = region no widget ever paints (uncovered)
        var root = UI.Column(1, baseWidget.Grow(1), new LayoutNode { Height = 1 });
        var (app, term) = MakeApp(root);

        app.RunFrame();
        Assert.Equal('T', app.Back[15, 0].Char);
        Assert.Equal(' ', app.Back[0, 11].Char); // bottom gap row: uncovered by any widget

        app.PushOverlay(new ModalWidget("t", () => ["x"]));
        app.RunFrame();
        Assert.Equal(236, app.Back[15, 0].Bg); // dimmed

        app.PopOverlay();
        app.RunFrame();
        Assert.Equal(-1, app.Back[15, 0].Bg);   // dim gone everywhere
        Assert.Equal(' ', app.Back[0, 11].Char); // uncovered gap restored too
        Assert.Equal('T', app.Back[15, 5].Char);

        // the TERMINAL must have been told to erase the uncovered gap row:
        // the pop frame's byte stream contains a cursor move to row 12
        var wire = System.Text.Encoding.Latin1.GetString(term.LastFrame!);
        Assert.Contains("\x1b[12;1H", wire);
    }

    [Fact]
    public void ModalWithoutDim_LeavesBackgroundLive()
    {
        var baseWidget = new FillWidget('T');
        var root = UI.Column(0, baseWidget.Grow(1));
        var (app, _) = MakeApp(root);

        app.RunFrame();
        app.PushOverlay(new ModalWidget("t", () => ["x"], dim: false));
        app.RunFrame();

        // outside the box the base content is untouched (floating popup)
        Assert.Equal('T', app.Back[0, 0].Char);
        Assert.Equal(-1, app.Back[0, 0].Bg);
    }

    [Fact]
    public void Motion_SetsHover_OnTopmostWidgetOnly()
    {
        var a = new ClickTarget();
        var b = new ClickTarget();
        var root = UI.Row(0, a.Grow(1), b.Grow(1));
        var (app, term) = MakeApp(root, 20, 10);

        app.RunFrame(); // layout

        void Motion(int x, int y)
        {
            foreach (byte ch in System.Text.Encoding.ASCII.GetBytes(
                     $"\x1b[<32;{x + 1};{y + 1}M"))
                term.QueueInput(ch);
            app.RunFrame();
        }

        Motion(5, 5);
        Assert.True(a.Hovered);
        Assert.False(b.Hovered);

        Motion(15, 5);
        Assert.False(a.Hovered);
        Assert.True(b.Hovered);
    }
}

public class ModalActionTests
{
    [Fact]
    public void ActionKey_TriggersWithoutClosing()
    {
        int hits = 0;
        var root = UI.Column(0, new FillWidget('T').Grow(1));
        var term = new FakeTerminal(30, 12);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        app.PushOverlay(new ModalWidget("t", () => ["x"], dim: false,
            actions: [("go", () => hits++)]));

        term.QueueInput((byte)'1');
        app.RunFrame();
        Assert.Equal(1, hits);
        Assert.NotNull(app.Overlay); // still open

        term.QueueInput((byte)'2'); // no second action: swallowed, no crash
        app.RunFrame();
        Assert.Equal(1, hits);
    }

    [Fact]
    public void ActionClick_TriggersAndStaysOpen()
    {
        int hits = 0;
        var root = UI.Column(0, new FillWidget('T').Grow(1));
        var term = new FakeTerminal(40, 12);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        var modal = new ModalWidget("t", () => ["x"], dim: false,
            actions: [("go", () => hits++)]);
        app.PushOverlay(modal);
        app.RunFrame();

        // chip layout: box (8,2,24,7); action row y=7; hint at x=10,
        // button chip rect x=12..15 → click (13,7) = 1-based (14,8)
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;14;8M\x1b[<0;14;8m"))
            term.QueueInput(b);
        app.RunFrame();

        Assert.Equal(1, hits);
        Assert.NotNull(app.Overlay);
    }
}

public class FloatingPopupTests
{
    [Fact]
    public void FloatingPopup_KeepsBaseRendering()
    {
        var baseWidget = new FillWidget('T');
        var root = UI.Column(0, baseWidget.Grow(1));
        var term = new FakeTerminal(30, 12);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        app.RunFrame();
        Assert.Equal(1, baseWidget.RenderCount);

        app.PushOverlay(new ModalWidget("t", () => ["x"], dim: false));
        baseWidget.Invalidate();
        app.RunFrame();
        baseWidget.Invalidate();
        app.RunFrame();

        Assert.Equal(3, baseWidget.RenderCount); // background stays live
    }

    [Fact]
    public void DimmedModal_StillFreezesBase()
    {
        var baseWidget = new FillWidget('T');
        var root = UI.Column(0, baseWidget.Grow(1));
        var term = new FakeTerminal(30, 12);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        app.RunFrame();
        app.PushOverlay(new ModalWidget("t", () => ["x"], dim: true));
        baseWidget.Invalidate();
        app.RunFrame();

        Assert.Equal(1, baseWidget.RenderCount); // frozen under the dim
    }

    [Fact]
    public void ModalContent_IsReactive()
    {
        int counter = 0;
        var root = UI.Column(0, new FillWidget('T').Grow(1));
        var term = new FakeTerminal(30, 12);
        var clock = new QxTui.Animation.VirtualTimeSource();
        using var app = new App(term, root, targetFps: 60, time: clock, asyncPresenting: false);

        var modal = new ModalWidget("t", () => [$"n={counter}"], dim: false);
        app.PushOverlay(modal);
        app.RunFrame();
        Assert.Equal(1, modal.RenderCount);

        counter = 7;
        clock.Advance(TimeSpan.FromMilliseconds(300)); // past the 250ms tick
        app.RunFrame();

        Assert.True(modal.RenderCount >= 2,
            $"modal must re-render on its tick to stay reactive, got {modal.RenderCount}");
    }
}

public class OverlayZOrderTests
{
    [Fact]
    public void Overlay_RepaintsOverBase_EveryFrame_EvenWhenBaseIsDirty()
    {
        var baseWidget = new FillWidget('T');
        var root = UI.Column(0, baseWidget.Grow(1));
        var term = new FakeTerminal(30, 12);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        app.RunFrame();
        app.PushOverlay(new ModalWidget("t", () => ["x"], dim: false));

        // base repaints every frame (floating popup => not frozen)
        for (int i = 0; i < 3; i++)
        {
            baseWidget.Invalidate();
            app.RunFrame();
        }

        // box interior must still be the modal's, not the base's 'T'
        // screen 30x12, box = (3,3,24,5) → center (15,5)
        Assert.NotEqual('T', app.Back[15, 5].Char);
        Assert.Equal(233, app.Back[15, 5].Bg); // modal panel bg wins
    }
}

public class ButtonStyleTests
{
    private static CellBuffer RenderButton(ButtonStyle style, bool hovered, out ButtonWidget btn)
    {
        var buf = new CellBuffer(20, 5);
        btn = new ButtonWidget(() => " ok ", () => { }, ThemeRole.Accent, style);
        var root = UI.Column(0, btn.Grow(1));
        var term = new FakeTerminal(20, 5);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();
        if (hovered)
        {
            btn.Hovered = true;
            btn.Invalidate();
            app.RunFrame();
        }
        return buf; // note: assertions read app.Back via caller instead
    }

    [Fact]
    public void Filled_SolidBackground_Outline_And_Rounded_Differ()
    {
        foreach (var style in new[] { ButtonStyle.Filled, ButtonStyle.Outline, ButtonStyle.Rounded })
        {
            var term = new FakeTerminal(20, 5);
            var btn = new ButtonWidget(() => " ok ", () => { }, ThemeRole.Accent, style);
            var root = UI.Column(0, btn.Grow(1));
            using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
            app.RunFrame();

            var back = app.Back;
            switch (style)
            {
                case ButtonStyle.Filled:
                    Assert.Equal(Theme.Night.Accent, back[10, 2].Bg); // solid fill
                    break;
                case ButtonStyle.Outline:
                    Assert.Equal('┌', back[0, 0].Char);
                    Assert.Equal(-1, back[10, 2].Bg); // hollow inside
                    break;
                case ButtonStyle.Rounded:
                    Assert.Equal('╭', back[0, 0].Char);
                    Assert.Equal(-1, back[10, 2].Bg);
                    break;
            }
        }
    }

    [Fact]
    public void Hover_BrightensEveryStyle()
    {
        foreach (var style in new[] { ButtonStyle.Filled, ButtonStyle.Outline, ButtonStyle.Rounded })
        {
            var term = new FakeTerminal(20, 5);
            var btn = new ButtonWidget(() => " ok ", () => { }, ThemeRole.Accent, style);
            var root = UI.Column(0, btn.Grow(1));
            using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
            app.RunFrame();
            int cold = style == ButtonStyle.Filled ? app.Back[10, 2].Bg : app.Back[0, 0].Fg;

            btn.Hovered = true;
            btn.Invalidate();
            app.RunFrame();
            int hot = style == ButtonStyle.Filled ? app.Back[10, 2].Bg : app.Back[0, 0].Fg;

            Assert.NotEqual(cold, hot);
            Assert.Equal(Theme.Night.Title, hot);
        }
    }

    [Fact]
    public void Resize_WithOpenOverlay_LeavesNoGhost()
    {
        var baseWidget = new FillWidget('T');
        var root = UI.Column(0, baseWidget.Grow(1));
        var term = new FakeTerminal(40, 12);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        app.RunFrame();
        app.PushOverlay(new ModalWidget("t", () => ["x"], dim: false));
        app.RunFrame();

        // modal panel bg present at old box center
        Assert.Equal(233, app.Back[20, 5].Bg);

        term.SimulateResize(70, 20);
        app.RunFrame();
        app.RunFrame();

        // no modal-bg cell may survive outside the NEW box (35-24/2=.. compute:
        // new box w=24,h=5 at ((70-24)/2, (20-5)/2) = (23,7)..(47,12)
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 70; x++)
            {
                bool insideNewBox = x >= 23 && x < 47 && y >= 7 && y < 12;
                if (!insideNewBox)
                    Assert.True(app.Back[x, y].Bg != 233,
                        $"ghost modal pixel at ({x},{y}) after resize");
            }
    }
}

public class ClickInvalidationTests
{
    private sealed class ToggleButton : Widget
    {
        public bool On;
        public ToggleButton() => Clickable = true;
        public override void OnClick(Point local) => On = !On;
        public override void Render(CellBuffer buffer)
        {
            var r = Rect;
            buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
            buffer.WriteTextClipped(Clip, r.X, r.Y, On ? "ON " : "OFF", fg: 7);
        }
    }

    [Fact]
    public void Click_RepaintsTarget_WithoutMouseMotion()
    {
        var btn = new ToggleButton();
        var root = UI.Column(0, btn.Height(1));
        var term = new FakeTerminal(20, 6);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        app.RunFrame();
        Assert.Equal('O', app.Back[0, 0].Char);
        Assert.Equal('F', app.Back[2, 0].Char); // "OFF"

        // press+release on the button, no motion afterwards
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;3;1M\x1b[<0;3;1m"))
            term.QueueInput(b);
        app.RunFrame();

        Assert.True(btn.On);
        Assert.Equal('N', app.Back[1, 0].Char); // "ON " painted immediately
        Assert.Equal(' ', app.Back[2, 0].Char);
    }
}

public class PressedStateTests
{
    [Fact]
    public void Press_SetsPressedUntilRelease_AndLooksDifferent()
    {
        var term = new FakeTerminal(20, 5);
        var btn = new ButtonWidget(() => " ok ", () => { }, ThemeRole.Accent, ButtonStyle.Filled);
        var root = UI.Column(0, btn.Grow(1));
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        app.RunFrame();
        int idleBg = app.Back[10, 2].Bg;

        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;11;3M")) // press only
            term.QueueInput(b);
        app.RunFrame();

        Assert.True(btn.Pressed);
        int pressedBg = app.Back[10, 2].Bg;
        Assert.NotEqual(idleBg, pressedBg); // visible push-down

        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;11;3m")) // release
            term.QueueInput(b);
        app.RunFrame();

        Assert.False(btn.Pressed);
        Assert.Equal(idleBg, app.Back[10, 2].Bg); // back to idle
    }
}
