using System.Globalization;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Widgets;

namespace QxTui.SysMon;

internal static class Panel
{
    public static void Frame(CellBuffer buf, Rect r, string title, int accent, Theme theme)
        => Box.Draw(buf, r.X, r.Y, r.W, r.H, BoxStyles.Single, theme.Border, title, accent);

    public static int LoadColor(double pct) => pct switch
    {
        < 50 => 42,
        < 80 => 214,
        _ => 196,
    };
}

internal sealed class HeaderWidget : Widget
{
    private readonly string _host = Environment.MachineName;

    public HeaderWidget() => Tick(TimeSpan.FromSeconds(1));

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        var up = LinuxProc.ReadUptime();
        var text = $" qxtop │ {_host} │ up {LinuxProc.HumanDuration(up)} │ {DateTime.Now:HH:mm:ss} ";
        buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = ' ', Fg = Theme.Text, Bg = Theme.Faint });
        buffer.WriteTextClipped(Clip, r.X, r.Y, text, fg: 255, bg: Theme.Faint, bold: true);
    }
}

internal sealed class CpuWidget : Widget
{
    private readonly RingHistory _hist = new(180);
    private readonly SmoothedSampler _agg;
    private LinuxProc.CpuSample _prevAgg;
    private List<LinuxProc.CpuSample> _lastCores = [];
    private List<LinuxProc.CpuSample> _prevCores = [];
    private readonly double[] _prevCore = new double[256];
    private readonly double[] _currCore = new double[256];
    private int _coreCount;

    public CpuWidget()
    {
        // sample /proc at 5Hz inside the sampler; render interpolates at 30Hz
        _agg = new SmoothedSampler(() =>
        {
            var (aggSample, cores) = LinuxProc.ReadCpu();
            double u = LinuxProc.Utilization(_prevAgg, aggSample);
            _prevAgg = aggSample;
            _lastCores = cores;
            return u;
        }, 200, 0.35);
        Tick(TimeSpan.FromMilliseconds(33));
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);

        long now = Environment.TickCount64;
        if (_agg.Update(now))
        {
            _coreCount = Math.Min(_lastCores.Count, _currCore.Length);
            for (int i = 0; i < _coreCount; i++)
            {
                double pct = i < _prevCores.Count
                    ? LinuxProc.Utilization(_prevCores[i], _lastCores[i])
                    : 0;
                _prevCore[i] = _currCore[i];
                _currCore[i] += (pct - _currCore[i]) * 0.35;
            }
            _prevCores = _lastCores;
            _hist.Push((float)_agg.Current);
        }

        double agg = _agg.Value;
        float f = _agg.Phase;

        Panel.Frame(buffer, r, $"CPU ({_coreCount})", Theme.Success, Theme);

        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1,
            $"all {agg.ToString("F0", CultureInfo.InvariantCulture),3}%",
            fg: Panel.LoadColor(agg), bold: true);
        buffer.WriteTextClipped(Clip, r.X + 11, r.Y + 1, "last 36s", fg: Theme.Faint);
        Sparkline.Draw(buffer, Clip, r.X + 19, r.Y + 1, Math.Max(0, r.W - 21), _hist,
            fg: Theme.Success, scaleMax: 100f, phase: f);

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
                i.ToString("D2", CultureInfo.InvariantCulture), fg: Theme.Dim);
            Bar.Draw(buffer, Clip, x + 3, y, Math.Max(4, colW - 9), pct / 100.0,
                fg: Panel.LoadColor(pct));
            buffer.WriteTextClipped(Clip, x + colW - 5, y,
                $"{pct.ToString("F0", CultureInfo.InvariantCulture),3}%", fg: Theme.Dim);
        }
    }
}

internal sealed class MemWidget : Widget
{
    private readonly RingHistory _hist = new(120);
    private readonly SmoothedSampler _used;
    private LinuxProc.MemInfo _last = new(0, 0, 0, 0, 0, 0);

    public MemWidget()
    {
        _used = new SmoothedSampler(() =>
        {
            _last = LinuxProc.ReadMem();
            return _last.TotalKb > 0
                ? (_last.TotalKb - _last.AvailableKb) / (double)_last.TotalKb * 100
                : 0;
        }, 250, 0.5);
        Tick(TimeSpan.FromMilliseconds(100));
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Panel.Frame(buffer, r, "MEMORY", Theme.Accent, Theme);

        long now = Environment.TickCount64;
        if (_used.Update(now))
            _hist.Push((float)_used.Current);

        double usedFrac = _used.Value / 100.0;
        float f = _used.Phase;
        var m = _last;

        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1,
            $"{Format.HumanBytes((m.TotalKb - m.AvailableKb) * 1024.0)} / {Format.HumanBytes(m.TotalKb * 1024.0)}"
            + $"  ({usedFrac * 100:0}%)",
            fg: Theme.Text);
        Bar.Draw(buffer, Clip, r.X + 2, r.Y + 2, r.W - 4, usedFrac, fg: Theme.Accent);
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 3, "used% over last 30s:", fg: Theme.Faint);
        Sparkline.Draw(buffer, Clip, r.X + 2, r.Y + 4, r.W - 4, _hist,
            fg: Theme.Accent, scaleMax: 100f, phase: f);
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 5,
            $"avail {Format.HumanBytes(m.AvailableKb * 1024.0)}"
            + $"  cache {Format.HumanBytes(m.CacheKb * 1024.0)}"
            + $"  free {Format.HumanBytes(m.FreeKb * 1024.0)}", fg: Theme.Dim);
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 6,
            "used = total - available (btop/free convention)", fg: Theme.Faint);

        double swapUsed = m.SwapTotalKb - m.SwapFreeKb;
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 7,
            $"swp {Format.HumanBytes(swapUsed * 1024.0)} / {Format.HumanBytes(m.SwapTotalKb * 1024.0)}",
            fg: swapUsed > 0 ? Theme.Warning : Theme.Dim);
        if (r.H > 9)
            Bar.Draw(buffer, Clip, r.X + 2, r.Y + 8, r.W - 4,
                m.SwapTotalKb > 0 ? swapUsed / m.SwapTotalKb : 0, fg: Theme.Warning);
    }
}

internal sealed class NetWidget : Widget
{
    private readonly RingHistory _rx = new(120);
    private readonly RingHistory _tx = new(120);
    private readonly SmoothedSampler _rxS;
    private (long Rx, long Tx) _prev;
    private long _lastSampleTs;
    private double _txRaw, _prevTx, _currTx;

    private static readonly double[] Scales =
        [100 * 1024, 1024 * 1024, 10 * 1024 * 1024, 100 * 1024 * 1024, 1024 * 1024 * 1024];
    private double _scale = Scales[0];
    private int _lowSamples;

    public NetWidget()
    {
        _rxS = new SmoothedSampler(() =>
        {
            var (rx, tx) = LinuxProc.ReadNet();
            long now = Environment.TickCount64;
            double dt = Math.Max(1, now - _lastSampleTs) / 1000.0;
            _lastSampleTs = now;
            double rxNow = Math.Max(0, (rx - _prev.Rx) / dt);
            _txRaw = Math.Max(0, (tx - _prev.Tx) / dt);
            _prev = (rx, tx);
            return rxNow;
        }, 100, 0.3);
        Tick(TimeSpan.FromMilliseconds(33));
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Panel.Frame(buffer, r, "NETWORK", Theme.Warning, Theme);

        long now = Environment.TickCount64;
        if (_rxS.Update(now))
        {
            _prevTx = _currTx;
            _currTx += (_txRaw - _currTx) * 0.3;
            _rx.Push((float)_rxS.Current);
            _tx.Push((float)_currTx);

            double peak = Math.Max(_rx.Max(0f), _tx.Max(0f));
            if (peak > _scale)
            {
                foreach (double candidate in Scales)
                    if (peak <= candidate) { _scale = candidate; break; }
                _lowSamples = 0;
            }
            else if (peak < _scale / 10)
            {
                if (++_lowSamples >= 36)
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

        float f = _rxS.Phase;
        double rxRate = _rxS.Value;
        double txRate = _prevTx + (_currTx - _prevTx) * f;

        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1,
            $"▼ {Format.HumanBytes(rxRate)}/s", fg: Theme.Success);
        buffer.WriteTextClipped(Clip, r.X + 15, r.Y + 1, "last 12s", fg: Theme.Faint);
        Sparkline.Draw(buffer, Clip, r.X + 23, r.Y + 1, Math.Max(0, r.W - 25), _rx,
            fg: Theme.Success, scaleMax: (float)_scale);
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 2,
            $"▲ {Format.HumanBytes(txRate)}/s", fg: Theme.Accent);
        buffer.WriteTextClipped(Clip, r.X + 15, r.Y + 2, "last 12s", fg: Theme.Faint);
        Sparkline.Draw(buffer, Clip, r.X + 23, r.Y + 2, Math.Max(0, r.W - 25), _tx,
            fg: Theme.Accent, scaleMax: (float)_scale);
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 3,
            $"graph full-scale: {Format.HumanBytes(_scale)}/s", fg: Theme.Faint);
    }
}

internal sealed class ProcWidget : Widget
{
    private readonly Dictionary<int, long> _prevTicks = new(256);
    private readonly Dictionary<int, double> _smoothCpu = new(256);
    private List<LinuxProc.ProcRow> _rows = [];
    private long _lastSampleTs;
    private readonly ListView _list;
    public bool Paused;

    public ProcWidget()
    {
        // virtualized: 2000+ processes, only visible rows ever painted
        _list = new ListView(() => _rows.Count)
        {
            RenderRow = (buffer, clip, rect, index, selected) =>
            {
                var p = _rows[index];
                int cpuColor = Panel.LoadColor(Math.Min(100, p.CpuPercent * Environment.ProcessorCount));
                if (selected)
                    buffer.FillRect(rect.X, rect.Y, rect.W, rect.H,
                        new Cell { Char = ' ', Fg = -1, Bg = Theme.Get(ThemeRole.PanelBg) });
                buffer.WriteTextClipped(clip, rect.X + 2, rect.Y,
                    string.Create(CultureInfo.InvariantCulture,
                        $"{p.Pid,7} {p.State,1} {p.CpuPercent,5:F1} {Format.CompactKiB(p.MemKb),7}  "),
                    fg: selected ? Theme.Get(ThemeRole.Title) : Theme.Get(ThemeRole.Text));
                buffer.WriteTextClipped(clip, rect.X + 28, rect.Y, p.Name, fg: cpuColor);
            },
        };
        // header occupies rows 0-1; spacer then virtualized list below
        var headerSpacer = new ContainerWidget();
        AddChild(headerSpacer);
        headerSpacer.Node.Height = 2;
        AddChild(_list);
        _list.Node.Grow = 1f;
        Tick(TimeSpan.FromSeconds(1)); // 1Hz: /proc scan is the dominant cost
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Panel.Frame(buffer, r, Paused ? "PROCESSES (paused)" : "PROCESSES", Theme.Title, Theme);

        long now = Environment.TickCount64;
        double dt = Math.Max(1, now - _lastSampleTs) / 1000.0;
        _lastSampleTs = now;

        if (!Paused)
        {
            var mem = LinuxProc.ReadMem();
            var raw = LinuxProc.ReadProcesses(_prevTicks, dt, mem.TotalKb);

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
            _list.Invalidate();
        }

        // header row inside the frame, list lives below it
        buffer.WriteTextClipped(Clip, r.X + 2, r.Y + 1,
            $"{"PID",7} {"S",1} {"CPU%",5} {"MEM",7}  NAME", fg: Theme.Dim, bold: true);
    }
}

internal sealed class FooterWidget : Widget
{
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = ' ', Fg = Theme.Text, Bg = Theme.Faint });
        buffer.WriteTextClipped(Clip, r.X + 1, r.Y,
            " q: quit   p: pause processes   cpu 5Hz smoothed - net 10Hz - mem 4Hz - procs 1Hz - clock 1Hz ",
            fg: Theme.Text, bg: Theme.Faint);
    }
}

