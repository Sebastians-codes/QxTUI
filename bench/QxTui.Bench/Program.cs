using System.Buffers;
using System.Diagnostics;
using QxTui.Render;

// ---------------------------------------------------------------------------
// qxtui headless benchmark (AGENT.md §3): proves the renderer-side 200fps
// claim without a terminal. Measures diff+serialize frame times for the
// standard scenes and reports p50/p99 and bytes/frame.
//
// Budget (AGENT.md §2): < 5 ms per frame at 200x60 INCLUDING serialization.
// ---------------------------------------------------------------------------

const int W = 200, H = 60;
const int Frames = 2000;
const int Warmup = 200;

var output = new ArrayBufferWriter<byte>(1 << 16);
var frameTimes = new double[Frames];

Console.WriteLine($"qxtui renderer benchmark — {W}x{H} grid, {Frames} frames\n");

Benchmark("idle (no damage)", () =>
{
    using var back = new CellBuffer(W, H);
    using var renderer = new Renderer(W, H, synchronizedOutput: true);
    back.WriteText(10, 10, "static screen", fg: 7);
    renderer.Render(back, output);

    return () =>
    {
        output.Clear();
        return renderer.Render(back, output).BytesWritten;
    };
});

Benchmark("one animating cell (spinner-sized damage)", () =>
{
    using var back = new CellBuffer(W, H);
    using var renderer = new Renderer(W, H, synchronizedOutput: true);
    FillStaticContent(back);
    renderer.Render(back, output);

    int i = 0;
    char[] frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏".ToCharArray();
    return () =>
    {
        back[100, 30] = new Cell { Char = frames[i++ % frames.Length], Fg = 45, Bg = -1 };
        output.Clear();
        return renderer.Render(back, output).BytesWritten;
    };
});

Benchmark("500 bouncing particles (worst case)", () =>
{
    using var back = new CellBuffer(W, H);
    using var renderer = new Renderer(W, H, synchronizedOutput: true);
    FillStaticContent(back);
    renderer.Render(back, output);

    var rng = new Random(42);
    var px = new float[500]; var py = new float[500];
    var vx = new float[500]; var vy = new float[500];
    for (int p = 0; p < 500; p++)
    {
        px[p] = (float)rng.NextDouble(); py[p] = (float)rng.NextDouble();
        double a = rng.NextDouble() * Math.PI * 2, s = 0.2;
        vx[p] = (float)(Math.Cos(a) * s); vy[p] = (float)(Math.Sin(a) * s);
    }

    return () =>
    {
        // clear previous particle positions by repainting the field background
        for (int p = 0; p < 500; p++)
        {
            int ox = (int)(px[p] * (W - 1)), oy = (int)(py[p] * (H - 1));
            back[ox, oy] = new Cell { Char = ' ', Fg = -1, Bg = 232 };

            px[p] += vx[p] * 0.016f; py[p] += vy[p] * 0.016f;
            if (px[p] is < 0 or > 1) vx[p] = -vx[p];
            if (py[p] is < 0 or > 1) vy[p] = -vy[p];
            px[p] = Math.Clamp(px[p], 0, 1); py[p] = Math.Clamp(py[p], 0, 1);

            int nx = (int)(px[p] * (W - 1)), ny = (int)(py[p] * (H - 1));
            back[nx, ny] = new Cell { Char = '•', Fg = 33 + p % 200, Bg = 232 };
        }
        output.Clear();
        return renderer.Render(back, output).BytesWritten;
    };
});

return 0;

void Benchmark(string name, Func<Func<int>> setup)
{
    var step = setup();

    for (int i = 0; i < Warmup; i++) step();

    long totalBytes = 0;
    for (int i = 0; i < Frames; i++)
    {
        long t0 = Stopwatch.GetTimestamp();
        totalBytes += step();
        frameTimes[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    Array.Sort(frameTimes);
    double p50 = frameTimes[Frames / 2];
    double p99 = frameTimes[(int)(Frames * 0.99)];
    double maxFps = 1000.0 / Math.Max(p99, 1e-6);

    string verdict = p99 < 5.0 ? "✓ within 5ms budget" : "✗ OVER BUDGET";
    Console.WriteLine($"  {name}");
    Console.WriteLine($"    frame time p50: {p50,8:F3} ms   p99: {p99,8:F3} ms   {verdict}");
    Console.WriteLine($"    bytes/frame:    {totalBytes / Frames,8:N0}      max sustainable fps (p99): {maxFps,8:F0}\n");
}

static void FillStaticContent(CellBuffer back)
{
    for (int y = 0; y < H; y++)
    {
        back.WriteText(0, y, new string(y % 2 == 0 ? '░' : '▒', W), fg: 240, bg: 232);
    }
}
