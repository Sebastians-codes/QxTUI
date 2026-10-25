using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace QxTui.Render;

public readonly record struct RenderResult(
    int BytesWritten,
    int DirtyCells,
    int DamageRects,
    bool FullRepaint);

/// <summary>
/// Diffs the retained back buffer against the previous frame, coalesces the
/// damage, and serializes minimal ANSI into an IBufferWriter — ONE logical
/// write per frame, wrapped in synchronized output when enabled
/// (AGENT.md §2 rendering pipeline).
///
/// Fully headless-testable: no terminal dependency. Zero allocations per
/// frame (pooled damage list, stackalloc scratch, Utf8Formatter).
/// </summary>
public sealed class Renderer : IDisposable
{
    /// <summary>More dirty runs than this → full repaint (coalescing O(n²) not worth it).</summary>
    internal const int MaxDirtyRuns = 256;

    private const byte Esc = (byte)'\x1b';

    private CellBuffer _prev;
    private readonly List<DamageRect> _damage = new(64);
    private readonly bool _synchronizedOutput;
    private bool _resync;

    public int Width => _prev.Width;
    public int Height => _prev.Height;

    public Renderer(int width, int height, bool synchronizedOutput = true)
    {
        _prev = new CellBuffer(width, height);
        _synchronizedOutput = synchronizedOutput;
    }

    /// <summary>Call on terminal resize: reallocates the previous-frame buffer
    /// and schedules a full resync repaint.</summary>
    public void Resize(int width, int height)
    {
        _prev.Dispose();
        _prev = new CellBuffer(width, height);
        Resync();
    }

    /// <summary>
    /// Declare that the terminal's content is unknown/stale: the next Render
    /// serializes the WHOLE screen (including empty cells), repairing any
    /// ghosts. An empty prev frame is NOT enough — cells empty in both back
    /// and prev produce no damage, so old pixels would survive forever.
    /// </summary>
    public void Resync() => _resync = true;

    /// <summary>Resize the prev buffer to match a content-preserving back
    /// buffer: prev becomes a copy of back, so the next diff only reports
    /// cells that widgets actually re-render (partial repaint on resize).</summary>
    public void ResizeToContent(CellBuffer back)
    {
        _prev.Dispose();
        _prev = new CellBuffer(back.Width, back.Height);
        back.Cells.CopyTo(_prev.Cells);
    }

    /// <param name="forceFull">Emit a whole-screen repaint regardless of
    /// the diff. Used to RESYNC the terminal after dropped delta frames:
    /// a missed delta leaves the terminal on a wrong base forever, and only
    /// a full frame repairs it.</param>
    public RenderResult Render(CellBuffer back, IBufferWriter<byte> output, bool forceFull = false)
    {
        if (back.Width != _prev.Width || back.Height != _prev.Height)
            throw new InvalidOperationException(
                $"Back buffer {back.Width}x{back.Height} != renderer {_prev.Width}x{_prev.Height}. Call Resize first.");

        _damage.Clear();
        int dirtyCells = Diff(back, _damage, out bool fullRepaint);
        if (forceFull || _resync)
        {
            _resync = false;
            fullRepaint = true;
            _damage.Clear();
            dirtyCells = back.Width * back.Height;
        }

        var writer = new SpanWriter(output);
        if (_synchronizedOutput)
            writer.WriteRaw("\x1b[?2026h"u8);

        if (fullRepaint)
        {
            writer.WriteRaw("\x1b[H"u8);
            SerializeRect(back, ref writer, new DamageRect(0, 0, back.Width, back.Height));
        }
        else
        {
            for (int i = 0; i < _damage.Count; i++)
                SerializeRect(back, ref writer, _damage[i]);
        }

        if (_synchronizedOutput)
            writer.WriteRaw("\x1b[?2026l"u8);

        writer.Commit();

        // Back buffer becomes the previous frame.
        SwapPrev(back);

        return new RenderResult(writer.TotalWritten, dirtyCells, fullRepaint ? 1 : _damage.Count, fullRepaint);
    }

    /// <summary>
    /// Row-run diff: scans each row for runs of changed cells → damage rects,
    /// then coalesces. Falls back to a single full-screen rect when the run
    /// count explodes. Allocation-free (list is reused, coalescer is in-place).
    /// </summary>
    private int Diff(CellBuffer back, List<DamageRect> damage, out bool fullRepaint)
    {
        int dirty = 0;
        int w = back.Width, h = back.Height;

        for (int y = 0; y < h; y++)
        {
            int runStart = -1;
            for (int x = 0; x < w; x++)
            {
                bool differs = !back.AtUnchecked(y * w + x).Equals(_prev.AtUnchecked(y * w + x));
                if (differs)
                {
                    dirty++;
                    if (runStart < 0) runStart = x;
                }
                else if (runStart >= 0)
                {
                    damage.Add(new DamageRect(runStart, y, x - runStart, 1));
                    runStart = -1;
                    if (damage.Count > MaxDirtyRuns)
                    {
                        fullRepaint = true;
                        damage.Clear();
                        return dirty;
                    }
                }
            }
            if (runStart >= 0)
            {
                damage.Add(new DamageRect(runStart, y, w - runStart, 1));
                if (damage.Count > MaxDirtyRuns)
                {
                    fullRepaint = true;
                    damage.Clear();
                    return dirty;
                }
            }
        }

        fullRepaint = false;
        if (damage.Count > 1)
            DamageCoalescer.Coalesce(damage);
        return dirty;
    }

    private void SerializeRect(CellBuffer back, ref SpanWriter w, in DamageRect rect)
    {
        int curFg = int.MinValue, curBg = int.MinValue, curBold = int.MinValue; // force first SGR
        Span<char> textScratch = stackalloc char[512];
        int textLen = 0;

        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            // Move cursor to start of row run (1-based).
            w.WriteRaw("\x1b["u8);
            w.WriteInt(y + 1);
            w.WriteRaw(";"u8);
            w.WriteInt(rect.X + 1);
            w.WriteRaw("H"u8);
            curFg = curBg = int.MinValue; curBold = int.MinValue; // cursor move: attrs still tracked by terminal, but re-emit for safety

            for (int x = rect.X; x < rect.Right; x++)
            {
                ref var cell = ref back[x, y];
                if (cell.WidePad) continue; // wide char already advanced the cursor

                if (cell.Fg != curFg || cell.Bg != curBg || (cell.Bold ? 1 : 0) != curBold)
                {
                    if (textLen > 0) { w.WriteText(textScratch[..textLen]); textLen = 0; }
                    WriteSgr(ref w, cell.Fg, cell.Bg, cell.Bold);
                    curFg = cell.Fg; curBg = cell.Bg; curBold = cell.Bold ? 1 : 0;
                }

                int need = char.IsHighSurrogate(cell.Char) ? 2 : 1;
                if (textLen + need > textScratch.Length)
                {
                    w.WriteText(textScratch[..textLen]);
                    textLen = 0;
                }

                textScratch[textLen++] = cell.Char;
                if (need == 2 && x + 1 < back.Width)
                    textScratch[textLen++] = back[x + 1, y].Char; // low surrogate
            }

            // End of row: flush pending text BEFORE the next row's cursor move,
            // otherwise it lands at the wrong position in the stream.
            if (textLen > 0)
            {
                w.WriteText(textScratch[..textLen]);
                textLen = 0;
            }
        }
    }

    private static void WriteSgr(ref SpanWriter w, int fg, int bg, bool bold)
    {
        w.WriteRaw("\x1b[0"u8); // reset first — simple and correct
        if (bold)
            w.WriteRaw(";1"u8);
        if (fg >= 0)
            WriteColor(ref w, "38"u8, fg);
        if (bg >= 0)
            WriteColor(ref w, "48"u8, bg);
        w.WriteRaw("m"u8);
    }

    private static void WriteColor(ref SpanWriter w, System.ReadOnlySpan<byte> kind, int c)
    {
        // EVERY SGR part must start with ';': dropping it produced
        // ESC[038;5;45m which terminals misparse (and sometimes swallow
        // following bytes as sequence params — vanishing panels)
        w.WriteRaw(";"u8);
        if (Core.Color.IsRgb(c))
        {
            w.WriteRaw(kind);
            w.WriteRaw(";2;"u8);
            w.WriteInt(Core.Color.R(c));
            w.WriteRaw(";"u8);
            w.WriteInt(Core.Color.G(c));
            w.WriteRaw(";"u8);
            w.WriteInt(Core.Color.B(c));
        }
        else
        {
            w.WriteRaw(kind);
            w.WriteRaw(";5;"u8);
            w.WriteInt(c);
        }
    }

    private void SwapPrev(CellBuffer back) => back.Cells.CopyTo(_prev.Cells);

    public void Dispose() => _prev.Dispose();
}
