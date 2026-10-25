namespace QxTui.Layout;

public struct Size(int width, int height) : IEquatable<Size>
{
    public int Width = width;
    public int Height = height;

    public static readonly Size Zero = new(0, 0);

    public readonly bool Equals(Size other) => Width == other.Width && Height == other.Height;
    public override readonly bool Equals(object? obj) => obj is Size s && Equals(s);
    public override readonly int GetHashCode() => HashCode.Combine(Width, Height);
    public override readonly string ToString() => $"{Width}x{Height}";
}

/// <summary>Absolute cell-space rectangle (output of layout).</summary>
public struct Rect(int x, int y, int w, int h) : IEquatable<Rect>
{
    public int X = x;
    public int Y = y;
    public int W = w;
    public int H = h;

    public readonly int Right => X + W;
    public readonly int Bottom => Y + H;

    public readonly bool Contains(int px, int py) =>
        px >= X && px < Right && py >= Y && py < Bottom;

    public readonly bool Equals(Rect other) => X == other.X && Y == other.Y && W == other.W && H == other.H;
    public override readonly bool Equals(object? obj) => obj is Rect r && Equals(r);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, W, H);
    public override readonly string ToString() => $"({X},{Y}) {W}x{H}";
}
