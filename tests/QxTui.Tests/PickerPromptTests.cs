using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Terminal;
using QxTui.Widgets;

namespace QxTui.Tests;

/// <summary>The generic floating choice/text overlays: Picker (centered
/// single-choice list, optional search) and Prompt (one-line input).
/// Both swallow keys while open and close on Esc / outside press.</summary>
public class PickerPromptTests
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
    public void Picker_NavigatesCommitsIndex_EscClosesWithoutCommit()
    {
        int committed = -1;
        var (app, term, _) = MakeApp(UI.Column(0, new Label(() => "base")));
        app.PushOverlay(new Picker("pick one", () => ["one", "two", "three"],
            (i, _) => { committed = i; app.PopOverlay(); }));
        app.RunFrame();
        Assert.True(BackContains(app, "two"));

        Mouse(term, "\x1b[B");        // Down → cursor 1
        term.QueueInput((byte)'\r');  // Enter commits index 1 and closes
        app.RunFrame();
        Assert.Equal(1, committed);
        Assert.Null(app.Overlay);

        app.PushOverlay(new Picker("pick one", () => ["one", "two"],
            (i, _) => { committed = i; app.PopOverlay(); }));
        app.RunFrame();
        term.QueueInput((byte)0x1b);
        app.RunFrame();
        Thread.Sleep(KeyParser.EscSettleMs + 20);
        app.RunFrame();
        Assert.Null(app.Overlay);
        Assert.Equal(1, committed); // Esc committed nothing
    }

    [Fact]
    public void Picker_ExtraKeysReachTheHook_WithoutClosing()
    {
        char gotC = '\0';
        int gotI = -1;
        var (app, term, _) = MakeApp(UI.Column(0, new Label(() => "base")));
        app.PushOverlay(new Picker("pick", () => ["a", "b"],
            (_, _) => app.PopOverlay(),
            (c, i, q) => { gotC = c; gotI = i; Assert.Equal("", q); }));
        app.RunFrame();

        Mouse(term, "\x1b[B");   // cursor → 1
        term.QueueInput((byte)'x');
        app.RunFrame();

        Assert.Equal('x', gotC);
        Assert.Equal(1, gotI);
        Assert.IsType<Picker>(app.Overlay); // extra keys keep it open
    }

    [Fact]
    public void Prompt_TypesWithoutFocus_SubmitsOnEnter_EscCancels()
    {
        string? got = null;
        var (app, term, _) = MakeApp(UI.Column(0, new Label(() => "base")));
        app.PushOverlay(new Prompt("message", "type…", s => got = s));
        app.RunFrame();

        foreach (byte b in "abc"u8) term.QueueInput(b); // no app focus needed
        app.RunFrame();
        term.QueueInput((byte)'\r');
        app.RunFrame();
        Assert.Equal("abc", got);
        Assert.Null(app.Overlay);

        app.PushOverlay(new Prompt("message", "type…", s => got = s));
        app.RunFrame();
        term.QueueInput((byte)'z');
        app.RunFrame();
        term.QueueInput((byte)0x1b);
        app.RunFrame();
        Thread.Sleep(KeyParser.EscSettleMs + 20);
        app.RunFrame();
        Assert.Null(app.Overlay);
        Assert.Equal("abc", got); // cancelled submit changed nothing
    }
}

public class PickerSearchTests
{
    private static (App app, FakeTerminal term, VirtualTimeSource clock) MakeApp(
        LayoutNode root, int w = 40, int h = 12)
    {
        var term = new FakeTerminal(w, h);
        var clock = new VirtualTimeSource();
        var app = new App(term, root, targetFps: 60, time: clock, asyncPresenting: false);
        return (app, term, clock);
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
    public void Search_FiltersLines_EnterCommitsFilteredIndex()
    {
        string[] all = ["alpha one", "beta two", "alpha three"];
        int committed = -1;
        string committedQuery = "";
        var (app, term, _) = MakeApp(UI.Column(0, new Label(() => "x")));
        app.PushOverlay(new Picker("find",
            () => all,
            (i, q) => { committed = i; committedQuery = q; app.PopOverlay(); },
            search: q => q.Length == 0
                ? all
                : [.. all.Where(l => l.Contains(q, StringComparison.OrdinalIgnoreCase))]));
        app.RunFrame();

        foreach (byte b in "alpha"u8) term.QueueInput(b); // type the query
        app.RunFrame();
        Assert.True(BackContains(app, "alpha one"));
        Assert.False(BackContains(app, "beta two"));

        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[B"))
            term.QueueInput(b);          // Down → second filtered line
        term.QueueInput((byte)'\r');
        app.RunFrame();
        Assert.Equal(1, committed);       // index into the FILTERED list
        Assert.Equal("alpha", committedQuery);
    }

    [Fact]
    public void Search_NoMatch_EnterCallsOnCreate_WithTheQuery()
    {
        string? created = null;
        var (app, term, _) = MakeApp(UI.Column(0, new Label(() => "x")));
        app.PushOverlay(new Picker("find",
            () => ["existing"],
            (_, _) => app.PopOverlay(),
            search: q => q.Length == 0
                ? ["existing"]
                : [.. new[] { "existing" }.Where(l => l.Contains(q, StringComparison.OrdinalIgnoreCase))],
            onCreate: name => { created = name; app.PopOverlay(); }));
        app.RunFrame();

        foreach (byte b in "brand-new"u8) term.QueueInput(b);
        app.RunFrame();
        term.QueueInput((byte)'\r');     // no match → create what was typed
        app.RunFrame();
        Assert.Equal("brand-new", created);
    }
}

public class PickerCtrlKeyTests
{
    [Fact]
    public void CtrlChar_ReachesExtraKey_EvenWhileSearching_PrintableDoesNot()
    {
        string[] all = ["main", "feature"];
        char gotC = '\0';
        string gotQ = "";
        var term = new FakeTerminal(40, 12);
        using var app = new App(term, UI.Column(0, new Label(() => "x")),
            targetFps: 60, asyncPresenting: false);
        app.PushOverlay(new Picker("branches",
            () => all,
            (_, _) => app.PopOverlay(),
            (c, i, q) => { gotC = c; gotQ = q; },
            search: q => q.Length == 0
                ? all
                : [.. all.Where(l => l.Contains(q, StringComparison.OrdinalIgnoreCase))]));
        app.RunFrame();

        foreach (byte b in "ma"u8) term.QueueInput(b); // query = "ma"
        app.RunFrame();
        term.QueueInput((byte)'d');                    // printable → query
        app.RunFrame();
        Assert.Equal('\0', gotC);                     // …never reaches extraKey

        term.QueueInput((byte)0x04);                   // ctrl-d → command
        app.RunFrame();
        Assert.Equal('\x04', gotC);
        Assert.Equal("mad", gotQ);                     // query saw the 'd', not the ^d
    }
}
