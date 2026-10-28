using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>Titled frame panel: children lay out INSIDE the border ring
/// (ChildInset = 1), so hosted content can never paint the frame away —
/// the widget every sample used to hand-roll.</summary>
public sealed class Panel : Widget
{
    private readonly Func<string>? _title;

    public ThemeRole BorderRole = ThemeRole.Border;
    public ThemeRole TitleRole = ThemeRole.Title;

    public Panel(string? title = null) : this(title is null ? null : () => title) { }

    public Panel(Func<string>? title)
    {
        _title = title;
        Node.ChildInset = 1;
        if (title is not null)
            Tick(TimeSpan.FromMilliseconds(250)); // reactive titles repaint
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        Box.Draw(buffer, r.X, r.Y, r.W, r.H, BoxStyles.Single,
            Theme.Get(BorderRole), _title?.Invoke() ?? "", Theme.Get(TitleRole));
    }
}

/// <summary>Horizontal rule. Height 1 by convention; taller rects get one
/// line per row (use as a column divider by giving it Width 1 and the
/// vertical style renders instead).</summary>
public sealed class Separator : Widget
{
    public ThemeRole Role = ThemeRole.Faint;

    public Separator() => Node.Height = 1;

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        bool vertical = r.H > r.W;
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
                buffer.WriteTextClipped(Clip, x, y, vertical ? "│" : "─",
                    fg: Theme.Get(Role));
    }
}

/// <summary>Bound boolean toggle: [x]/[ ] plus label. State lives in a
/// Signal (the state layer), so several widgets can share one checkbox's
/// truth and settings screens stay testable headless.</summary>
public sealed class Checkbox : Widget
{
    private readonly Signal<bool> _value;
    private readonly Func<string> _label;

    public Checkbox(Signal<bool> value, Func<string> label)
    {
        _value = value;
        _label = label;
        Clickable = true;
        Focusable = true;
        OnSignal(value);
        Node.Height = 1;
    }

    public Checkbox(Signal<bool> value, string label) : this(value, () => label) { }

    public override void OnClick(Point local) => _value.Set(!_value.Value);

    public override bool HandleKey(KeyEvent ev)
    {
        if (Focused && ev.Key == Key.Space)
        {
            _value.Set(!_value.Value);
            return true;
        }
        return false;
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        int fg = Theme.Get(Hovered ? ThemeRole.Title
            : Focused ? ThemeRole.Accent : ThemeRole.Text);
        buffer.WriteTextClipped(Clip, r.X, r.Y, _value.Value ? "[x] " : "[ ] ",
            fg: fg, bold: Hovered || Pressed);
        buffer.WriteTextClipped(Clip, r.X + 4, r.Y,
            QxTui.Render.Text.Ellipsize(_label(), Math.Max(0, r.W - 4)), fg: fg);
    }
}

/// <summary>Radio group as a segmented control: exactly one of N options,
/// bound to a Signal&lt;int&gt;. Click a segment or use arrows while
/// focused.</summary>
public sealed class Segmented : Widget
{
    private readonly Signal<int> _selected;
    private readonly string[] _options;

    public Segmented(Signal<int> selected, params string[] options)
    {
        _selected = selected;
        _options = options;
        Clickable = true;
        Focusable = true;
        OnSignal(selected);
        Node.Height = 1;
    }

    /// <summary>Segment index under a widget-local x, or -1. Layout source
    /// for both render and hit-testing: ' lab ' + 1-col separators.</summary>
    private int SegmentAt(int localX)
    {
        int x = 0;
        for (int i = 0; i < _options.Length; i++)
        {
            int w = _options[i].Length + 2;
            if (localX >= x && localX < x + w) return i;
            x += w + 1;
        }
        return -1;
    }

    public override void OnClick(Point local)
    {
        int i = SegmentAt(local.X);
        if (i >= 0) _selected.Set(i);
    }

    public override bool HandleKey(KeyEvent ev)
    {
        if (!Focused) return false;
        if (ev.Key == Key.Right)
        {
            _selected.Set(Math.Min(_options.Length - 1, _selected.Value + 1));
            return true;
        }
        if (ev.Key == Key.Left)
        {
            _selected.Set(Math.Max(0, _selected.Value - 1));
            return true;
        }
        return false;
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        int x = r.X;
        for (int i = 0; i < _options.Length; i++)
        {
            bool sel = i == _selected.Value;
            int fg = sel ? 232 : Theme.Get(Hovered ? ThemeRole.Title : ThemeRole.Text);
            int bg = sel ? Theme.Get(ThemeRole.Accent) : -1;
            buffer.WriteTextClipped(Clip, x, r.Y, $" {_options[i]} ", fg: fg, bg: bg, bold: sel);
            x += _options[i].Length + 2;
            if (i < _options.Length - 1)
            {
                buffer.WriteTextClipped(Clip, x, r.Y, "│", fg: Theme.Get(ThemeRole.Dim));
                x += 1;
            }
        }
    }
}

/// <summary>Self-driving activity indicator (braille frames). For work
/// whose progress is unknown — pair with App.Effect.</summary>
public sealed class Spinner : Widget
{
    private static readonly string[] Frames =
        ["\u280b", "\u2819", "\u2839", "\u2838", "\u283c", "\u2834", "\u2826", "\u2827", "\u2807", "\u280f"];

    private int _frame;
    public ThemeRole Role = ThemeRole.Accent;

    public Spinner(int hz = 12)
    {
        Node.Height = 1;
        Node.Width = 1;
        Tick(TimeSpan.FromMilliseconds(1000.0 / hz),
            _ => { _frame = (_frame + 1) % Frames.Length; Invalidate(); });
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        buffer.WriteTextClipped(Clip, r.X, r.Y, Frames[_frame], fg: Theme.Get(Role));
    }
}

/// <summary>Determinate progress bar bound to a Signal&lt;float&gt; in
/// [0,1]: filled blocks plus a right-aligned percent when it fits.</summary>
public sealed class ProgressBar : Widget
{
    private readonly Signal<float> _value;
    public ThemeRole Role = ThemeRole.Success;

    public ProgressBar(Signal<float> value)
    {
        _value = value;
        OnSignal(value);
        Node.Height = 1;
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        float v = Math.Clamp(_value.Value, 0f, 1f);
        string pct = $"{(int)(v * 100)}%";
        int barW = r.W >= pct.Length + 1 ? r.W - pct.Length - 1 : r.W;
        int filled = (int)(v * barW);
        for (int x = 0; x < barW; x++)
            buffer.WriteTextClipped(Clip, r.X + x, r.Y, x < filled ? "█" : "░",
                fg: Theme.Get(x < filled ? Role : ThemeRole.Dim));
        if (barW < r.W)
            buffer.WriteTextClipped(Clip, r.X + barW + 1, r.Y, pct,
                fg: Theme.Get(ThemeRole.Text));
    }
}

/// <summary>Compact one-of-many chooser: a closed one-row control that
/// opens a floating option list (an overlay layer) anchored under itself.
/// The popup closes on outside press, Escape, or a choice — all via
/// features the overlay stack already has.</summary>
public sealed class Dropdown : Widget
{
    private readonly Signal<int> _selected;
    private readonly Func<string[]> _options;

    public Dropdown(Signal<int> selected, Func<string[]> options)
    {
        _selected = selected;
        _options = options;
        Clickable = true;
        Focusable = true;
        OnSignal(selected);
        Tick(TimeSpan.FromMilliseconds(250)); // reactive option lists
        Node.Height = 1;
    }

    public Dropdown(Signal<int> selected, params string[] options)
        : this(selected, () => options) { }

    public string Current =>
        _options()[Math.Clamp(_selected.Value, 0, Math.Max(0, _options().Length - 1))];

    public override void OnClick(Point local) => Toggle();

    public override bool HandleKey(KeyEvent ev)
    {
        if (!Focused) return false;
        if (ev.Key is Key.Enter or Key.Space or Key.Down)
        {
            Toggle();
            return true;
        }
        return false;
    }

    private void Toggle()
    {
        if (App is null) return;
        if (App.Overlay is DropdownPopup p && ReferenceEquals(p.Owner, this))
        {
            App.PopOverlay(p);
            return;
        }
        App.PushOverlay(new DropdownPopup(this, _selected, _options()));
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);
        int fg = Theme.Get(Hovered || Focused ? ThemeRole.Title : ThemeRole.Text);
        string label = QxTui.Render.Text.Ellipsize($" {Current} ▾", Math.Max(0, r.W - 2));
        buffer.WriteTextClipped(Clip, r.X, r.Y, label, fg: fg,
            bg: Theme.Get(ThemeRole.PanelBg), bold: Hovered);
        buffer.WriteTextClipped(Clip, r.X + r.W - 1, r.Y, " ",
            bg: Theme.Get(ThemeRole.PanelBg));
    }
}

/// <summary>The Dropdown's open option list: a floating layer anchored
/// under its owner. Swallows keys while open (modal-ish), closes on
/// outside press / Escape / choice.</summary>
public sealed class DropdownPopup : Widget
{
    public Dropdown Owner { get; }
    private readonly Signal<int> _value;
    private readonly string[] _options;
    private int _cursor;

    public DropdownPopup(Dropdown owner, Signal<int> value, string[] options)
    {
        Owner = owner;
        _value = value;
        _options = options;
        _cursor = Math.Clamp(value.Value, 0, Math.Max(0, options.Length - 1));
        OutsideClickCloses = true; // a press beside the list dismisses it
    }

    private Rect ListRect(Rect screen)
    {
        int w = 6;
        for (int i = 0; i < _options.Length; i++)
            w = Math.Max(w, _options[i].Length + 4);
        w = Math.Min(w, Math.Max(1, screen.W - 2));
        int h = _options.Length + 2;
        var a = Owner.Rect;
        int x = Math.Clamp(a.X, 0, Math.Max(0, screen.W - w));
        int y = Math.Clamp(a.Bottom, 0, Math.Max(0, screen.H - h));
        return new Rect(x, y, w, h);
    }

    public override bool HitTest(int x, int y) => ListRect(Rect).Contains(x, y);

    /// <summary>"Inside" for outside-press dismissal = the list box, not
    /// the fullscreen overlay rect — same rule as Modal.</summary>
    internal override bool OwnsPoint(int x, int y) => ListRect(Rect).Contains(x, y);

    public override bool HandleKey(KeyEvent ev)
    {
        switch (ev.Key)
        {
            case Key.Escape:
                App?.PopOverlay(this);
                return true;
            case Key.Up:
                _cursor = (_cursor - 1 + _options.Length) % _options.Length;
                Invalidate();
                return true;
            case Key.Down:
                _cursor = (_cursor + 1) % _options.Length;
                Invalidate();
                return true;
            case Key.Enter:
            case Key.Space:
                Commit(_cursor);
                return true;
        }
        return true; // swallow everything else while the list is open
    }

    private void Commit(int i)
    {
        if (i >= 0 && i < _options.Length) _value.Set(i);
        App?.PopOverlay(this);
    }

    public override bool OnMouse(MouseEvent ev)
    {
        var b = ListRect(Rect);
        int i = ev.Y - b.Y - 1;
        if (ev.Kind == MouseKind.Motion && i >= 0 && i < _options.Length && i != _cursor)
        {
            _cursor = i;
            Invalidate();
            return true;
        }
        if (ev.Kind == MouseKind.Release && i >= 0 && i < _options.Length)
        {
            Commit(i);
            return true;
        }
        return ev.Kind != MouseKind.Press || b.Contains(ev.X, ev.Y);
    }

    public override void Render(CellBuffer buffer)
    {
        var b = ListRect(Rect);
        buffer.FillRect(b.X, b.Y, b.W, b.H,
            new Cell { Char = ' ', Fg = Theme.Get(ThemeRole.Text), Bg = Theme.Get(ThemeRole.PanelBg) });
        QxTui.Render.Box.Draw(buffer, b.X, b.Y, b.W, b.H, BoxStyles.Single,
            Theme.Get(ThemeRole.Border), "", Theme.Get(ThemeRole.Title));
        for (int i = 0; i < _options.Length; i++)
        {
            bool cur = i == _cursor;
            buffer.WriteTextClipped(Clip, b.X + 1, b.Y + 1 + i,
                " " + QxTui.Render.Text.Ellipsize(_options[i], b.W - 3) + " ",
                fg: cur ? 232 : Theme.Get(ThemeRole.Text),
                bg: cur ? Theme.Get(ThemeRole.Accent) : Theme.Get(ThemeRole.PanelBg),
                bold: cur);
        }
    }

    internal override void CollectPaintedRects(List<Rect> dst) => dst.Add(ListRect(Rect));
}

/// <summary>Transient notification: a small floating box that pops itself
/// after a TTL. Clicks pass straight through (HitTest false). Open one via
/// <see cref="Core.App.Toast"/> (single slot: a new toast replaces the
/// old).</summary>
public sealed class Toast : Widget
{
    private readonly string _text;
    private readonly ThemeRole _role;

    public Toast(string text, ThemeRole role, TimeSpan ttl)
    {
        _text = text;
        _role = role;
        Tick(ttl, _ => App?.PopOverlay(this));
    }

    public override bool HitTest(int x, int y) => false; // never eats clicks

    /// <summary>Never owns a press: a toast must not shield opt-in
    /// modals below it from outside-press dismissal.</summary>
    internal override bool OwnsPoint(int x, int y) => false;

    private Rect Box(Rect screen)
    {
        int w = Math.Min(screen.W - 2, _text.Length + 4);
        int h = 3;
        int x = Math.Clamp((screen.W - w) / 2, 0, Math.Max(0, screen.W - w));
        int y = Math.Max(0, screen.H - h - 1);
        return new Rect(x, y, w, h);
    }

    public override void Render(CellBuffer buffer)
    {
        var b = Box(Rect);
        buffer.FillRect(b.X, b.Y, b.W, b.H,
            new Cell { Char = ' ', Fg = 232, Bg = Theme.Get(_role) });
        buffer.WriteTextClipped(Clip, b.X + 2, b.Y + 1,
            QxTui.Render.Text.Ellipsize(_text, b.W - 4), fg: 232, bold: true);
    }

    internal override void CollectPaintedRects(List<Rect> dst) => dst.Add(Box(Rect));
}

/// <summary>App-level convenience: transient notifications without the
/// caller managing overlay lifetimes.</summary>
public static class ToastExtensions
{
    /// <summary>Show a toast (single slot: replaces any current toast).
    /// Clicks pass through it; it pops itself after <paramref name="ms"/>.</summary>
    public static void Toast(this App app, string text,
        ThemeRole role = ThemeRole.Success, int ms = 2500)
    {
        for (int i = app.OverlayCount - 1; i >= 0; i--)
            if (app.OverlayAt(i) is Toast t)
                app.PopOverlay(t);
        app.PushOverlay(new Toast(text, role, TimeSpan.FromMilliseconds(ms)));
    }
}
