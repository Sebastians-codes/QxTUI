using System.Text;

namespace QxTui.Render;

/// <summary>
/// Terminal column width of unicode text (wcwidth-style, documented
/// approximations where the standard is fuzzy).
///
///  - combining marks (Mn/Me) and zero-width joiners/variation selectors: 0
///  - East Asian Wide/Fullwidth + most emoji: 2
///  - everything else: 1
///  - ZWJ emoji sequences (👨‍👩‍): counted as ONE 2-wide glyph, not the sum
///
/// Layout, wrapping and cursor math all go through this — a TUI that gets
/// widths wrong drifts every column after the first CJK char.
/// </summary>
public static class UnicodeWidth
{
    public const int Zero = 0;
    public const int Narrow = 1;
    public const int Wide = 2;

    public static int Of(Rune rune)
    {
        int cp = rune.Value;

        // zero-width: combining marks, joiners, variation selectors, BOM
        if (IsZeroWidth(cp))
            return Zero;

        // surrogate pairs handled by Rune; supplementary planes:
        // emoji + CJK extensions are wide
        if (cp >= 0x1F000)
            return cp switch
            {
                >= 0x1F300 and <= 0x1F64F => Wide, // emoticons, dingbats
                >= 0x1F680 and <= 0x1F6FF => Wide, // transport
                >= 0x1F900 and <= 0x1F9FF => Wide, // supplemental symbols
                >= 0x1FA70 and <= 0x1FAFF => Wide, // extended-A symbols
                >= 0x20000 and <= 0x3FFFD => Wide, // CJK ext B..F + compat
                _ => Narrow,
            };

        // BMP wide ranges (East Asian W/F)
        if (cp is >= 0x1100 and <= 0x115F or     // Hangul jamo
                >= 0x2E80 and <= 0x303E or       // CJK radicals, kangxi, cjk symbols
                >= 0x3041 and <= 0x33FF or       // kana, hangul compat, cjk compat
                >= 0x3400 and <= 0x4DBF or       // CJK ext A
                >= 0x4E00 and <= 0x9FFF or       // CJK unified
                >= 0xA000 and <= 0xA4CF or       // yi
                >= 0xAC00 and <= 0xD7A3 or       // hangul syllables
                >= 0xF900 and <= 0xFAFF or       // cjk compat ideographs
                >= 0xFE30 and <= 0xFE4F or       // cjk compat forms
                >= 0xFF00 and <= 0xFF60 or       // fullwidth forms
                >= 0xFFE0 and <= 0xFFE6)         // fullwidth signs
            return Wide;

        return Narrow;
    }

    private static bool IsZeroWidth(int cp) =>
        cp is >= 0x0300 and <= 0x036F or   // combining diacriticals
            >= 0x20D0 and <= 0x20FF or     // combining marks for symbols
            0x200B or 0x200C or 0x200D or  // zero-width space/joiners
            0xFEFF;                        // BOM / zero-width no-break

    /// <summary>True for combining marks (category Mn/Me) — width 0.</summary>
    public static bool IsCombining(Rune rune)
    {
        var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(rune.Value);
        return cat is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.EnclosingMark;
    }

    /// <summary>
    /// Column width of a text run. ZWJ sequences count once (2), variation
    /// selectors and combining marks attach to the previous base (0).
    /// </summary>
    public static int OfText(ReadOnlySpan<char> text)
    {
        int total = 0;
        bool inZwj = false;

        int i = 0;
        while (i < text.Length)
        {
            if (Rune.DecodeFromUtf16(text[i..], out var rune, out int consumed) != System.Buffers.OperationStatus.Done)
            {
                i++;
                continue;
            }

            int cp = rune.Value;

            if (cp == 0x200D) // ZWJ: glue next glyph into current cluster
            {
                inZwj = true;
                i += consumed;
                continue;
            }

            if (cp == 0xFE0F || cp == 0xFE0E || IsCombining(rune) || IsZeroWidth(cp))
            {
                i += consumed;
                continue; // attaches to previous cluster: no extra columns
            }

            if (inZwj)
            {
                // continuation of a ZWJ sequence: already counted as 2
                inZwj = false;
                i += consumed;
                continue;
            }

            total += Of(rune);
            i += consumed;
        }

        return total;
    }
}
