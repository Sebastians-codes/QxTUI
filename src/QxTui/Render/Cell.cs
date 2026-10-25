namespace QxTui.Render;

/// <summary>
/// One terminal cell. PoC scope: single UTF-16 char (braille/BMP safe),
/// 256-color palette indices (-1 = terminal default), bold flag.
/// Value-equality is what drives the frame diff.
/// </summary>
public struct Cell : IEquatable<Cell>
{
    public char Char;

    /// <summary>Second cell of a 2-column rune (CJK/emoji). The serializer
    /// emits NOTHING for pad cells — the leading wide char already advances
    /// the cursor by two. For supplementary-plane runes (surrogate pairs)
    /// the pad cell carries the low surrogate.</summary>
    public bool WidePad;

    public int Fg;  // -1 = default
    public int Bg;  // -1 = default
    public bool Bold;

    public static readonly Cell Empty = new() { Char = ' ', Fg = -1, Bg = -1 };

    public readonly bool Equals(Cell other) =>
        Char == other.Char && WidePad == other.WidePad &&
        Fg == other.Fg && Bg == other.Bg && Bold == other.Bold;

    public override readonly bool Equals(object? obj) => obj is Cell c && Equals(c);

    public override readonly int GetHashCode() =>
        HashCode.Combine(Char, WidePad, Fg, Bg, Bold);
}
