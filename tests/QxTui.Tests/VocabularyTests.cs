using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;
using QxTui.Widgets;

namespace QxTui.Tests;

/// <summary>Widget vocabulary batch: Table, TreeView, ContextMenu,
/// Tooltip, RadioGroup, ToggleGroup — rendering, selection, gestures and
/// the right-button disambiguation (still click = menu, drag = resize).</summary>
public class VocabularyTests
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

    internal static Table MakeTablePublic(int rows = 3) => MakeTable(rows);
    internal static TreeView MakeTreePublic() => MakeTree();

    private static Table MakeTable(int rows = 3)
    {
        var t = new Table(() => rows,
            new TableColumn("name", Weight: 2),
            new TableColumn("kind", Width: 6, Align: ColumnAlign.Right))
        {
            Cell = (r, c) => c == 0 ? $"n{r}" : $"k{r}",
        };
        t.Node.Grow = 1;
        return t;
    }

    // ---- Table --------------------------------------------------------------

    [Fact]
    public void Table_RendersHeaderAndCells()
    {
        var table = MakeTable();
        var (app, _, _) = MakeApp(UI.Column(0, table));
        app.RunFrame();
        Assert.True(BackContains(app, "name"));
        Assert.True(BackContains(app, "kind"));
        Assert.True(BackContains(app, "n0"));
        Assert.True(BackContains(app, "k2"));
    }

    [Fact]
    public void Table_ClickSelectsRowAndColumn()
    {
        var table = MakeTable();
        var (app, term, _) = MakeApp(UI.Column(0, table));
        app.RunFrame();

        // header at y0, separator y1, body from y2; 'kind' col starts at x33
        Mouse(term, "\x1b[<0;35;4M\x1b[<0;35;4m"); // row 1, kind column
        app.RunFrame();

        Assert.Equal(1, table.SelectedRow);
        Assert.Equal(1, table.SelectedCol);
        Assert.Same(table, app.Focused); // click-to-focus came along
    }

    [Fact]
    public void Table_KeysMoveCursor_EnterActivates()
    {
        var table = MakeTable(10);
        var (app, term, _) = MakeApp(UI.Column(0, table));
        app.RunFrame();
        app.Focus(table);

        (int Row, int Col)? activated = null;
        table.Activated += (r, c) => activated = (r, c);

        Mouse(term, "\x1b[B"); // Down
        Mouse(term, "\x1b[C"); // Right
        term.QueueInput((byte)'\r');
        app.RunFrame();

        Assert.Equal((1, 1), activated);
    }

    [Fact]
    public void Table_WheelScrolls_AndOffsetClamps()
    {
        var table = MakeTable(100);
        var (app, term, _) = MakeApp(UI.Column(0, table));
        app.RunFrame();

        Mouse(term, "\x1b[<65;10;6M"); // wheel down
        app.RunFrame();
        Assert.Equal(3, table.Offset);

        Mouse(term, "\x1b[<64;10;6M\x1b[<64;10;6M"); // wheel up twice
        app.RunFrame();
        Assert.Equal(0, table.Offset); // clamped at the top
    }

    // ---- TreeView -----------------------------------------------------------

    private static TreeView MakeTree()
    {
        var root = new TreeNode("proj");
        root.Add(new TreeNode("src").Add("main.cs").Add("lib.cs"));
        root.Add("readme.md");
        var tv = new TreeView(root);
        tv.Node.Grow = 1;
        return tv;
    }

    [Fact]
    public void Tree_RendersHierarchyWithGlyphs()
    {
        var tv = MakeTree();
        var (app, _, _) = MakeApp(UI.Column(0, tv));
        app.RunFrame();
        Assert.True(BackContains(app, "proj"));
        Assert.True(BackContains(app, "main.cs"));
        Assert.True(BackContains(app, "readme.md"));
        Assert.True(BackContains(app, "▾")); // expanded parent glyph
        Assert.True(BackContains(app, "·")); // leaf glyph
    }

    [Fact]
    public void Table_SelectionChanged_FiresOnNavigationOnly()
    {
        var table = MakeTable(10);
        var (app, term, _) = MakeApp(UI.Column(0, table));
        app.RunFrame();
        app.Focus(table);

        var moves = new List<(int Row, int Col)>();
        table.SelectionChanged += (r, c) => moves.Add((r, c));

        Mouse(term, "\x1b[B"); // Down → row 1
        Mouse(term, "\x1b[C"); // Right → col 1
        Mouse(term, "\x1b[C"); // Right again: clamped at last col → no move
        app.RunFrame();

        Assert.Equal([(1, 0), (1, 1)], moves);

        table.SelectedRow = 5; // direct set: navigation event must NOT fire
        Assert.Equal(2, moves.Count);
    }

    [Fact]
    public void Tree_SelectionChanged_FiresOnMoves_NotToggles()
    {
        var tv = MakeTree();
        var (app, term, _) = MakeApp(UI.Column(0, tv));
        app.RunFrame();
        app.Focus(tv);

        var seen = new List<string>();
        tv.SelectionChanged += n => seen.Add(n.Label);

        Mouse(term, "\x1b[B"); // Down → src
        app.RunFrame();
        Mouse(term, "\x1b[D"); // Left → collapse (no move)
        app.RunFrame();
        Mouse(term, "\x1b[B"); // Down → readme.md (src collapsed)
        app.RunFrame();

        Assert.Equal(["src", "readme.md"], seen);
    }

    [Fact]
    public void Tree_LeftCollapses_RightExpands()
    {
        var tv = MakeTree();
        var (app, term, _) = MakeApp(UI.Column(0, tv));
        app.RunFrame();
        app.Focus(tv);

        term.QueueInput((byte)0x1b); // isolate: (no-op, keeps parser clean)
        app.RunFrame();

        Mouse(term, "\x1b[B"); // Down → 'src' (a parent)
        app.RunFrame();
        Mouse(term, "\x1b[D"); // Left → collapse
        app.RunFrame();
        Assert.False(BackContains(app, "main.cs"));

        Mouse(term, "\x1b[C"); // Right → expand again
        app.RunFrame();
        Assert.True(BackContains(app, "main.cs"));
    }

    [Fact]
    public void Tree_ClickOnGlyphToggles_ClickOnLabelSelects()
    {
        var tv = MakeTree();
        var (app, term, _) = MakeApp(UI.Column(0, tv));
        app.RunFrame();

        // flat rows: proj(0) src(1) main.cs(2) lib.cs(3) readme.md(4)
        // 'src' glyph sits at x=2 (depth 1), row y=1 → SGR (3,2)
        Mouse(term, "\x1b[<0;3;2M\x1b[<0;3;2m");
        app.RunFrame();
        Assert.False(BackContains(app, "main.cs")); // collapsed
        Assert.Equal("src", tv.SelectedNode?.Label);

        // click the label of readme.md (row 2 after collapse, x=4)
        Mouse(term, "\x1b[<0;5;3M\x1b[<0;5;3m");
        app.RunFrame();
        Assert.Equal("readme.md", tv.SelectedNode?.Label);
    }

    [Fact]
    public void Tree_EnterOnLeafActivates()
    {
        var tv = MakeTree();
        var (app, term, _) = MakeApp(UI.Column(0, tv));
        app.RunFrame();
        app.Focus(tv);

        TreeNode? activated = null;
        tv.Activated += n => activated = n;

        for (int i = 0; i < 4; i++) Mouse(term, "\x1b[B"); // Down ×4 → readme.md
        term.QueueInput((byte)'\r');
        app.RunFrame();

        Assert.Equal("readme.md", activated?.Label);
    }

    // ---- Context menu --------------------------------------------------------

    private sealed record MenuMsg(string What);

    [Fact]
    public void RightClick_OpensMenu_EnterCommitsMessage()
    {
        var probe = new ParserFuzzProbe
        {
            MenuItems =
            [
                new MenuItem("alpha", Msg: new MenuMsg("alpha")),
                new MenuItem("off", Enabled: false),
                new MenuItem("beta", Msg: new MenuMsg("beta")),
            ],
        };
        probe.Node.Height = 5;
        MenuMsg? got = null;
        var root = UI.Column(0, probe);
        var (app, term, _) = MakeApp(root);
        app.OnMessage<MenuMsg>(m => got = m);
        app.RunFrame();

        Mouse(term, "\x1b[<2;5;3M"); // right press
        app.RunFrame();
        Assert.Null(app.Overlay);   // armed, not open yet
        Mouse(term, "\x1b[<2;5;3m"); // right release, no movement
        app.RunFrame();

        var menu = Assert.IsType<ContextMenuPopup>(app.Overlay);
        Assert.True(BackContains(app, "alpha"));

        term.QueueInput((byte)'\r'); // Enter commits the cursor item
        app.RunFrame();
        Assert.Equal("alpha", got?.What);
        Assert.Null(app.Overlay);
        Assert.NotNull(menu);
    }

    [Fact]
    public void Menu_DownSkipsDisabled_EscCloses()
    {
        var probe = new ParserFuzzProbe
        {
            MenuItems =
            [
                new MenuItem("one", Msg: new MenuMsg("one")),
                new MenuItem("off", Enabled: false),
                new MenuItem("three", Msg: new MenuMsg("three")),
            ],
        };
        probe.Node.Height = 5;
        MenuMsg? got = null;
        var (app, term, _) = MakeApp(UI.Column(0, probe));
        app.OnMessage<MenuMsg>(m => got = m);
        app.RunFrame();

        Mouse(term, "\x1b[<2;5;3M\x1b[<2;5;3m");
        app.RunFrame();
        Assert.IsType<ContextMenuPopup>(app.Overlay);

        Mouse(term, "\x1b[B"); // Down: skips the disabled row
        Mouse(term, "\x1b[B"); // Down again: clamps at 'three'
        term.QueueInput((byte)'\r');
        app.RunFrame();
        Assert.Equal("three", got?.What);

        // reopen and Escape out
        Mouse(term, "\x1b[<2;5;3M\x1b[<2;5;3m");
        app.RunFrame();
        Assert.IsType<ContextMenuPopup>(app.Overlay);
        term.QueueInput((byte)0x1b);
        app.RunFrame();                            // Esc still ambiguous...
        Thread.Sleep(KeyParser.EscSettleMs + 20);
        app.RunFrame();                            // ...settled → closes
        Assert.Null(app.Overlay);
    }

    [Fact]
    public void RightClick_OutsideTheMenuClosesIt()
    {
        var probe = new ParserFuzzProbe
        {
            MenuItems = [new MenuItem("only")],
        };
        probe.Node.Height = 3;
        probe.Node.Width = 10;
        var (app, term, _) = MakeApp(UI.Column(0, probe), w: 40, h: 12);
        app.RunFrame();

        Mouse(term, "\x1b[<2;5;2M\x1b[<2;5;2m");
        app.RunFrame();
        Assert.IsType<ContextMenuPopup>(app.Overlay);

        Mouse(term, "\x1b[<0;30;10M"); // left press far outside
        app.RunFrame();
        Assert.Null(app.Overlay); // OutsideClickCloses did its job
    }

    [Fact]
    public void RightDrag_OnResizableWithMenu_ResizesAndOpensNoMenu()
    {
        var probe = new ParserFuzzProbe
        {
            Resizable = true,
            MenuItems = [new MenuItem("never")],
        };
        probe.Node.Height = 5;
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(probe.Node);
        var (app, term, _) = MakeApp(root, w: 20, h: 10);
        app.RunFrame();
        int w0 = probe.Rect.W;

        Mouse(term, "\x1b[<2;16;3M");  // right press inside
        app.RunFrame();
        Mouse(term, "\x1b[<34;12;3M"); // drag → resize commits, menu dies
        app.RunFrame();
        Mouse(term, "\x1b[<2;12;3m");
        app.RunFrame();

        Assert.NotEqual(w0, probe.Rect.W);
        Assert.Null(app.Overlay);
    }

    [Fact]
    public void RightClick_WithoutMenuItems_DoesNothing()
    {
        var probe = new ParserFuzzProbe();
        probe.Node.Height = 5;
        var (app, term, _) = MakeApp(UI.Column(0, probe));
        app.RunFrame();

        Mouse(term, "\x1b[<2;5;3M\x1b[<2;5;3m");
        app.RunFrame();
        Assert.Null(app.Overlay);
    }

    [Fact]
    public void MenuItems_InheritFromAncestors()
    {
        var child = new ParserFuzzProbe();
        child.Node.Height = 3;
        var parent = new Container { MenuItems = [new MenuItem("inherited", Msg: new MenuMsg("i"))] };
        parent.AddChild(child);
        parent.Node.Grow = 1;
        MenuMsg? got = null;
        var (app, term, _) = MakeApp(UI.Column(0, parent));
        app.OnMessage<MenuMsg>(m => got = m);
        app.RunFrame();

        Mouse(term, "\x1b[<2;5;2M\x1b[<2;5;2m"); // right-click the CHILD
        app.RunFrame();
        Assert.IsType<ContextMenuPopup>(app.Overlay);
        term.QueueInput((byte)'\r');
        app.RunFrame();
        Assert.Equal("i", got?.What);
    }

    // ---- Tooltip --------------------------------------------------------------

    [Fact]
    public void Tooltip_ShowsAfterHoverDelay_PopsOnLeave()
    {
        var a = new ParserFuzzProbe { Tooltip = "hint text" };
        a.Node.Height = 4;
        a.Node.Width = 10;
        var b = new ParserFuzzProbe();
        b.Node.Height = 4;
        b.Node.Grow = 1;
        var root = new LayoutNode { Direction = FlexDirection.Row };
        root.Add(a.Node);
        root.Add(b.Node);
        var (app, term, clock) = MakeApp(root, w: 40, h: 8);
        app.TooltipDelayMs = 500;
        app.RunFrame();

        Mouse(term, "\x1b[<35;5;2M"); // hover onto a
        app.RunFrame();
        Assert.Null(app.Overlay);      // not yet — delay hasn't passed

        clock.Advance(TimeSpan.FromMilliseconds(600));
        app.RunFrame();
        app.RunFrame();
        Assert.IsType<TooltipBox>(app.Overlay);
        Assert.True(BackContains(app, "hint text"));

        Mouse(term, "\x1b[<35;30;2M"); // hover onto b (no tooltip)
        app.RunFrame();
        Assert.Null(app.Overlay);      // leaving popped it
    }

    [Fact]
    public void Tooltip_PressCancels_EvenBeforeDelay()
    {
        var a = new ParserFuzzProbe { Tooltip = "hint" };
        a.Node.Grow = 1;
        var (app, term, clock) = MakeApp(UI.Column(0, a), w: 20, h: 6);
        app.TooltipDelayMs = 500;
        app.RunFrame();

        Mouse(term, "\x1b[<35;5;3M"); // hover
        app.RunFrame();
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Mouse(term, "\x1b[<0;5;3M");  // press before the delay elapsed
        app.RunFrame();
        clock.Advance(TimeSpan.FromMilliseconds(600));
        app.RunFrame();
        app.RunFrame();
        Assert.Null(app.Overlay); // the armed timer was cancelled by the press
    }

    [Fact]
    public void Tooltip_MotionHidesAndRearms_SteadyPointerBringsItBack()
    {
        var a = new ParserFuzzProbe { Tooltip = "sticky" };
        a.Node.Grow = 1;
        var (app, term, clock) = MakeApp(UI.Column(0, a), w: 20, h: 6);
        app.TooltipDelayMs = 300;
        app.RunFrame();

        Mouse(term, "\x1b[<35;5;3M");
        app.RunFrame();
        clock.Advance(TimeSpan.FromMilliseconds(400));
        app.RunFrame();
        app.RunFrame();
        Assert.IsType<TooltipBox>(app.Overlay);

        // moving kills the shown box immediately…
        Mouse(term, "\x1b[<35;6;3M");
        app.RunFrame();
        Assert.Null(app.Overlay);

        // …and only a STILL pointer for the full delay brings it back
        clock.Advance(TimeSpan.FromMilliseconds(150));
        app.RunFrame();
        Assert.Null(app.Overlay);          // still inside the new delay
        clock.Advance(TimeSpan.FromMilliseconds(300));
        app.RunFrame();
        app.RunFrame();
        Assert.IsType<TooltipBox>(app.Overlay);
    }

    [Fact]
    public void Tooltip_ContinuousMotion_NeverShows()
    {
        var a = new ParserFuzzProbe { Tooltip = "never while sweeping" };
        a.Node.Grow = 1;
        var (app, term, clock) = MakeApp(UI.Column(0, a), w: 20, h: 6);
        app.TooltipDelayMs = 300;
        app.RunFrame();

        // sweep: a motion every 200ms of virtual time — always inside the
        // delay window, so the box must never appear
        for (int x = 3; x <= 9; x++)
        {
            Mouse(term, $"\x1b[<35;{x};3M");
            app.RunFrame();
            clock.Advance(TimeSpan.FromMilliseconds(200));
            app.RunFrame();
            Assert.Null(app.Overlay);
        }
    }

    [Fact]
    public void Tooltip_ContainersNeverShowTheirOwnHint()
    {
        // hovering the panel (its frame chrome) must not fire the panel's
        // tooltip — the hint belongs to the elements inside
        var inner = new ParserFuzzProbe { Tooltip = "inner hint" };
        inner.Node.Grow = 1;
        var panel = new Panel("box") { Tooltip = "container hint" };
        panel.AddChild(inner);
        panel.Node.Grow = 1;
        var (app, term, clock) = MakeApp(UI.Column(0, panel), w: 20, h: 8);
        app.TooltipDelayMs = 300;
        app.RunFrame();

        Mouse(term, "\x1b[<35;1;1M"); // the panel's top-left border cell
        app.RunFrame();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        app.RunFrame();
        app.RunFrame();
        Assert.Null(app.Overlay);        // container hint suppressed

        Mouse(term, "\x1b[<35;5;4M"); // inside: the inner element
        app.RunFrame();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        app.RunFrame();
        app.RunFrame();
        var tip = Assert.IsType<TooltipBox>(app.Overlay);
        Assert.Same(inner, tip.Owner);
    }

    // ---- RadioGroup / ToggleGroup ----------------------------------------------

    [Fact]
    public void Radio_ClickSelects_ArrowsMoveSelection()
    {
        var sel = new Signal<int>(0);
        var radio = new RadioGroup(sel, "red", "green", "blue");
        var (app, term, _) = MakeApp(UI.Column(0, radio));
        app.RunFrame();
        Assert.True(BackContains(app, "(•) red"));

        Mouse(term, "\x1b[<0;3;3M\x1b[<0;3;3m"); // click row 2 ('blue')
        app.RunFrame();
        Assert.Equal(2, sel.Value);

        app.Focus(radio);
        Mouse(term, "\x1b[A"); // Up
        app.RunFrame();
        Assert.Equal(1, sel.Value);
        Assert.True(BackContains(app, "(•) green"));
    }

    [Fact]
    public void Toggle_ClickTogglesChips_SpaceTogglesCursor()
    {
        var on = new Signal<HashSet<int>>([]);
        var tg = new ToggleGroup(on, "aa", "bb");
        var (app, term, _) = MakeApp(UI.Column(0, tg));
        app.RunFrame();

        // chips: ' aa ' x0..3, sep x4, ' bb ' x5..8 → click x=6
        Mouse(term, "\x1b[<0;7;1M\x1b[<0;7;1m");
        app.RunFrame();
        Assert.Contains(1, on.Value);
        Assert.DoesNotContain(0, on.Value);

        app.Focus(tg);
        Mouse(term, "\x1b[D");        // Left: click had moved the cursor to chip 1
        term.QueueInput((byte)' ');   // Space toggles the cursor chip (0)
        app.RunFrame();
        Assert.Contains(0, on.Value);

        term.QueueInput((byte)' '); // and off again
        app.RunFrame();
        Assert.DoesNotContain(0, on.Value);
    }
}

public class DoubleClickTests
{
    private sealed class DblProbe : Widget
    {
        public int Doubles;
        public Point LastLocal;
        public override void OnDoubleClick(Point local)
        {
            Doubles++;
            LastLocal = local;
        }
        public override void Render(CellBuffer buffer) { }
    }

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
    public void SecondClickWithinWindow_FiresOnce_SlowPairDoesNot()
    {
        var probe = new DblProbe { Clickable = true };
        probe.Node.Height = 3;
        var (app, term, clock) = MakeApp(UI.Column(0, probe));
        app.RunFrame();

        Mouse(term, "\x1b[<0;5;2M\x1b[<0;5;2m"); // click 1
        app.RunFrame();
        Mouse(term, "\x1b[<0;5;2M\x1b[<0;5;2m"); // click 2 → double
        app.RunFrame();
        Assert.Equal(1, probe.Doubles);
        Assert.Equal(new Point(4, 1), probe.LastLocal);

        clock.Advance(TimeSpan.FromSeconds(2));   // past the window
        Mouse(term, "\x1b[<0;5;2M\x1b[<0;5;2m"); // slow single
        app.RunFrame();
        Assert.Equal(1, probe.Doubles);
    }

    [Fact]
    public void ClicksFarApart_OnOneSingleElementWidget_AreADouble()
    {
        // a button is ONE element: double-clicking it anywhere on its
        // face is a double-click (desktop semantics). Element granularity
        // is what data widgets override — see TreeMultiSelectTests.
        var probe = new DblProbe { Clickable = true };
        probe.Node.Height = 3;
        probe.Node.Width = 30;
        var (app, term, _) = MakeApp(UI.Column(0, probe));
        app.RunFrame();

        Mouse(term, "\x1b[<0;5;2M\x1b[<0;5;2m");
        app.RunFrame();
        Mouse(term, "\x1b[<0;25;2M\x1b[<0;25;2m");
        app.RunFrame();
        Assert.Equal(1, probe.Doubles);
    }

    [Fact]
    public void Table_DoubleClick_ActivatesTheCursorCell()
    {
        var table = VocabularyTests.MakeTablePublic(5);
        (int Row, int Col)? act = null;
        table.Activated += (r, c) => act = (r, c);
        var (app, term, _) = MakeApp(UI.Column(0, table));
        app.RunFrame();

        // header y0-1, body from y2: SGR y3 = row 0; x6 lands in col 0
        Mouse(term, "\x1b[<0;6;3M\x1b[<0;6;3m");
        app.RunFrame();
        Mouse(term, "\x1b[<0;6;3M\x1b[<0;6;3m");
        app.RunFrame();
        Assert.Equal((0, 0), act);
    }

    [Fact]
    public void Tree_DoubleClickOnLeaf_Activates_ParentDoesNot()
    {
        var tv = VocabularyTests.MakeTreePublic();
        TreeNode? act = null;
        tv.Activated += n => act = n;
        var (app, term, _) = MakeApp(UI.Column(0, tv));
        app.RunFrame();

        // flat: proj0 src1 main.cs2 lib.cs3 readme.md4 → SGR y5
        Mouse(term, "\x1b[<0;6;5M\x1b[<0;6;5m");
        app.RunFrame();
        Mouse(term, "\x1b[<0;6;5M\x1b[<0;6;5m");
        app.RunFrame();
        Assert.Equal("readme.md", act?.Label);

        // parent row (src, y2): double-click must NOT activate
        act = null;
        Mouse(term, "\x1b[<0;6;2M\x1b[<0;6;2m");
        app.RunFrame();
        Mouse(term, "\x1b[<0;6;2M\x1b[<0;6;2m");
        app.RunFrame();
        Assert.Null(act);
    }

    [Fact]
    public void ModalBody_ReceivesWheel_InsteadOfTheModal()
    {
        // regression: TopMostAt returned the overlay ROOT for any point in
        // its box, so a hosted body never saw a mouse event (no scrolling)
        var body = new ListView(() => 100);
        var modal = new Modal("t", () => [], dim: false) { Body = body, BodyRows = 6 };
        var (app, term, _) = MakeApp(UI.Column(0, new ParserFuzzProbe()), w: 40, h: 20);
        app.PushOverlay(modal);
        app.RunFrame(); // modal render hands the body its rect
        app.RunFrame();
        Assert.True(body.Rect.H > 0);

        var r = body.Rect;
        Mouse(term, $"\x1b[<65;{r.X + 3 + 1};{r.Y + 2 + 1}M"); // wheel down inside body
        app.RunFrame();
        Assert.Equal(3, body.Offset);
    }
}

public class TooltipPlacementTests
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
    public void Tooltip_HugsThePointer_FlipsAboveNearBottom_FollowsMotion()
    {
        var a = new ParserFuzzProbe { Tooltip = "hint text" };
        a.Node.Grow = 1; // fills the 40x8 screen: owner edges are useless anchors
        var (app, term, clock) = MakeApp(UI.Column(0, a), w: 40, h: 8);
        app.TooltipDelayMs = 300;
        app.RunFrame();

        Mouse(term, "\x1b[<35;6;3M"); // hover cell (5,2)
        app.RunFrame();
        clock.Advance(TimeSpan.FromMilliseconds(400));
        app.RunFrame();
        app.RunFrame();
        TooltipBox tip = Assert.IsType<TooltipBox>(app.Overlay);

        // one row BELOW the cursor: y=3; w = len+4 = 13; x = cursor x
        Assert.Equal(new Rect(5, 3, 13, 3), tip.Box);

        // motion over the same owner hides the box (stillness rule)…
        Mouse(term, "\x1b[<35;10;4M"); // cell (9,3)
        app.RunFrame();
        Assert.Null(app.Overlay);

        // …and after a still delay it returns AT THE NEW POINTER CELL
        clock.Advance(TimeSpan.FromMilliseconds(400));
        app.RunFrame();
        app.RunFrame();
        tip = Assert.IsType<TooltipBox>(app.Overlay);
        Assert.Equal(new Rect(9, 4, 13, 3), tip.Box);

        // near the bottom edge it flips above the cursor
        Mouse(term, "\x1b[<35;6;8M"); // cell (5,7): 7+1+3 > 8
        app.RunFrame();
        Assert.Null(app.Overlay);
        clock.Advance(TimeSpan.FromMilliseconds(400));
        app.RunFrame();
        app.RunFrame();
        tip = Assert.IsType<TooltipBox>(app.Overlay);
        Assert.Equal(new Rect(5, 4, 13, 3), tip.Box);
    }
}

public class ResizeAxisMaskTests
{
    [Fact]
    public void HorizontalOnly_MasksVerticalDrag_AndRestoresPreGrabSpecs()
    {
        // row root: height comes from cross-axis STRETCH (null spec), so
        // unpinning after the drag must hand the stretch back
        var probe = new ParserFuzzProbe
        {
            Resizable = true,
            ResizeAxes = ResizeAxis.Horizontal,
        };
        probe.Node.Width = 20;
        var root = new LayoutNode { Direction = FlexDirection.Row };
        root.Add(probe.Node);
        var term = new FakeTerminal(20, 10);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();
        Assert.Equal(10, probe.Rect.H); // stretched

        void Mouse(string seq)
        {
            foreach (byte b in System.Text.Encoding.ASCII.GetBytes(seq))
                term.QueueInput(b);
        }

        Mouse("\x1b[<2;17;3M");  // right-press inside, right of center
        app.RunFrame();
        Mouse("\x1b[<34;13;6M"); // drag left AND down (diagonal)
        app.RunFrame();
        Mouse("\x1b[<2;13;6m");
        app.RunFrame();

        Assert.Equal(16, probe.Rect.W); // horizontal moved: 20 - 4
        Assert.Equal(10, probe.Rect.H); // vertical masked: untouched
        Assert.Null(probe.Node.Height); // unpinned: stretch survives resizes
    }

    [Fact]
    public void HorizontalOnly_WithExplicitHeight_RestoresThatHeight()
    {
        var probe = new ParserFuzzProbe
        {
            Resizable = true,
            ResizeAxes = ResizeAxis.Horizontal,
        };
        probe.Node.Height = 5;
        probe.Node.Width = 20;
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(probe.Node);
        var term = new FakeTerminal(20, 10);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        void Mouse(string seq)
        {
            foreach (byte b in System.Text.Encoding.ASCII.GetBytes(seq))
                term.QueueInput(b);
        }

        Mouse("\x1b[<2;17;3M");
        app.RunFrame();
        Mouse("\x1b[<34;13;6M");
        app.RunFrame();
        Mouse("\x1b[<2;13;6m");
        app.RunFrame();

        Assert.Equal(16, probe.Rect.W);
        Assert.Equal(5, probe.Rect.H);   // masked axis never moved
        Assert.Equal(5, probe.Node.Height); // ...and its spec came back as-is
    }
}

public class TableSortTests
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
    public void HeaderDoubleClick_SortsAscThenDesc_PaintsIndicator_FiresEvent()
    {
        var table = new Table(() => 3,
            new TableColumn("name", Weight: 1),
            new TableColumn("size", Width: 6));
        table.Node.Grow = 1;
        var events = new List<(int Col, bool Desc)>();
        table.SortChanged += (c, d) => events.Add((c, d));
        var (app, term, _) = MakeApp(UI.Column(0, table));
        app.RunFrame();

        // 'size' header cell: starts at x33 → click x35,y0 → SGR (36,1)
        void Dbl()
        {
            Mouse(term, "\x1b[<0;36;1M\x1b[<0;36;1m");
            app.RunFrame();
            Mouse(term, "\x1b[<0;36;1M\x1b[<0;36;1m");
            app.RunFrame();
        }

        Dbl();
        Assert.Equal([(1, false)], events);
        Assert.True(BackContains(app, "size ▲"));

        Dbl();
        Assert.Equal([(1, false), (1, true)], events);
        Assert.True(BackContains(app, "size ▼"));

        table.SetSort(0, false); // programmatic
        Assert.Equal(3, events.Count);
        Assert.Equal((0, false), events[2]);
    }
}

public class TreeStageMarkTests
{
    [Fact]
    public void SelectingADirectory_MarksAllDescendantRows()
    {
        var root = new TreeNode("proj");
        var dir = new TreeNode("dir");
        dir.Add("x.cs");
        dir.Add("y.cs");
        root.Add(dir);
        var tv = new TreeView(root);
        tv.Node.Grow = 1;
        var term = new FakeTerminal(40, 12);
        using var app = new App(term, UI.Column(0, tv), targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        // click dir's label row (flat index 1 → y1 → SGR y2)
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;6;2M\x1b[<0;6;2m"))
            term.QueueInput(b);
        app.RunFrame();

        // cursor row (dir) is the accent bar; the two children carry the
        // ▌ edge marker — what's marked is exactly what would stage
        var cells = app.Back.Cells;
        int marks = 0;
        for (int i = 0; i < cells.Length; i++)
            if (cells[i].Char == '▌') marks++;
        Assert.Equal(2, marks);

        tv.ClearSelection();
        app.RunFrame();
        marks = 0;
        for (int i = 0; i < cells.Length; i++)
            if (cells[i].Char == '▌') marks++;
        Assert.Equal(0, marks);
    }
}
