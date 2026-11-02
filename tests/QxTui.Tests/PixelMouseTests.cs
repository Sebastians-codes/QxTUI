using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;
using QxTui.Widgets;

namespace QxTui.Tests;

public class PixelMouseTests
{
    [Fact]
    public void Parser_ReadsCellSizeReply_AndRawPixelCoords()
    {
        var p = new KeyParser();
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[16;10;20t"))
            p.Feed(b);
        Assert.Equal(10, p.CellPixW);
        Assert.Equal(20, p.CellPixH);

        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;296;106M"))
            p.Feed(b);
        Assert.True(p.TryReadMouse(out var ev));
        Assert.Equal(295, ev.Px); // raw pixels preserved
        Assert.Equal(105, ev.Py);
    }

    [Fact]
    public void PixelDrag_MinimumMovement_IsSubCell()
    {
        // 1000 rows over a 9-cell track: a CELL step is ~110 rows. With a
        // 20px cell, one mouse PIXEL must move ~5 rows — not 110.
        var list = new ListView(() => 1000)
        {
            RenderRow = (b, c, r, i, sel) =>
                b.WriteTextClipped(c, r.X, r.Y, $"row {i}", fg: 7),
        };
        var root = UI.Column(0, list.Grow(1));
        var term = new FakeTerminal(30, 10);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.SetCellPixelSize(10, 20); // 16t reply: 10x20 px cells
        app.RunFrame();

        void Mouse(string seq)
        {
            foreach (byte b in System.Text.Encoding.ASCII.GetBytes(seq))
                term.QueueInput(b);
        }

        // scrollbar col x=29 -> px 290..299; thumb at top rows px 0..19
        Mouse("\x1b[<0;296;6M");    // press thumb at py=5
        app.RunFrame();
        Assert.Equal(0, list.Offset);

        Mouse("\x1b[<32;296;7M");   // move ONE pixel down
        app.RunFrame();
        Settle.Run(app, 300);
        Assert.True(list.Offset > 0 && list.Offset <= 20,
            $"one pixel must be a fine step, got {list.Offset} (cell step would be ~110)");

        Mouse("\x1b[<32;296;26M");  // one full cell (20px): ~110 rows
        Settle.Run(app, 400);
        Assert.True(list.Offset >= 80, $"cell-scale move expected, got {list.Offset}");

        Mouse("\x1b[<0;296;26m");   // release
        app.RunFrame();
    }

    [Fact]
    public void WithoutCellSizeReply_PixelCoordsAreDropped()
    {
        // a terminal that never answers CSI 16 t must keep cell math —
        // mis-scaled pixel drags would pin the thumb to the top
        var list = new ListView(() => 1000);
        var root = UI.Column(0, list.Grow(1));
        var term = new FakeTerminal(30, 10);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<0;296;6M"))
            term.QueueInput(b);
        app.RunFrame();
        // raw 296/6 would be cell (296,6) without conversion: routing must
        // have used the raw-as-cell coords (off-widget press = no crash,
        // no capture) and px must not leak into widget math
        Assert.False(list.GetType()
            .GetField("_dragThumb", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(list) is true);
    }
}

public class CellSizeFallbackTests
{
    [Fact]
    public void Parser_ReadsWindowSizeReply()
    {
        var p = new KeyParser();
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[4;800;1600t"))
            p.Feed(b);
        Assert.Equal(1600, p.WinPixW);
        Assert.Equal(800, p.WinPixH);
    }

    [Fact]
    public void WindowReply_DerivesCellSize_When16tUnanswered()
    {
        var list = new ListView(() => 100);
        var root = UI.Column(0, list.Grow(1));
        var term = new FakeTerminal(80, 40); // 1600/80=20px cols, 800/40=20px rows
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[4;800;1600t"))
            term.QueueInput(b);
        app.RunFrame();

        Assert.Equal(20, app.CellPixelH);
        Assert.Equal(20, app.CellPixelW);
    }
}

public class SplitSequenceFlushTests
{
    [Fact]
    public void WheelSequence_SplitAcrossFrameEnd_DoesNotLeakDigitsAsKeys()
    {
        // the tab-teleport bug: end-of-frame flush reset the parser
        // mid-mouse-sequence; the remainder (";60;32M") typed its digits
        // as keypresses — a '3' in the coordinates switched tabs
        var p = new KeyParser();
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[<65;60;3"))
            p.Feed(b);
        Assert.False(p.HasPending); // mid-sequence is not flushable
        p.Flush();                  // frame ends here

        Assert.False(p.TryReadKey(out _), "no phantom Escape, no leaked digits");

        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("2M"))
            p.Feed(b);              // rest arrives with the next drain

        Assert.True(p.TryReadMouse(out var ev));
        Assert.Equal(MouseKind.WheelDown, ev.Kind);
        Assert.Equal(59, ev.X);
        Assert.Equal(31, ev.Y);
        Assert.False(p.TryReadKey(out _), "sequence bytes must never become keys");
    }

    [Fact]
    public void LoneEscape_StillFlushesAtFrameEnd()
    {
        var p = new KeyParser();
        p.Feed(0x1b);
        Assert.True(p.HasPending);
        p.Flush(); // still ambiguous (could introduce a sequence)
        Assert.False(p.TryReadKey(out _));
        Thread.Sleep(KeyParser.EscSettleMs + 20);
        p.Flush(); // settled lone Esc -> keypress
        Assert.True(p.TryReadKey(out var ev));
        Assert.Equal(Key.Escape, ev.Key);
    }
}

public class ModeReportParsingTests
{
    private static KeyParser Feed(string input)
    {
        var parser = new KeyParser();
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes(input)) parser.Feed(b);
        return parser;
    }

    [Theory]
    [InlineData(0, false)] // not recognised
    [InlineData(1, true)]  // set
    [InlineData(2, true)]  // reset: supported, just off
    [InlineData(3, true)]  // permanently set
    [InlineData(4, false)] // permanently reset: cannot be enabled
    public void PixelMouseReply_StatusDecidesSupport(int status, bool supported)
    {
        var parser = Feed($"\u001b[?1016;{status}$y");

        Assert.Equal(supported, parser.PixelMouseSupported);
    }

    [Fact]
    public void BeforeAnyReply_SupportIsUnknown() =>
        Assert.Null(new KeyParser().PixelMouseSupported);

    [Fact]
    public void ReplyForAnotherMode_IsIgnored()
    {
        var parser = Feed("\u001b[?2004;1$y");

        Assert.Null(parser.PixelMouseSupported);
    }

    [Fact]
    public void TheReplyNeverLeaksIntoTheKeyStream()
    {
        // the trailing 'y' used to escape the aborted sequence and arrive as a typed key
        var parser = Feed("\u001b[?1016;2$y");

        Assert.False(parser.TryReadKey(out var key), $"leaked {key}");
        Assert.False(parser.TryReadMouse(out _));
    }

    [Fact]
    public void AReplySplitAcrossReads_StillParses_AndDoesNotLeak()
    {
        var parser = new KeyParser();
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\u001b[?1016;2"))
            parser.Feed(b);
        Assert.False(parser.HasPending); // mid-sequence is not a lone ESC
        parser.Flush();                  // a frame boundary falls here
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes("$y"))
            parser.Feed(b);

        Assert.True(parser.PixelMouseSupported);
        Assert.False(parser.TryReadKey(out _));
    }

    [Fact]
    public void OtherIntermediateSequences_AreDropped_NotTypedAsKeys()
    {
        var parser = Feed("\u001b[0 q"); // cursor-style style sequence: 'q' must not become a key

        Assert.False(parser.TryReadKey(out var key), $"leaked {key}");
    }

    [Fact]
    public void KeysStillParseAfterAnIntermediateSequence()
    {
        var parser = Feed("\u001b[?1016;2$y\u001b[1;5A\u001b[B");

        Assert.True(parser.TryReadKey(out var up));
        Assert.Equal(Key.Up, up.Key);
        Assert.True(parser.TryReadKey(out var down));
        Assert.Equal(Key.Down, down.Key);
    }
}

public class PixelMouseCapabilityTests
{
    private const string Press = "\u001b[<0;{0};{1}M";

    private sealed class Probe : Widget
    {
        public readonly List<MouseEvent> Events = [];
        public Probe() => Clickable = true;
        public override bool OnMouse(MouseEvent ev)
        {
            Events.Add(ev);
            return true;
        }
        public override void Render(CellBuffer buffer) { }
    }

    private sealed class Rig
    {
        public readonly FakeTerminal Term = new(80, 40);
        public readonly App App;
        public readonly Probe Probe = new();

        public Rig()
        {
            Probe.Node.Grow = 1f;
            var root = new LayoutNode { Direction = FlexDirection.Column };
            root.Add(Probe);
            App = new App(Term, root, targetFps: 60, time: new VirtualTimeSource(), asyncPresenting: false);
            App.RunFrame();
        }

        public void Feed(string input)
        {
            foreach (byte b in System.Text.Encoding.ASCII.GetBytes(input)) Term.QueueInput(b);
            App.RunFrame();
        }

        /// <summary>A left press reported by the terminal at 1-based coordinates (whatever unit it uses).</summary>
        public MouseEvent PressAt(int x, int y)
        {
            Probe.Events.Clear();
            Feed(string.Format(Press, x, y));
            return Probe.Events.First(e => e.Kind == MouseKind.Press);
        }
    }

    // 1600x800 px over 80x40 cells = 20x20 px cells (the 14 t fallback some terminals need)
    private const string WindowSizeReply = "\u001b[4;800;1600t";
    private const string AskedForPixelMouse = "\u001b[?1016$p";
    private const string EnabledPixelMouse = "\u001b[?1016h";

    [Fact]
    public void AnsweringTheSizeQuery_DoesNotSwitchToPixelCoordinates_ItAsksFirst()
    {
        // Windows Terminal: answers the size query, then keeps sending CELL coordinates
        var rig = new Rig();

        rig.Feed(WindowSizeReply);

        Assert.Contains(AskedForPixelMouse, rig.Term.AllOutput);
        Assert.DoesNotContain(EnabledPixelMouse, rig.Term.AllOutput);

        var press = rig.PressAt(54, 37); // 1-based cell (54, 37)
        Assert.Equal((53, 36), (press.X, press.Y)); // NOT divided by the cell size: not (2, 2)
        Assert.Equal(-1, press.Px);                 // and no bogus pixel position
    }

    [Fact]
    public void TheSameForThe16tAnswer()
    {
        var rig = new Rig();

        rig.Feed("\u001b[16;10;20t");

        Assert.Contains(AskedForPixelMouse, rig.Term.AllOutput);
        Assert.DoesNotContain(EnabledPixelMouse, rig.Term.AllOutput);
        Assert.Equal((53, 36), (rig.PressAt(54, 37).X, rig.PressAt(54, 37).Y));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WhenTheTerminalConfirms_PixelModeIsEnabled_AndPositionsAreReadAsPixels(int status)
    {
        var rig = new Rig();
        rig.Feed(WindowSizeReply);

        rig.Feed($"\u001b[?1016;{status}$y");

        Assert.Contains(EnabledPixelMouse, rig.Term.AllOutput);
        var press = rig.PressAt(206, 106); // pixel (205, 105) with 20x20 px cells
        Assert.Equal((10, 5), (press.X, press.Y));
        Assert.Equal((205, 105), (press.Px, press.Py));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void WhenTheTerminalSaysNo_StaysInCells(int status)
    {
        var rig = new Rig();
        rig.Feed(WindowSizeReply);

        rig.Feed($"\u001b[?1016;{status}$y");

        Assert.DoesNotContain(EnabledPixelMouse, rig.Term.AllOutput);
        var press = rig.PressAt(54, 37);
        Assert.Equal((53, 36), (press.X, press.Y));
    }

    [Fact]
    public void AterminalThatNeverAnswers_StaysInCells()
    {
        var rig = new Rig();
        rig.Feed(WindowSizeReply);
        for (int i = 0; i < 5; i++) rig.App.RunFrame();

        Assert.DoesNotContain(EnabledPixelMouse, rig.Term.AllOutput);
        Assert.Equal((53, 36), (rig.PressAt(54, 37).X, rig.PressAt(54, 37).Y));
    }

    [Fact]
    public void ANoReplyBeforeAnySizeIsKnown_ChangesNothing()
    {
        var rig = new Rig();

        rig.Feed("\u001b[?1016;2$y"); // an answer with no cell size to divide by

        Assert.DoesNotContain(EnabledPixelMouse, rig.Term.AllOutput);
        Assert.Equal((10, 5), (rig.PressAt(11, 6).X, rig.PressAt(11, 6).Y));
    }

    [Fact]
    public void TheQueryIsSentOnce()
    {
        var rig = new Rig();

        rig.Feed(WindowSizeReply);
        for (int i = 0; i < 5; i++) rig.App.RunFrame();

        string output = rig.Term.AllOutput;
        Assert.Equal(1, output.Split(AskedForPixelMouse).Length - 1);
    }
}
