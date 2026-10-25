using System.Globalization;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;

namespace QxTui.Playground;

/// <summary>Top line: theme name, fps target, live bytes/frame.
/// Shows OnSignal + Tick coexisting on one widget.</summary>
internal sealed class HudWidget : Widget
{
    private readonly Func<string> _themeName;
    private readonly Signal<int> _fps;

    public HudWidget(Func<string> themeName, Signal<int> fps)
    {
        _themeName = themeName;
        _fps = fps;
        OnSignal(fps);                          // repaint on fps change
        Tick(TimeSpan.FromMilliseconds(250));   // repaint for bytes/frame
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        var stats = App?.Stats;
        buffer.FillRect(r.X, r.Y, r.W, r.H,
            new Cell { Char = ' ', Fg = Theme.Get(ThemeRole.Text), Bg = Theme.Get(ThemeRole.Faint) });
        buffer.WriteTextClipped(Clip, r.X, r.Y,
            QxTui.Render.Text.Ellipsize(
                $" qxtui playground │ theme: {_themeName()} │ {_fps.Value}fps │ "
                + $"{stats?.LastBytesWritten.ToString("N0", CultureInfo.InvariantCulture) ?? "-"} B/frame ",
                r.W),
            fg: 255, bg: Theme.Get(ThemeRole.Faint), bold: true);
    }
}

/// <summary>Feeds two ring histories at 20Hz and invalidates the sparklines
/// that depend on them — Tick with a custom callback (no self-invalidate).</summary>
internal sealed class FeederWidget : Widget
{
    private readonly RingHistory _sine;
    private readonly RingHistory _walk;
    private double _t;
    private float _level = 50f;
    private readonly Random _rng = new(1234);

    // no invalidation wiring: RingHistory.Pushed notifies bound sparklines
    public FeederWidget(RingHistory sine, RingHistory walk)
    {
        _sine = sine;
        _walk = walk;

        Tick(TimeSpan.FromMilliseconds(50), _ =>
        {
            _t += 0.05;
            _sine.Push((float)(50 + 45 * Math.Sin(_t * 2.1)));
            _level = Math.Clamp(_level + (float)(_rng.NextDouble() - 0.5) * 18, 2, 98);
            _walk.Push(_level);
        });
    }

    /// <summary>Zero-height: this widget only feeds data, never paints.</summary>
    public override void Render(CellBuffer buffer) { }
}

/// <summary>Bouncing ball on square half-block pixels, with a fading trail.
/// The fast-moving element that makes the fps target visible.</summary>
internal sealed class BallWidget : Widget
{
    private float _px = 0.3f, _py = 0.4f, _vx = 0.55f, _vy = 0.4f;
    // time-based trail: a frame-count trail shrinks as fps rises (24 points
    // = 0.2s at 120fps but 0.024s at 1000fps). Age-based expiry keeps the
    // tail the same LENGTH IN TIME at any frame rate.
    private readonly Queue<(int X, int Y, long TsMs)> _trail = new();
    private const int TrailMs = 250;
    private long _lastTs = Environment.TickCount64;
    private readonly Signal<float> _speed;

    /// <summary>Test hook: pixel-canvas height in square pixels.</summary>
    public int SurfacePixelHeight => _probeHeight;
    private int _probeHeight;

    public BallWidget(Signal<float> speed)
    {
        _speed = speed;
        Node.ChildInset = 1; // frame ring; children (slider row) live inside
        TickPerFrame(); // refresh follows the app's fps target, whatever it is
    }

    /// <summary>0..1 slider → 0.2x..100x, piecewise-exponential with 1.0x
    /// exactly at mid-travel: 0.2→1 over the first half, 1→100 over the
    /// second. (A single exponential put 1x at an arbitrary spot — the
    /// default read 1.3x and felt like broken math.)</summary>
    public static float MultFor(float v) => v <= 0.5f
        ? 0.2f * MathF.Pow(5f, v * 2f)
        : MathF.Pow(100f, (v - 0.5f) * 2f);
    private float Mult => MultFor(_speed.Value);

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        if (r.W < 6 || r.H < 4) return;

        // surface fills the inset area ABOVE the child row (last inner row
        // belongs to children, e.g. the slider)
        // framework-cached canvas, rebuilt on any rect change. Height is
        // r.H-1 so the surface STOPS ABOVE the slider strip: the ball
        // bounces off the UI strip instead of sliding underneath it
        var (surface, rebuilt) = Pixels(new Rect(r.X, r.Y, r.W, r.H - 1),
            new Cell { Char = ' ', Fg = -1, Bg = Theme.Get(ThemeRole.PanelBg) });
        if (rebuilt)
            _trail.Clear();
        _probeHeight = surface.PixelHeight;

        long now = Environment.TickCount64;
        double dt = Math.Min(0.1, (now - _lastTs) / 1000.0);
        _lastTs = now;

        // substep at high multipliers: without this, 100x moves ~1 screen
        // per frame and the ball tunnels through walls instead of bouncing
        float m = Mult;
        double travel = 0.8 * m * dt;                 // worst-case fraction this frame
        int steps = Math.Clamp((int)Math.Ceiling(travel / 0.02), 1, 128);
        double sdt = m * dt / steps;
        for (int sub = 0; sub < steps; sub++)
        {
            _px += _vx * (float)sdt;
            _py += _vy * (float)sdt;
            if (_px is < 0 or > 1) { _vx = -_vx; _px = Math.Clamp(_px, 0, 1); }
            if (_py is < 0 or > 1) { _vy = -_vy; _py = Math.Clamp(_py, 0, 1); }
        }

        int bx = (int)(_px * (surface.PixelWidth - 1));
        int by = (int)(_py * (surface.PixelHeight - 1));
        _trail.Enqueue((bx, by, now));
        while (_trail.Count > 0 && now - _trail.Peek().TsMs > TrailMs)
            _trail.Dequeue();

        surface.Clear();
        foreach (var (tx, ty, ts) in _trail)
        {
            // fade by AGE: oldest dim → newest bright, identical at any fps
            int age = (int)(now - ts);
            int c = 252 - (age * (252 - 236)) / TrailMs;
            surface.SetPixel(tx, ty, Math.Clamp(c, 236, 252));
        }
        surface.SetPixel(bx, by, 255);
        surface.Draw();

        // opaque strip where the child slider row lives: the ball must read
        // as colliding with the UI, not sneaking behind transparent chrome
        buffer.FillRect(r.X + 1, r.Bottom - 2, r.W - 2, 1,
            new Cell { Char = ' ', Fg = -1, Bg = Theme.Get(ThemeRole.PanelBg) });

        Box.Draw(buffer, r.X, r.Y, r.W, r.H, BoxStyles.Rounded,
            Theme.Get(ThemeRole.Border), "square pixels", Theme.Get(ThemeRole.Title));
    }
}

/// <summary>Never invalidates after first paint — the isolation proof:
/// its RenderCount in the stats panel stays 1 forever.</summary>
internal sealed class StaticPanelWidget : Widget
{
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Box.Draw(buffer, r.X, r.Y, r.W, r.H, BoxStyles.Single,
            Theme.Get(ThemeRole.Border), "damage isolation", Theme.Get(ThemeRole.Title));

        string[] lines =
        [
            "This panel rendered exactly",
            "once. Everything around it",
            "animates at 20-100Hz, yet",
            "these cells cost 0 bytes",
            "per frame. Check its render",
            "count in the STATS panel →",
        ];
        for (int i = 0; i < lines.Length && r.Y + 1 + i < r.Bottom - 1; i++)
            buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1 + i,
                QxTui.Render.Text.Ellipsize(lines[i], r.W - 4), fg: Theme.Get(ThemeRole.Text));
    }
}

/// <summary>Live framework telemetry: wire bytes, presented fps, drops, and
/// the render counts that prove isolation.</summary>
internal sealed class StatsPanelWidget : Widget
{
    private readonly StaticPanelWidget _staticPanel;
    private readonly BallWidget _ball;

    public StatsPanelWidget(StaticPanelWidget staticPanel, BallWidget ball)
    {
        _staticPanel = staticPanel;
        _ball = ball;
        Tick(TimeSpan.FromMilliseconds(250));
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        var s = App?.Stats;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Box.Draw(buffer, r.X, r.Y, r.W, r.H, BoxStyles.Single,
            Theme.Get(ThemeRole.Border), "stats", Theme.Get(ThemeRole.Warning));

        string[] lines = s is null ? [] :
        [
            $"bytes/frame: {s.LastBytesWritten.ToString("N0", CultureInfo.InvariantCulture)}",
            $"dirty cells: {s.LastDirtyCells}  rects: {s.LastDamageRects}",
            $"produced: {s.Fps.ToString("F0", CultureInfo.InvariantCulture)} fps"
            + (App is { } host && s.Fps < host.RequestedFps
                ? $" (adapted from {host.RequestedFps})" : " (target-capped)"),
            $"presented: {s.PresentedFps.ToString("F0", CultureInfo.InvariantCulture)} fps (terminal-capped)",
            App is { } m
                ? $"mouse: {(m.CellPixelH > 0
                    ? $"pixel {m.CellPixelW}x{m.CellPixelH}px cells (sub-cell drags)"
                    : "cell (terminal ignored CSI 16 t)")}"
                : "mouse: -",
            $"dropped: {s.DroppedFrames.ToString("N0", CultureInfo.InvariantCulture)}",
            $"static renders: {_staticPanel.RenderCount}",
            $"ball renders:   {_ball.RenderCount.ToString("N0", CultureInfo.InvariantCulture)}",
        ];
        for (int i = 0; i < lines.Length && r.Y + 1 + i < r.Bottom - 1; i++)
            buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1 + i, lines[i], fg: Theme.Get(ThemeRole.Text));
    }
}
