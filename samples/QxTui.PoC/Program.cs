using System.Diagnostics;
using System.Globalization;
using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;

// ---------------------------------------------------------------------------
// qxtui PoC — proves on screen (AGENT.md §1 core claims):
//   1. isolated updates   → static panel's render count stays 1 while
//                           spinner, bar and 500 particles animate
//   2. declarative anim   → progress bar = Transition(2s, OutCubic, Reverse)
//   3. 200fps-class C#    → live fps + frame-work p50/p99 counters
//   4. flexbox + resize   → drag the terminal window; relayouts counter ticks,
//                           bytes/frame stays tiny (damage isolation)
// ---------------------------------------------------------------------------

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("qxtui PoC: Linux only for now (Phase 3 adds Windows).");
    return 1;
}

var terminal = new PosixTerminal(); // owned (and disposed) by App

// ---- widgets ----------------------------------------------------------------

var spinner = new SpinnerWidget();
var staticPanel = new StaticPanelWidget("STATIC PANEL",
    "This panel never re-renders",
    "while everything around it",
    "animates. Its render count in",
    "STATS stays at 1. That is the",
    "damage-isolation proof.");
var stats = new StatsWidget();
var progress = new ProgressBarWidget();
var particles = new ParticlesWidget(count: 500);
var help = new LabelWidget("q: quit   r: refresh stats   ↔: resize the terminal window!");

// ---- layout tree: flexbox from day one --------------------------------------

var row1 = new LayoutNode { Direction = FlexDirection.Row, Height = 12, Gap = 1 };
row1.Add(spinner.Node);      // fixed width
row1.Add(staticPanel.Node);  // grow: 1
row1.Add(stats.Node);        // 25%

var root = new LayoutNode { Direction = FlexDirection.Column, Gap = 1 };
root.Add(row1);
root.Add(progress.Node);     // height 1, full width
root.Add(particles.Node);    // grow: fills remaining height
root.Add(help.Node);         // height 1

spinner.Node.Width = 14;
staticPanel.Node.Grow = 1f;
stats.Node.WidthPercent = 0.25f;
progress.Node.Height = 1;
particles.Node.Grow = 1f;
help.Node.Height = 1;

// ---- app ---------------------------------------------------------------------

var app = new App(terminal, root, targetFps: 240)
    .AddWidget(spinner)
    .AddWidget(staticPanel)
    .AddWidget(progress)
    .AddWidget(particles)
    .AddWidget(help)
    .AddWidget(stats);
stats.Attach(app);

app.OnInput = b =>
{
    if (b is (byte)'q' or 3) { app.Quit(); return false; } // q or Ctrl-C
    if (b is (byte)'r') stats.Invalidate();
    return true;
};

using (app)
{
    // ---- declarative animations: ONE scheduler, signal writes only ------------

    // Progress: OutCubic, 2s, ping-pong forever. No component code per tick.
    app.Scheduler.Animate(progress.Progress, 1f,
        new Transition(TimeSpan.FromSeconds(2), Easing.OutCubic, RepeatMode.Reverse));

    // Spinner: full rotation loop (braille frames driven off the angle).
    app.Scheduler.Animate(spinner.Angle, 1f,
        new Transition(TimeSpan.FromMilliseconds(833), Easing.Linear, RepeatMode.Loop));

    // Particle clock: looping signal that marks the particle field dirty per frame.
    app.Scheduler.Animate(particles.Clock, 1f,
        new Transition(TimeSpan.FromHours(1), Easing.Linear, RepeatMode.Loop));

    // Stats refresh at ~4Hz — deliberately NOT per frame, so the stats panel
    // doesn't become its own biggest damage source.
    app.Scheduler.Animate(stats.Ticker, 1f,
        new Transition(TimeSpan.FromMilliseconds(250), Easing.Linear, RepeatMode.Loop));

    app.Run();
}

return 0;

// =============================================================================
// Demo widgets
// =============================================================================

file static class Box
{
    public static void Draw(CellBuffer buf, Rect r, string? title, int fg = 244)
    {
        if (r.W < 2 || r.H < 2) return;

        for (int x = r.X; x < r.Right; x++)
        {
            buf[x, r.Y] = new Cell { Char = '─', Fg = fg, Bg = -1 };
            buf[x, r.Bottom - 1] = new Cell { Char = '─', Fg = fg, Bg = -1 };
        }
        for (int y = r.Y; y < r.Bottom; y++)
        {
            buf[r.X, y] = new Cell { Char = '│', Fg = fg, Bg = -1 };
            buf[r.Right - 1, y] = new Cell { Char = '│', Fg = fg, Bg = -1 };
        }
        buf[r.X, r.Y] = new Cell { Char = '┌', Fg = fg, Bg = -1 };
        buf[r.Right - 1, r.Y] = new Cell { Char = '┐', Fg = fg, Bg = -1 };
        buf[r.X, r.Bottom - 1] = new Cell { Char = '└', Fg = fg, Bg = -1 };
        buf[r.Right - 1, r.Bottom - 1] = new Cell { Char = '┘', Fg = fg, Bg = -1 };

        if (title is { Length: > 0 } && r.W > title.Length + 4)
            buf.WriteTextClipped(new RenderClip(r.X, r.Y, r.W, r.H), r.X + 2, r.Y, $" {title} ", fg: 39, bg: -1, bold: true);
    }
}

file sealed class SpinnerWidget() : Widget
{
    private static readonly char[] Frames = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];

    public Signal<float> Angle { get; } = new(0f);
    private IDisposable? _angleSub;

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Box.Draw(buffer, r, "SPINNER", fg: 39);

        _angleSub ??= Angle.Subscribe(_ => Invalidate());

        int frame = (int)(Angle.Value * Frames.Length) % Frames.Length;
        if (r.H >= 4 && r.W >= 4)
        {
            buffer[r.X + r.W / 2, r.Y + r.H / 2] =
                new Cell { Char = Frames[frame], Fg = 45, Bg = -1, Bold = true };
            buffer.WriteTextClipped(Clip, r.X + 2, r.Y + r.H - 2,
                $"{(Angle.Value * 360f).ToString("F0", CultureInfo.InvariantCulture),4}°", fg: 244);
        }
    }

    public override void Dispose() => _angleSub?.Dispose();
}

file sealed class StaticPanelWidget(string? title, params string[] lines) : Widget
{
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Box.Draw(buffer, r, title, fg: 240);

        for (int i = 0; i < lines.Length && r.Y + 1 + i < r.Bottom - 1; i++)
            buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1 + i, lines[i], fg: 250);
    }
}

file sealed class LabelWidget(string text) : Widget
{
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = ' ', Fg = -1, Bg = 236 });
        buffer.WriteTextClipped(Clip, r.X + 1, r.Y, text, fg: 250, bg: 236);
    }
}

file sealed class ProgressBarWidget() : Widget
{
    public Signal<float> Progress { get; } = new(0f);
    private IDisposable? _sub;

    public override void Render(CellBuffer buffer)
    {
        _sub ??= Progress.Subscribe(_ => Invalidate());

        var r = Rect;
        if (r.W < 1 || r.H < 1) return;
        int filled = (int)(Progress.Value * r.W);
        for (int x = 0; x < r.W; x++)
        {
            buffer[r.X + x, r.Y] = x < filled
                ? new Cell { Char = '█', Fg = 232, Bg = 42 }
                : new Cell { Char = '─', Fg = 240, Bg = -1 };
        }
        string label = $" {(Progress.Value * 100f).ToString("F0", CultureInfo.InvariantCulture)}% ";
        if (r.W > label.Length + 4)
            buffer.WriteTextClipped(Clip, r.X + 2, r.Y, label, fg: 232, bg: 42, bold: true);
    }

    public override void Dispose() => _sub?.Dispose();
}

file sealed class ParticlesWidget(int count) : Widget
{
    public Signal<float> Clock { get; } = new(0f);

    private readonly float[] _px = new float[count], _py = new float[count];
    private readonly float[] _vx = new float[count], _vy = new float[count];
    private readonly int[] _color = new int[count];
    private readonly Random _rng = new(42); // deterministic demo
    private long _lastTs = Stopwatch.GetTimestamp();
    private bool _initialized;
    private IDisposable? _sub;

    private void Init()
    {
        for (int i = 0; i < count; i++)
        {
            _px[i] = (float)_rng.NextDouble();
            _py[i] = (float)_rng.NextDouble();
            double a = _rng.NextDouble() * Math.PI * 2;
            double s = 0.05 + _rng.NextDouble() * 0.25;
            _vx[i] = (float)(Math.Cos(a) * s);
            _vy[i] = (float)(Math.Sin(a) * s);
            _color[i] = Math.Clamp(33 + _rng.Next(0, 6) * 36 + _rng.Next(0, 6), 0, 255);
        }
        _initialized = true;
    }

    public override void Render(CellBuffer buffer)
    {
        _sub ??= Clock.Subscribe(_ => Invalidate());
        if (!_initialized) Init();

        var r = Rect;
        if (r.W < 2 || r.H < 2) return;

        // physics: dt-based so speed is frame-rate independent
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Min((now - _lastTs) / (double)Stopwatch.Frequency, 0.1);
        _lastTs = now;

        buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = ' ', Fg = -1, Bg = 232 });

        for (int i = 0; i < count; i++)
        {
            _px[i] += _vx[i] * (float)dt;
            _py[i] += _vy[i] * (float)dt;
            if (_px[i] is < 0f or > 1f) { _vx[i] = -_vx[i]; _px[i] = Math.Clamp(_px[i], 0f, 1f); }
            if (_py[i] is < 0f or > 1f) { _vy[i] = -_vy[i]; _py[i] = Math.Clamp(_py[i], 0f, 1f); }

            int cx = r.X + (int)(_px[i] * (r.W - 1));
            int cy = r.Y + (int)(_py[i] * (r.H - 1));
            if (buffer.InBounds(cx, cy))
                buffer[cx, cy] = new Cell { Char = '•', Fg = _color[i], Bg = 232 };
        }
    }

    public override void Dispose() => _sub?.Dispose();
}

file sealed class StatsWidget() : Widget
{
    public Signal<float> Ticker { get; } = new(0f);

    private App? _app;
    private IDisposable? _sub;

    public void Attach(App app) => _app = app;

    public override void Render(CellBuffer buffer)
    {
        _sub ??= Ticker.Subscribe(_ => Invalidate());

        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Box.Draw(buffer, r, "STATS", fg: 214);

        if (_app is not { } app) return;
        var s = app.Stats;
        int fullRepaint = app.BackWidth * app.BackHeight * 3; // ≈3 bytes/cell avg

        string[] lines =
        [
            $"bytes/frame: {s.LastBytesWritten.ToString("N0", CultureInfo.InvariantCulture)}",
            $"  full repaint ≈ {fullRepaint.ToString("N0", CultureInfo.InvariantCulture)} B",
            $"dirty cells: {s.LastDirtyCells}  rects: {s.LastDamageRects}",
            $"fps: {s.Fps.ToString("F0", CultureInfo.InvariantCulture)} (target {app.TargetFps})",
            $"presented: {s.PresentedFps.ToString("F0", CultureInfo.InvariantCulture)} fps (terminal-capped)",
            $"dropped: {s.DroppedFrames.ToString("N0", CultureInfo.InvariantCulture)}",
            $"work p50: {s.FrameWorkP50Ms.ToString("F2", CultureInfo.InvariantCulture)}ms",
            $"work p99: {s.FrameWorkP99Ms.ToString("F2", CultureInfo.InvariantCulture)}ms",
            $"relayouts: {s.Relayouts}",
        ];

        for (int i = 0; i < lines.Length && r.Y + 1 + i < r.Bottom - 1; i++)
            buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1 + i, lines[i], fg: 250);
    }

    public override void Dispose() => _sub?.Dispose();
}
