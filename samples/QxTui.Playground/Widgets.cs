using System.Globalization;
using QxTui.Core;
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
            $" qxtui playground │ theme: {_themeName()} │ {_fps.Value}fps │ "
            + $"{stats?.LastBytesWritten.ToString("N0", CultureInfo.InvariantCulture) ?? "-"} B/frame ",
            fg: 255, bg: Theme.Get(ThemeRole.Faint), bold: true);
    }
}

/// <summary>Feeds two ring histories at 20Hz and invalidates the sparklines
/// that depend on them — Tick with a custom callback (no self-invalidate).</summary>
internal sealed class FeederWidget : Widget
{
    private readonly RingHistory _sine;
    private readonly RingHistory _walk;
    private readonly Action _onChanged;
    private double _t;
    private float _level = 50f;
    private readonly Random _rng = new(1234);

    public FeederWidget(RingHistory sine, RingHistory walk, Action onChanged)
    {
        _sine = sine;
        _walk = walk;
        _onChanged = onChanged;

        Tick(TimeSpan.FromMilliseconds(50), _ =>
        {
            _t += 0.05;
            _sine.Push((float)(50 + 45 * Math.Sin(_t * 2.1)));
            _level = Math.Clamp(_level + (float)(_rng.NextDouble() - 0.5) * 18, 2, 98);
            _walk.Push(_level);
            _onChanged();
        });
    }

    /// <summary>Zero-height: this widget only feeds data, never paints.</summary>
    public override void Render(CellBuffer buffer) { }
}

/// <summary>Bouncing ball on square half-block pixels, with a fading trail.
/// The fast-moving element that makes the fps target visible.</summary>
internal sealed class BallWidget : Widget
{
    private HalfBlockSurface? _surface;
    private float _px = 0.3f, _py = 0.4f, _vx = 0.55f, _vy = 0.4f;
    private readonly Queue<(int X, int Y)> _trail = new();
    private long _lastTs = Environment.TickCount64;
    private readonly Signal<float> _speed;
    private Rect _lastRect;

    public BallWidget(Signal<float> speed)
    {
        _speed = speed;
        Tick(TimeSpan.FromMilliseconds(10)); // 100Hz
    }

    /// <summary>0..1 slider value → 0.2x..3.0x ball speed.</summary>
    private float Mult => 0.2f + _speed.Value * 2.8f;

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        if (r.W < 6 || r.H < 4) return;

        int pw = r.W - 2, ph = (r.H - 2) * 2;
        // ANY rect change (size OR position) needs a new surface: a stale
        // origin paints the ball into the wrong place, and stale trail
        // points from the old coordinate space show up as lone specks
        if (_surface is null || _lastRect != r)
        {
            _surface = new HalfBlockSurface(buffer, r.X + 1, r.Y + 1, pw, ph,
                new Cell { Char = ' ', Fg = -1, Bg = Theme.Get(ThemeRole.PanelBg) });
            _trail.Clear();
            _lastRect = r;
        }

        long now = Environment.TickCount64;
        double dt = Math.Min(0.1, (now - _lastTs) / 1000.0);
        _lastTs = now;

        float m = Mult;
        _px += _vx * m * (float)dt;
        _py += _vy * m * (float)dt;
        if (_px is < 0 or > 1) { _vx = -_vx; _px = Math.Clamp(_px, 0, 1); }
        if (_py is < 0 or > 1) { _vy = -_vy; _py = Math.Clamp(_py, 0, 1); }

        int bx = (int)(_px * (pw - 1));
        int by = (int)(_py * (ph - 1));
        _trail.Enqueue((bx, by));
        while (_trail.Count > 24) _trail.Dequeue();

        _surface.Clear();
        int i = 0;
        foreach (var (tx, ty) in _trail)
        {
            // fade: oldest dim → newest bright, grayscale so any theme works
            int c = 236 + (i * (252 - 236)) / Math.Max(1, _trail.Count - 1);
            _surface.SetPixel(tx, ty, c);
            i++;
        }
        _surface.SetPixel(bx, by, 255);
        _surface.Draw();

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
            buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1 + i, lines[i], fg: Theme.Get(ThemeRole.Text));
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
            $"produced: {s.Fps.ToString("F0", CultureInfo.InvariantCulture)} fps (target-capped)",
            $"presented: {s.PresentedFps.ToString("F0", CultureInfo.InvariantCulture)} fps (terminal-capped)",
            $"dropped: {s.DroppedFrames.ToString("N0", CultureInfo.InvariantCulture)}",
            $"static renders: {_staticPanel.RenderCount}",
            $"ball renders:   {_ball.RenderCount.ToString("N0", CultureInfo.InvariantCulture)}",
        ];
        for (int i = 0; i < lines.Length && r.Y + 1 + i < r.Bottom - 1; i++)
            buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1 + i, lines[i], fg: Theme.Get(ThemeRole.Text));
    }
}
