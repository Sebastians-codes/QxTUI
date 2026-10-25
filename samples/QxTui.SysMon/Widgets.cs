using System.Globalization;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;

namespace QxTui.SysMon;

internal static class Panel
{
    public static void Frame(CellBuffer buf, Rect r, string title, int accent)
    {
        if (r.W < 2 || r.H < 2) return;

        for (int x = r.X; x < r.Right; x++)
        {
            buf[x, r.Y] = new Cell { Char = '─', Fg = 238, Bg = -1 };
            buf[x, r.Bottom - 1] = new Cell { Char = '─', Fg = 238, Bg = -1 };
        }
        for (int y = r.Y; y < r.Bottom; y++)
        {
            buf[r.X, y] = new Cell { Char = '│', Fg = 238, Bg = -1 };
            buf[r.Right - 1, y] = new Cell { Char = '│', Fg = 238, Bg = -1 };
        }
        buf[r.X, r.Y] = new Cell { Char = '┌', Fg = 238, Bg = -1 };
        buf[r.Right - 1, r.Y] = new Cell { Char = '┐', Fg = 238, Bg = -1 };
        buf[r.X, r.Bottom - 1] = new Cell { Char = '└', Fg = 238, Bg = -1 };
        buf[r.Right - 1, r.Bottom - 1] = new Cell { Char = '┘', Fg = 238, Bg = -1 };

        buf.WriteTextClipped(new RenderClip(r.X, r.Y, r.W, 1), r.X + 2, r.Y,
            $" {title} ", fg: accent, bg: -1, bold: true);
    }

    /// <summary>8-level single-row sparkline from a ring buffer, right-aligned.</summary>
    /// <param name="scaleMax">Fixed full-scale value (e.g. 100 for percent).
    /// 0 = auto-scale to history max with <paramref name="autoFloor"/>.</param>
    public static void Sparkline(CellBuffer buf, RenderClip clip, int x, int y, int width,
        RingHistory history, int fg, float scaleMax = 0f, float autoFloor = 1f)
    {
        const string Levels = "▁▂▃▄▅▆▇█";
        int n = Math.Min(width, history.Count);
        if (n <= 0) return;

        float max = scaleMax > 0 ? scaleMax : history.Max(autoFloor);
        int start = x + width - n;

        for (int i = 0; i < n; i++)
        {
            float v = history[history.Count - n + i];
            int level = Math.Clamp((int)(v / max * 7.999f), 0, 7);
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
    // Kernel cpu ticks are 10ms-granular: a 50ms sample window quantizes each
    // core to 0%/20%/40%... which reads as blinking. 200ms window (20 ticks)
    // plus exponential smoothing gives readable, btop-like bars.
    private const double SampleMs = 200;
    private const double Alpha = 0.35;

    private readonly RingHistory _hist = new(180);
    private LinuxProc.CpuSample _prevAgg;
    private List<LinuxProc.CpuSample> _prevCores = [];
    private double _aggSmooth;
    private readonly double[] _coreSmooth = new double[256];
    private int _coreCount;

    public CpuWidget(QxTui.Animation.AnimationScheduler scheduler)
    {
        scheduler.Every(TimeSpan.FromMilliseconds(SampleMs), _ => Invalidate());
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);

        var (agg, cores) = LinuxProc.ReadCpu();
        double aggPct = LinuxProc.Utilization(_prevAgg, agg);
        _prevAgg = agg;

        _coreCount = Math.Min(cores.Count, _coreSmooth.Length);
        for (int i = 0; i < _coreCount; i++)
        {
            double pct = i < _prevCores.Count
                ? LinuxProc.Utilization(_prevCores[i], cores[i])
                : 0;
            _coreSmooth[i] += (pct - _coreSmooth[i]) * Alpha;
        }
        _prevCores = cores;

        _aggSmooth += (aggPct - _aggSmooth) * Alpha;
        _hist.Push((float)_aggSmooth);

        Panel.Frame(buffer, r, $"CPU ({_coreCount})", 42);

        // aggregate line + sparkline
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1,
            $"all {_aggSmooth.ToString("F0", CultureInfo.InvariantCulture),3}%",
            fg: Panel.LoadColor(_aggSmooth), bold: true);
        buffer.WriteTextClipped(Clip, r.X + 11, r.Y + 1, "36s", fg: 238);
        Panel.Sparkline(buffer, Clip, r.X + 15, r.Y + 1, Math.Max(0, r.W - 17), _hist, 42, scaleMax: 100f);

        // per-core bars, two columns when narrow
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
            double pct = _coreSmooth[i];

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
    private readonly RingHistory _hist = new(120);

    public MemWidget(QxTui.Animation.AnimationScheduler scheduler)
    {
        scheduler.Every(TimeSpan.FromMilliseconds(250), _ => Invalidate()); // 4Hz
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Panel.Frame(buffer, r, "MEMORY", 39);

        var m = LinuxProc.ReadMem();
        double usedFrac = m.TotalKb > 0 ? (m.TotalKb - m.AvailableKb) / (double)m.TotalKb : 0;
        _hist.Push((float)(usedFrac * 100));

        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1,
            $"{LinuxProc.HumanBytes((m.TotalKb - m.AvailableKb) * 1024.0)} / {LinuxProc.HumanBytes(m.TotalKb * 1024.0)}"
            + $"  ({usedFrac * 100:0}%)",
            fg: 250);
        Panel.HBar(buffer, Clip, r.X + 2, r.Y + 2, r.W - 4, usedFrac, 39);
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 3, "used% over last 30s:", fg: 238);
        Panel.Sparkline(buffer, Clip, r.X + 2, r.Y + 4, r.W - 4, _hist, 39, scaleMax: 100f);
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
    private readonly RingHistory _rx = new(120);
    private readonly RingHistory _tx = new(120);
    private (long Rx, long Tx) _prev;
    private long _lastTs = Environment.TickCount64;
    private double _rxRate, _txRate;

    public NetWidget(QxTui.Animation.AnimationScheduler scheduler)
    {
        scheduler.Every(TimeSpan.FromMilliseconds(100), _ => Invalidate()); // 10Hz
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Panel.Frame(buffer, r, "NETWORK", 141);

        var (rx, tx) = LinuxProc.ReadNet();
        long now = Environment.TickCount64;
        double dt = Math.Max(1, now - _lastTs) / 1000.0;
        double rxNow = Math.Max(0, (rx - _prev.Rx) / dt);
        double txNow = Math.Max(0, (tx - _prev.Tx) / dt);
        _prev = (rx, tx);
        _lastTs = now;

        // exponential smoothing: 100ms deltas are bursty
        _rxRate += (rxNow - _rxRate) * 0.3;
        _txRate += (txNow - _txRate) * 0.3;

        _rx.Push((float)_rxRate);
        _tx.Push((float)_txRate);

        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1,
            $"▼ {LinuxProc.HumanBytes(_rxRate)}/s", fg: 42);
        buffer.WriteTextClipped(Clip, r.X + 15, r.Y + 1, "12s", fg: 238);
        Panel.Sparkline(buffer, Clip, r.X + 19, r.Y + 1, Math.Max(0, r.W - 21), _rx, 42, autoFloor: 100 * 1024f);
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 2,
            $"▲ {LinuxProc.HumanBytes(_txRate)}/s", fg: 141);
        buffer.WriteTextClipped(Clip, r.X + 15, r.Y + 2, "12s", fg: 238);
        Panel.Sparkline(buffer, Clip, r.X + 19, r.Y + 2, Math.Max(0, r.W - 21), _tx, 141, autoFloor: 100 * 1024f);
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
            int cpuColor = Panel.LoadColor(Math.Min(100, p.CpuPercent));

            buffer.WriteTextClipped(Clip, r.X + 2, y,
                string.Create(CultureInfo.InvariantCulture,
                    $"{p.Pid,7} {p.State,1} {p.CpuPercent,5:F1} {Fmt.MemFmt(p.MemKb),7}  "),
                fg: 250);
            buffer.WriteTextClipped(Clip, r.X + 28, y, p.Name, fg: cpuColor);
        }
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

internal sealed class FooterWidget : Widget
{
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = ' ', Fg = 250, Bg = 236 });
        buffer.WriteTextClipped(Clip, r.X + 1, r.Y,
            " q: quit   p: pause processes   cpu 5Hz smoothed - net 10Hz - mem/load 4Hz - procs 1Hz - clock 1Hz ",
            fg: 250, bg: 236);
    }
}
