using System.Text;
using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;
using QxTui.Widgets;
using Xunit;

namespace QxTui.Tests;

public class TextViewTests
{
    /// <summary>A view filling a w×h screen. Text starts at column 1 and is w-2 cells wide.</summary>
    private sealed class Rig
    {
        public readonly List<string> Lines;
        public long Trimmed;
        public readonly TextView View;
        public readonly LayoutNode Root = new() { Direction = FlexDirection.Column };
        private int _w, _h;

        public Rig(int w, int h, params string[] lines)
        {
            _w = w;
            _h = h;
            Lines = [.. lines];
            View = new TextView(() => Lines.Count, i => Lines[i], () => Trimmed);
            View.Node.Grow = 1f;
            Root.Add(View);
            Layout();
        }

        public void Layout() => LayoutNode.Compute(Root, _w, _h);

        public void Resize(int w, int h)
        {
            (_w, _h) = (w, h);
            Layout();
        }

        public CellBuffer Paint()
        {
            var buffer = new CellBuffer(_w, _h);
            View.Render(buffer);
            return buffer;
        }

        /// <summary>Screen rows, text area only (no padding or scrollbar column), right-trimmed.</summary>
        public string[] Screen()
        {
            var buffer = Paint();
            var rows = new string[_h];
            for (int y = 0; y < _h; y++)
            {
                var row = new StringBuilder();
                for (int x = 1; x < _w - 1; x++)
                    row.Append(buffer[x, y].Char == '\0' ? ' ' : buffer[x, y].Char);
                rows[y] = row.ToString().TrimEnd();
            }
            return rows;
        }

        public string ScrollbarColumn()
        {
            var buffer = Paint();
            var column = new StringBuilder();
            for (int y = 0; y < _h; y++) column.Append(buffer[_w - 1, y].Char);
            return column.ToString();
        }

        public bool Mouse(MouseKind kind, int x, int y, int button = 0, bool shift = false) =>
            View.OnMouse(new MouseEvent(x, y, button, kind, Shift: shift));

        public void Drag(int x0, int y0, int x1, int y1)
        {
            Paint(); // input is handled against what was last rendered
            Mouse(MouseKind.Press, x0, y0);
            Mouse(MouseKind.Motion, x1, y1, 0);
            Mouse(MouseKind.Release, x1, y1);
        }

        public void Key(Key key) => View.HandleKey(KeyEvent.Of(key));
    }

    private static string Numbered(int count, string prefix = "line") =>
        string.Join("\n", Enumerable.Range(0, count).Select(i => $"{prefix}{i}"));

    private static string[] NumberedLines(int count, string prefix = "line") =>
        [.. Enumerable.Range(0, count).Select(i => $"{prefix}{i}")];

    #region Wrapping

    [Fact]
    public void LongLine_WrapsAtTheViewWidth()
    {
        var rig = new Rig(12, 6, "abcdefghijklmnopqrstuvwxyz");

        Assert.Equal(3, rig.View.TotalRows);
        Assert.Equal(["abcdefghij", "klmnopqrst", "uvwxyz", "", "", ""], rig.Screen());
    }

    [Fact]
    public void ShortAndEmptyLines_TakeOneRowEach()
    {
        var rig = new Rig(12, 5, "one", "", "three");

        Assert.Equal(3, rig.View.TotalRows);
        Assert.Equal(["one", "", "three", "", ""], rig.Screen());
    }

    [Fact]
    public void ResizingRewraps()
    {
        var rig = new Rig(12, 8, "abcdefghijklmnopqrst");
        Assert.Equal(2, rig.View.TotalRows);

        rig.Resize(7, 8); // 5 cells of text
        Assert.Equal(4, rig.View.TotalRows);
        Assert.Equal("abcde", rig.Screen()[0]);

        rig.Resize(30, 8);
        Assert.Equal(1, rig.View.TotalRows);
    }

    [Fact]
    public void WideCharacters_NeverSplitAcrossRows()
    {
        var rig = new Rig(7, 6, "日本語日本"); // 5 text cells: two wide chars fit, the third starts row 2

        // a wide character fills two cells; the second is a blank continuation cell
        Assert.Equal(["日本", "語日", "本", "", "", ""], rig.Screen().Select(r => r.Replace(" ", "")).ToArray());
        Assert.Equal(3, rig.View.TotalRows);
    }

    #endregion

    #region No-wrap and sideways scrolling

    [Fact]
    public void NoWrap_ClipsAndScrollsSideways()
    {
        var rig = new Rig(12, 3, "abcdefghijklmnopqrstuvwxyz");
        rig.View.Wrap = false;

        Assert.Equal(1, rig.View.TotalRows);
        Assert.Equal("abcdefghij", rig.Screen()[0]);

        rig.Key(Key.Right);
        Assert.Equal(8, rig.View.OffsetX);
        Assert.Equal("ijklmnopqr", rig.Screen()[0]);

        rig.Key(Key.Left);
        Assert.Equal(0, rig.View.OffsetX);
    }

    [Fact]
    public void NoWrap_SidewaysScrollStopsAtTheLongestLine()
    {
        var rig = new Rig(12, 3, "abcdefghijklmnopqrstuvwxyz"); // 26 wide, 10 visible
        rig.View.Wrap = false;
        rig.Paint();

        for (int i = 0; i < 10; i++) rig.Key(Key.Right);

        Assert.Equal(16, rig.View.OffsetX);
        Assert.Equal("qrstuvwxyz", rig.Screen()[0]);
    }

    [Fact]
    public void Wrap_IgnoresSidewaysKeysAndWheel()
    {
        var rig = new Rig(12, 3, "abcdefghijklmnopqrstuvwxyz");

        rig.Key(Key.Right);
        rig.Mouse(MouseKind.WheelDown, 3, 1, shift: true);

        Assert.Equal(0, rig.View.OffsetX);
    }

    [Fact]
    public void NoWrap_ShiftWheelAndSidewaysWheelScrollColumns()
    {
        var rig = new Rig(12, 3, "abcdefghijklmnopqrstuvwxyz");
        rig.View.Wrap = false;
        rig.Paint();

        rig.Mouse(MouseKind.WheelDown, 3, 1, shift: true);
        Assert.True(rig.View.OffsetX > 0);
        rig.Mouse(MouseKind.WheelLeft, 3, 1);
        Assert.Equal(0, rig.View.OffsetX);
        rig.Mouse(MouseKind.WheelRight, 3, 1);
        Assert.True(rig.View.OffsetX > 0);
    }

    #endregion

    #region Vertical scrolling, follow-tail, scrollbar

    [Fact]
    public void StaticText_StartsAtTheTop_AndScrollsByRows()
    {
        var rig = new Rig(20, 5, NumberedLines(50));

        Assert.Equal("line0", rig.Screen()[0]);
        rig.Key(Key.Down);
        Assert.Equal("line1", rig.Screen()[0]);
        rig.Key(Key.PageDown);
        Assert.Equal("line6", rig.Screen()[0]);
        rig.Key(Key.PageUp);
        Assert.Equal("line1", rig.Screen()[0]);
    }

    [Fact]
    public void Scrolling_ClampsAtBothEnds()
    {
        var rig = new Rig(20, 5, NumberedLines(10));
        rig.Paint();

        rig.Key(Key.Up);
        Assert.Equal(0, rig.View.Offset);

        rig.Key(Key.End);
        Assert.Equal(5, rig.View.Offset);
        rig.Key(Key.Down);
        Assert.Equal(5, rig.View.Offset);

        rig.Key(Key.Home);
        Assert.Equal(0, rig.View.Offset);
    }

    [Fact]
    public void Wheel_ScrollsThreeRows()
    {
        var rig = new Rig(20, 5, NumberedLines(50));
        rig.Paint();

        rig.Mouse(MouseKind.WheelDown, 3, 2);
        Assert.Equal(3, rig.View.Offset);
        rig.Mouse(MouseKind.WheelUp, 3, 2);
        Assert.Equal(0, rig.View.Offset);
    }

    [Fact]
    public void FollowTail_PinsToTheNewestLine_UntilTheUserScrollsUp()
    {
        var rig = new Rig(20, 5, NumberedLines(30));
        rig.View.FollowTail = true;

        Assert.Equal("line29", rig.Screen()[4]);
        Assert.True(rig.View.Following);

        rig.Lines.AddRange(NumberedLines(5, "new"));
        Assert.Equal("new4", rig.Screen()[4]); // followed

        rig.Key(Key.Up);
        rig.Key(Key.Up);
        Assert.False(rig.View.Following);
        string held = rig.Screen()[0];

        rig.Lines.AddRange(NumberedLines(5, "more"));
        Assert.Equal(held, rig.Screen()[0]); // reading history: the view stays put

        rig.Key(Key.End);
        Assert.True(rig.View.Following);
        Assert.Equal("more4", rig.Screen()[4]);
    }

    [Fact]
    public void FollowTail_ScrollingBackToTheBottom_PinsAgain()
    {
        var rig = new Rig(20, 5, NumberedLines(30));
        rig.View.FollowTail = true;
        rig.Paint();

        rig.Key(Key.Up);
        Assert.False(rig.View.Following);
        rig.Key(Key.Down);
        Assert.True(rig.View.Following);
    }

    [Fact]
    public void WithoutFollowTail_NewLinesDoNotMoveTheView()
    {
        var rig = new Rig(20, 5, NumberedLines(30));
        rig.Paint();

        rig.Lines.AddRange(NumberedLines(10, "new"));

        Assert.Equal("line0", rig.Screen()[0]);
        Assert.False(rig.View.Following);
    }

    [Fact]
    public void FollowTail_CountsWrappedRows()
    {
        var rig = new Rig(12, 4, "short", new string('x', 25)); // 1 + 3 rows
        rig.View.FollowTail = true;

        Assert.Equal(4, rig.View.TotalRows);
        Assert.Equal(["short", "xxxxxxxxxx", "xxxxxxxxxx", "xxxxx"], rig.Screen());
    }

    [Fact]
    public void Scrollbar_OnlyWhenContentIsTallerThanTheView()
    {
        var short_ = new Rig(20, 5, NumberedLines(3));
        Assert.DoesNotContain("│", short_.ScrollbarColumn());

        var tall = new Rig(20, 5, NumberedLines(50));
        string column = tall.ScrollbarColumn();
        Assert.Contains("│", column);                       // the track
        Assert.Contains(column, c => c is >= '⠀' and <= '⣿'); // the braille thumb, as in ListView
    }

    [Fact]
    public void Scrollbar_ThumbMovesDownAsYouScroll()
    {
        var rig = new Rig(20, 10, NumberedLines(100));
        int thumbAtTop = rig.ScrollbarColumn().IndexOfAny(Enumerable.Range(0x2800, 0x100).Select(c => (char)c).ToArray());

        rig.Key(Key.End);
        string column = rig.ScrollbarColumn();
        int thumbAtEnd = column.LastIndexOfAny(Enumerable.Range(0x2800, 0x100).Select(c => (char)c).ToArray());

        Assert.Equal(0, thumbAtTop);
        Assert.Equal(9, thumbAtEnd);
    }

    [Fact]
    public void EmptyView_RendersNothingAndNeverThrows()
    {
        var rig = new Rig(20, 5);

        Assert.Equal(0, rig.View.TotalRows);
        Assert.All(rig.Screen(), row => Assert.Equal("", row));
        rig.Key(Key.Down);
        rig.Key(Key.End);
        Assert.False(rig.View.SelectAll());
        Assert.Null(rig.View.SelectedText);
    }

    [Fact]
    public void LineStyle_ColoursWholeLines()
    {
        var rig = new Rig(20, 3, "=== heading", "plain");
        rig.View.LineStyle = l => l.StartsWith("===") ? (ThemeRole.Accent, true) : (ThemeRole.Text, false);

        var buffer = rig.Paint();

        Assert.Equal(Theme.Default.Accent, buffer[1, 0].Fg);
        Assert.Equal(Theme.Default.Text, buffer[1, 1].Fg);
    }

    #endregion

    #region Selection

    [Fact]
    public void Drag_SelectsTheCellsFromPressToReleaseInclusive()
    {
        var rig = new Rig(20, 5, "abcdefghij");

        rig.Drag(3, 0, 6, 0); // screen cols 3..6 are text cols 2..5

        Assert.Equal("cdef", rig.View.SelectedText);
    }

    [Fact]
    public void Drag_Backwards_SelectsTheSameText()
    {
        var rig = new Rig(20, 5, "abcdefghij");

        rig.Drag(6, 0, 3, 0);

        Assert.Equal("cdef", rig.View.SelectedText);
    }

    [Fact]
    public void Drag_AcrossLines_JoinsThemWithNewlines()
    {
        var rig = new Rig(20, 5, "first line", "second line", "third line");

        rig.Drag(7, 0, 3, 2); // screen col 7 = text col 6 ("line"); col 3 = text col 2, inclusive: "thi"

        Assert.Equal("line\nsecond line\nthi", rig.View.SelectedText);
    }

    [Fact]
    public void Drag_AcrossWrappedRows_CopiesTheOriginalLineWithoutBreaks()
    {
        const string line = "abcdefghijklmnopqrstuvwxyz";
        var rig = new Rig(12, 5, line); // wraps into 10 + 10 + 6

        rig.Drag(1, 0, 6, 2); // first cell of row 0 to the last cell of row 2

        Assert.Equal(line, rig.View.SelectedText);
        Assert.DoesNotContain('\n', rig.View.SelectedText!);
    }

    [Fact]
    public void Drag_PartWayThroughAWrappedLine_MapsRowsBackToColumns()
    {
        var rig = new Rig(12, 5, "abcdefghijklmnopqrstuvwxyz");

        rig.Drag(2, 1, 4, 1); // row 1 = "klmnopqrst": cells 2..4 are "lmn"

        Assert.Equal("lmn", rig.View.SelectedText);
    }

    [Fact]
    public void DoubleClick_SelectsTheWholeLine_EvenWhenWrapped()
    {
        var rig = new Rig(12, 6, "short", "abcdefghijklmnopqrstuvwxyz", "tail");
        rig.Paint();

        rig.View.OnDoubleClick(new Point(3, 2)); // a wrapped row of line 1

        Assert.Equal("abcdefghijklmnopqrstuvwxyz", rig.View.SelectedText);
    }

    [Fact]
    public void ClickWithoutDragging_ClearsTheSelection()
    {
        var rig = new Rig(20, 5, "abcdefghij");
        rig.Drag(3, 0, 6, 0);
        Assert.True(rig.View.HasSelection);

        rig.Mouse(MouseKind.Press, 4, 0);
        rig.Mouse(MouseKind.Release, 4, 0);

        Assert.False(rig.View.HasSelection);
        Assert.Null(rig.View.SelectedText);
    }

    [Fact]
    public void Press_DoesNotConsumeTheEvent_SoTheAppCanFocusTheView()
    {
        var rig = new Rig(20, 5, "abc");
        rig.Paint();
        Assert.False(rig.Mouse(MouseKind.Press, 3, 0));
    }

    [Fact]
    public void SelectAll_SelectsEveryLine()
    {
        var rig = new Rig(20, 5, "one", "two", "three");

        Assert.True(rig.View.SelectAll());

        Assert.Equal("one\ntwo\nthree", rig.View.SelectedText);
    }

    [Fact]
    public void Escape_ClearsTheSelection_ThenFallsThrough()
    {
        var rig = new Rig(20, 5, "abcdefghij");
        rig.Drag(3, 0, 6, 0);

        Assert.True(rig.View.HandleKey(KeyEvent.Of(Key.Escape)));
        Assert.False(rig.View.HasSelection);
        Assert.False(rig.View.HandleKey(KeyEvent.Of(Key.Escape)));
    }

    [Fact]
    public void Selection_IsHighlightedOnTheAccentBackground()
    {
        var rig = new Rig(20, 5, "abcdefghij");
        rig.Drag(3, 0, 6, 0);

        var buffer = rig.Paint();

        for (int x = 1; x <= 10; x++)
        {
            bool selected = x is >= 3 and <= 6;
            Assert.Equal(selected, buffer[x, 0].Bg == Theme.Default.Accent);
        }
    }

    [Fact]
    public void MultiLineSelection_AlsoHighlightsTheNewlineCell()
    {
        var rig = new Rig(20, 5, "ab", "cd");
        rig.Drag(1, 0, 2, 1);

        var buffer = rig.Paint();

        Assert.Equal(Theme.Default.Accent, buffer[3, 0].Bg); // one past "ab": the line break
    }

    [Fact]
    public void Selection_StaysOnItsText_WhenOldLinesAreTrimmed()
    {
        var rig = new Rig(20, 5, NumberedLines(30));
        rig.View.FollowTail = true;
        rig.Paint();
        rig.Drag(1, 4, 5, 4); // the last visible row: "line29"
        string selected = rig.View.SelectedText!;

        rig.Lines.RemoveRange(0, 10); // the source trims its oldest ten lines
        rig.Trimmed += 10;
        rig.Paint();

        Assert.Equal(selected, rig.View.SelectedText);
    }

    [Fact]
    public void Selection_MadeAfterTrimmingHasStarted_AlsoSurvivesMoreTrimming()
    {
        // the source has already dropped 100 lines: ids are index + 100, not index
        var rig = new Rig(20, 5, NumberedLines(30)) { Trimmed = 100 };
        rig.View.FollowTail = true;
        rig.Paint();
        rig.Drag(1, 3, 6, 4); // "line28" and "line29" (screen col 6 = text col 5, inclusive)
        Assert.Equal("line28\nline29", rig.View.SelectedText);

        rig.Lines.RemoveRange(0, 10);
        rig.Trimmed += 10;
        rig.Lines.AddRange(NumberedLines(3, "new"));
        rig.Paint();

        Assert.Equal("line28\nline29", rig.View.SelectedText);
    }

    [Fact]
    public void Selection_OfLinesThatWereTrimmedAway_ShrinksInsteadOfFailing()
    {
        var rig = new Rig(20, 5, NumberedLines(30));
        rig.Paint();
        rig.View.SelectAll();

        rig.Lines.RemoveRange(0, 25);
        rig.Trimmed += 25;
        rig.Paint();

        Assert.Equal("line25\nline26\nline27\nline28\nline29", rig.View.SelectedText);
    }

    [Fact]
    public void WideCharacters_AreSelectedByTheirCells()
    {
        var rig = new Rig(20, 5, "日本語abc"); // cells: 日 0-1, 本 2-3, 語 4-5, a 6

        rig.Drag(3, 0, 4, 0); // text cols 2..3 = 本

        Assert.Equal("本", rig.View.SelectedText);
    }

    [Fact]
    public void DraggingBelowTheView_ScrollsDown()
    {
        var rig = new Rig(20, 5, NumberedLines(50));
        rig.Paint();
        rig.Mouse(MouseKind.Press, 3, 2);

        rig.Mouse(MouseKind.Motion, 3, 9);
        rig.Mouse(MouseKind.Motion, 3, 9);

        Assert.Equal(2, rig.View.Offset);
        Assert.True(rig.View.HasSelection);
    }

    [Fact]
    public void Motion_WithoutAPress_SelectsNothing()
    {
        var rig = new Rig(20, 5, "abcdefghij");
        rig.Paint();

        rig.Mouse(MouseKind.Motion, 5, 0);

        Assert.False(rig.View.HasSelection);
    }

    [Fact]
    public void Reset_ClearsSelectionAndReturnsToTheStart()
    {
        var rig = new Rig(20, 5, NumberedLines(50));
        rig.View.FollowTail = true;
        rig.Paint();
        rig.Key(Key.Up);
        rig.View.SelectAll();

        rig.View.Reset();

        Assert.False(rig.View.HasSelection);
        Assert.True(rig.View.Following);
    }

    [Fact]
    public void ContextMenu_OffersCopySelectAllAndJumpToEnd_WithCopyOnlyWhenSelected()
    {
        var rig = new Rig(20, 5, "abcdefghij");

        var none = rig.View.MenuAt!(new Point(0, 0))!;
        Assert.Equal(["copy", "select all", "jump to end"], none.Select(m => m.Label));
        Assert.False(none[0].Enabled);

        rig.Drag(3, 0, 6, 0);
        Assert.True(rig.View.MenuAt!(new Point(0, 0))![0].Enabled);
    }

    #endregion

    #region Through an App: Ctrl+C and the right-click menu

    private sealed class AppRig
    {
        public readonly TextView View;
        public readonly App App;
        public readonly FakeTerminal Term = new(30, 8);
        public readonly List<string> Copied = [];
        public readonly List<KeyEvent> Unhandled = [];

        public AppRig(params string[] lines)
        {
            View = new TextView(() => lines.Length, i => lines[i]);
            View.Node.Grow = 1f;
            var root = new LayoutNode { Direction = FlexDirection.Column };
            root.Add(View);
            App = new App(Term, root, targetFps: 60, time: new VirtualTimeSource(), asyncPresenting: false)
            {
                ClipboardWriter = Copied.Add,
                OnKey = k => { Unhandled.Add(k); return true; },
            };
            App.RunFrame();
        }

        public void Send(string input)
        {
            foreach (byte b in Encoding.UTF8.GetBytes(input)) Term.QueueInput(b);
            App.RunFrame();
        }

        // SGR mouse, 1-based: press, drag motion (btn+32), release
        public void Press(int x, int y, int button = 0) => Send($"\u001b[<{button};{x + 1};{y + 1}M");
        public void MoveTo(int x, int y) => Send($"\u001b[<32;{x + 1};{y + 1}M");
        public void Release(int x, int y, int button = 0) => Send($"\u001b[<{button};{x + 1};{y + 1}m");
    }

    [Fact]
    public void DragThenCtrlC_CopiesTheSelection_AndTheViewTookFocusFromTheClick()
    {
        var rig = new AppRig("hello world", "second line");

        rig.Press(2, 0);
        rig.MoveTo(6, 0);
        rig.Release(6, 0);
        Assert.Same(rig.View, rig.App.Focused);

        rig.Send("\u0003");

        Assert.Equal(["ello "], rig.Copied);
        Assert.Empty(rig.Unhandled);
    }

    [Fact]
    public void CtrlC_WithNothingSelected_ReachesTheApp()
    {
        var rig = new AppRig("hello world");
        rig.App.Focus(rig.View);

        rig.Send("\u0003");

        Assert.Empty(rig.Copied);
        Assert.Equal('\u0003', Assert.Single(rig.Unhandled).Char);
    }

    [Fact]
    public void CtrlA_ThenCtrlC_CopiesEverything()
    {
        var rig = new AppRig("one", "two");
        rig.App.Focus(rig.View);

        rig.Send("\u0001");
        rig.Send("\u0003");

        Assert.Equal(["one\ntwo"], rig.Copied);
    }

    [Fact]
    public void RightClick_OpensTheCopyMenu_WithoutLosingTheSelection()
    {
        var rig = new AppRig("hello world");
        rig.Press(2, 0);
        rig.MoveTo(6, 0);
        rig.Release(6, 0);
        Assert.Equal(0, rig.App.OverlayCount);

        rig.Press(4, 0, button: 2);
        rig.Release(4, 0, button: 2);

        Assert.Equal(1, rig.App.OverlayCount);
        Assert.True(rig.View.HasSelection);
        Assert.Equal("ello ", rig.View.SelectedText);
    }

    #endregion
}
