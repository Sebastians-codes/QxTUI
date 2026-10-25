using System.Globalization;

namespace QxTui.SysMon;

/// <summary>
/// Minimal Linux /proc readers. Sampling happens at widget tick rates
/// (1-20Hz), i.e. cold path — allocations are fine here (AGENT.md §2:
/// hot path is the render/diff/serialize loop, not data acquisition).
/// </summary>
internal static class LinuxProc
{
    public readonly record struct CpuSample(long User, long Nice, long System, long Idle, long IoWait, long Irq, long SoftIrq, long Steal)
    {
        public long Total => User + Nice + System + Idle + IoWait + Irq + SoftIrq + Steal;
        public long Busy => Total - Idle - IoWait;
    }

    /// <summary>Aggregate + per-core cpu counters from /proc/stat.</summary>
    public static (CpuSample Aggregate, List<CpuSample> Cores) ReadCpu()
    {
        var cores = new List<CpuSample>(8);
        var agg = default(CpuSample);

        foreach (var line in File.ReadLines("/proc/stat"))
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal))
                break;

            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var s = new CpuSample(
                Parse(p, 1), Parse(p, 2), Parse(p, 3), Parse(p, 4),
                Parse(p, 5), Parse(p, 6), Parse(p, 7), Parse(p, 8));

            if (line.StartsWith("cpu ", StringComparison.Ordinal))
                agg = s;
            else
                cores.Add(s);
        }

        return (agg, cores);
    }

    private static long Parse(string[] p, int i) =>
        i < p.Length ? long.Parse(p[i], CultureInfo.InvariantCulture) : 0;

    public static double Utilization(CpuSample prev, CpuSample now)
    {
        long dt = now.Total - prev.Total;
        if (dt <= 0) return 0;
        long db = now.Busy - prev.Busy;
        return Math.Clamp(db * 100.0 / dt, 0, 100);
    }

    public readonly record struct MemInfo(long TotalKb, long AvailableKb, long SwapTotalKb, long SwapFreeKb);

    public static MemInfo ReadMem()
    {
        long total = 0, avail = 0, swapTotal = 0, swapFree = 0;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = Kb(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) avail = Kb(line);
            else if (line.StartsWith("SwapTotal:", StringComparison.Ordinal)) swapTotal = Kb(line);
            else if (line.StartsWith("SwapFree:", StringComparison.Ordinal)) swapFree = Kb(line);
        }
        return new MemInfo(total, avail, swapTotal, swapFree);
    }

    private static long Kb(string line)
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p.Length >= 2 ? long.Parse(p[1], CultureInfo.InvariantCulture) : 0;
    }

    public static (double One, double Five, double Fifteen, int Running, int Total) ReadLoad()
    {
        var p = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var procs = p[3].Split('/');
        return (
            double.Parse(p[0], CultureInfo.InvariantCulture),
            double.Parse(p[1], CultureInfo.InvariantCulture),
            double.Parse(p[2], CultureInfo.InvariantCulture),
            int.Parse(procs[0], CultureInfo.InvariantCulture),
            int.Parse(procs[1], CultureInfo.InvariantCulture));
    }

    public static TimeSpan ReadUptime() =>
        TimeSpan.FromSeconds(double.Parse(
            File.ReadAllText("/proc/uptime").Split(' ')[0], CultureInfo.InvariantCulture));

    /// <summary>rx/tx byte counters summed over physical-ish interfaces.</summary>
    public static (long RxBytes, long TxBytes) ReadNet()
    {
        long rx = 0, tx = 0;
        foreach (var line in File.ReadLines("/proc/net/dev"))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line[..colon].Trim();
            if (name is "lo" or "docker0" or "veth0") continue;

            var p = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 9)
            {
                rx += long.Parse(p[0], CultureInfo.InvariantCulture);
                tx += long.Parse(p[8], CultureInfo.InvariantCulture);
            }
        }
        return (rx, tx);
    }

    public readonly record struct ProcRow(int Pid, char State, double CpuPercent, double MemPercent, string Name);

    /// <summary>Per-process cpu% (delta of utime+stime between calls) and rss%.</summary>
    public static List<ProcRow> ReadProcesses(
        Dictionary<int, long> prevCpuTicks, double sampleSeconds, long memTotalKb)
    {
        var rows = new List<ProcRow>(64);
        long clk = Environment.TickCount64; // unused placeholder guard
        _ = clk;

        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = dir.AsSpan(5);
            if (!int.TryParse(name, out int pid))
                continue;

            string stat;
            try
            {
                stat = File.ReadAllText($"{dir}/stat");
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            // comm may contain spaces/parens: cut between first '(' and last ')'
            int open = stat.IndexOf('(');
            int close = stat.LastIndexOf(')');
            if (open < 0 || close < 0 || close + 2 >= stat.Length) continue;

            string comm = stat[(open + 1)..close];
            var rest = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // rest[0]=state ... rest[11]=utime(13) rest[12]=stime(14) rest[21]=rss(23)
            if (rest.Length < 22) continue;

            char state = rest[0][0];
            long utime = long.Parse(rest[11], CultureInfo.InvariantCulture);
            long stime = long.Parse(rest[12], CultureInfo.InvariantCulture);
            long rssPages = long.Parse(rest[21], CultureInfo.InvariantCulture);

            long ticks = utime + stime;
            double cpuPct = 0;
            if (prevCpuTicks.TryGetValue(pid, out long prev))
            {
                long hz = 100; // USER_HZ on Linux
                cpuPct = Math.Max(0, (ticks - prev) / (hz * sampleSeconds)) * 100.0;
            }
            prevCpuTicks[pid] = ticks;

            double memPct = memTotalKb > 0
                ? rssPages * 4.0 / 1024.0 / memTotalKb * 100.0   // 4K pages
                : 0;

            rows.Add(new ProcRow(pid, state, cpuPct, memPct, comm));
        }

        // drop exited pids from the delta map occasionally
        if (prevCpuTicks.Count > rows.Count + 512)
        {
            var alive = new HashSet<int>(rows.Select(r => r.Pid));
            foreach (var key in prevCpuTicks.Keys.ToList())
                if (!alive.Contains(key)) prevCpuTicks.Remove(key);
        }

        rows.Sort(static (a, b) => b.CpuPercent.CompareTo(a.CpuPercent));
        return rows;
    }

    public static string HumanBytes(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        int u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        return $"{bytes.ToString(bytes >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture)}{units[u]}";
    }

    public static string HumanDuration(TimeSpan ts) => ts switch
    {
        { Days: > 0 } d => $"{d.Days}d {d.Hours}h {d.Minutes}m",
        { Hours: > 0 } h => $"{h.Hours}h {h.Minutes}m {h.Seconds}s",
        _ => $"{ts.Minutes}m {ts.Seconds}s",
    };
}
