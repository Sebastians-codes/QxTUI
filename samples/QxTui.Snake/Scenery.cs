using QxTui.Render;

namespace QxTui.Snake;

/// <summary>
/// Generative nightscape painted BEHIND the game on the same square-pixel
/// surface: gradient sky, drifting twinkling stars, moon, and two parallax
/// ridge lines scrolling at different speeds.
///
/// Deliberately dim palette (deep blues/blacks) so gameplay pixels (bright
/// green snake, red food) keep full contrast. Deterministic per seed.
/// Mostly static between frames — the diff only transmits star twinkle and
/// ridge edges, so the scenery is nearly free on the wire.
/// </summary>
internal sealed class Scenery(int seed = 7)
{
    private readonly struct Star(double x, double y, double speed, double twinkle, double phase)
    {
        public readonly double X = x;
        public readonly double Y = y;
        public readonly double Speed = speed;
        public readonly double Twinkle = twinkle;
        public readonly double Phase = phase;
    }

    // Fixed pool in proportional coords; the COUNT used scales with the
    // surface area (stable subset → resizing never reshuffles the sky).
    private readonly Star[] _stars = MakeStars(seed);
    private readonly double _p1 = new Random(seed).NextDouble() * Math.PI * 2;
    private readonly double _p2 = new Random(seed + 1).NextDouble() * Math.PI * 2;
    private readonly double _p3 = new Random(seed + 2).NextDouble() * Math.PI * 2;

    private static Star[] MakeStars(int seed)
    {
        var rng = new Random(seed + 100);
        var stars = new Star[70];
        for (int i = 0; i < stars.Length; i++)
        {
            stars[i] = new Star(
                rng.NextDouble(),
                rng.NextDouble() * 0.42,          // above the ridgeline
                0.002 + rng.NextDouble() * 0.006, // slow drift
                1.0 + rng.NextDouble() * 3.0,     // twinkle rate
                rng.NextDouble() * Math.PI * 2);
        }
        return stars;
    }

    public void Paint(HalfBlockSurface s, double t)
    {
        int w = s.PixelWidth, h = s.PixelHeight;
        if (w < 8 || h < 8) return;

        int horizon = h * 62 / 100;
        int starCount = Math.Clamp(w * h / 120, 12, _stars.Length);
        double ridgeScale = 14.0 / w;   // ridge wavelength ∝ width
        int moonR = Math.Clamp(Math.Min(w, h) / 30, 1, 3);

        // sky gradient: deep space → dusk purple at the horizon
        for (int y = 0; y < horizon; y++)
        {
            int c = SkyColor(y, horizon);
            for (int x = 0; x < w; x++)
                s.SetPixel(x, y, c);
        }

        // ground base (near ridge paints over it down to the bottom)
        for (int y = horizon; y < h; y++)
            for (int x = 0; x < w; x++)
                s.SetPixel(x, y, 233);

        // stars: drift + twinkle
        for (int i = 0; i < starCount; i++)
        {
            ref readonly var st = ref _stars[i];
            double fx = (st.X + t * st.Speed) % 1.0;
            int sx = (int)(fx * w);
            int sy = (int)(st.Y * horizon);
            double tw = Math.Sin(t * st.Twinkle + st.Phase);
            int c = tw > 0.4 ? 252 : tw > -0.3 ? 244 : 236; // off-state melts into sky
            s.SetPixel(sx, sy, c);
        }

        // moon with a soft glow, sized to the window
        int mx = w * 78 / 100, my = Math.Max(moonR + 1, horizon * 28 / 100);
        for (int dy = -moonR - 1; dy <= moonR + 1; dy++)
            for (int dx = -moonR - 1; dx <= moonR + 1; dx++)
            {
                int d = dx * dx + dy * dy;
                if (d <= moonR * moonR) s.SetPixel(mx + dx, my + dy, 230);
                else if (d <= (moonR + 1) * (moonR + 1)) s.SetPixel(mx + dx, my + dy, 244);
            }

        // far ridge (slow parallax), then near ridge (faster, darkest,
        // painted down to the bottom so there is no dead band)
        PaintRidge(s, t * 0.6, horizon, horizon * 22 / 100, 235, ridgeScale, _p1, _p2, horizon + horizon * 22 / 100 / 3);
        PaintRidge(s, t * 1.7, horizon, horizon * 34 / 100, 232, ridgeScale, _p2, _p3, h);
        // shooting star: a FAST element so the fps target is actually
        // visible — steps at 30fps, smooth streak at 240. One per 7s cycle,
        // deterministic per cycle index (allocation-free hash, no Random).
        double cycle = t % 7.0;
        if (cycle < 1.1)
        {
            long idx = (long)(t / 7.0);
            double sx = 0.3 + Hash(idx, seed, 1) * 0.4;
            double sy = 0.05 + Hash(idx, seed, 2) * 0.25;
            double vx = (0.08 + Hash(idx, seed, 3) * 0.14) * (Hash(idx, seed, 4) < 0.5 ? -1 : 1);
            double vy = 0.15 + Hash(idx, seed, 5) * 0.25;

            double hx = (sx + vx * cycle) * w;
            double hy = (sy + vy * cycle) * horizon;
            // tail trails opposite the motion, spaced in whole pixels —
            // sub-pixel spacing piled every tail pixel onto the head
            int sgn = vx >= 0 ? -1 : 1;
            for (int k = 0; k < 10; k++)
            {
                int px = (int)(hx + sgn * k * 1.5);
                int py = (int)(hy - k * 0.6);
                int c = k == 0 ? 255 : k < 3 ? 252 : k < 6 ? 244 : 236;
                s.SetPixel(px, py, c);
            }
        }

    }

    /// <summary>Deterministic 0..1 hash — stable scenery without allocations.</summary>
    private static double Hash(long n, int seed, int salt)
    {
        ulong x = (ulong)(n * 6364136223846793005L + (seed * 31 + salt) * 1442695040888963407L);
        x ^= x >> 33;
        x *= 0xff51afd7ed558ccdL;
        x ^= x >> 33;
        return (x >> 11) / (double)(1UL << 53);
    }

    private static int SkyColor(int y, int horizon)
    {
        // Grayscale ramp only (232-250): terminal themes remap cube colors
        // like 17/18/54 into saturated blues/magentas, but leave the gray
        // ramp alone. Night sky = subtle vertical gray gradient.
        double f = (double)y / Math.Max(1, horizon);
        if (f < 0.45) return 234;
        if (f < 0.72) return 235;
        if (f < 0.90) return 236;
        return 237;
    }

    private static void PaintRidge(HalfBlockSurface s, double scroll, int horizon,
        int amplitude, int color, double ridgeScale, double pa, double pb, int fillTo)
    {
        int w = s.PixelWidth;
        for (int x = 0; x < w; x++)
        {
            double u = (x + scroll * 12.0) * ridgeScale;
            double hgt = 0.55 + 0.30 * Math.Sin(u + pa) + 0.15 * Math.Sin(u * 2.7 + pb);
            int top = horizon - (int)(hgt * amplitude);
            for (int y = Math.Max(0, top); y < fillTo; y++)
                s.SetPixel(x, y, color);
        }
    }
}
