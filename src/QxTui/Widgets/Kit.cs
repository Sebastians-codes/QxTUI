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
        if (_bg is { } role)
            buffer.FillRect(r.X, r.Y, r.W, r.H, new Cell { Char = ' ', Fg = -1, Bg = Theme.Get(role) });
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
        Sparkline.Draw(buffer, Clip, r.X, r.Y, r.W, history,
            fg: Theme.Get(fg), scaleMax: scaleMax, autoFloor: autoFloor,
            phase: phase?.Invoke() ?? 0f);
    }
}

/// <summary>Clickable labeled button. The click target is its whole rect.</summary>
public sealed class ButtonWidget : Widget
{
    private readonly Func<string> _label;
    private readonly Action _onClick;
    private readonly ThemeRole _fg;

    public ButtonWidget(Func<string> label, Action onClick, ThemeRole fg = ThemeRole.Accent)
    {
        _label = label;
        _onClick = onClick;
        _fg = fg;
        Clickable = true;
    }

    public override void OnClick(Point local) => _onClick();

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Box.Draw(buffer, r.X, r.Y, r.W, r.H, BoxStyles.Rounded,
            Theme.Get(_fg), _label(), Theme.Get(ThemeRole.Title));
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

    public ModalWidget(string title, Func<string[]> lines)
    {
        _title = title;
        _lines = lines;
        Clickable = true;
    }

    public override bool HandleKey(KeyEvent ev)
    {
        if (ev.Key == Key.Escape)
        {
            App?.PopOverlay();
            return true;
        }
        return true; // modal: swallow everything else
    }

    private Rect BoxRect(Rect screen)
    {
        var lines = _lines();
        int w = Math.Min(screen.W - 4, Math.Max(24, _title.Length + 8));
        foreach (var l in lines) w = Math.Min(screen.W - 4, Math.Max(w, l.Length + 4));
        int h = Math.Min(screen.H - 2, lines.Length + 4);
        return new Rect((screen.W - w) / 2, (screen.H - h) / 2, w, h);
    }

    public override void OnClick(Point local)
    {
        var box = BoxRect(Rect);
        bool insideBox = local.X >= box.X && local.X < box.Right &&
                         local.Y >= box.Y && local.Y < box.Bottom;
        // [x] button sits at the box top-right; dim area = outside the box
        bool closeBtn = local.X >= box.Right - 4 && local.X < box.Right - 1 && local.Y == box.Y;
        if (!insideBox || closeBtn)
            App?.PopOverlay();
    }

    public override void Render(CellBuffer buffer)
    {
        var screen = Rect;
        // dim everything
        buffer.FillRect(0, 0, screen.W, screen.H, new Cell { Char = ' ', Fg = -1, Bg = 236 });

        var box = BoxRect(screen);
        buffer.FillRect(box.X, box.Y, box.W, box.H,
            new Cell { Char = ' ', Fg = Theme.Get(ThemeRole.Text), Bg = Theme.Get(ThemeRole.PanelBg) });
        Box.Draw(buffer, box.X, box.Y, box.W, box.H, BoxStyles.Double,
            Theme.Get(ThemeRole.Border), _title, Theme.Get(ThemeRole.Title));
        buffer.WriteTextClipped(Clip, box.Right - 4, box.Y, "[x]", fg: Theme.Get(ThemeRole.Danger), bold: true);

        var lines = _lines();
        for (int i = 0; i < lines.Length && box.Y + 2 + i < box.Bottom - 1; i++)
            buffer.WriteTextClipped(Clip, box.X + 2, box.Y + 2 + i, lines[i],
                fg: Theme.Get(ThemeRole.Text), bg: Theme.Get(ThemeRole.PanelBg));
    }
}
