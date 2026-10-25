using QxTui.Core;
using QxTui.Layout;
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
