using System.Buffers;
using QxTui.Animation;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Core;

/// <summary>Live frame statistics — surfaced on screen by the PoC demo
/// as proof of the damage-isolation claim (AGENT.md §2).</summary>
public sealed class FrameStats
{
    public int LastBytesWritten;
    public int LastDirtyCells;
    public int LastDamageRects;
    public double Fps;
    public double FrameWorkP50Ms;
    public double FrameWorkP99Ms;
    public int Relayouts;

    /// <summary>Frames the terminal actually received per second. Capped by
    /// the terminal emulator's drain rate — the honest "what you see" number,
    /// as opposed to <see cref="Fps"/> (frames produced).</summary>
    public double PresentedFps;

    /// <summary>Frames dropped by the mailbox because a newer frame replaced
    /// them before the writer thread could hand them to the terminal.</summary>
    public long DroppedFrames;
}

/// <summary>
/// The frame loop. Order per frame (AGENT.md §2 pipeline):
///   input → resize check → animation tick → layout (if needed) →
///   dirty widgets render into back buffer → diff/damage → ONE write → pace.
/// Headless-testable: drive single frames with <see cref="RunFrame"/> using
/// an ITerminal fake; no real tty required.
/// </summary>
public sealed class App : IDisposable
{
    private readonly ITerminal _terminal;
    private readonly LayoutNode _root;
    private readonly List<Widget> _widgets = [];
    private readonly AnimationScheduler _scheduler;
    private readonly FramePacer _pacer;
    private readonly ArrayBufferWriter<byte> _output = new(1 << 16);
    private readonly List<LayoutNode> _changedNodes = [];

    private CellBuffer _back;
    private Renderer _renderer;
    private readonly FramePresenter? _presenter;
    private bool _needsLayout = true;
    private bool _running;
    private long _lastPresentedCount;
    private int _presentedWindowFrames;
    private double _presentedWindowStart;

    public FrameStats Stats { get; } = new();

    /// <summary>Current back-buffer dimensions (viewport size in cells).</summary>
    public int BackWidth => _back.Width;
    public int BackHeight => _back.Height;
    public int TargetFps { get; }

    /// <summary>Input callback (PoC scope: raw bytes). Return false to quit.</summary>
    public Func<byte, bool>? OnInput { get; set; }

    public AnimationScheduler Scheduler => _scheduler;

    /// <param name="asyncPresenting">When false, frames are written to the
    /// terminal inline (deterministic, for headless tests). Production keeps
    /// true so the frame loop never blocks on tty backpressure.</param>
    public App(ITerminal terminal, LayoutNode root, int targetFps = 60, ITimeSource? time = null, bool asyncPresenting = true)
    {
        _terminal = terminal;
        _root = root;
        TargetFps = targetFps;
        _scheduler = new AnimationScheduler(time);
        _pacer = new FramePacer(targetFps);
        _back = new CellBuffer(Math.Max(1, terminal.Width), Math.Max(1, terminal.Height));
        _renderer = new Renderer(Math.Max(1, terminal.Width), Math.Max(1, terminal.Height));
        _presenter = asyncPresenting ? new FramePresenter(terminal.Write) : null;
        _presentedWindowStart = NowMs() / 1000.0;
    }

    /// <summary>Register a widget (attached to its Node) for the frame loop.</summary>
    public App AddWidget(Widget widget)
    {
        _widgets.Add(widget);
        return this;
    }

    public void Run()
    {
        _terminal.Enter();
        _running = true;
        try
        {
            while (_running)
                RunFrame(pace: true);
        }
        finally
        {
            // Flush pending frame BEFORE exit sequences so ordering holds.
            _presenter?.Dispose();
            _terminal.Exit();
        }
    }

    public void Quit() => _running = false;

    /// <summary>Execute exactly one frame. Public for headless tests.</summary>
    public RenderResult RunFrame(bool pace = false)
    {
        double startMs = NowMs();
        _pacer.MarkFpsSample();

        // 1) input
        while (_terminal.TryReadInput(out byte b))
        {
            if (OnInput is { } handler && !handler(b))
            {
                _running = false;
                break;
            }
        }

        // 2) resize → preserve overlapping cells; only rect-changed widgets
        //    re-render (AGENT.md invariant: unchanged rect → cached cells kept)
        if (_terminal.ConsumeResize() ||
            _terminal.Width != _back.Width || _terminal.Height != _back.Height)
        {
            int w = Math.Max(1, _terminal.Width);
            int h = Math.Max(1, _terminal.Height);
            _back.Resize(w, h, preserve: true);
            _renderer.ResizeToContent(_back);
            _needsLayout = true;
            Stats.Relayouts++;
        }

        // 3) animations tick → signals → widgets mark themselves dirty
        _scheduler.Tick();

        // 4) layout pass (pure) when needed; rect-changed widgets go dirty.
        //    Their stale cells at the PREVIOUS rect must be cleared so the
        //    diff picks them up as damage (v1: no z-order compositing).
        if (_needsLayout)
        {
            LayoutNode.Compute(_root, _back.Width, _back.Height);
            _changedNodes.Clear();
            _root.CollectChanged(_changedNodes);
            for (int i = 0; i < _widgets.Count; i++)
            {
                var widget = _widgets[i];
                if (!_changedNodes.Contains(widget.Node))
                    continue;

                var prev = widget.Node.PreviousRect;
                if (!prev.Equals(default(Rect)) && !prev.Equals(widget.Rect))
                    _back.FillRect(prev.X, prev.Y, prev.W, prev.H, Cell.Empty);
                widget.Invalidate();
            }

            // Clear everything no widget covers anymore (gap rows, vacated
            // regions after resize). Without this, stale pixels survive
            // forever because nothing is dirty there. Cold path: fine.
            ClearUncovered();

            _needsLayout = false;
        }

        // 5) retained render: only dirty widgets touch the back buffer
        for (int i = 0; i < _widgets.Count; i++)
        {
            var widget = _widgets[i];
            if (widget.Dirty)
                widget.RenderTracked(_back);
        }

        // 6) diff → damage → serialize → ONE write
        _output.Clear();
        var result = _renderer.Render(_back, _output);
        if (_presenter is { } presenter)
            presenter.Submit(_output.WrittenSpan);
        else
            _terminal.Write(_output.WrittenSpan); // synchronous test mode

        // 7) stats + pace
        double workMs = NowMs() - startMs;
        Stats.LastBytesWritten = result.BytesWritten;
        Stats.LastDirtyCells = result.DirtyCells;
        Stats.LastDamageRects = result.DamageRects;
        Stats.Fps = _pacer.Fps;
        Stats.FrameWorkP50Ms = _pacer.FrameTimeP50Ms;
        Stats.FrameWorkP99Ms = _pacer.FrameTimeP99Ms;
        Stats.DroppedFrames = _presenter?.DroppedCount ?? 0;
        UpdatePresentedFps();

        if (pace)
            _pacer.PaceFrame(workMs);

        return result;
    }

    /// <summary>
    /// Cold-path cleanup after a relayout: any cell not covered by at least
    /// one widget's CURRENT rect is stale (gap rows, vacated regions after a
    /// resize) and gets cleared so the diff erases it on the terminal.
    /// O(cells × widgets) — only runs on layout frames.
    /// </summary>
    private void ClearUncovered()
    {
        if (_widgets.Count == 0)
        {
            _back.Clear();
            return;
        }

        int w = _back.Width, h = _back.Height;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool covered = false;
                for (int i = 0; i < _widgets.Count; i++)
                {
                    if (_widgets[i].Rect.Contains(x, y))
                    {
                        covered = true;
                        break;
                    }
                }
                if (!covered)
                    _back[x, y] = Cell.Empty;
            }
        }
    }

    /// <summary>Rolling presented-fps estimate over ~0.5s windows, measured
    /// from the writer thread's hand-off count (terminal-limited).</summary>
    private void UpdatePresentedFps()
    {
        long presented = _presenter?.PresentedCount ?? 0;
        _presentedWindowFrames += (int)(presented - _lastPresentedCount);
        _lastPresentedCount = presented;

        double nowS = NowMs() / 1000.0;
        double span = nowS - _presentedWindowStart;
        if (span >= 0.5)
        {
            Stats.PresentedFps = _presentedWindowFrames / span;
            _presentedWindowFrames = 0;
            _presentedWindowStart = nowS;
        }
    }

    private static double NowMs() =>
        System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public void Dispose()
    {
        foreach (var w in _widgets)
            w.Dispose();
        _presenter?.Dispose();
        _back.Dispose();
        _renderer.Dispose();
        _terminal.Dispose();
    }
}
