using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;
using QxTui.Widgets;

namespace QxTui.Tests;

public class TextFieldTests
{
    private static (App app, FakeTerminal term, TextFieldWidget field) Make(string initial = "")
    {
        var field = new TextFieldWidget(initial);
        var root = UI.Column(0, field.Height(1));
        var term = new FakeTerminal(20, 4);
        var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.Focus(field);
        app.RunFrame();
        return (app, term, field);
    }

    private static void Type(FakeTerminal term, string s)
    {
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes(s))
            term.QueueInput(b);
    }

    [Fact]
    public void Typing_And_Backspace()
    {
        var (app, term, field) = Make();
        Type(term, "hello");
        app.RunFrame();
        Assert.Equal("hello", field.Text);

        term.QueueInput(0x7f); // backspace
        app.RunFrame();
        Assert.Equal("hell", field.Text);
    }

    [Fact]
    public void Arrows_MoveCursor_InsertInMiddle()
    {
        var (app, term, field) = Make("ac");
        // cursor at end (2); left once → 1
        Type(term, "\x1b[D");
        Type(term, "b");
        app.RunFrame();
        Assert.Equal("abc", field.Text);
        Assert.Equal(2, field.Cursor);
    }

    [Fact]
    public void Tab_IsNotConsumed_FocusMovesOn()
    {
        var field = new TextFieldWidget();
        var other = new ListView(() => 0);
        var root = UI.Column(0, field.Height(1), other.Grow(1));
        var term = new FakeTerminal(20, 6);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.Focus(field);
        app.RunFrame();

        term.QueueInput((byte)'\t');
        app.RunFrame();

        Assert.Same(other, app.Focused); // tab passed through the field
        Assert.Equal("", field.Text);
    }

    [Fact]
    public void Enter_FiresSubmitted_AndIsConsumed()
    {
        var (app, term, field) = Make();
        string? got = null;
        field.Submitted += t => got = t;

        Type(term, "go");
        app.RunFrame();
        term.QueueInput((byte)'\r');
        app.RunFrame();

        Assert.Equal("go", got);
    }

    [Fact]
    public void Changed_FiresPerEdit()
    {
        var (app, term, field) = Make();
        int hits = 0;
        field.Changed += _ => hits++;

        Type(term, "ab");
        app.RunFrame();
        Assert.Equal(2, hits);
    }

    [Fact]
    public void LongText_ScrollsWindowAroundCursor()
    {
        var (app, term, field) = Make();
        Type(term, "012345678901234567890123456789"); // 30 chars, field 18 inner
        app.RunFrame();

        // cursor at end must be visible: view starts after 0
        Assert.Equal(30, field.Cursor);
        // inner width 18, view starts at 13 → last char lands at x=17
        Assert.Equal('9', app.Back[17, 0].Char);
        Assert.Equal('3', app.Back[1, 0].Char); // view start = chars[13]
    }
}
