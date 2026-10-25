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
                rng.NextDouble() * 0.55,          // upper sky only
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

        // sky gradient: deep space → dusk purple at the horizon
        for (int y = 0; y < horizon; y++)
        {
            int c = SkyColor(y, horizon);
            for (int x = 0; x < w; x++)
                s.SetPixel(x, y, c);
        }

        // ground
        for (int y = horizon; y < h; y++)
            for (int x = 0; x < w; x++)
                s.SetPixel(x, y, 16);

        // stars: drift + twinkle
        for (int i = 0; i < _stars.Length; i++)
        {
            ref readonly var st = ref _stars[i];
            double fx = (st.X + t * st.Speed) % 1.0;
            int sx = (int)(fx * w);
            int sy = (int)(st.Y * horizon);
            double tw = Math.Sin(t * st.Twinkle + st.Phase);
            int c = tw > 0.4 ? 252 : tw > -0.3 ? 244 : 60;
            s.SetPixel(sx, sy, c);
        }

        // moon with a soft glow
        int mx = w * 78 / 100, my = Math.Max(2, horizon * 28 / 100);
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int d = dx * dx + dy * dy;
                if (d <= 1) s.SetPixel(mx + dx, my + dy, 230);
                else if (d <= 4) s.SetPixel(mx + dx, my + dy, 95); // glow
            }

        // far ridge (slow parallax), then near ridge (faster, darker)
        PaintRidge(s, t * 0.6, horizon, horizon * 22 / 100, 18, _p1, _p2);
        PaintRidge(s, t * 1.7, horizon, horizon * 34 / 100, 0, _p2, _p3);
    }

    private static int SkyColor(int y, int horizon)
    {
        // 16 → 17 → 18 → 54 towards the horizon
        double f = (double)y / Math.Max(1, horizon);
        if (f < 0.45) return 16;
        if (f < 0.72) return 17;
        if (f < 0.90) return 18;
        return 54;
    }

    private static void PaintRidge(HalfBlockSurface s, double scroll, int horizon,
        int amplitude, int color, double pa, double pb)
    {
        int w = s.PixelWidth;
        for (int x = 0; x < w; x++)
        {
            double u = (x + scroll * 12.0) * 0.09;
            double hgt = 0.55 + 0.30 * Math.Sin(u + pa) + 0.15 * Math.Sin(u * 2.7 + pb);
            int top = horizon - (int)(hgt * amplitude);
            for (int y = Math.Max(0, top); y < horizon + amplitude / 3; y++)
                s.SetPixel(x, y, color);
        }
    }
}
