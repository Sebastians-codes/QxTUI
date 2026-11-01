using System.Text;
using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;
using QxTui.Widgets;

namespace QxTui.Tests;

public class WheelParsingTests
{
    [Theory]
    [InlineData(64, MouseKind.WheelUp)]
    [InlineData(65, MouseKind.WheelDown)]
    [InlineData(66, MouseKind.WheelLeft)]
    [InlineData(67, MouseKind.WheelRight)]
    public void WheelButtons_ParseToTheirDirection(int button, MouseKind kind)
    {
        var parser = new KeyParser();
        foreach (var b in Encoding.ASCII.GetBytes($"\u001b[<{button};5;6M")) parser.Feed(b);

        Assert.True(parser.TryReadMouse(out var ev));
        Assert.Equal(kind, ev.Kind);
        Assert.Equal(0, ev.Button); // a wheel is not the right mouse button
        Assert.Equal((4, 5), (ev.X, ev.Y));
    }

    [Fact]
    public void ShiftBit_SurvivesOnWheelEvents()
    {
        var parser = new KeyParser();
        foreach (var b in Encoding.ASCII.GetBytes("\u001b[<69;5;6M")) parser.Feed(b);

        Assert.True(parser.TryReadMouse(out var ev));
        Assert.Equal(MouseKind.WheelDown, ev.Kind);
        Assert.True(ev.Shift);
    }
}

public class ListViewHorizontalTests
{
    private const int Width = 40, Height = 12;
    private const string Down = "\u001b[B", Up = "\u001b[A", Left = "\u001b[D", Right = "\u001b[C", Home = "\u001b[H";

    private sealed class Rig
    {
        public readonly ListView List;
        public readonly App App;
        public readonly FakeTerminal Term = new(Width, Height);
        public readonly VirtualTimeSource Clock = new();
        public readonly List<(int Index, bool Selected, Rect Rect)> Painted = [];

        public Rig(int rows = 100, int contentWidth = 200, bool viewer = false)
        {
            List = new ListView(() => rows)
            {
                ContentWidth = contentWidth,
                ViewerMode = viewer,
                RenderRow = (buffer, clip, rect, i, sel) =>
                {
                    Painted.Add((i, sel, rect));
                    buffer.WriteTextClipped(clip, rect.X, rect.Y, Line(i));
                },
            };
            List.Node.Grow = 1f;
            var root = new LayoutNode { Direction = FlexDirection.Column };
            root.Add(List);
            App = new App(Term, root, targetFps: 60, time: Clock, asyncPresenting: false);
            App.Focus(List);
            App.RunFrame();
        }

        public static string Line(int i) =>
            string.Concat(Enumerable.Range(0, 30).Select(k => $"{i:00}-{k:00} "));

        public void Send(string input)
        {
            foreach (var b in Encoding.UTF8.GetBytes(input)) Term.QueueInput(b);
            App.RunFrame();
        }

        public void Wheel(int button) => Send($"\u001b[<{button};5;5M");

        public void Wait(int ms) => Clock.Advance(TimeSpan.FromMilliseconds(ms));
    }

    [Fact]
    public void RightMouseButton_StillNotAWheel_LeftWheelReachesTheList()
    {
        // 66 & 3 == 2: it used to be routed as a right click and vanish
        var rig = new Rig();
        rig.List.ScrollXTo(20);

        rig.Wheel(66);

        Assert.True(rig.List.OffsetX < 20);
    }

    [Fact]
    public void SidewaysWheel_MovesBothWays()
    {
        var rig = new Rig();

        rig.Wheel(67);
        int right = rig.List.OffsetX;
        Assert.True(right > 0);

        rig.Wheel(66);
        Assert.Equal(0, rig.List.OffsetX);
    }

    [Theory]
    [InlineData(69)] // shift
    [InlineData(73)] // alt: some terminals keep shift for themselves
    public void ModifierWheel_ScrollsSideways(int button)
    {
        var rig = new Rig();

        rig.Wheel(button);

        Assert.True(rig.List.OffsetX > 0);
        Assert.Equal(0, rig.List.Offset);
    }

    [Fact]
    public void VerticalJitterDuringASidewaysSwipe_IsIgnored()
    {
        var rig = new Rig();

        rig.Wheel(67);
        rig.Wait(30);
        rig.Wheel(65); // trackpads leak the other axis

        Assert.Equal(0, rig.List.Offset);
    }

    [Fact]
    public void AfterAPause_TheOtherAxisWorksAgain()
    {
        var rig = new Rig();

        rig.Wheel(67);
        rig.Wait(400);
        rig.Wheel(65);

        Assert.Equal(3, rig.List.Offset);
    }

    [Fact]
    public void Scrolling_ClampsAtBothEnds()
    {
        var rig = new Rig(contentWidth: 45);

        for (int i = 0; i < 20; i++) rig.Send(Right);
        Assert.Equal(6, rig.List.OffsetX); // 45 wide, 39 visible beside the scrollbar

        for (int i = 0; i < 20; i++) rig.Send(Left);
        Assert.Equal(0, rig.List.OffsetX);
    }

    [Fact]
    public void Rows_AreHandedAShiftedRect_ClippedToTheViewport()
    {
        var rig = new Rig();
        rig.List.ScrollXTo(12);
        rig.Painted.Clear();

        rig.App.RunFrame();

        var row = rig.Painted[0];
        Assert.Equal(-12, row.Rect.X);
        Assert.Equal(39 + 12, row.Rect.W); // the far edge stays on screen
    }

    [Fact]
    public void TextScrollsLeft_WithoutPaintingOutsideTheList()
    {
        var rig = new Rig();
        rig.List.ScrollXTo(7); // one "00-00 " cell plus one
        rig.Term.SimulateResize(Width, Height); // repaint in full, not a delta
        rig.App.RunFrame();

        var text = Encoding.UTF8.GetString(rig.Term.LastFrame!);

        Assert.Contains("0-01", text);
        Assert.DoesNotContain("00-00", text);
    }

    [Fact]
    public void WideContent_GivesUpARowForTheScrollbar()
    {
        var wide = new Rig(contentWidth: 200);
        var narrow = new Rig(contentWidth: 20);

        wide.List.ScrollTo(int.MaxValue);
        narrow.List.ScrollTo(int.MaxValue);

        Assert.Equal(100 - (Height - 1), wide.List.Offset);
        Assert.Equal(100 - Height, narrow.List.Offset);
    }

    [Fact]
    public void ZeroContentWidth_KeepsThePlainListBehaviour()
    {
        var rig = new Rig(contentWidth: 0);
        int reachedApp = 0;
        rig.App.OnKey = _ => { reachedApp++; return true; };

        rig.Send(Right);
        rig.Wheel(67);

        Assert.Equal(1, reachedApp); // arrows stay free for the app
        Assert.Equal(0, rig.List.OffsetX);
        Assert.Equal(-0, rig.Painted[^1].Rect.X);
    }

    [Fact]
    public void Arrows_AreConsumedEvenAtTheEdge()
    {
        // a viewer must not leak them into global tab switching mid-read
        var rig = new Rig();
        int reachedApp = 0;
        rig.App.OnKey = _ => { reachedApp++; return true; };

        rig.Send(Left);

        Assert.Equal(0, reachedApp);
    }

    [Fact]
    public void DraggingTheBottomThumb_ScrollsEndToEnd()
    {
        var rig = new Rig();

        rig.Send($"\u001b[<0;1;{Height}M");
        Assert.Equal(0, rig.List.OffsetX);

        rig.Send($"\u001b[<32;39;{Height}M");
        Assert.Equal(200 - 39, rig.List.OffsetX);

        rig.Send($"\u001b[<0;39;{Height}m\u001b[<32;5;{Height}M");
        Assert.Equal(200 - 39, rig.List.OffsetX); // released: motion is just hover
    }

    [Fact]
    public void BottomThumb_IsTwoDotsHigh_NotAFullCell()
    {
        // only a horizontal bar here, so every thumb glyph is the bottom one
        var rig = new Rig(rows: 3);

        var text = Encoding.UTF8.GetString(rig.Term.LastFrame!);

        Assert.Contains("\u2836", text); // dot rows 1 and 2 of 0..3
        Assert.DoesNotContain("\u28FF", text);
        Assert.Contains("─", text);
    }

    [Fact]
    public void BottomThumb_MovesInHalfCells_AtTheSameHeight()
    {
        // an odd half-cell position starts and ends on half a glyph
        var rig = new Rig(rows: 3, contentWidth: 80);
        rig.List.ScrollXTo(3);
        rig.Term.SimulateResize(Width, Height);
        rig.App.RunFrame();

        var text = Encoding.UTF8.GetString(rig.Term.LastFrame!);

        Assert.Contains("\u2830", text); // right half
        Assert.Contains("\u2806", text); // left half
    }

    [Fact]
    public void ViewerMode_ArrowsScrollImmediately_NoCursor()
    {
        var rig = new Rig(viewer: true);

        rig.Send(Down + Down);
        Assert.Equal(2, rig.List.Offset);

        rig.Send(Up);
        Assert.Equal(1, rig.List.Offset);
        Assert.DoesNotContain(rig.Painted, p => p.Selected);
    }

    [Fact]
    public void ViewerMode_ClickSelectsNothing()
    {
        var rig = new Rig(viewer: true);
        rig.List.Selected = 0;

        rig.Send("\u001b[<0;5;4M\u001b[<0;5;4m");

        Assert.Equal(0, rig.List.Selected);
    }

    [Fact]
    public void ViewerMode_HomeReturnsToTheOrigin()
    {
        var rig = new Rig(viewer: true);
        rig.Send(Down + Right);

        rig.Send(Home);

        Assert.Equal((0, 0), (rig.List.Offset, rig.List.OffsetX));
    }

    [Fact]
    public void NarrowerList_PullsTheOffsetBackInRange()
    {
        var rig = new Rig(contentWidth: 200);
        rig.List.ScrollXTo(150);

        rig.List.ContentWidth = 45;
        rig.App.RunFrame();

        Assert.Equal(6, rig.List.OffsetX);
    }

    [Fact]
    public void ZeroSizedList_StillRenders()
    {
        // collapsed subtrees are painted at zero size
        var rig = new Rig();
        rig.List.Collapsed = true;

        rig.App.RunFrame();

        Assert.Equal(0, rig.List.Rect.W);
    }
}
