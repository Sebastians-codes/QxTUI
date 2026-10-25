using System.Globalization;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;

namespace QxTui.SysMon;

internal static class Panel
{
    public static void Frame(CellBuffer buf, Rect r, string title, int accent)
        => QxTui.Render.Box.Draw(buf, r.X, r.Y, r.W, r.H, BoxStyles.Single, 238, title, accent);

    /// <summary>8-level single-row sparkline from a ring buffer, right-aligned.</summary>
    /// <param name="scaleMax">Fixed full-scale value (e.g. 100 for percent).
    /// 0 = auto-scale to history max with <paramref name="autoFloor"/>.</param>
    /// <param name="phase">0..1 sub-cell scroll offset: 0 right after a new
    /// sample, approaching 1 just before the next — makes history flow
    /// instead of stepping one column per sample.</param>
    public static void Sparkline(CellBuffer buf, RenderClip clip, int x, int y, int width,
        RingHistory history, int fg, float scaleMax = 0f, float autoFloor = 1f, float phase = 0f)
    {
        const string Levels = "▁▂▃▄▅▆▇█";
        int n = Math.Min(width, history.Count);
        if (n <= 0) return;

        float max = scaleMax > 0 ? scaleMax : history.Max(autoFloor);
        int start = x + width - n;

        for (int i = 0; i < n; i++)
        {
            float idx = history.Count - n + i + phase;
            float v = history.AtInterp(Math.Min(idx, history.Count - 1));
            int level = Math.Clamp((int)(v / max * Levels.Length), 0, Levels.Length - 1);
            buf.WriteTextClipped(clip, start + i, y, Levels.AsSpan(level, 1), fg: fg);
        }
    }

    public static int LoadColor(double pct) => pct switch
    {
        < 50 => 42,
        < 80 => 214,
        _ => 196,
    };

    public static void HBar(CellBuffer buf, RenderClip clip, int x, int y, int width,
        double fraction, int fg)
    {
        int filled = Math.Clamp((int)(fraction * width), 0, width);
        for (int i = 0; i < width; i++)
            buf.WriteTextClipped(clip, x + i, y, i < filled ? "█" : "░", fg: i < filled ? fg : 236);
    }
}

/// <summary>Fixed-capacity float ring for sparklines.</summary>
internal sealed class RingHistory(int capacity)
{
    private readonly float[] _data = new float[capacity];
    private int _head;
    public int Count { get; private set; }

    public void Push(float value)
    {
        _data[_head] = value;
        _head = (_head + 1) % capacity;
        if (Count < capacity) Count++;
    }

    public float this[int i] => _data[(_head - Count + i + capacity * 2) % capacity];

    /// <summary>Linear-interpolated read at a fractional index — lets
    /// sparklines scroll sub-cell smoothly between samples.</summary>
    public float AtInterp(float idx)
    {
        if (Count == 0) return 0f;
        if (idx <= 0f) return this[0];
        if (idx >= Count - 1) return this[Count - 1];
        int i = (int)idx;
        float t = idx - i;
        return this[i] * (1f - t) + this[i + 1] * t;
    }

    public float Max(float floor)
    {
        float m = floor;
        for (int i = 0; i < Count; i++)
            m = Math.Max(m, this[i]);
        return m;
    }

    public void Clear() => Count = 0;
}

internal sealed class HeaderWidget : Widget
{
    private readonly string _host = Environment.MachineName;

    public HeaderWidget(QxTui.Animation.AnimationScheduler scheduler)
    {
        scheduler.Every(TimeSpan.FromSeconds(1), _ => Invalidate());
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        var up = LinuxProc.ReadUptime();
        var text = $" qxtop │ {_host} │ up {LinuxProc.HumanDuration(up)} │ {DateTime.Now:HH:mm:ss} ";
        buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = ' ', Fg = 250, Bg = 236 });
        buffer.WriteTextClipped(Clip, r.X, r.Y, text, fg: 255, bg: 236, bold: true);
    }
}

internal sealed class CpuWidget : Widget
{
    // Sample the kernel at 5Hz (200ms windows: enough ticks for resolution),
    // but RENDER at 30Hz interpolating between samples — sampling and
    // rendering at the same low rate is what made everything look choppy.
    private const double SampleMs = 200;
    private const double Alpha = 0.35;

    private readonly RingHistory _hist = new(180);
    private LinuxProc.CpuSample _prevAgg;
    private List<LinuxProc.CpuSample> _prevCores = [];
    private double _prevAggSmooth, _currAggSmooth;
    private readonly double[] _prevCore = new double[256];
    private readonly double[] _currCore = new double[256];
    private int _coreCount;
    private long _lastSampleTs;

    public CpuWidget(QxTui.Animation.AnimationScheduler scheduler)
    {
        scheduler.Every(TimeSpan.FromMilliseconds(33), _ => Invalidate()); // 30Hz render
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);

        long now = Environment.TickCount64;
        if (now - _lastSampleTs >= SampleMs || _lastSampleTs == 0)
        {
            var (aggSample, cores) = LinuxProc.ReadCpu();
            double aggPct = LinuxProc.Utilization(_prevAgg, aggSample);
            _prevAgg = aggSample;

            _prevAggSmooth = _currAggSmooth;
            _currAggSmooth += (aggPct - _currAggSmooth) * Alpha;

            _coreCount = Math.Min(cores.Count, _currCore.Length);
            for (int i = 0; i < _coreCount; i++)
            {
                double pct = i < _prevCores.Count
                    ? LinuxProc.Utilization(_prevCores[i], cores[i])
                    : 0;
                _prevCore[i] = _currCore[i];
                _currCore[i] += (pct - _currCore[i]) * Alpha;
            }
            _prevCores = cores;

            _hist.Push((float)_currAggSmooth);
            _lastSampleTs = now;
        }

        // interpolation fraction between the last two smoothed samples
        float f = (float)Math.Clamp((now - _lastSampleTs) / SampleMs, 0, 1);
        double agg = _prevAggSmooth + (_currAggSmooth - _prevAggSmooth) * f;

        Panel.Frame(buffer, r, $"CPU ({_coreCount})", 42);

        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1,
            $"all {agg.ToString("F0", CultureInfo.InvariantCulture),3}%",
            fg: Panel.LoadColor(agg), bold: true);
        buffer.WriteTextClipped(Clip, r.X + 11, r.Y + 1, "last 36s", fg: 238);
        Panel.Sparkline(buffer, Clip, r.X + 19, r.Y + 1, Math.Max(0, r.W - 21), _hist, 42,
            scaleMax: 100f, phase: f);

        int rows = r.H - 3;
        int cols = _coreCount > rows ? 2 : 1;
        int perCol = (_coreCount + cols - 1) / cols;
        int colW = (r.W - 4) / cols;

        for (int i = 0; i < _coreCount; i++)
        {
            int col = i / perCol;
            int row = i % perCol;
            if (row >= rows) break;

            int x = r.X + 2 + col * colW;
            int y = r.Y + 2 + row;
            double pct = _prevCore[i] + (_currCore[i] - _prevCore[i]) * f;

            buffer.WriteTextClipped(Clip, x, y,
                i.ToString("D2", CultureInfo.InvariantCulture), fg: 244);
            Panel.HBar(buffer, Clip, x + 3, y, Math.Max(4, colW - 9), pct / 100.0, Panel.LoadColor(pct));
            buffer.WriteTextClipped(Clip, x + colW - 5, y,
                $"{pct.ToString("F0", CultureInfo.InvariantCulture),3}%", fg: 244);
        }
    }
}

internal sealed class MemWidget : Widget
{
    private const double SampleMs = 250;

    private readonly RingHistory _hist = new(120);
    private double _prevFrac, _currFrac;
    private LinuxProc.MemInfo _last = new(0, 0, 0, 0, 0, 0);
    private long _lastSampleTs;

    public MemWidget(QxTui.Animation.AnimationScheduler scheduler)
    {
        scheduler.Every(TimeSpan.FromMilliseconds(100), _ => Invalidate()); // 10Hz render
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Panel.Frame(buffer, r, "MEMORY", 39);

        long now = Environment.TickCount64;
        if (now - _lastSampleTs >= SampleMs || _lastSampleTs == 0)
        {
            _last = LinuxProc.ReadMem();
            double frac = _last.TotalKb > 0
                ? (_last.TotalKb - _last.AvailableKb) / (double)_last.TotalKb
                : 0;
            _prevFrac = _currFrac;
            _currFrac = frac;
            _hist.Push((float)(frac * 100));
            _lastSampleTs = now;
        }

        float f = (float)Math.Clamp((now - _lastSampleTs) / SampleMs, 0, 1);
        double usedFrac = _prevFrac + (_currFrac - _prevFrac) * f;
        var m = _last;

        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1,
            $"{LinuxProc.HumanBytes((m.TotalKb - m.AvailableKb) * 1024.0)} / {LinuxProc.HumanBytes(m.TotalKb * 1024.0)}"
            + $"  ({usedFrac * 100:0}%)",
            fg: 250);
        Panel.HBar(buffer, Clip, r.X + 2, r.Y + 2, r.W - 4, usedFrac, 39);
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 3, "used% over last 30s:", fg: 238);
        Panel.Sparkline(buffer, Clip, r.X + 2, r.Y + 4, r.W - 4, _hist, 39,
            scaleMax: 100f, phase: f);
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 5,
            $"avail {LinuxProc.HumanBytes(m.AvailableKb * 1024.0)}"
            + $"  cache {LinuxProc.HumanBytes(m.CacheKb * 1024.0)}"
            + $"  free {LinuxProc.HumanBytes(m.FreeKb * 1024.0)}", fg: 240);
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 6,
            "used = total - available (btop/free convention)", fg: 238);

        double swapUsed = m.SwapTotalKb - m.SwapFreeKb;
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 7,
            $"swp {LinuxProc.HumanBytes(swapUsed * 1024.0)} / {LinuxProc.HumanBytes(m.SwapTotalKb * 1024.0)}",
            fg: swapUsed > 0 ? 214 : 240);
        if (r.H > 9)
            Panel.HBar(buffer, Clip, r.X + 2, r.Y + 8, r.W - 4,
                m.SwapTotalKb > 0 ? swapUsed / m.SwapTotalKb : 0, 214);
    }
}

internal sealed class NetWidget : Widget
{
    // 500ms sample windows: at 100ms, background chatter (ACKs, keepalives)
    // made the history zig-zag and the smooth scroll turned that jitter into
    // a weird animated wave. Longer averages = calm, meaningful shape.
    private const double SampleMs = 500;

    private readonly RingHistory _rx = new(120);
    private readonly RingHistory _tx = new(120);
    private (long Rx, long Tx) _prev;
    private long _lastSampleTs;
    private double _prevRx, _currRx, _prevTx, _currTx;

    // Stepped full-scale: auto-scaling every sample re-stretched the whole
    // line vertically each tick ("animating more than sideways"). Decade
    // steps mean the graph only scrolls sideways until traffic crosses a
    // step, and the active scale is printed so magnitudes stay readable.
    private static readonly double[] Scales =
        [100 * 1024, 1024 * 1024, 10 * 1024 * 1024, 100 * 1024 * 1024, 1024 * 1024 * 1024];
    private double _scale = Scales[0];
    private int _lowSamples;

    public NetWidget(QxTui.Animation.AnimationScheduler scheduler)
    {
        scheduler.Every(TimeSpan.FromMilliseconds(33), _ => Invalidate()); // 30Hz render
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Panel.Frame(buffer, r, "NETWORK", 141);

        long now = Environment.TickCount64;
        if (now - _lastSampleTs >= SampleMs || _lastSampleTs == 0)
        {
            var (rx, tx) = LinuxProc.ReadNet();
            double dt = Math.Max(1, now - _lastSampleTs) / 1000.0;
            double rxNow = Math.Max(0, (rx - _prev.Rx) / dt);
            double txNow = Math.Max(0, (tx - _prev.Tx) / dt);
            _prev = (rx, tx);

            _prevRx = _currRx; _currRx += (rxNow - _currRx) * 0.3;
            _prevTx = _currTx; _currTx += (txNow - _currTx) * 0.3;
            _rx.Push((float)_currRx);
            _tx.Push((float)_currTx);
            _lastSampleTs = now;

            // Hysteresis: step UP immediately when traffic demands it, but
            // step DOWN only after ~3 windows of quiet — otherwise a burst
            // leaving the window flips the scale and re-stretches the line.
            double peak = Math.Max(_rx.Max(0f), _tx.Max(0f));
            if (peak > _scale)
            {
                foreach (double candidate in Scales)
                    if (peak <= candidate) { _scale = candidate; break; }
                _lowSamples = 0;
            }
            else if (peak < _scale / 10)
            {
                if (++_lowSamples >= 36) // 36 samples = 3 windows at 10Hz/12s
                {
                    int idx = Array.IndexOf(Scales, _scale);
                    if (idx > 0) _scale = Scales[idx - 1];
                    _lowSamples = 0;
                }
            }
            else
            {
                _lowSamples = 0;
            }
        }

        float f = (float)Math.Clamp((now - _lastSampleTs) / SampleMs, 0, 1);
        double rxRate = _prevRx + (_currRx - _prevRx) * f;
        double txRate = _prevTx + (_currTx - _prevTx) * f;

        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1,
            $"▼ {LinuxProc.HumanBytes(rxRate)}/s", fg: 42);
        buffer.WriteTextClipped(Clip, r.X + 15, r.Y + 1, "last 12s", fg: 238);
        Panel.Sparkline(buffer, Clip, r.X + 23, r.Y + 1, Math.Max(0, r.W - 25), _rx, 42,
            scaleMax: (float)_scale); // rigid columns: spiky data must translate, not morph
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 2,
            $"▲ {LinuxProc.HumanBytes(txRate)}/s", fg: 141);
        buffer.WriteTextClipped(Clip, r.X + 15, r.Y + 2, "last 12s", fg: 238);
        Panel.Sparkline(buffer, Clip, r.X + 23, r.Y + 2, Math.Max(0, r.W - 25), _tx, 141,
            scaleMax: (float)_scale); // rigid columns
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 3,
            $"graph full-scale: {LinuxProc.HumanBytes(_scale)}/s", fg: 238);
    }
}

internal sealed class ProcWidget : Widget
{
    private readonly Dictionary<int, long> _prevTicks = new(256);
    private readonly Dictionary<int, double> _smoothCpu = new(256);
    private List<LinuxProc.ProcRow> _rows = [];
    private long _lastSampleTs;
    public bool Paused;

    public ProcWidget(QxTui.Animation.AnimationScheduler scheduler)
    {
        scheduler.Every(TimeSpan.FromSeconds(1), _ => Invalidate()); // 1Hz: ~2000 /proc reads per sample is the dominant cost
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Panel.Frame(buffer, r, Paused ? "PROCESSES (paused)" : "PROCESSES", 45);

        long now = Environment.TickCount64;
        double dt = Math.Max(1, now - _lastSampleTs) / 1000.0;
        _lastSampleTs = now;

        if (!Paused)
        {
            var mem = LinuxProc.ReadMem();
            var raw = LinuxProc.ReadProcesses(_prevTicks, dt, mem.TotalKb);

            // Raw 1s windows spike hard (a 300ms burst reads as 30%): smooth
            // per-pid like btop does, so the table is readable, not jumpy.
            const double alpha = 0.3;
            var seen = new HashSet<int>(raw.Count);
            foreach (var row in raw)
            {
                seen.Add(row.Pid);
                double prev = _smoothCpu.GetValueOrDefault(row.Pid, row.CpuPercent);
                _smoothCpu[row.Pid] = prev + (row.CpuPercent - prev) * alpha;
            }
            foreach (var pid in _smoothCpu.Keys.ToList())
                if (!seen.Contains(pid))
                    _smoothCpu.Remove(pid);

            _rows = raw
                .Select(row => row with { CpuPercent = _smoothCpu.GetValueOrDefault(row.Pid) })
                .OrderByDescending(row => row.CpuPercent)
                .ToList();
        }

        // header
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1,
            $"{"PID",7} {"S",1} {"CPU%",5} {"MEM",7}  NAME", fg: 244, bold: true);

        int maxRows = r.H - 4;
        for (int i = 0; i < _rows.Count && i < maxRows; i++)
        {
            var p = _rows[i];
            int y = r.Y + 2 + i;
            int cpuColor = Panel.LoadColor(Math.Min(100, p.CpuPercent * Environment.ProcessorCount));

            buffer.WriteTextClipped(Clip, r.X + 2, y,
                string.Create(CultureInfo.InvariantCulture,
                    $"{p.Pid,7} {p.State,1} {p.CpuPercent,5:F1} {Fmt.MemFmt(p.MemKb),7}  "),
                fg: 250);
            buffer.WriteTextClipped(Clip, r.X + 28, y, p.Name, fg: cpuColor);
        }
    }
}

internal sealed class FooterWidget : Widget
{
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = ' ', Fg = 250, Bg = 236 });
        buffer.WriteTextClipped(Clip, r.X + 1, r.Y,
            " q: quit   p: pause processes   graphs render 30fps interpolated from 5-10Hz samples - procs 1Hz ",
            fg: 250, bg: 236);
    }
}

internal static class Fmt
{
    /// <summary>btop-style compact memory: 720M, 8.2M, 512K, 1.4G.</summary>
    public static string MemFmt(long kb) => kb switch
    {
        >= 10 * 1024 * 1024 => $"{kb / (1024.0 * 1024.0):F1}G",
        >= 10 * 1024 => $"{kb / 1024.0:F0}M",
        >= 1024 => $"{kb / 1024.0:F1}M",
        _ => $"{kb}K",
    };
}

