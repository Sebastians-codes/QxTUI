using System.Buffers;
using System.Text;
using QxTui.Render;

namespace QxTui.Tests;

/// <summary>
/// Headless renderer tests (AGENT.md §3): assert on exact ANSI bytes written
/// to an in-memory buffer. These encode core invariants #1 and #2.
/// </summary>
public class RendererTests
{
    private static string AsText(ReadOnlySpan<byte> bytes) =>
        Encoding.Latin1.GetString(bytes);

    [Fact]
    public void Invariant1_IdenticalFrame_WritesOnlySyncBytes()
    {
        using var back = new CellBuffer(40, 10);
        using var renderer = new Renderer(40, 10, synchronizedOutput: true);
        var output = new ArrayBufferWriter<byte>();

        back.WriteText(5, 3, "hello world", fg: 42);

        output.Clear();
        renderer.Render(back, output); // first frame: real damage

        output.Clear();
        var result = renderer.Render(back, output); // identical frame

        var text = AsText(output.WrittenSpan);
        Assert.Equal("\x1b[?2026h\x1b[?2026l", text); // sync begin/end ONLY
        Assert.Equal(0, result.DirtyCells);
        Assert.True(result.BytesWritten <= 16, $"got {result.BytesWritten} bytes");
    }

    [Fact]
    public void Invariant2_SingleCellChange_MinimalOutput()
    {
        using var back = new CellBuffer(40, 10);
        using var renderer = new Renderer(40, 10, synchronizedOutput: true);
        var output = new ArrayBufferWriter<byte>();

        back.WriteText(0, 0, "aaaa", fg: 7);
        renderer.Render(back, output); // establish baseline

        // change exactly one cell
        back[2, 5] = new Cell { Char = 'X', Fg = 7, Bg = -1 };

        output.Clear();
        var result = renderer.Render(back, output);

        var text = AsText(output.WrittenSpan);
        Assert.Equal(1, result.DirtyCells);
        Assert.True(result.BytesWritten < 64, $"got {result.BytesWritten} bytes:\n{text}");
        Assert.Contains("\x1b[6;3H", text); // cursor move to row 6, col 3 (1-based)
        Assert.Contains('X', text);
        Assert.DoesNotContain('a', text);   // untouched cells NOT re-sent
    }

    [Fact]
    public void WithoutSync_NoWrapperBytes()
    {
        using var back = new CellBuffer(10, 4);
        using var renderer = new Renderer(10, 4, synchronizedOutput: false);
        var output = new ArrayBufferWriter<byte>();

        renderer.Render(back, output); // empty frame, nothing dirty

        Assert.Equal(0, output.WrittenCount);
    }

    [Fact]
    public void FullRepaint_OnMassiveDamage()
    {
        // Change more cells than MaxDirtyRuns can track → full repaint path.
        using var back = new CellBuffer(200, 60);
        using var renderer = new Renderer(200, 60, synchronizedOutput: false);
        var output = new ArrayBufferWriter<byte>();

        renderer.Render(back, output); // baseline (empty vs empty: no damage)

        for (int y = 0; y < 60; y++)
            for (int x = 0; x < 200; x += 2) // 100 isolated runs per row
                back[x, y] = new Cell { Char = '*', Fg = 9, Bg = -1 };

        output.Clear();
        var result = renderer.Render(back, output);

        Assert.True(result.FullRepaint, "expected full-repaint fallback");
        Assert.Contains("\x1b[H", AsText(output.WrittenSpan)); // home + repaint
    }

    [Fact]
    public void DamageRects_AdjacentRunsMerge()
    {
        using var back = new CellBuffer(40, 10);
        using var renderer = new Renderer(40, 10, synchronizedOutput: false);
        var output = new ArrayBufferWriter<byte>();
        renderer.Render(back, output);

        // two adjacent dirty cells on the same row → one run → one rect
        back[5, 2] = new Cell { Char = 'a', Fg = 7, Bg = -1 };
        back[6, 2] = new Cell { Char = 'b', Fg = 7, Bg = -1 };

        output.Clear();
        var result = renderer.Render(back, output);

        Assert.Equal(2, result.DirtyCells);
        Assert.Equal(1, result.DamageRects);
        var text = AsText(output.WrittenSpan);
        Assert.Contains("\x1b[3;6H", text);
        Assert.Contains("ab", text); // batched as contiguous text
    }

    [Fact]
    public void MultiRowDamage_RowTextFlushedBeforeNextCursorMove()
    {
        // Regression: pending row text must be flushed BEFORE the next row's
        // cursor move, otherwise multi-row damage lands at wrong positions.
        using var back = new CellBuffer(20, 4);
        using var renderer = new Renderer(20, 4, synchronizedOutput: false);
        var output = new ArrayBufferWriter<byte>();

        back.WriteText(0, 0, "row-zero", fg: 7);
        back.WriteText(0, 1, "row-one!", fg: 7); // same attrs: no SGR flush in between

        var result = renderer.Render(back, output);
        var text = AsText(output.WrittenSpan);

        int posRow0Text = text.IndexOf("row-zero", StringComparison.Ordinal);
        int posRow1Cup = text.IndexOf("\x1b[2;1H", StringComparison.Ordinal);
        int posRow1Text = text.IndexOf("row-one!", StringComparison.Ordinal);

        Assert.True(posRow0Text >= 0, "row 0 text missing");
        Assert.True(posRow1Cup >= 0, "row 1 cursor move missing");
        Assert.True(posRow1Text >= 0, "row 1 text missing");
        Assert.True(posRow0Text < posRow1Cup,
            $"row 0 text ({posRow0Text}) must precede row 1 cursor move ({posRow1Cup})");
        Assert.True(posRow1Cup < posRow1Text,
            $"row 1 cursor move ({posRow1Cup}) must precede row 1 text ({posRow1Text})");
    }

    [Fact]
    public void WriteTextClipped_StopsAtClipEdge()
    {
        using var buf = new CellBuffer(40, 5);
        var clip = new RenderClip(10, 0, 5, 5); // widget rect: x 10..14

        int written = buf.WriteTextClipped(clip, 12, 1, "hello world", fg: 7);

        Assert.Equal(3, written); // "hel" fits before clip right edge (15)
        Assert.Equal('l', buf[14, 1].Char);
        Assert.Equal(Cell.Empty, buf[15, 1]); // outside clip: untouched
        Assert.Equal(Cell.Empty, buf[11, 1]); // before clip: untouched
    }

    [Fact]
    public void AttributeChange_OnlyEmitsSgrOnChange()
    {
        using var back = new CellBuffer(20, 3);
        using var renderer = new Renderer(20, 3, synchronizedOutput: false);
        var output = new ArrayBufferWriter<byte>();

        // same char, different colors in a row
        for (int x = 0; x < 10; x++)
            back[x, 1] = new Cell { Char = '=', Fg = x < 5 ? 1 : 2, Bg = -1 };

        var result = renderer.Render(back, output);

        var text = AsText(output.WrittenSpan);
        int sgrCount = CountOccurrences(text, "\x1b[0");
        Assert.Equal(2, sgrCount); // one SGR at row start + one at color switch
        Assert.Equal(10, result.DirtyCells);
    }

    [Fact]
    public void Resize_ForcesFullRepaint()
    {
        using var back = new CellBuffer(10, 5);
        using var renderer = new Renderer(10, 5, synchronizedOutput: false);
        var output = new ArrayBufferWriter<byte>();

        back.WriteText(0, 0, "hi");
        renderer.Render(back, output);

        back.Resize(12, 6, preserve: true);
        renderer.Resize(12, 6); // fresh empty prev → everything is damage

        output.Clear();
        var result = renderer.Render(back, output);

        Assert.True(result.DirtyCells > 0 || result.FullRepaint);
        Assert.Contains("hi", AsText(output.WrittenSpan));
    }

    [Fact]
    public void MismatchedBufferSizes_Throws()
    {
        using var back = new CellBuffer(10, 5);
        using var renderer = new Renderer(20, 5);
        var output = new ArrayBufferWriter<byte>();

        Assert.Throws<InvalidOperationException>(() => renderer.Render(back, output));
    }

    private static int CountOccurrences(string s, string sub)
    {
        int count = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { count++; i += sub.Length; }
        return count;
    }
}
