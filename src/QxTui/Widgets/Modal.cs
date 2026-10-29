using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>A modal chip as intent (authoring API): a label plus EITHER a
/// message (the modal emits, the app's update function decides — reusable
/// modals) or a closure (app-specific policy at the composition root).</summary>
public sealed record ChipAction(string Label, object? Msg = null, Action? Act = null);

/// <summary>
/// Modal popup: dims the screen, draws a centered box, consumes all keys
/// (modal semantics). Esc and the [x] chip close; outside-press dismissal
/// is opt-in via <see cref="Widget.OutsideClickCloses"/> (default off).
/// </summary>
public sealed class Modal : Widget
{
    private readonly string _title;
    private readonly Func<string[]> _lines;
    private readonly bool _dim;
    private readonly ChipAction[] _actions;

    /// <param name="dim">Dim everything behind the box (classic modal).
    /// false = floating popup over live content.</param>
    /// <param name="chips">Button row inside the box: clickable AND bound
    /// to keys 1..n while the modal is open. Each chip is either pure
    /// intent (<c>Msg</c>: the modal emits, the app decides) or app policy
    /// (<c>Act</c>: a closure at the composition root).</param>
    public Modal(string title, Func<string[]> lines, bool dim = true,
        ChipAction[]? chips = null)
    {
        _title = title;
        _lines = lines;
        _dim = dim;
        _actions = chips ?? [];
        Clickable = true;
        CapturesMouse = true; // title-bar drags survive leaving the box
        BlocksBaseRendering = dim; // floating popups keep the background live
        Tick(TimeSpan.FromMilliseconds(250)); // reactive content (Func lines)
    }

    /// <summary>Chip dispatch, single source for keys 1..n and clicks:
    /// message chips emit on the channel, closure chips run inline.</summary>
    private void InvokeChip(int i)
    {
        var chip = _actions[i];
        if (chip.Msg is { } msg) Send(msg);
        else chip.Act?.Invoke();
    }

    private bool _dragging;
    private int _grabDX, _grabDY;

    /// <summary>Optional live widget tree hosted inside the box (between
    /// the text lines and the action chips) — animated content in an
    /// overlay: overlays repaint every frame, so sims/tickers run fine.
    /// The body is laid out by hand (overlays live outside the flexbox
    /// tree); BodyRows reserves its height.</summary>
    public Widget? Body
    {
        get => _body;
        set
        {
            _body = value;
            if (value is not null)
            {
                value.SurvivesParentRepaint = true; // own tick = own rate
                if (value.Parent is null)
                    AddChild(value);
            }
        }
    }
    private Widget? _body;

    public int BodyRows { get; set; }

    /// <summary>Shift the box off-center (floating popups that coexist
    /// side by side instead of perfectly overlapping). Self-invalidating:
    /// moving a window is a data write, the next compose blits it.</summary>
    public int OffsetX { get => _ox; set { if (_ox == value) return; _ox = value; Invalidate(); } }
    public int OffsetY { get => _oy; set { if (_oy == value) return; _oy = value; Invalidate(); } }
    private int _ox, _oy;

    /// <summary>Right-drag resize pins these; null = size to content.
    /// Self-invalidating like the offsets.</summary>
    public int? ForceW { get => _fw; private set { if (_fw == value) return; _fw = value; Invalidate(); } }
    public int? ForceH { get => _fh; private set { if (_fh == value) return; _fh = value; Invalidate(); } }
    private int? _fw, _fh;

    internal override int ResizeEdgeAt(int x, int y)
    {
        var b = BoxRect(Rect);
        return b.Contains(x, y) ? 3 : 0;
    }

    internal override (int W, int H) ResizeBaseSize()
    {
        var b = BoxRect(Rect);
        return (b.W, b.H);
    }


    internal override void ApplyResizeSize(int w, int h)
    {
        // center-mirrored: the box re-centers as it grows — that mirror
        // motion IS the intended feel, so no pinning
        ForceW = w;
        ForceH = h;
    }

    internal override (int X, int Y) ResizeCenter()
    {
        var b = BoxRect(Rect);
        return (b.X + b.W / 2, b.Y + b.H / 2);
    }

    internal override bool CenterMirroredResize => true; // free-floating

    /// <summary>Blocking modals own the whole screen for routing (dim
    /// presses reach them); floating popups own only their box.</summary>
    public override bool HitTest(int x, int y)
    {
        if (BlocksBaseRendering)
            return Rect.Contains(x, y);
        return OwnsPoint(x, y);
    }

    /// <summary>"Inside the window" = inside the BOX, in both modes: a
    /// press on the dim layer is an outside press (dismissal candidate).</summary>
    internal override bool OwnsPoint(int x, int y)
    {
        var b = BoxRect(Rect);
        return x >= b.X && x < b.Right && y >= b.Y && y < b.Bottom;
    }

    /// <summary>The [x] chip rect test, shared by drag-start exclusion,
    /// raise suppression and the hover/click paths.</summary>
    private bool OnCloseChip(MouseEvent ev)
    {
        var b = BoxRect(Rect);
        return ev.Y == b.Y && ev.X >= b.Right - 4 && ev.X < b.Right - 1;
    }

    /// <summary>Pressing the [x] chip closes the window — raising it first
    /// would blink it in front of the windows it sits behind.</summary>
    internal override bool RaisesOnPress(MouseEvent ev) => !OnCloseChip(ev);

    /// <summary>Floating popups paint ONLY inside their box: without this,
    /// a lower window's lines land inside a higher window's preserved
    /// body region and show through as glitch pixels.</summary>
    public override RenderClip Clip
    {
        get
        {
            var c = base.Clip;
            if (BlocksBaseRendering) return c;
            var b = BoxRect(Rect);
            int x = Math.Max(c.X, b.X), y = Math.Max(c.Y, b.Y);
            return new RenderClip(x, y,
                Math.Max(0, Math.Min(c.Right, b.Right) - x),
                Math.Max(0, Math.Min(c.Bottom, b.Bottom) - y));
        }
    }

    internal override void CollectPaintedRects(List<Rect> dst)
    {
        // erase-on-detach must match what was painted, or popping a
        // floating popup wipes the whole screen (the blink you saw)
        dst.Add(BlocksBaseRendering ? Rect : BoxRect(Rect));
    }

    public override bool HandleKey(KeyEvent ev)
    {
        if (ev.Key == Key.Escape)
        {
            App?.PopOverlay(this);
            return true;
        }
        if (ev.Key == Key.Char && ev.Char >= '1' && ev.Char <= '9')
        {
            int idx = ev.Char - '1';
            if (idx < _actions.Length)
                InvokeChip(idx);
            return true;
        }
        return true; // modal: swallow everything else
    }

    private Rect BoxRect(Rect screen)
    {
        var lines = _lines();
        int w = Math.Min(screen.W - 4, Math.Max(24, _title.Length + 8));
        foreach (var l in lines) w = Math.Min(screen.W - 4, Math.Max(w, l.Length + 4));
        int actionsWidth = 0;
        for (int i = 0; i < _actions.Length; i++)
            actionsWidth += _actions[i].Label.Length + 5;
        w = Math.Min(screen.W - 4, Math.Max(w, actionsWidth + 4));
        w = Math.Min(screen.W - 4, ForceW ?? w);

        // chips wrap inside the clamped width; the box grows vertically
        int actionRows = _actions.Length == 0 ? 0 : ActionRows(w - 4);
        int h = Math.Min(screen.H - 2,
            lines.Length + 4 + (actionRows > 0 ? actionRows + 1 : 0)
            + (BodyRows > 0 ? BodyRows + 1 : 0));
        h = Math.Min(screen.H - 2, ForceH ?? h);
        int x = Math.Clamp((screen.W - w) / 2 + OffsetX, 0, Math.Max(0, screen.W - w));
        int y = Math.Clamp((screen.H - h) / 2 + OffsetY, 0, Math.Max(0, screen.H - h));
        return new Rect(x, y, w, h);
    }

    private Rect BodyInner(Rect box)
    {
        // body height is DERIVED from the box, so a right-drag resize
        // grows the body with its parent instead of leaving a gap
        var lines = _lines();
        int actionRows = _actions.Length == 0 ? 0 : ActionRows(box.W - 4);
        int top = box.Y + 2 + lines.Length + 1;
        // gap + border, plus the chip zone (rows + its gap) when present
        int bottomZone = 2 + (actionRows > 0 ? actionRows + 1 : 0);
        int h = Math.Max(0, box.Bottom - bottomZone - top);
        return h > 0 ? new Rect(box.X + 2, top, box.W - 4, h) : default;
    }

    private int ActionRows(int innerW)
    {
        int rows = 1, x = 0;
        for (int i = 0; i < _actions.Length; i++)
        {
            int need = _actions[i].Label.Length + 5;
            if (x + need > innerW && x > 0) { rows++; x = 0; }
            x += need;
        }
        return rows;
    }

    /// <summary>Chip rects wrapped inside the box; rows stack upward from
    /// the bottom edge. Single source for render + hit-testing.</summary>
    private List<(Rect Rect, int Idx)> LayoutActions(Rect box)
    {
        var result = new List<(Rect, int)>(_actions.Length);
        int innerW = box.W - 4;
        int x = 0, row = 0;
        for (int i = 0; i < _actions.Length; i++)
        {
            int btnW = _actions[i].Label.Length + 2;
            int need = btnW + 3;
            if (x + need > innerW && x > 0) { row++; x = 0; }
            int y = box.Bottom - 2 - row;
            result.Add((new Rect(box.X + 2 + x + 2, y, btnW, 1), i));
            x += need;
        }
        return result;
    }

    private int _hoverAction = -1;
    private bool _pressOnChip;
    private int _pressAction = -1;

    /// <summary>Action chip under the point, else -1. Single source for
    /// hover, press bookkeeping and click dispatch.</summary>
    private int ActionAt(int x, int y)
    {
        foreach (var (r, i) in LayoutActions(BoxRect(Rect)))
            if (x >= r.X && x < r.Right && y == r.Y) return i;
        return -1;
    }

    public override bool OnMouse(MouseEvent ev)
    {
        var box0 = BoxRect(Rect);
        bool onXChip = OnCloseChip(ev);
        if (ev.Kind == MouseKind.Press)
        {
            // a click is a press+release PAIR: chips and the [x] only fire
            // when the press STARTED on them, so a drag that ends on a
            // chip (or leaves the box) is a drag, never a close/click
            _pressOnChip = onXChip;
            _pressAction = ActionAt(ev.X, ev.Y);
        }
        if (ev.Kind == MouseKind.Press && ev.Y == box0.Y &&
            ev.X >= box0.X && ev.X < box0.Right && !onXChip)
        {
            // title bar = window-manager-style drag handle
            _dragging = true;
            _grabDX = ev.X - box0.X;
            _grabDY = ev.Y - box0.Y;
            return true;
        }
        if (ev.Kind == MouseKind.Motion && _dragging)
        {
            int nw = box0.W, nh = box0.H;
            int nx = Math.Clamp(ev.X - _grabDX, 0, Math.Max(0, Rect.W - nw));
            int ny = Math.Clamp(ev.Y - _grabDY, 0, Math.Max(0, Rect.H - nh));
            int cx = (Rect.W - nw) / 2, cy = (Rect.H - nh) / 2;
            int ox = nx - cx, oy = ny - cy;
            if (ox != OffsetX || oy != OffsetY)
            {
                // compositing: moving is just data — the next compose
                // blits the box at its new origin and the old pixels were
                // never in the base layer, so nothing to erase or repaint
                OffsetX = ox;
                OffsetY = oy;
                Invalidate();
            }
            return true;
        }
        if (ev.Kind == MouseKind.Release)
            _dragging = false;

        // per-button hover for the chip highlight
        int idx = ActionAt(ev.X, ev.Y);
        if (idx != _hoverAction)
        {
            _hoverAction = idx;
            Invalidate();
        }
        return false;
    }

    public override void OnClick(Point local)
    {
        var box = BoxRect(Rect);

        // action buttons first — only the chip the press STARTED on fires
        foreach (var (r, i) in LayoutActions(box))
        {
            if (i != _pressAction) continue;
            if (local.X >= r.X && local.X < r.Right && local.Y == r.Y)
            {
                InvokeChip(i);
                return;
            }
        }

        // [x] chip, same pair semantics. Outside-press dismissal is NOT
        // here anymore: a release outside the box after a drag that
        // started inside used to close the window. Opt-in dismissal lives
        // in App.RouteMouse (OutsideClickCloses) and keys off the PRESS.
        bool closeBtn = local.X >= box.Right - 4 && local.X < box.Right - 1 && local.Y == box.Y;
        if (closeBtn && _pressOnChip)
            App?.PopOverlay(this);
    }

    public override void Render(CellBuffer buffer)
    {
        var screen = Rect;
        if (_dim)
            buffer.FillRect(0, 0, screen.W, screen.H, new Cell { Char = ' ', Fg = -1, Bg = 236 });

        var box = BoxRect(screen);
        // opaque box on every repaint: the layer buffer is retained, so a
        // partial repaint would leave OUR OWN stale pixels (a shrunk box
        // must overwrite the rows it no longer owns)
        buffer.FillRect(box.X, box.Y, box.W, box.H,
            new Cell { Char = ' ', Fg = Theme.Get(ThemeRole.Text), Bg = Theme.Get(ThemeRole.PanelBg) });
        Box.Draw(buffer, box.X, box.Y, box.W, box.H, BoxStyles.Double,
            Theme.Get(ThemeRole.Border), _title, Theme.Get(ThemeRole.Title));
        // [x] as a filled chip, same language as the action buttons
        buffer.WriteTextClipped(Clip, box.Right - 4, box.Y, " x ",
            fg: 232, bg: Theme.Get(ThemeRole.Danger), bold: true);

        var lines = _lines();
        for (int i = 0; i < lines.Length && box.Y + 2 + i < box.Bottom - 1; i++)
            buffer.WriteTextClipped(Clip, box.X + 2, box.Y + 2 + i,
                QxTui.Render.Text.Ellipsize(lines[i], box.W - 4),
                fg: Theme.Get(ThemeRole.Text), bg: Theme.Get(ThemeRole.PanelBg));

        // hosted body: hand-laid rect between lines and chips; children
        // paint after the parent in pre-order, so it lands inside the box
        if (_body is { } body && BodyRows > 0)
        {
            var want = BodyInner(box);
            if (!body.Rect.Equals(want))
            {
                body.Node.SetRect(want);
                body.Node.LayoutChildren(); // overlays skip the root pass
            }
            body.Invalidate(); // body repaints with the modal every frame
        }

        // action chips: key hint + filled button, wrapped inside the box
        foreach (var (r, i) in LayoutActions(box))
        {
            string label = _actions[i].Label;
            int bg = Theme.Get(i == _hoverAction ? ThemeRole.Title : ThemeRole.Accent);
            buffer.WriteTextClipped(Clip, r.X - 2, r.Y, $"{i + 1}", fg: Theme.Get(ThemeRole.Dim));
            for (int cx = 0; cx < r.W; cx++)
                buffer.WriteTextClipped(Clip, r.X + cx, r.Y, " ", fg: 232, bg: bg);
            buffer.WriteTextClipped(Clip, r.X + 1, r.Y, label, fg: 232, bg: bg, bold: true);
        }
    }
}
