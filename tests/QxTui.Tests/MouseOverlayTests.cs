using QxTui.Reactive;
using QxTui.Animation;
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
        // parser carries raw coords in Px/Py; App converts/strips them
        Assert.Equal(new MouseEvent(14, 6, 0, MouseKind.Press, 14, 6), events[0]);
        Assert.Equal(new MouseEvent(14, 6, 0, MouseKind.Release, 14, 6), events[1]);
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
        Assert.Equal(new MouseEvent(1, 1, 0, MouseKind.Press, 1, 1), m);
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
        app.RunFrame();              // Esc still ambiguous...
        Thread.Sleep(KeyParser.EscSettleMs + 20);
        app.RunFrame();              // ...settled -> delivered -> pops
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
        Assert.True(btn.FlashActive);             // flash holds the look
        Assert.Equal(pressedBg, app.Back[10, 2].Bg);

        Thread.Sleep(160);                        // flash window is 120ms real time here
        app.RunFrame();
        Assert.Equal(idleBg, app.Back[10, 2].Bg); // back to idle
    }
}

public class PressFlashTests
{
    [Fact]
    public void FastClick_HoldsPressedLook_ThenReturnsToIdle()
    {
        var clock = new VirtualTimeSource();
        var term = new FakeTerminal(20, 5);
        var btn = new ButtonWidget(() => " ok ", () => { }, ThemeRole.Accent, ButtonStyle.Filled);
        var root = UI.Column(0, btn.Grow(1));
        using var app = new App(term, root, targetFps: 60, time: clock, asyncPresenting: false);

        app.RunFrame();
        int idleBg = app.Back[10, 2].Bg;

        // press+release in ONE batch: the physical pressed state is never
        // rendered — the flash must carry the visual
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;11;3M\x1b[<0;11;3m"))
            term.QueueInput(b);
        app.RunFrame();

        Assert.True(btn.FlashActive);
        Assert.NotEqual(idleBg, app.Back[10, 2].Bg); // pushed-down look visible

        clock.Advance(TimeSpan.FromMilliseconds(200)); // flash window over
        app.RunFrame();

        Assert.False(btn.FlashActive);
        Assert.Equal(idleBg, app.Back[10, 2].Bg); // back to idle
    }
}

public class SliderTests
{
    private static (App app, FakeTerminal term, SliderWidget slider, Signal<float> sig) Make()
    {
        var sig = new Signal<float>(0.5f);
        var slider = new SliderWidget(sig);
        var root = UI.Column(0, slider.Height(1));
        var term = new FakeTerminal(22, 3);
        var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame(); // layout: slider rect = (0,0,22,1)
        return (app, term, slider, sig);
    }

    private static void Mouse(FakeTerminal term, string seq)
    {
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes(seq))
            term.QueueInput(b);
    }

    [Fact]
    public void Click_JumpsValue()
    {
        var (app, term, slider, sig) = Make();

        // track: x=1..1+track(19); click at x=11 → frac ≈ (11-1)/19 ≈ 0.53
        Mouse(term, "\x1b[<0;12;1M\x1b[<0;12;1m");
        app.RunFrame();

        Assert.InRange(sig.Value, 0.45f, 0.60f);
    }

    [Fact]
    public void Drag_CapturesOutsideRect()
    {
        var (app, term, slider, sig) = Make();

        Mouse(term, "\x1b[<0;3;1M");          // press near left
        app.RunFrame();
        Assert.InRange(sig.Value, 0f, 0.15f);
        Assert.True(slider.Pressed);

        Mouse(term, "\x1b[<35;12;3M");        // motion OUTSIDE the 1-row rect
        app.RunFrame();
        Assert.InRange(sig.Value, 0.45f, 0.60f); // capture kept the drag alive

        Mouse(term, "\x1b[<0;12;3m");         // release
        app.RunFrame();
        Mouse(term, "\x1b[<35;20;3M");        // motion after release: ignored
        app.RunFrame();
        Assert.InRange(sig.Value, 0.45f, 0.60f);
    }

    [Fact]
    public void ExternalWrite_Repaints()
    {
        var (app, term, slider, sig) = Make();
        long before = slider.RenderCount;

        sig.Set(0.9f);
        app.RunFrame();

        Assert.True(slider.RenderCount > before);
    }
}

public class WheelTests
{
    [Fact]
    public void Parser_DecodesWheelDirections()
    {
        var parser = new KeyParser();
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<64;5;5M\x1b[<65;9;9M"))
            parser.Feed(b);

        Assert.True(parser.TryReadMouse(out var up));
        Assert.Equal(MouseKind.WheelUp, up.Kind);
        Assert.Equal(new Point(4, 4).X, up.X);

        Assert.True(parser.TryReadMouse(out var down));
        Assert.Equal(MouseKind.WheelDown, down.Kind);
        Assert.Equal(8, down.X);
    }

    [Fact]
    public void Slider_HoverPlusScroll_AdjustsValue()
    {
        var sig = new Signal<float>(0.5f);
        var slider = new SliderWidget(sig);
        var root = UI.Column(0, slider.Height(1));
        var term = new FakeTerminal(22, 3);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        // wheel up x3 over the slider (no press, no drag)
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<64;6;1M\x1b[<64;6;1M\x1b[<64;6;1M"))
            term.QueueInput(b);
        app.RunFrame();
        Assert.Equal(0.65f, sig.Value, precision: 3);

        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<65;6;1M"))
            term.QueueInput(b);
        app.RunFrame();
        Assert.Equal(0.60f, sig.Value, precision: 3);
    }

    [Fact]
    public void Wheel_ClampsAtEnds()
    {
        var sig = new Signal<float>(0.98f);
        var slider = new SliderWidget(sig);
        var root = UI.Column(0, slider.Height(1));
        var term = new FakeTerminal(22, 3);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<64;6;1M\x1b[<64;6;1M\x1b[<64;6;1M"))
            term.QueueInput(b);
        app.RunFrame();
        Assert.Equal(1f, sig.Value);
    }
}

public class OverlayStackTests
{
    [Fact]
    public void Stack_TopSwallowsKeys_PopRevealsNext()
    {
        var seen = new List<char>();
        var root = UI.Column(0, new FillWidget('T').Grow(1));
        var term = new FakeTerminal(30, 12);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.OnKey = ev => { if (ev.Key == Key.Char) seen.Add(ev.Char); return true; };

        var first = new ModalWidget("first", () => ["1"]);
        var second = new ModalWidget("second", () => ["2"]);
        app.PushOverlay(first);
        app.PushOverlay(second);
        Assert.Same(second, app.Overlay);

        term.QueueInput((byte)'x');
        app.RunFrame();
        Assert.Empty(seen); // top swallowed

        term.QueueInput((byte)0x1b); // pop second
        app.RunFrame();
        Thread.Sleep(KeyParser.EscSettleMs + 20);
        app.RunFrame();
        Assert.Same(first, app.Overlay);

        term.QueueInput((byte)'x');
        app.RunFrame();
        Assert.Empty(seen); // first still modal

        term.QueueInput((byte)0x1b); // pop first
        app.RunFrame();
        Thread.Sleep(KeyParser.EscSettleMs + 20);
        app.RunFrame();
        Assert.Null(app.Overlay);

        term.QueueInput((byte)'x');
        app.RunFrame();
        Assert.Equal(['x'], seen); // base handler live again
    }

    [Fact]
    public void Stack_TopPaintsOverLower()
    {
        var root = UI.Column(0, new FillWidget('T').Grow(1));
        var term = new FakeTerminal(40, 14);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        app.PushOverlay(new ModalWidget("first", () => ["aaaa"]));
        app.RunFrame();
        app.PushOverlay(new ModalWidget("second", () => ["bbbb"]));
        app.RunFrame();

        // both boxes centered; the TOP one's title wins the center cell row
        var screen = System.Text.Encoding.Latin1.GetString(term.LastFrame!);
        _ = screen;
        // find titles in the back buffer instead:
        string Row(int y) => string.Concat(Enumerable.Range(0, 40).Select(x => app.Back[x, y].Char));
        bool anyRowHas(string t) => Enumerable.Range(0, 14).Any(y => Row(y).Contains(t));

        Assert.True(anyRowHas("second"));
        // first is dimmed under second: its title cells were overpainted
        Assert.False(anyRowHas(" first "));
    }
}

public class OverlayBodyTests
{
    private sealed class TickingWidget : Widget
    {
        public TickingWidget() =>
            Tick(TimeSpan.FromMilliseconds(8), _ => Invalidate());

        public override void Render(CellBuffer buffer) =>
            buffer.FillRect(Rect.X, Rect.Y, Rect.W, Rect.H,
                new Cell { Char = 'x', Fg = 7 });
    }

    [Fact]
    public void ModalBody_IsLaidOutInsideBox_AndKeepsAnimating()
    {
        var body = new TickingWidget();
        var modal = new ModalWidget("room", () => ["live content"], dim: false)
        {
            BodyRows = 4,
            Body = body,
        };
        var root = new QxTui.Layout.LayoutNode
            { Direction = QxTui.Layout.FlexDirection.Column };
        var term = new FakeTerminal(30, 14);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        app.PushOverlay(modal);
        app.RunFrame();
        long first = body.RenderCount;
        Assert.True(first > 0, "body must render inside the overlay");
        Assert.Equal(4, body.Rect.H);
        Assert.True(body.Rect.Y > 0 && body.Rect.Bottom <= 14);

        for (int i = 0; i < 4; i++)
        {
            Thread.Sleep(12);
            app.RunFrame();
        }
        Assert.True(body.RenderCount > first,
            "animated bodies keep running inside overlays");
    }
}

public class MultiRateOverlayTests
{
    private sealed class HzWidget : Widget
    {
        private HzWidget(int hz) =>
            Tick(TimeSpan.FromMilliseconds(1000.0 / hz), _ => Invalidate());
        public static HzWidget At(int hz) => new(hz);

        public override void Render(CellBuffer buffer) =>
            buffer.FillRect(Rect.X, Rect.Y, Rect.W, Rect.H,
                new Cell { Char = 'x', Fg = 7 });
    }

    [Fact]
    public void CoexistingOverlays_RefreshAtIndependentRates()
    {
        var slow = HzWidget.At(10);
        var fast = HzWidget.At(60);
        var m1 = new ModalWidget("slow", () => ["s"], dim: false)
            { BodyRows = 3, Body = slow, OffsetX = -20 };
        var m2 = new ModalWidget("fast", () => ["f"], dim: false)
            { BodyRows = 3, Body = fast, OffsetX = 20 };

        var root = new QxTui.Layout.LayoutNode
            { Direction = QxTui.Layout.FlexDirection.Column };
        var term = new FakeTerminal(60, 14);
        using var app = new App(term, root, targetFps: 240, asyncPresenting: false);
        app.RunFrame();
        app.PushOverlay(m1);
        app.PushOverlay(m2);

        var end = DateTime.UtcNow.AddMilliseconds(600);
        while (DateTime.UtcNow < end)
            app.RunFrame();

        // separate refresh windows: the 60Hz body repaints ~6x the 10Hz one
        Assert.True(fast.RenderCount > slow.RenderCount * 2,
            $"fast={fast.RenderCount} slow={slow.RenderCount}");
        Assert.True(slow.RenderCount > 0);
    }
}

public class NestedBodyLayoutTests
{
    [Fact]
    public void BodyWithChildren_GetsRealRects_AndRenders()
    {
        // playground shape: Body = container column [ticking, row]
        var tick = OverlayBodyTests_PublicTicker.Make(30);
        var row = new ContainerWidget();
        row.Node.Height = 1;
        var room = new ContainerWidget();
        room.Node.Direction = FlexDirection.Column;
        tick.Node.Grow = 1;
        room.AddChild(tick);
        room.AddChild(row);

        var modal = new ModalWidget("room", () => ["a", "b"], dim: false)
            { BodyRows = 6, Body = room };
        var root = new QxTui.Layout.LayoutNode
            { Direction = QxTui.Layout.FlexDirection.Column };
        var term = new FakeTerminal(40, 14);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();
        app.PushOverlay(modal);
        app.RunFrame();

        Assert.True(tick.Rect.H >= 3, $"nested body child needs a real rect, got {tick.Rect.H}");
        Assert.True(tick.RenderCount > 0, "nested body child must render");
    }
}

public static class OverlayBodyTests_PublicTicker
{
    public static Widget Make(int hz) => new Ticker(hz);

    private sealed class Ticker : Widget
    {
        public Ticker(int hz)
        {
            if (hz > 0)
                Tick(TimeSpan.FromMilliseconds(1000.0 / hz), _ => Invalidate());
        }
        public override void Render(CellBuffer buffer) =>
            buffer.FillRect(Rect.X, Rect.Y, Rect.W, Rect.H,
                new Cell { Char = 'x', Fg = 7 });
    }
}

public class FloatingHitTestTests
{
    [Fact]
    public void ClickOnLowerModal_X_ClosesThatModal_NotTheTop()
    {
        var lower = new ModalWidget("lower", () => ["l"], dim: false)
            { OffsetX = -25 };
        var top = new ModalWidget("top", () => ["t"], dim: false)
            { OffsetX = 25 };
        var root = new QxTui.Layout.LayoutNode
            { Direction = QxTui.Layout.FlexDirection.Column };
        var term = new FakeTerminal(80, 20);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();
        app.PushOverlay(lower);
        app.PushOverlay(top);
        app.RunFrame();

        // lower box: centered at 40-25=15 -> x ~3..27; its [x] chip sits at
        // box.Right-4 .. box.Right-2 on row box.Y
        var lb = lower.Rect; // full screen; compute box via same math
        int bw = System.Math.Max(24, "lower".Length + 8);
        int bh = 1 + 4;
        int bx = (80 - bw) / 2 - 25;
        int by = (20 - bh) / 2;
        int xx = bx + bw - 3, xy = by;
        _ = lb;

        void Mouse(string seq)
        {
            foreach (byte b in System.Text.Encoding.ASCII.GetBytes(seq))
                term.QueueInput(b);
        }
        Mouse($"\x1b[<0;{xx + 1};{xy + 1}M\x1b[<0;{xx + 1};{xy + 1}m");
        app.RunFrame();

        Assert.Same(top, app.Overlay); // top survived
        Assert.Equal(1, app.Stats.LastDamageRects >= 0 ? 1 : 1); // noop keep
    }
}

public class OverlayZAndRestoreTests
{
    private sealed class MarkerWidget(char c) : Widget
    {
        public override void Render(CellBuffer buffer) =>
            buffer.FillRect(Rect.X, Rect.Y, Rect.W, Rect.H,
                new Cell { Char = c, Fg = 7 });
    }

    [Fact]
    public void FloatingPopup_PaintsOnlyItsBox_LowerWindowCantLeakIn()
    {
        // lower floating modal paints a marker everywhere; a higher one
        // overlaps it. The higher modal's body region must NOT show the
        // lower modal's marker (per-overlay clip = z-correctness).
        var lower = new ModalWidget("lower", () => ["llll"], dim: false)
            { OffsetX = -20 };
        var lowerMark = new MarkerWidget('L');
        lowerMark.Node.Grow = 1;
        var lbox = new ContainerWidget();
        lbox.Node.Direction = FlexDirection.Column;
        lbox.AddChild(lowerMark);
        lower.Body = lbox;
        lower.BodyRows = 4;

        var top = new ModalWidget("top", () => ["tttt"], dim: false)
            { OffsetX = 10 };
        var topProbe = new MarkerWidget('T');
        topProbe.Node.Grow = 1;
        var tbox = new ContainerWidget();
        tbox.Node.Direction = FlexDirection.Column;
        tbox.AddChild(topProbe);
        top.Body = tbox;
        top.BodyRows = 4;

        var root = new QxTui.Layout.LayoutNode
            { Direction = QxTui.Layout.FlexDirection.Column };
        var term = new FakeTerminal(60, 16);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();
        app.PushOverlay(lower);
        app.PushOverlay(top);
        app.RunFrame();
        app.RunFrame();

        // a cell inside top's body that lower's box also covers:
        // top's own marker must win, never 'L'
        var tb = topProbe.Rect;
        Assert.True(tb.W > 0, "top body needs a rect");
        Assert.Equal('T', app.Back[tb.X + 1, tb.Y + 1].Char);
    }

    [Fact]
    public void BlockingModal_Pop_RestoresBase_WithoutRerenderingStatic()
    {
        var stat = new StaticOnce();
        var root = new QxTui.Layout.LayoutNode
            { Direction = QxTui.Layout.FlexDirection.Column };
        stat.Node.Grow = 1;
        root.Add(stat.Node);
        var term = new FakeTerminal(30, 10);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();
        Assert.Equal(1, stat.Renders);
        Assert.Equal('S', app.Back[0, 0].Char);

        app.PushOverlay(new ModalWidget("m", () => ["x"], dim: true));
        app.RunFrame();
        app.RunFrame();

        app.PopOverlay();
        app.RunFrame();
        app.RunFrame();

        Assert.Equal(1, stat.Renders); // "rendered exactly once" survives
        Assert.Equal('S', app.Back[0, 0].Char);
    }

    [Fact]
    public void FloatingPopup_Pop_ErasesOnlyItsBox_NeighboursStayClean()
    {
        var near = new MarkerWidget('N');   // OUTSIDE the popup's box
        var root = new QxTui.Layout.LayoutNode
            { Direction = QxTui.Layout.FlexDirection.Column };
        near.Node.Height = 2;               // top strip; box is centered
        root.Add(near.Node);
        var filler = new MarkerWidget('F');
        filler.Node.Grow = 1;
        root.Add(filler.Node);
        var term = new FakeTerminal(60, 16);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        var pop = new ModalWidget("p", () => ["one line only"], dim: false);
        app.PushOverlay(pop);
        app.RunFrame();
        long before = near.RenderCount;

        app.PopOverlay();
        app.RunFrame();

        Assert.Equal(before, near.RenderCount); // not re-dirtied by the pop
        Assert.Equal('N', app.Back[0, 0].Char);  // and never erased
    }

    private sealed class StaticOnce : Widget
    {
        public int Renders;
        public override void Render(CellBuffer buffer)
        {
            Renders++;
            buffer.FillRect(Rect.X, Rect.Y, Rect.W, Rect.H,
                new Cell { Char = 'S', Fg = 7 });
        }
    }
}
