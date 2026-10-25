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
        buffer.WriteTextClipped(Clip, r.X + 1, r.Y, _text(), fg: Theme.Get(_fg),
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
public sealed class SparklineWidget(
    RingHistory history,
    ThemeRole fg = ThemeRole.Accent,
    float scaleMax = 0f,
    float autoFloor = 1f,
    Func<float>? phase = null) : Widget
{
    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty); // no stale trail on reflow
        Sparkline.Draw(buffer, Clip, r.X, r.Y, r.W, history,
            fg: Theme.Get(fg), scaleMax: scaleMax, autoFloor: autoFloor,
            phase: phase?.Invoke() ?? 0f);
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
                Box.Draw(buffer, r.X, r.Y, r.W, r.H, BoxStyles.Single, hot);
                if (down)
                    buffer.FillRect(r.X + 1, r.Y + 1, r.W - 2, r.H - 2,
                        new Cell { Char = ' ', Fg = -1, Bg = Theme.Get(ThemeRole.Faint) });
                buffer.WriteTextClipped(Clip, lx, ly, label, fg: down ? Theme.Get(ThemeRole.Title) : hot, bold: true);
                break;

            default:
                Box.Draw(buffer, r.X, r.Y, r.W, r.H, BoxStyles.Rounded, hot);
                if (down)
                    buffer.FillRect(r.X + 1, r.Y + 1, r.W - 2, r.H - 2,
                        new Cell { Char = ' ', Fg = -1, Bg = Theme.Get(ThemeRole.Faint) });
                buffer.WriteTextClipped(Clip, lx, ly, label, fg: down ? Theme.Get(ThemeRole.Title) : hot, bold: true);
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
            actionsWidth += _actions[i].Label.Length + 5; // hint(2)+pad(2)+gap(1)
        w = Math.Min(screen.W - 4, Math.Max(w, actionsWidth + 4));
        int h = Math.Min(screen.H - 2, lines.Length + 4 + (_actions.Length > 0 ? 2 : 0));
        return new Rect((screen.W - w) / 2, (screen.H - h) / 2, w, h);
    }

    /// <summary>Button-chip rects (the filled part), in order.</summary>
    private List<(Rect Rect, Action Act)> ActionRects(Rect box)
    {
        var result = new List<(Rect, Action)>(_actions.Length);
        int x = box.X + 2;
        int y = box.Bottom - 2;
        for (int i = 0; i < _actions.Length; i++)
        {
            int btnW = _actions[i].Label.Length + 2;
            result.Add((new Rect(x + 2, y, btnW, 1), _actions[i].Act));
            x += 2 + btnW + 1; // hint(2) + button + gap(1)
        }
        return result;
    }

    private int _hoverAction = -1;

    public override bool OnMouse(MouseEvent ev)
    {
        // per-button hover for the chip highlight
        var box = BoxRect(Rect);
        int idx = -1;
        var rects = ActionRects(box);
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i].Rect;
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
        foreach (var (r, act) in ActionRects(box))
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
            buffer.WriteTextClipped(Clip, box.X + 2, box.Y + 2 + i, lines[i],
                fg: Theme.Get(ThemeRole.Text), bg: Theme.Get(ThemeRole.PanelBg));

        // action button row: key hint + filled chip (hover brightens)
        int ax = box.X + 2;
        int ay = box.Bottom - 2;
        for (int i = 0; i < _actions.Length; i++)
        {
            string label = _actions[i].Label;
            int btnW = label.Length + 2;

            buffer.WriteTextClipped(Clip, ax, ay, $"{i + 1}", fg: Theme.Get(ThemeRole.Dim));
            int bg = Theme.Get(i == _hoverAction ? ThemeRole.Title : ThemeRole.Accent);
            for (int cx = 0; cx < btnW; cx++)
                buffer.WriteTextClipped(Clip, ax + 2 + cx, ay, " ", fg: 232, bg: bg);
            buffer.WriteTextClipped(Clip, ax + 3, ay, label, fg: 232, bg: bg, bold: true);

            ax += 2 + btnW + 1;
        }
    }
}
