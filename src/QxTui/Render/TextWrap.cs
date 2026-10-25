using System.Text;

namespace QxTui.Render;

/// <summary>
/// Width-aware word wrap. Breaks on spaces, honors explicit newlines,
/// hard-splits words wider than the column budget, and never places a
/// 2-column rune where only 1 column remains (the classic CJK ragged-edge
/// bug). Returns line spans over the input — no per-line string allocs
/// beyond the list itself (cold path: text layout, not the frame loop).
/// </summary>
public static class TextWrap
{
    public static List<ReadOnlyMemory<char>> Wrap(ReadOnlySpan<char> text, int maxWidth)
    {
        var lines = new List<ReadOnlyMemory<char>>();
        if (maxWidth <= 0)
            return lines;

        foreach (var paragraph in SplitLines(text))
            WrapParagraph(paragraph, maxWidth, lines);

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
        int lineStart = 0;
        int used = 0;
        int? wordStart = null;
        int wordWidth = 0;

        void FlushLine(int end)
        {
            lines.Add(paragraph.Slice(lineStart, Math.Max(0, end - lineStart)));
        }

        int i = 0;
        while (i <= span.Length)
        {
            if (i == span.Length || char.IsWhiteSpace(span[i]))
            {
                // word boundary: place the pending word if it fits
                if (wordStart is { } ws)
                {
                    if (used + wordWidth > maxWidth && used > 0)
                    {
                        FlushLine(TrimEnd(span, lineStart, ws));
                        lineStart = TrimStart(span, ws, i);
                        used = WordWidthAt(span, lineStart, i);
                    }
                    else
                    {
                        used += wordWidth;
                    }
                    wordStart = null;
                    wordWidth = 0;
                }

                if (i == span.Length)
                    break;

                // the space itself: counts only mid-line
                int spaceW = 1;
                if (used > 0 && used + spaceW <= maxWidth)
                    used += spaceW;
                else if (used > 0)
                {
                    FlushLine(TrimEnd(span, lineStart, i));
                    lineStart = i + 1;
                    used = 0;
                }
                else
                {
                    lineStart = i + 1;
                }
                i++;
                continue;
            }

            // start/extend a word (cluster-aware: combining marks join)
            if (Rune.DecodeFromUtf16(span[i..], out var rune, out int consumed) != System.Buffers.OperationStatus.Done)
            {
                consumed = 1;
                rune = new Rune('?');
            }

            int w = UnicodeWidth.Of(rune);
            if (w == 0 && wordStart is not null)
            {
                // combining mark: attaches, no width
            }
            else
            {
                wordStart ??= i;
                // hard-split words wider than the budget
                if (w > maxWidth)
                {
                    if (used > 0)
                    {
                        FlushLine(TrimEnd(span, lineStart, i));
                        lineStart = i;
                        used = 0;
                    }
                    lines.Add(paragraph.Slice(i, 1));
                    lineStart = i + consumed;
                    used = 0;
                    wordStart = null;
                    i += consumed;
                    continue;
                }

                if (used + wordWidth + w > maxWidth && used > 0 && wordStart == i)
                {
                    FlushLine(TrimEnd(span, lineStart, i));
                    lineStart = i;
                    used = 0;
                }
                wordWidth += w;
            }

            i += consumed;
        }

        FlushLine(TrimEnd(span, lineStart, span.Length));
    }

    private static int TrimEnd(ReadOnlySpan<char> span, int start, int end)
    {
        while (end > start && char.IsWhiteSpace(span[end - 1])) end--;
        return end;
    }

    private static int TrimStart(ReadOnlySpan<char> span, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(span[start])) start++;
        return start;
    }

    private static int WordWidthAt(ReadOnlySpan<char> span, int start, int end)
    {
        int w = 0;
        int i = start;
        while (i < end)
        {
            if (Rune.DecodeFromUtf16(span[i..], out var rune, out int consumed) != System.Buffers.OperationStatus.Done)
            {
                consumed = 1;
                rune = new Rune('?');
            }
            w += UnicodeWidth.Of(rune);
            i += consumed;
        }
        return w;
    }
}
