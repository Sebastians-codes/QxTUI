namespace QxTui.Render;

/// <summary>
/// One terminal cell. PoC scope: single UTF-16 char (braille/BMP safe),
/// 256-color palette indices (-1 = terminal default), bold flag.
/// Value-equality is what drives the frame diff.
/// </summary>
public struct Cell : IEquatable<Cell>
{
    public char Char;
    public int Fg;  // -1 = default
    public int Bg;  // -1 = default
    public bool Bold;

    public static readonly Cell Empty = new() { Char = ' ', Fg = -1, Bg = -1 };

    public readonly bool Equals(Cell other) =>
        Char == other.Char && Fg == other.Fg && Bg == other.Bg && Bold == other.Bold;

    public override readonly bool Equals(object? obj) => obj is Cell c && Equals(c);

    public override readonly int GetHashCode() =>
        HashCode.Combine(Char, Fg, Bg, Bold);
}
