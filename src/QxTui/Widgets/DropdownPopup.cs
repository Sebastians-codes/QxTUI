using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

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
