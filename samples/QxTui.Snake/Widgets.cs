using System.Globalization;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;

namespace QxTui.Snake;

/// <summary>
/// The playfield. Fixed-step simulation (score-dependent speed) decoupled
/// from a 40Hz render tick; only steps/state changes invalidate, so between
/// steps the frame loop sleeps (on-demand rendering).
/// </summary>
internal sealed class SnakeWidget : Widget
{
    private readonly SnakeModel _model;
    private readonly Signal<int> _score;
    private readonly Signal<bool> _alive;
    private long _lastStepTs;
    private HalfBlockSurface? _surface;
    private readonly Scenery _scenery = new();

    // draw-time interpolation: the snake slides between grid cells instead
    // of teleporting per step — the single biggest smoothness win
    private List<Point> _prevBody = [];
    private long _stepTs;
    private double _stepIntervalMs = 130;
    public bool Paused;

    public SnakeWidget(QxTui.Animation.AnimationScheduler scheduler, Signal<int> score, Signal<bool> alive)
    {
        _model = new SnakeModel(40, 20);
        _score = score;
        _alive = alive;
        scheduler.Every(TimeSpan.FromMilliseconds(10), _ => Invalidate()); // 100Hz render
    }

    public SnakeModel Model => _model;

    /// <summary>Speed ramps with score: 130ms → 45ms per step.</summary>
    private double StepIntervalMs => Math.Max(45, 130 - _model.Score * 3);

    public void Turn(Direction d)
    {
        _model.Turn(d);
        Invalidate();
    }

    public void TogglePause()
    {
        if (!_model.Alive) return;
        Paused = !Paused;
        Invalidate();
    }

    public void Restart()
    {
        _model.Restart(0);
        Paused = false;
        _lastStepTs = 0;
        _score.Set(0);
        _alive.Set(true);
        Invalidate();
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        if (r.W < 8 || r.H < 5) return;

        // playfield in SQUARE pixels: one column per cell, two rows per cell
        int pw = r.W - 2, ph = (r.H - 2) * 2;
        if (_model.Width != pw || _model.Height != ph)
        {
            _model.Resize(pw, ph);
            _surface = null; // rebuild canvas for the new size
        }

        _surface ??= new HalfBlockSurface(buffer, r.X + 1, r.Y + 1, pw, ph,
            new Cell { Char = ' ', Fg = -1, Bg = 233 });

        // fixed-step simulation; snapshot the pre-step body for interpolation
        long now = Environment.TickCount64;
        if (!Paused && _model.Alive)
        {
            if (_lastStepTs == 0) _lastStepTs = now;
            while (now - _lastStepTs >= StepIntervalMs)
            {
                _lastStepTs += (long)StepIntervalMs;
                _prevBody = [.. _model.Body];
                _stepTs = now;
                _stepIntervalMs = StepIntervalMs;
                bool survived = _model.Step();
                _score.Set(_model.Score);
                if (!survived)
                {
                    _alive.Set(false);
                    break;
                }
            }
        }
        else
        {
            _lastStepTs = now;
        }

        float prog = _model.Alive && !Paused
            ? (float)Math.Clamp((now - _stepTs) / _stepIntervalMs, 0, 1)
            : 1f;

        // square pixels: scenery first, then food, then the interpolated snake
        _surface.Clear();
        _scenery.Paint(_surface, now / 1000.0);
        _surface.SetPixel(_model.Food.X, _model.Food.Y, 196);

        for (int i = _model.Body.Count - 1; i >= 0; i--)
        {
            var curr = _model.Body[i];
            var prev = i < _prevBody.Count ? _prevBody[i] : curr;
            int px = (int)(prev.X + (curr.X - prev.X) * prog + 0.5);
            int py = (int)(prev.Y + (curr.Y - prev.Y) * prog + 0.5);
            _surface.SetPixel(px, py, i == 0 ? 46 : 42);
        }
        _surface.Draw();

        // border: framework primitive
        Box.Draw(buffer, r.X, r.Y, r.W, r.H, BoxStyles.Rounded, 238);

        // overlays
        if (!_model.Alive)
        {
            Center(buffer, r, r.Y + r.H / 2 - 1, "GAME OVER", 196, bold: true);
            Center(buffer, r, r.Y + r.H / 2,
                string.Create(CultureInfo.InvariantCulture, $"score {_model.Score}"), 250);
            Center(buffer, r, r.Y + r.H / 2 + 1, "enter: restart", 244);
        }
        else if (Paused)
        {
            Center(buffer, r, r.Y + r.H / 2 - 1, "PAUSED", 214, bold: true);
            Center(buffer, r, r.Y + r.H / 2, "space: resume", 244);
            Center(buffer, r, r.Y + r.H / 2 + 1, "f: cycle fps   1-4: 30/60/120/240", 244);
        }
    }

    private static void Center(CellBuffer buffer, Rect r, int y, string text, int fg, bool bold = false)
    {
        int x = r.X + Math.Max(1, (r.W - text.Length) / 2);
        buffer.WriteTextClipped(new RenderClip(r.X, r.Y, r.W, r.H), x, y, text, fg: fg, bg: 233, bold: bold);
    }
}

/// <summary>Score line. Subscribes to signals: re-renders ONLY when score or
/// alive-state actually changes — the isolation demo, again.</summary>
internal sealed class HudWidget : Widget
{
    private readonly IDisposable _s1;
    private readonly IDisposable _s2;
    private readonly IDisposable _s3;
    private readonly Signal<int> _score;
    private readonly Signal<bool> _alive;
    private readonly Signal<int> _fps;

    public HudWidget(Signal<int> score, Signal<bool> alive, Signal<int> fps)
    {
        _score = score;
        _alive = alive;
        _fps = fps;
        _s1 = score.Subscribe(_ => Invalidate());
        _s2 = alive.Subscribe(_ => Invalidate());
        _s3 = fps.Subscribe(_ => Invalidate());
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = ' ', Fg = 250, Bg = 236 });
        string state = !_alive.Value ? "DEAD" : "ALIVE";
        buffer.WriteTextClipped(Clip, r.X + 1, r.Y,
            $" qxtui snake │ score {_score.Value} │ {state} │ {_fps.Value}fps ",
            fg: 255, bg: 236, bold: true);
    }

    public override void Dispose()
    {
        _s1.Dispose();
        _s2.Dispose();
        _s3.Dispose();
    }
}

internal sealed class HelpWidget : Widget
{
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = ' ', Fg = 250, Bg = 236 });
        buffer.WriteTextClipped(Clip, r.X + 1, r.Y,
            " arrows/wasd/hjkl: turn   space: pause   enter: restart   q: quit ", fg: 250, bg: 236);
    }
}
