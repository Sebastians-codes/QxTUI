namespace QxTui.Render;

/// <summary>
/// An axis-aligned dirty region in cell coordinates.
/// </summary>
public struct DamageRect : IEquatable<DamageRect>
{
    public int X, Y, W, H;

    public DamageRect(int x, int y, int w, int h)
    {
        X = x; Y = y; W = w; H = h;
    }

    public readonly int Right => X + W;
    public readonly int Bottom => Y + H;
    public readonly int Area => W * H;
    public readonly bool IsEmpty => W <= 0 || H <= 0;

    public readonly bool Intersects(in DamageRect o) =>
        X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    /// <summary>True when rects overlap OR touch (share an edge) — merging
    /// touching rects reduces cursor seeks at negligible repaint cost.</summary>
    public readonly bool Touches(in DamageRect o) =>
        X <= o.Right && o.X <= Right && Y <= o.Bottom && o.Y <= Bottom;

    public readonly DamageRect Union(in DamageRect o)
    {
        int x = Math.Min(X, o.X);
        int y = Math.Min(Y, o.Y);
        return new DamageRect(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
    }

    public readonly bool Equals(DamageRect other) => X == other.X && Y == other.Y && W == other.W && H == other.H;
    public override readonly bool Equals(object? obj) => obj is DamageRect r && Equals(r);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, W, H);
    public override readonly string ToString() => $"[{X},{Y} {W}x{H}]";
}

/// <summary>
/// Coalesces overlapping/touching damage rects (AGENT.md §2: damage rects →
/// coalesce). Operates in-place on a caller-owned list; O(n²) is fine at
/// terminal scale (n is typically &lt; 32).
/// </summary>
public static class DamageCoalescer
{
    public static void Coalesce(List<DamageRect> rects)
    {
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < rects.Count && !merged; i++)
            {
                for (int j = i + 1; j < rects.Count; j++)
                {
                    var a = rects[i];
                    var b = rects[j];
                    if (a.Touches(b))
                    {
                        rects[i] = a.Union(b);
                        rects[j] = rects[^1];
                        rects.RemoveAt(rects.Count - 1);
                        merged = true;
                        break;
                    }
                }
            }
        }

        // Raster order: top-to-bottom, left-to-right — minimizes cursor travel.
        rects.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
    }
}
