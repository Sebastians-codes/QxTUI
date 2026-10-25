using System.Text;

namespace QxTui.Render;

/// <summary>Width-aware single-line text helpers.</summary>
public static class Text
{
    /// <summary>
    /// Fit text into <paramref name="maxWidth"/> columns: wide runes count
    /// double, and a clipped line ends in '…' instead of a hard mid-word cut.
    /// Never splits a wide rune.
    /// </summary>
    public static string Ellipsize(ReadOnlySpan<char> text, int maxWidth)
    {
        if (maxWidth <= 0) return string.Empty;

        int total = UnicodeWidth.OfText(text);
        if (total <= maxWidth)
            return text.ToString();

        if (maxWidth == 1)
            return "…";

        var sb = new StringBuilder(text.Length);
        int used = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (Rune.DecodeFromUtf16(text[i..], out var rune, out int consumed)
                != System.Buffers.OperationStatus.Done)
            {
                rune = new Rune('?');
                consumed = 1;
            }

            int w = UnicodeWidth.Of(rune);
            if (used + w > maxWidth - 1) // reserve 1 col for the ellipsis
                break;

            sb.Append(text.Slice(i, consumed));
            used += w;
            i += consumed;
        }

        sb.Append('…');
        return sb.ToString();
    }
}
