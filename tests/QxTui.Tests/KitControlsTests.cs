using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;
using QxTui.Widgets;

namespace QxTui.Tests;

public class KitControlsTests
{
    private static (App app, FakeTerminal term, VirtualTimeSource clock) MakeApp(
        LayoutNode root, int w = 40, int h = 12)
    {
        var term = new FakeTerminal(w, h);
        var clock = new VirtualTimeSource();
        var app = new App(term, root, targetFps: 60, time: clock, asyncPresenting: false);
        return (app, term, clock);
    }

    private static void Mouse(FakeTerminal term, string seq)
    {
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes(seq))
            term.QueueInput(b);
    }

    private static bool BackContains(App app, string text)
    {
        var cells = app.Back.Cells;
        int ni = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            ni = cells[i].Char == text[ni] ? ni + 1 : 0;
            if (ni == text.Length) return true;
        }
        return false;
    }

    [Fact]
    public void Checkbox_ClickToggles_AndSpaceWhenFocused()
    {
        var on = new Signal<bool>(false);
        var cb = new Checkbox(on, "dark mode");
        var (app, term, _) = MakeApp(UI.Column(0, cb));
        app.RunFrame();
        Assert.True(BackContains(app, "[ ] dark mode"));

        Mouse(term, "\x1b[<0;2;1M\x1b[<0;2;1m");
        app.RunFrame();
        Assert.True(on.Value);
        Assert.True(BackContains(app, "[x] dark mode"));

        app.Focus(cb);
        term.QueueInput((byte)' ');
        app.RunFrame();
        Assert.False(on.Value);
    }

    [Fact]
    public void Segmented_ClickSelects_AndArrowsWhenFocused()
    {
        var sel = new Signal<int>(0);
        var seg = new Segmented(sel, "day", "night");
        var (app, term, _) = MakeApp(UI.Column(0, seg));
        app.RunFrame();

        Mouse(term, "\x1b[<0;8;1M\x1b[<0;8;1m"); // second segment (' night ' starts at x=6)
        app.RunFrame();
        Assert.Equal(1, sel.Value);

        app.Focus(seg);
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[D"))
            term.QueueInput(b); // Left
        app.RunFrame();
        Assert.Equal(0, sel.Value);
    }

    [Fact]
    public void Panel_ChildrenLiveInsideTheRing_AndTitleDraws()
    {
        var inner = new Container();
        inner.Node.Grow = 1;
        var panel = new Panel("settings");
        panel.AddChild(inner);
        var (app, _, _) = MakeApp(UI.Column(0, panel.Grow(1)));
        app.RunFrame();

        Assert.Equal(new Rect(1, 1, 38, 10), inner.Rect); // ChildInset ring
        Assert.True(BackContains(app, "settings"));
    }

    [Fact]
    public void Separator_DrawsARule()
    {
        var sep = new Separator();
        var (app, _, _) = MakeApp(UI.Column(0, sep));
        app.RunFrame();
        Assert.Equal('─', app.Back[5, 0].Char);
    }

    [Fact]
    public void ProgressBar_BlocksAndPercent()
    {
        var v = new Signal<float>(0.5f);
        var bar = new ProgressBar(v);
        var (app, _, _) = MakeApp(UI.Column(0, bar));
        app.RunFrame();

        Assert.Equal('█', app.Back[0, 0].Char);   // filled half
        Assert.Equal('░', app.Back[20, 0].Char);  // empty half
        Assert.True(BackContains(app, "50%"));
    }

    [Fact]
    public void Spinner_AdvancesWithTheClock()
    {
        var sp = new Spinner();
        var (app, _, clock) = MakeApp(UI.Column(0, sp));
        app.RunFrame();
        char first = app.Back[0, 0].Char;
        Assert.NotEqual(' ', first);

        clock.Advance(TimeSpan.FromMilliseconds(200)); // 12Hz → several frames
        app.RunFrame();
        Assert.NotEqual(first, app.Back[0, 0].Char);
    }

    [Fact]
    public void Select_ClickOpens_ChoiceSetsAndCloses()
    {
        var sel = new Signal<int>(0);
        var dropdown = new Dropdown(sel, "alpha", "beta", "gamma");
        var (app, term, _) = MakeApp(UI.Column(0, dropdown));
        app.RunFrame();
        Assert.True(BackContains(app, "alpha"));

        Mouse(term, "\x1b[<0;2;1M\x1b[<0;2;1m"); // open
        app.RunFrame();
        Assert.IsType<DropdownPopup>(app.Overlay);

        Mouse(term, "\x1b[<0;3;4M\x1b[<0;3;4m"); // click option 1 ("beta", row y=3)
        app.RunFrame();
        Assert.Equal(1, sel.Value);
        Assert.Null(app.Overlay);
        Assert.True(BackContains(app, "beta"));
    }

    [Fact]
    public void Select_KeyboardOpenNavigateSelect_AndEscapeCloses()
    {
        var sel = new Signal<int>(0);
        var dropdown = new Dropdown(sel, "alpha", "beta", "gamma");
        var (app, term, _) = MakeApp(UI.Column(0, dropdown));
        app.RunFrame();
        app.Focus(dropdown);

        term.QueueInput((byte)13); // Enter opens
        app.RunFrame();
        Assert.IsType<DropdownPopup>(app.Overlay);

        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[B"))
            term.QueueInput(b); // Down → cursor 1
        app.RunFrame();
        term.QueueInput((byte)13); // Enter commits
        app.RunFrame();
        Assert.Equal(1, sel.Value);
        Assert.Null(app.Overlay);

        term.QueueInput((byte)13); // open again...
        app.RunFrame();
        Assert.IsType<DropdownPopup>(app.Overlay);
        term.QueueInput((byte)27); // ...Escape closes (settle like any lone ESC)
        app.RunFrame();
        Thread.Sleep(KeyParser.EscSettleMs + 20);
        app.RunFrame();
        Assert.Null(app.Overlay);
        Assert.Equal(1, sel.Value); // escape keeps the old choice
    }

    [Fact]
    public void Select_OutsidePressCloses_WithoutRetriggering()
    {
        var sel = new Signal<int>(0);
        var dropdown = new Dropdown(sel, "alpha", "beta");
        var (app, term, _) = MakeApp(UI.Column(0, dropdown));
        app.RunFrame();
        Mouse(term, "\x1b[<0;2;1M\x1b[<0;2;1m"); // open
        app.RunFrame();
        Assert.IsType<DropdownPopup>(app.Overlay);

        Mouse(term, "\x1b[<0;31;10M\x1b[<0;31;10m"); // press far outside the list
        app.RunFrame();
        Assert.Null(app.Overlay);   // dismissed...
        Assert.Equal(0, sel.Value); // ...without choosing or reopening
    }

    [Fact]
    public void Toast_Shows_SelfPops_OnTtl_AndReplaces()
    {
        var (app, _, clock) = MakeApp(UI.Column(0, new Container().Grow(1)));
        app.RunFrame();

        app.Toast("saved");
        app.RunFrame();
        Assert.IsType<Toast>(app.Overlay);
        Assert.True(BackContains(app, "saved"));

        app.Toast("again"); // single slot: replaces
        app.RunFrame();
        Assert.Equal(1, app.OverlayCount);
        Assert.True(BackContains(app, "again"));

        clock.Advance(TimeSpan.FromSeconds(3)); // ttl 2500ms
        app.RunFrame();
        Assert.Null(app.Overlay);
    }

    [Fact]
    public void Button_HasOneRowByDefault_NotZero()
    {
        // regression: a leaf without Measure flexes to height 0 — a bare
        // button was invisible until every call site set Height by hand
        var btn = new Button(() => " toast ");
        var (app, _, _) = MakeApp(UI.Column(0, btn));
        app.RunFrame();
        Assert.Equal(1, btn.Rect.H);
        Assert.True(BackContains(app, "toast"));
    }

    [Fact]
    public void Toast_KindOwnsItsFullColorPair()
    {
        var (app, _, _) = MakeApp(UI.Column(0, new Container().Grow(1)));
        app.RunFrame();
        app.Toast("boom", ToastKind.Error);
        app.RunFrame();

        // box: w=8 at x=16, h=3 at y=8; filled variant = kind pair edge to edge
        Assert.Equal(196, app.Back[16, 9].Bg);   // error red fill
        Assert.Equal('b', app.Back[18, 9].Char);
        Assert.Equal(255, app.Back[18, 9].Fg);    // white ink on red
        Assert.Equal(196, app.Back[18, 9].Bg);    // text keeps the fill: no holes
    }

    [Fact]
    public void Toast_FrameOption_DrawsRoundedBorderInKindColor()
    {
        var (app, _, _) = MakeApp(UI.Column(0, new Container().Grow(1)));
        app.RunFrame();
        app.Toast("note", ToastKind.Info, frame: BoxStyles.Rounded);
        app.RunFrame();

        Assert.Equal('╭', app.Back[16, 8].Char); // rounded corner, info blue
        Assert.Equal(33, app.Back[16, 8].Fg);
        Assert.Equal(236, app.Back[20, 9].Bg);   // dark fill inside the frame
    }
}

public class PanelCollapseTests
{
    private static (App app, FakeTerminal term, VirtualTimeSource clock) MakeApp(
        LayoutNode root, int w = 40, int h = 12)
    {
        var term = new FakeTerminal(w, h);
        var clock = new VirtualTimeSource();
        var app = new App(term, root, targetFps: 60, time: clock, asyncPresenting: false);
        return (app, term, clock);
    }

    private static void Mouse(FakeTerminal term, string seq)
    {
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes(seq))
            term.QueueInput(b);
    }

    [Fact]
    public void CollapseChip_IsOptIn_OffByDefault()
    {
        var plain = new Panel("pane");
        plain.Node.Width = 12;
        var root = new LayoutNode { Direction = FlexDirection.Row };
        root.Add(plain.Node);
        var (app, term, _) = MakeApp(root, w: 30, h: 8);
        app.RunFrame();

        // clicking where the chip would sit does nothing without opt-in
        Mouse(term, "\x1b[<0;11;1M\x1b[<0;11;1m");
        app.RunFrame();
        Assert.Equal(12, plain.Rect.W);
        plain.IsCollapsed = true; // ignored: not collapsible
        app.RunFrame();
        Assert.Equal(12, plain.Rect.W);
    }

    [Fact]
    public void ChipCollapses_ToRail_ExpandRestoresWidth()
    {
        var panel = new Panel("pane") { Collapsible = true };
        panel.Node.Width = 12;
        var sibling = new ParserFuzzProbeWidget2();
        sibling.Node.Grow = 1;
        var root = new LayoutNode { Direction = FlexDirection.Row };
        root.Add(panel.Node);
        root.Add(sibling.Node);
        var (app, term, _) = MakeApp(root, w: 30, h: 8);
        app.RunFrame();
        Assert.Equal(12, panel.Rect.W);

        // title-bar chip at local (W-2, 0) = (10,0) → SGR (11,1)
        Mouse(term, "\x1b[<0;11;1M\x1b[<0;11;1m");
        app.RunFrame();
        Assert.True(panel.IsCollapsed);
        app.RunFrame();
        Assert.Equal(1, panel.Rect.W);      // the rail
        Assert.Equal(29, sibling.Rect.W);   // sibling took the room

        // rail chip at (0,0) → SGR (1,1)
        Mouse(term, "\x1b[<0;1;1M\x1b[<0;1;1m");
        app.RunFrame();
        Assert.False(panel.IsCollapsed);
        app.RunFrame();
        Assert.Equal(12, panel.Rect.W);     // expanded width remembered
    }

    [Fact]
    public void CollapsedPanel_RefusesResizeGrabs()
    {
        var panel = new Panel("pane") { Collapsible = true, Resizable = true };
        panel.Node.Width = 12;
        panel.IsCollapsed = true; // Collapsible set first, so this sticks
        var root = new LayoutNode { Direction = FlexDirection.Row };
        root.Add(panel.Node);
        var (app, term, _) = MakeApp(root, w: 30, h: 8);
        app.RunFrame();
        Assert.Equal(1, panel.Rect.W);

        Mouse(term, "\x1b[<2;1;1M");  // right-press on the rail
        app.RunFrame();
        Mouse(term, "\x1b[<34;10;1M"); // drag: must NOT resize
        app.RunFrame();
        Mouse(term, "\x1b[<2;10;1m");
        app.RunFrame();
        Assert.Equal(1, panel.Rect.W);
    }

    private sealed class ParserFuzzProbeWidget2 : Widget
    {
        public override void Render(CellBuffer buffer) { }
    }
}
