using QxTui.Terminal;

namespace QxTui.Tests;

public class KeyParserTests
{
    private static List<KeyEvent> Feed(string bytes)
    {
        var parser = new KeyParser();
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes(bytes))
            parser.Feed(b);
        parser.Flush(); // a lone Esc is still ambiguous for EscSettleMs
        Thread.Sleep(KeyParser.EscSettleMs + 20);
        parser.Flush(); // settled -> keypress

        var events = new List<KeyEvent>();
        while (parser.TryReadKey(out var ev))
            events.Add(ev);
        return events;
    }

    [Fact]
    public void PlainChars()
    {
        var events = Feed("qw");
        Assert.Equal([KeyEvent.OfChar('q'), KeyEvent.OfChar('w')], events);
    }

    [Fact]
    public void Arrows_Csi()
    {
        var events = Feed("\x1b[A\x1b[B\x1b[C\x1b[D");
        Assert.Equal([Key.Up, Key.Down, Key.Right, Key.Left],
            events.Select(e => e.Key));
    }

    [Fact]
    public void Arrows_Ss3()
    {
        var events = Feed("\x1bOA\x1bOD");
        Assert.Equal([Key.Up, Key.Left], events.Select(e => e.Key));
    }

    [Fact]
    public void Enter_Space_Escape()
    {
        var events = Feed("\r \x1b");
        Assert.Equal([Key.Enter, Key.Space, Key.Escape], events.Select(e => e.Key));
    }

    [Fact]
    public void PartialSequence_BufferedUntilComplete()
    {
        var parser = new KeyParser();

        parser.Feed(0x1b);
        parser.Feed((byte)'[');
        Assert.False(parser.TryReadKey(out _)); // still incomplete

        parser.Feed((byte)'C');
        Assert.True(parser.TryReadKey(out var ev));
        Assert.Equal(Key.Right, ev.Key);
    }

    [Fact]
    public void LoneEsc_ThenChar_YieldsBoth()
    {
        var events = Feed("q");
        Assert.Equal([Key.Escape, Key.Char], events.Select(e => e.Key));
        Assert.Equal('q', events[1].Char);
    }

    [Fact]
    public void DeleteWithParams()
    {
        var events = Feed("\x1b[3~");
        Assert.Equal([Key.Delete], events.Select(e => e.Key));
    }

    [Fact]
    public void CtrlC_IsInterruptChar_NotPlainC()
    {
        // Ctrl-C must stay distinguishable from a typed 'c' now that raw
        // byte input is gone: OnKey handlers match '\u0003' for interrupt
        var events = Feed("\x3");
        Assert.Equal(KeyEvent.OfChar('\u0003'), events.Single());
    }
}
