using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>Single-line clipped label. Text is a provider so it can be
/// live without extra invalidation plumbing.</summary>
public sealed class LabelWidget : Widget
{
    private readonly Func<string> _text;
    private readonly ThemeRole _fg;
    private readonly ThemeRole? _bg;

    public LabelWidget(Func<string> text, ThemeRole fg = ThemeRole.Text, ThemeRole? bg = null)
    {
        _text = text;
        _fg = fg;
        _bg = bg;
    }

    /// <summary>Signal-bound: re-renders whenever the source changes.</summary>
    public LabelWidget(Signal<float> source, Func<float, string> format,
        ThemeRole fg = ThemeRole.Text, ThemeRole? bg = null)
        : this(() => format(source.Value), fg, bg)
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
public sealed class BarWidget : Widget
{
    private readonly Func<double> _fraction;
    private readonly ThemeRole _fg;

    public BarWidget(Func<double> fraction, ThemeRole fg = ThemeRole.Accent)
    {
        _fraction = fraction;
        _fg = fg;
    }

    /// <summary>Signal-bound: re-renders automatically whenever the source
    /// signal changes — the invalidation wiring you'd otherwise forget.</summary>
    public BarWidget(Signal<float> source, ThemeRole fg = ThemeRole.Accent)
        : this(() => source.Value, fg)
    {
        OnSignal(source);
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        Bar.Draw(buffer, Clip, r.X, r.Y, r.W, _fraction(), fg: Theme.Get(_fg));
    }
}

/// <summary>Sparkline bound to a RingHistory the app owns and pushes to.</summary>
public sealed class SparklineWidget : Widget
{
    private readonly RingHistory _history;
    private readonly ThemeRole _fg;
    private readonly float _scaleMax;
    private readonly float _autoFloor;
    private readonly Func<float>? _phase;

    public SparklineWidget(RingHistory history, ThemeRole fg = ThemeRole.Accent,
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
        Sparkline.Draw(buffer, Clip, r.X, r.Y, r.W, _history,
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
public sealed class ButtonWidget : Widget
{
    private readonly Func<string> _label;
    private readonly Action _onClick;
    private readonly ThemeRole _fg;
    private readonly ButtonStyle _style;

    /// <summary>Optional persistent state color (e.g. a toggle: green when
    /// on, warning when off). Overrides <c>fg</c> while provided.</summary>
    public Func<ThemeRole>? RoleProvider { get; set; }

    public ButtonWidget(Func<string> label, Action onClick,
        ThemeRole fg = ThemeRole.Accent, ButtonStyle style = ButtonStyle.Rounded)
    {
        _label = label;
        _onClick = onClick;
        _fg = fg;
        _style = style;
        Clickable = true;
    }

    public override void OnClick(Point local) => _onClick();

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

/// <summary>
/// Modal popup: dims the screen, draws a centered box, consumes all keys
/// (modal semantics). Esc closes; clicking the [x] or the dim area closes.
/// </summary>
public sealed class ModalWidget : Widget
{
    private readonly string _title;
    private readonly Func<string[]> _lines;
    private readonly bool _dim;
    private readonly (string Label, Action Act)[] _actions;

    /// <param name="dim">Dim everything behind the box (classic modal).
    /// false = floating popup over live content.</param>
    /// <param name="actions">Button row inside the box: clickable AND
    /// bound to keys 1..n while the modal is open.</param>
    public ModalWidget(string title, Func<string[]> lines, bool dim = true,
        (string Label, Action Act)[]? actions = null)
    {
        _title = title;
        _lines = lines;
        _dim = dim;
        _actions = actions ?? [];
        Clickable = true;
        BlocksBaseRendering = dim; // floating popups keep the background live
        Tick(TimeSpan.FromMilliseconds(250)); // reactive content (Func lines)
    }

    public override bool HandleKey(KeyEvent ev)
    {
        if (ev.Key == Key.Escape)
        {
            App?.PopOverlay();
            return true;
        }
        if (ev.Key == Key.Char && ev.Char >= '1' && ev.Char <= '9')
        {
            int idx = ev.Char - '1';
            if (idx < _actions.Length)
                _actions[idx].Act();
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

        // chips wrap inside the clamped width; the box grows vertically
        int actionRows = _actions.Length == 0 ? 0 : ActionRows(w - 4);
        int h = Math.Min(screen.H - 2,
            lines.Length + 4 + (actionRows > 0 ? actionRows + 1 : 0));
        return new Rect((screen.W - w) / 2, (screen.H - h) / 2, w, h);
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
    private List<(Rect Rect, Action Act, int Idx)> LayoutActions(Rect box)
    {
        var result = new List<(Rect, Action, int)>(_actions.Length);
        int innerW = box.W - 4;
        int x = 0, row = 0;
        for (int i = 0; i < _actions.Length; i++)
        {
            int btnW = _actions[i].Label.Length + 2;
            int need = btnW + 3;
            if (x + need > innerW && x > 0) { row++; x = 0; }
            int y = box.Bottom - 2 - row;
            result.Add((new Rect(box.X + 2 + x + 2, y, btnW, 1), _actions[i].Act, i));
            x += need;
        }
        return result;
    }

    private int _hoverAction = -1;

    public override bool OnMouse(MouseEvent ev)
    {
        // per-button hover for the chip highlight
        var box = BoxRect(Rect);
        int idx = -1;
        foreach (var (r, _, i) in LayoutActions(box))
        {
            if (ev.X >= r.X && ev.X < r.Right && ev.Y == r.Y) { idx = i; break; }
        }
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

        // action buttons first
        foreach (var (r, act, _) in LayoutActions(box))
        {
            if (local.X >= r.X && local.X < r.Right && local.Y == r.Y)
            {
                act();
                return;
            }
        }

        bool insideBox = local.X >= box.X && local.X < box.Right &&
                         local.Y >= box.Y && local.Y < box.Bottom;
        // [x] button sits at the box top-right; outside the box closes
        bool closeBtn = local.X >= box.Right - 4 && local.X < box.Right - 1 && local.Y == box.Y;
        if (!insideBox || closeBtn)
            App?.PopOverlay();
    }

    public override void Render(CellBuffer buffer)
    {
        var screen = Rect;
        if (_dim)
            buffer.FillRect(0, 0, screen.W, screen.H, new Cell { Char = ' ', Fg = -1, Bg = 236 });

        var box = BoxRect(screen);
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

        // action chips: key hint + filled button, wrapped inside the box
        foreach (var (r, _, i) in LayoutActions(box))
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
public sealed class SliderWidget : Widget
{
    private readonly Signal<float> _value;
    private readonly ThemeRole _fg;
    private bool _dragging;

    public SliderWidget(Signal<float> value, ThemeRole fg = ThemeRole.Accent)
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
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        int y = r.Y + r.H / 2;
        int track = Math.Max(1, r.W - 3);
        int pos = r.X + 1 + (int)(_value.Value * track);

        int col = Theme.Get(_fg);
        int hot = Theme.Get(Hovered || Pressed ? ThemeRole.Title : _fg);

        for (int x = r.X + 1; x <= r.X + 1 + track; x++)
            buffer.WriteTextClipped(Clip, x, y, "─", fg: x <= pos ? col : Theme.Get(ThemeRole.Faint));

        buffer.WriteTextClipped(Clip, pos, y, "█", fg: hot, bold: true);
    }
}

/// <summary>Multi-line word-wrapped paragraph, unicode-width aware.
/// Invalidate (or Tick/OnSignal) when the text provider changes.</summary>
public sealed class TextWidget(Func<string> text, ThemeRole fg = ThemeRole.Text) : Widget
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
public sealed class ContainerWidget : Widget
{
    public override void Render(CellBuffer buffer) { }
}
