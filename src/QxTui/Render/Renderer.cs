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
    /// <summary>Emit DECSTBM+SU/SD hardware scrolls from ScrollHint: only
    /// exposed rows serialize, keeping scroll frames ~1KB so terminals
    /// keep presenting at speed. Disable if an emulator misimplements
    /// SU/SD (front buffer stays authoritative either way).</summary>
    public bool HardwareScroll { get; set; } = true;

    private Layout.Rect _hintRect;
    private int _hintDelta;
    private bool _hasHint;

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

    /// <summary>
    /// Widget hint: `rect`'s content is the previous frame's content shifted
    /// vertically by `delta` rows (positive = up, i.e. scrolled down). The
    /// renderer then hardware-scrolls (DECSTBM region + SU/SD), shifts its
    /// own front buffer to match, and diffs as usual — so only the exposed
    /// rows serialize. A 3-row wheel notch becomes ~1KB instead of a
    /// full-window repaint; that is what keeps terminals' presented fps
    /// (and therefore perceived smoothness) alive while scrolling.
    /// Ignored when there is no row overlap (|delta| >= rect.H).
    /// </summary>
    public void ScrollHint(Layout.Rect rect, int delta)
    {
        if (delta == 0 || Math.Abs(delta) >= rect.H) return;
        _hintRect = rect;
        _hintDelta = delta;
        _hasHint = true;
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

        // consume the hint BEFORE diffing: shifting the front buffer makes
        // the diff see only the exposed rows
        string? scrollCmd = null;
        if (_hasHint)
        {
            _hasHint = false;
            var r = _hintRect;
            int d = _hintDelta;
            if (HardwareScroll && !forceFull && !_resync && d != 0 && Math.Abs(d) < r.H &&
                r.X >= 0 && r.Y >= 0 && r.Right <= _prev.Width && r.Bottom <= _prev.Height)
            {
                ShiftPrev(r, d);
                scrollCmd = d > 0
                    ? $"\x1b[{r.Y + 1};{r.Bottom}r\x1b[{d}S\x1b[r"
                    : $"\x1b[{r.Y + 1};{r.Bottom}r\x1b[{-d}T\x1b[r";
            }
        }

        _damage.Clear();
        int dirtyCells = Diff(back, _damage, out bool fullRepaint);
        if (forceFull || _resync)
        {
            _resync = false;
            fullRepaint = true;
            _damage.Clear();
            dirtyCells = back.Width * back.Height;
            scrollCmd = null; // full repaint supersedes the scroll
        }

        var writer = new SpanWriter(output);
        if (_synchronizedOutput)
            writer.WriteRaw("\x1b[?2026h"u8);

        if (scrollCmd is not null)
            writer.WriteText(scrollCmd.AsSpan());

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

    /// <summary>Shift front-buffer rows inside `rect` by `d` (memmove-safe),
    /// mirroring what SU/SD does on the terminal.</summary>
    private void ShiftPrev(Layout.Rect r, int d)
    {
        int w = _prev.Width;
        int rows = r.H - Math.Abs(d);
        // per-row copy: a flat slice smears row boundaries when X > 0
        if (d > 0)
            for (int i = 0; i < rows; i++)
                _prev.Cells.Slice((r.Y + d + i) * w + r.X, r.W)
                     .CopyTo(_prev.Cells.Slice((r.Y + i) * w + r.X));
        else
            for (int i = rows - 1; i >= 0; i--)
                _prev.Cells.Slice((r.Y + i) * w + r.X, r.W)
                     .CopyTo(_prev.Cells.Slice((r.Y - d + i) * w + r.X));

        // THE invariant that was missing: SU/SD leaves the exposed rows
        // BLANK on the terminal, so the front buffer must blank them too.
        // Leaving old content there makes the diff skip cells identical
        // between old and new rows (repeating glyphs like a "row "
        // prefix) — they never repaint over the terminal's blanks and
        // you get ghost columns with holes punched in them.
        int n = Math.Abs(d);
        int blankRow = d > 0 ? r.Bottom - n : r.Y;
        for (int i = 0; i < n; i++)
            _prev.Cells.Slice((blankRow + i) * w + r.X, r.W).Fill(Cell.Empty);
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
