using System.Text;

namespace QxTui.Render;

/// <summary>
/// Width-aware greedy word wrap with unicode line-break opportunities:
/// after spaces and around wide (CJK) runes, per UAX #14's spirit. Never
/// splits a wide rune across lines; hard-splits words wider than the
/// budget; honors explicit newlines; trims trailing spaces.
/// Cold path (text layout), not the frame loop.
/// </summary>
public static class TextWrap
{
    public static List<ReadOnlyMemory<char>> Wrap(ReadOnlySpan<char> text, int maxWidth)
    {
        var lines = new List<ReadOnlyMemory<char>>();
        if (maxWidth <= 0)
            return lines;

        foreach (var para in SplitLines(text))
            WrapParagraph(para, maxWidth, lines);

        return lines;
    }

    private static List<ReadOnlyMemory<char>> SplitLines(ReadOnlySpan<char> text)
    {
        var parts = new List<ReadOnlyMemory<char>>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                parts.Add(text[start..i].ToArray());
                start = i + 1;
            }
        }
        parts.Add(text[start..].ToArray());
        return parts;
    }

    private static void WrapParagraph(ReadOnlyMemory<char> paragraph, int maxWidth,
        List<ReadOnlyMemory<char>> lines)
    {
        var span = paragraph.Span;

        void Emit(ReadOnlySpan<char> sp, int start, int end)
        {
            while (end > start && char.IsWhiteSpace(sp[end - 1])) end--;
            lines.Add(paragraph.Slice(start, end - start));
        }

        int lineStart = 0, used = 0, candStart = -1, i = 0;

        while (i < span.Length)
        {
            if (span[i] == '\n')
            {
                Emit(span, lineStart, i);
                i++;
                lineStart = i;
                used = 0;
                candStart = -1;
                continue;
            }

            if (Rune.DecodeFromUtf16(span[i..], out var rune, out int consumed)
                != System.Buffers.OperationStatus.Done)
            {
                rune = new Rune('?');
                consumed = 1;
            }

            int w = UnicodeWidth.Of(rune);
            bool isSpace = char.IsWhiteSpace(span[i]);

            if (isSpace)
            {
                if (used > 0 && used + 1 > maxWidth)
                {
                    // dangling space at line end: free, but a break point
                    candStart = i + consumed;
                    i += consumed;
                    continue;
                }
                candStart = i + consumed;
                used += 1;
                i += consumed;
                continue;
            }

            if (w > 0 && used > 0 && used + w > maxWidth)
            {
                int breakAt = candStart > lineStart ? candStart : i;
                Emit(span, lineStart, breakAt);
                lineStart = breakAt;
                while (lineStart < span.Length && char.IsWhiteSpace(span[lineStart]))
                    lineStart++;
                i = Math.Max(i, lineStart);
                used = UnicodeWidth.OfText(span[lineStart..i]);
                candStart = -1;
                continue; // re-process this cluster on the fresh line
            }

            if (w >= 2)
                candStart = i + consumed; // break allowed after wide runes

            used += w;
            i += consumed;
        }

        Emit(span, lineStart, span.Length);
    }
}
