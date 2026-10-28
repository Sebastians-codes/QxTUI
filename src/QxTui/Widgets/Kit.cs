using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>Single-line clipped label. Text is a provider so it can be
/// live without extra invalidation plumbing.</summary>
public sealed class Label : Widget
{
    private readonly Func<string> _text;
    private readonly ThemeRole _fg;
    private readonly ThemeRole? _bg;

    private Label(Func<string> text, ThemeRole fg, ThemeRole? bg, bool live)
    {
        _text = text;
        _fg = fg;
        _bg = bg;
        // a provider is only evaluated WHEN we render: without a heartbeat
        // a Func label paints once and its text is stale pixels forever
        // (the "counters don't increase until I hover it" bug)
        if (live)
            Tick(TimeSpan.FromMilliseconds(250), _ => Invalidate());
    }

    public Label(Func<string> text, ThemeRole fg = ThemeRole.Text, ThemeRole? bg = null)
        : this(text, fg, bg, live: true)
    {
    }

    /// <summary>Signal-bound: re-renders whenever the source changes —
    /// event-driven, no heartbeat needed.</summary>
    public Label(Signal<float> source, Func<float, string> format,
        ThemeRole fg = ThemeRole.Text, ThemeRole? bg = null)
        : this(() => format(source.Value), fg, bg, live: false)
    {
        OnSignal(source);
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        // always clear our rect: a moved/resized widget must not leave
        // stale text behind (only writing text ghosts on reflow)
        buffer.FillRect(r.X, r.Y, r.W, r.H,
            new Cell { Char = ' ', Fg = -1, Bg = _bg is { } r3 ? Theme.Get(r3) : -1 });
        buffer.WriteTextClipped(Clip, r.X + 1, r.Y,
            QxTui.Render.Text.Ellipsize(_text(), r.W - 2), fg: Theme.Get(_fg),
            bg: _bg is { } r2 ? Theme.Get(r2) : -1);
    }
}

/// <summary>Horizontal fraction bar bound to a provider.</summary>
public sealed class Bar : Widget
{
    private readonly Func<double> _fraction;
    private readonly ThemeRole _fg;

    public Bar(Func<double> fraction, ThemeRole fg = ThemeRole.Accent)
    {
        _fraction = fraction;
        _fg = fg;
    }

    /// <summary>Signal-bound: re-renders automatically whenever the source
    /// signal changes — the invalidation wiring you'd otherwise forget.</summary>
    public Bar(Signal<float> source, ThemeRole fg = ThemeRole.Accent)
        : this(() => source.Value, fg)
    {
        OnSignal(source);
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        QxTui.Render.BarPainter.Draw(buffer, Clip, r.X, r.Y, r.W, _fraction(), fg: Theme.Get(_fg));
    }
}

/// <summary>Sparkline bound to a RingHistory the app owns and pushes to.</summary>
public sealed class Sparkline : Widget
{
    private readonly RingHistory _history;
    private readonly ThemeRole _fg;
    private readonly float _scaleMax;
    private readonly float _autoFloor;
    private readonly Func<float>? _phase;

    public Sparkline(RingHistory history, ThemeRole fg = ThemeRole.Accent,
        float scaleMax = 0f, float autoFloor = 1f, Func<float>? phase = null)
    {
        _history = history;
        _fg = fg;
        _scaleMax = scaleMax;
        _autoFloor = autoFloor;
        _phase = phase;
        OnSignal(history.Pushed); // repaint whenever new data lands
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty); // no stale trail on reflow
        QxTui.Render.SparklinePainter.Draw(buffer, Clip, r.X, r.Y, r.W, _history,
            fg: Theme.Get(_fg), scaleMax: _scaleMax, autoFloor: _autoFloor,
            phase: _phase?.Invoke() ?? 0f);
    }
}

public enum ButtonStyle
{
    /// <summary>Solid fill, dark bold label.</summary>
    Filled,
    /// <summary>Single-line box outline.</summary>
    Outline,
    /// <summary>Rounded-corner outline.</summary>
    Rounded,
}

/// <summary>Clickable labeled button in three styles, all hover-reactive:
/// the pointer brightens border/fill/label — the "this is clickable"
/// affordance. Click target is the whole rect.</summary>
public sealed class Button : Widget
{
    private readonly Func<string> _label;
    private readonly Action? _onClick;
    private readonly ThemeRole _fg;
    private readonly ButtonStyle _style;

    /// <summary>Optional persistent state color (e.g. a toggle: green when
    /// on, warning when off). Overrides <c>fg</c> while provided.</summary>
    public Func<ThemeRole>? RoleProvider { get; set; }

    /// <summary>Intent emitted on click (authoring API): a button that only
    /// knows "I was clicked, this is my message" drops into any app — the
    /// update function decides what the message means. Composes with
    /// <c>onClick</c> (message first, then the callback).</summary>
    public object? EmitOnClick { get; set; }

    public Button(Func<string> label, Action? onClick,
        ThemeRole fg = ThemeRole.Accent, ButtonStyle style = ButtonStyle.Rounded)
    {
        _label = label;
        _onClick = onClick;
        _fg = fg;
        _style = style;
        Clickable = true;
        Node.Height = 1; // sane default: a bare button is one row, not zero
    }

    /// <summary>Pure-intent button: no callback at all, just EmitOnClick.</summary>
    public Button(Func<string> label,
        ThemeRole fg = ThemeRole.Accent, ButtonStyle style = ButtonStyle.Rounded)
        : this(label, null, fg, style) { }

    public override void OnClick(Point local)
    {
        if (EmitOnClick is { } msg) Send(msg);
        _onClick?.Invoke();
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);

        // reactive states: idle → hovered (brighter) → pressed/flashing (inverted)
        bool down = Pressed || FlashActive;
        ThemeRole role = RoleProvider?.Invoke() ?? _fg;
        int baseCol = Theme.Get(role);
        int hot = Theme.Get(Hovered ? ThemeRole.Title : role);
        int fill = down ? Theme.Get(ThemeRole.Faint)
                 : Hovered ? Theme.Get(ThemeRole.Title)
                 : baseCol;
        int textOnFill = down ? hot : 232;

        var label = _label();
        int lx = r.X + Math.Max(1, (r.W - label.Length) / 2);
        int ly = r.Y + r.H / 2;

        switch (_style)
        {
            case ButtonStyle.Filled:
                for (int y = r.Y; y < r.Bottom; y++)
                    for (int x = r.X; x < r.Right; x++)
                        buffer.WriteTextClipped(Clip, x, y, " ", fg: textOnFill, bg: fill);
                buffer.WriteTextClipped(Clip, lx, ly, label, fg: textOnFill, bg: fill, bold: true);
                break;

            case ButtonStyle.Outline:
            default:
                Box.Draw(buffer, r.X, r.Y, r.W, r.H,
                    _style == ButtonStyle.Outline ? BoxStyles.Single : BoxStyles.Rounded, hot);
                if (down)
                {
                    // pressed indicator: invert the LABEL only (dark text on
                    // a bright chip) while the border brightens — a crisp
                    // push-down, not a gray wash over the whole button
                    buffer.WriteTextClipped(Clip, lx, ly, label,
                        fg: 232, bg: hot, bold: true);
                }
                else
                {
                    buffer.WriteTextClipped(Clip, lx, ly, label, fg: hot, bold: true);
                }
                break;
        }
    }
}

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

/// <summary>
/// Horizontal slider bound to a Signal&lt;float&gt; in [0,1].
/// Click to jump, drag to slide (mouse capture keeps the drag alive even
/// when the pointer leaves the row). Knob brightens on hover/press.
/// </summary>
public sealed class Slider : Widget
{
    private readonly Signal<float> _value;
    private readonly ThemeRole _fg;
    private bool _dragging;

    public Slider(Signal<float> value, ThemeRole fg = ThemeRole.Accent)
    {
        _value = value;
        _fg = fg;
        Clickable = true;
        CapturesMouse = true;
        OnSignal(value); // external writes repaint too
    }

    public float Value => _value.Value;

    /// <summary>Value change per wheel notch.</summary>
    public float WheelStep { get; set; } = 0.05f;

    public override bool OnMouse(MouseEvent ev)
    {
        switch (ev.Kind)
        {
            case MouseKind.Press:
                _dragging = true;
                SetFromX(ev.X);
                return true;
            case MouseKind.Motion:
                if (_dragging) SetFromX(ev.X);
                return _dragging;
            case MouseKind.WheelUp:
                Nudge(+WheelStep); // hover + scroll adjusts, no click needed
                return true;
            case MouseKind.WheelDown:
                Nudge(-WheelStep);
                return true;
            default: // release
                _dragging = false;
                return true;
        }
    }

    private void Nudge(float delta) =>
        _value.Set(Math.Clamp(_value.Value + delta, 0f, 1f));

    private void SetFromX(int x)
    {
        var r = Rect;
        int track = Math.Max(1, r.W - 3); // 1-cell inset each side + knob
        float frac = Math.Clamp((x - r.X - 1) / (float)track, 0f, 1f);
        _value.Set(frac);
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        // opaque: a transparent slider row lets whatever is behind the
        // panel (or the terminal wallpaper) show through — widgets that
        // form a UI strip must own their background
        buffer.FillRect(r.X, r.Y, r.W, r.H,
            new Cell { Char = ' ', Fg = -1, Bg = Theme.Get(ThemeRole.PanelBg) });
        int y = r.Y + r.H / 2;
        int track = Math.Max(1, r.W - 3);
        int pos = r.X + 1 + (int)(_value.Value * track);

        int col = Theme.Get(_fg);
        int hot = Theme.Get(Hovered || Pressed ? ThemeRole.Title : _fg);

        int panelBg = Theme.Get(ThemeRole.PanelBg);
        for (int x = r.X + 1; x <= r.X + 1 + track; x++)
            buffer.WriteTextClipped(Clip, x, y, "─",
                fg: x <= pos ? col : Theme.Get(ThemeRole.Faint), bg: panelBg);

        buffer.WriteTextClipped(Clip, pos, y, "█", fg: hot, bg: panelBg, bold: true);
    }
}

/// <summary>Multi-line word-wrapped paragraph, unicode-width aware.
/// Invalidate (or Tick/OnSignal) when the text provider changes.</summary>
public sealed class Paragraph(Func<string> text, ThemeRole fg = ThemeRole.Text) : Widget
{
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        buffer.WriteTextWrapped(Clip, r.X + 1, r.Y, text(), fg: Theme.Get(fg));
    }
}

/// <summary>Composition-only widget: paints nothing, exists to hold
/// children in the widget tree (panels, groups, rows of controls).</summary>
public sealed class Container : Widget
{
    public override void Render(CellBuffer buffer) { }
}
