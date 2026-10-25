using System.Buffers;
using System.Text;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Widgets;

namespace QxTui.Tests;

public class HardwareScrollTests
{
    internal static CellBuffer MakeBufPublic(int w, int h, int shift) => MakeBuf(w, h, shift);

    private static CellBuffer MakeBuf(int w, int h, int shift)
    {
        var b = new CellBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                b[x, y] = new Cell { Char = (char)('a' + ((y + shift) % 26)), Fg = 7 };
        return b;
    }

    private static string RenderToString(Renderer r, CellBuffer b, out RenderResult res)
    {
        var outp = new ArrayBufferWriter<byte>();
        res = r.Render(b, outp);
        return Encoding.UTF8.GetString(outp.WrittenSpan);
    }

    [Fact]
    public void ScrollHint_EmitsRegionScroll_AndRepaintsOnlyExposedRows()
    {
        using var r = new Renderer(20, 10, synchronizedOutput: false) { HardwareScroll = true };
        var first = MakeBuf(20, 10, 0);
        RenderToString(r, first, out var full);

        var shifted = MakeBuf(20, 10, 3);
        r.ScrollHint(new Rect(0, 0, 20, 10), 3);
        string s = RenderToString(r, shifted, out var hinted);

        Assert.Contains("\x1b[1;10r", s);  // scroll region = the rect
        Assert.Contains("\x1b[3S", s);    // SU 3: content moved up
        // exposed rows (3 of 10) instead of a whole-window repaint
        Assert.True(hinted.BytesWritten < full.BytesWritten / 2,
            $"hint frame {hinted.BytesWritten}B vs full {full.BytesWritten}B");

        // front buffer mirrors the terminal now: same content = clean frame
        RenderToString(r, shifted, out var idle);
        Assert.Equal(0, idle.DirtyCells);
    }

    [Fact]
    public void ScrollHint_Downward_UsesSD()
    {
        using var r = new Renderer(20, 10, synchronizedOutput: false) { HardwareScroll = true };
        RenderToString(r, MakeBuf(20, 10, 3), out _);

        r.ScrollHint(new Rect(0, 0, 20, 10), -3);
        string s = RenderToString(r, MakeBuf(20, 10, 0), out _);

        Assert.Contains("\x1b[3T", s);    // SD 3: content moved down
    }

    [Fact]
    public void ScrollHint_NoOverlap_IsIgnored()
    {
        using var r = new Renderer(20, 10, synchronizedOutput: false) { HardwareScroll = true };
        RenderToString(r, MakeBuf(20, 10, 0), out _);

        r.ScrollHint(new Rect(0, 0, 20, 10), 10); // >= height: no overlap
        string s = RenderToString(r, MakeBuf(20, 10, 10), out _);

        Assert.DoesNotContain("S\x1b", s);
        Assert.DoesNotContain("\x1b[1;10r", s);
    }

    [Fact]
    public void ExposedRows_AreBlankedInFrontBuffer_RepeatingGlyphsRepaint()
    {
        // regression: every row identical, so a naive diff after a shift
        // sees "no change" and never repaints the terminal's blank
        // exposed rows -> ghost holes (the bug behind the user screenshot)
        using var r = new Renderer(20, 10, synchronizedOutput: false)
            { HardwareScroll = true };
        static CellBuffer Uniform()
        {
            var b = new CellBuffer(20, 10);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 20; x++)
                    b[x, y] = new Cell { Char = 'x', Fg = 7 };
            return b;
        }
        var outp = new ArrayBufferWriter<byte>();
        r.Render(Uniform(), outp);

        r.ScrollHint(new Rect(0, 0, 20, 10), 2);
        var out2 = new ArrayBufferWriter<byte>();
        var res = r.Render(Uniform(), out2); // content "identical" post-shift

        // exposed bottom 2 rows MUST repaint despite identical content
        Assert.True(res.DirtyCells >= 2 * 20,
            $"exposed rows must repaint, got {res.DirtyCells} dirty");
    }

    [Fact]
    public void ScrollHint_PartialRect_LeavesRestUntouched()
    {
        // a static header row outside the rect must not shift or repaint
        using var r = new Renderer(20, 10, synchronizedOutput: false) { HardwareScroll = true };
        var a = MakeBuf(20, 10, 0);
        for (int x = 0; x < 20; x++) a[x, 0] = new Cell { Char = 'H', Fg = 7 };
        RenderToString(r, a, out _);

        var b = MakeBuf(20, 10, 2);
        for (int x = 0; x < 20; x++) b[x, 0] = new Cell { Char = 'H', Fg = 7 };
        r.ScrollHint(new Rect(0, 1, 20, 9), 2);
        string s = RenderToString(r, b, out var res);

        Assert.Contains("\x1b[2;10r", s); // region starts below the header
        Assert.True(res.DirtyCells <= 2 * 20, $"only exposed rows dirty, got {res.DirtyCells}");
    }
}

public class HardwareScrollGateTests
{
    [Fact]
    public void ExplicitDisable_StaysDiffOnly()
    {
        // an emulator that misimplements SU/SD can be switched off
        using var r = new Renderer(20, 10, synchronizedOutput: false)
            { HardwareScroll = false };
        var outp = new ArrayBufferWriter<byte>();
        r.Render(HardwareScrollTests.MakeBufPublic(20, 10, 0), outp);

        r.ScrollHint(new Rect(0, 0, 20, 10), 3);
        var out2 = new ArrayBufferWriter<byte>();
        r.Render(HardwareScrollTests.MakeBufPublic(20, 10, 3), out2);
        string s = Encoding.UTF8.GetString(out2.WrittenSpan);

        Assert.DoesNotContain("\x1b[3S", s);
        Assert.DoesNotContain("\x1b[1;10r", s);
    }
}

public class ScrollbarColumnSurvivesScrollTests
{
    private static CellBuffer WithTrack(int shift)
    {
        var b = new CellBuffer(20, 10);
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 19; x++)
                b[x, y] = new Cell { Char = (char)('a' + ((y + shift) % 26)), Fg = 7 };
            b[19, y] = new Cell { Char = '│', Fg = 8 }; // scrollbar track
        }
        return b;
    }

    [Fact]
    public void FullWidthHint_RepaintsExposedScrollbarColumn()
    {
        using var r = new Renderer(20, 10, synchronizedOutput: false)
            { HardwareScroll = true };
        var outp = new ArrayBufferWriter<byte>();
        r.Render(WithTrack(0), outp);

        // hint covers the track column (what ListView now sends)
        r.ScrollHint(new Rect(0, 0, 20, 10), 2);
        var out2 = new ArrayBufferWriter<byte>();
        r.Render(WithTrack(2), out2);
        string frame = Encoding.UTF8.GetString(out2.WrittenSpan);

        // exposed rows serialize the track glyph too — otherwise the
        // terminal's shifted-blank track cells never repaint (vanishing
        // scrollbar)
        Assert.Contains("│", frame);
    }

    [Fact]
    public void ContentOnlyHint_WouldLeaveTrackBlank_Documented()
    {
        // the old bug, pinned: a content-width hint never serializes the
        // track column on scroll frames
        using var r = new Renderer(20, 10, synchronizedOutput: false)
            { HardwareScroll = true };
        var outp = new ArrayBufferWriter<byte>();
        r.Render(WithTrack(0), outp);

        r.ScrollHint(new Rect(0, 0, 19, 10), 2); // content only (old rect)
        var out2 = new ArrayBufferWriter<byte>();
        r.Render(WithTrack(2), out2);
        string frame = Encoding.UTF8.GetString(out2.WrittenSpan);

        Assert.DoesNotContain("│", frame);
    }
}

public class DropRepairTests
{
    [Fact]
    public void AdoptPrev_RebasesFrontBuffer_MinimalRepairAfterDrop()
    {
        using var r = new Renderer(20, 10, synchronizedOutput: false);
        var a = new CellBuffer(20, 10);
        var b = new CellBuffer(20, 10);
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 20; x++)
            {
                a[x, y] = new Cell { Char = 'a', Fg = 7 };
                b[x, y] = new Cell { Char = 'b', Fg = 7 };
            }
        var outp = new ArrayBufferWriter<byte>();
        r.Render(a, outp);
        r.Render(b, outp);          // front buffer now = b

        r.AdoptPrev(a);             // terminal actually shows a (drop happened)
        var out2 = new ArrayBufferWriter<byte>();
        var res = r.Render(b, out2);

        // repair frame diffs b against a: every cell, but NOT a forced
        // full-repaint flag (which would also serialize empty margins)
        Assert.True(res.DirtyCells >= 200, $"delta repair expected, got {res.DirtyCells}");
        Assert.False(res.FullRepaint);

        var out3 = new ArrayBufferWriter<byte>();
        var idle = r.Render(b, out3);
        Assert.Equal(0, idle.DirtyCells); // converged
    }
}

public class TabRoundTripHoleTests
{
    private sealed class RenderOnceWidget : Widget
    {
        public int Renders;
        public override void Render(CellBuffer buffer)
        {
            // like the playground's static panel: paints fully whenever the
            // FRAMEWORK asks (i.e. when dirty); "static" means never dirtied
            // in steady state — NOT refusing to repaint after an erase
            Renders++;
            var r = Rect;
            buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = 'S', Fg = 7 });
        }
    }

    [Fact]
    public void StaticWidget_SurvivesTabRoundTrip_NoErasedHoles()
    {
        var once = new RenderOnceWidget();
        var page1 = new ContainerWidget();
        page1.Node.Direction = FlexDirection.Column;
        page1.Node.Height = 5;
        once.Node.Grow = 1;
        page1.AddChild(once);

        var page2 = new ContainerWidget();
        page2.Node.Direction = FlexDirection.Column;
        page2.Node.Height = 5;

        var router = new Router();
        router.Register("a", () => page1);
        router.Register("b", () => page2);
        var root = UI.Column(0, router.Grow(1));
        var term = new FakeTerminal(20, 8);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);

        router.Switch("a");
        app.RunFrame();
        Assert.Equal('S', app.Back[0, 0].Char);

        router.Switch("b");
        app.RunFrame();
        router.Switch("a");   // round trip: page1 was erased on detach
        app.RunFrame();

        // the render-once widget must have been re-dirtied on re-attach,
        // not left clean over its erased pixels
        Assert.Equal('S', app.Back[0, 0].Char);
        Assert.Equal(2, once.Renders); // exactly one repaint for the return
    }
}
