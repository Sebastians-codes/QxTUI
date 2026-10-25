using QxTui.Core;
using QxTui.Layout;
using System.Text;
using QxTui.Render;
using QxTui.Widgets;

namespace QxTui.Tests;

public class UnicodeWidthTests
{
    [Theory]
    [InlineData("abc", 3)]
    [InlineData("你好", 4)]
    [InlineData("ｱｲ", 2)]           // halfwidth katakana
    [InlineData("a\u0301", 1)]       // a + combining acute
    [InlineData("👍", 2)]            // emoji
    [InlineData("👨‍👩‍👧", 2)]  // ZWJ family = one glyph
    [InlineData("", 0)]
    public void OfText_MeasuresColumns(string text, int expected) =>
        Assert.Equal(expected, UnicodeWidth.OfText(text));

    [Fact]
    public void Rune_Widths()
    {
        Assert.Equal(2, UnicodeWidth.Of(new Rune('你')));
        Assert.Equal(1, UnicodeWidth.Of(new Rune('a')));
        Assert.Equal(0, UnicodeWidth.Of(new Rune('\u0301')));
    }
}

public class TextWrapTests
{
    private static string[] Wrap(string text, int width) =>
        TextWrap.Wrap(text, width).Select(m => m.Span.ToString()).ToArray();

    [Fact]
    public void Words_WrapAtBudget()
    {
        Assert.Equal(["aaa bbb", "ccc"], Wrap("aaa bbb ccc", 7));
    }

    [Fact]
    public void Cjk_WrapsWithoutSplittingWideChars()
    {
        // width 5: 你好=4 fits, adding 世(2) would be 6 → wrap
        Assert.Equal(["你好", "世界"], Wrap("你好世界", 5));
    }

    [Fact]
    public void WideChar_AtLineEnd_MovesWhole()
    {
        // width 3: "a" then 你(2) = 3 fits; next 好 would overflow
        Assert.Equal(["a你", "好"], Wrap("a你好", 3));
    }

    [Fact]
    public void OverlongWord_HardSplits()
    {
        Assert.Equal(["abc", "def", "gh"], Wrap("abcdefgh", 3));
    }

    [Fact]
    public void Newlines_AreHonored()
    {
        Assert.Equal(["ab", "cd"], Wrap("ab\ncd", 10));
    }
}

public class WideCellRenderTests
{
    [Fact]
    public void Cjk_OccupiesTwoCells_WithPad()
    {
        using var buf = new CellBuffer(10, 3);
        buf.WriteTextClipped(new RenderClip(0, 0, 10, 3), 0, 0, "你a", fg: 7);

        Assert.Equal('你', buf[0, 0].Char);
        Assert.False(buf[0, 0].WidePad);
        Assert.True(buf[1, 0].WidePad);      // pad cell
        Assert.Equal('a', buf[2, 0].Char);   // next char lands at column 2
    }

    [Fact]
    public void Serializer_EmitsWideCharOnce_AndSkipsPad()
    {
        using var back = new CellBuffer(10, 3);
        using var renderer = new Renderer(10, 3, synchronizedOutput: false);
        var output = new System.Buffers.ArrayBufferWriter<byte>();

        back.WriteTextClipped(new RenderClip(0, 0, 10, 3), 0, 0, "你好", fg: 7);
        renderer.Render(back, output);

        var text = Encoding.UTF8.GetString(output.WrittenSpan);
        Assert.Equal(1, CountOccurrences(text, "你"));
        Assert.Equal(1, CountOccurrences(text, "好"));
        Assert.DoesNotContain('\0', text); // pad marker never reaches the wire
    }

    [Fact]
    public void Emoji_SurrogatePair_SurvivesRoundTrip()
    {
        using var back = new CellBuffer(10, 3);
        using var renderer = new Renderer(10, 3, synchronizedOutput: false);
        var output = new System.Buffers.ArrayBufferWriter<byte>();

        back.WriteTextClipped(new RenderClip(0, 0, 10, 3), 0, 0, "👍", fg: 7);
        renderer.Render(back, output);

        var text = Encoding.UTF8.GetString(output.WrittenSpan);
        Assert.Contains("👍", text);
        Assert.True(back[1, 0].WidePad);
    }

    [Fact]
    public void TextWidget_WrapsInsideRect()
    {
        var text = new TextWidget(() => "aaa bbb ccc");
        var root = QxTui.Layout.UI.Column(0, text.Width(8).Height(3));
        var term = new FakeTerminal(8, 3);
        using var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        app.RunFrame();

        Assert.Equal('a', app.Back[1, 0].Char);
        Assert.Equal('c', app.Back[1, 1].Char); // wrapped to line 2
    }

    private static int CountOccurrences(string s, string sub)
    {
        int count = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { count++; i += sub.Length; }
        return count;
    }
}

public class EllipsizeTests
{
    [Fact]
    public void ShortText_Unchanged() =>
        Assert.Equal("hello", Text.Ellipsize("hello", 10));

    [Fact]
    public void LongText_GetsEllipsis_NotHardCut()
    {
        string result = Text.Ellipsize("hello world", 8);
        Assert.Equal("hello w…", result);
        Assert.Equal(8, UnicodeWidth.OfText(result));
    }

    [Fact]
    public void WideRunes_NeverSplit()
    {
        // width 5: 你(2)好(2) = 4, next 世 would need 2 but only 4 left (5-1 for …)
        string result = Text.Ellipsize("你好世界", 5);
        Assert.Equal("你好…", result);
        Assert.Equal(5, UnicodeWidth.OfText(result));
    }

    [Fact]
    public void TinyWidths()
    {
        Assert.Equal("", Text.Ellipsize("abc", 0));
        Assert.Equal("…", Text.Ellipsize("abc", 1));
    }
}
