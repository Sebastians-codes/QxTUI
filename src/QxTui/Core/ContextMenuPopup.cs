using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Core;

/// <summary>Floating right-click menu anchored at the click point. Opened
/// by the App: a right press+release WITHOUT drag on a widget whose
/// <see cref="Widget.MenuItems"/> (or nearest ancestor's) is non-empty —
/// a right DRAG stays the resize gesture, so the two never collide.
/// Swallows keys while open; closes on outside press / Escape / commit.</summary>
public sealed class ContextMenuPopup : Widget
{
    private readonly MenuItem[] _items;
    private readonly int _atX, _atY;
    private int _cursor;

    public ContextMenuPopup(MenuItem[] items, int x, int y)
    {
        _items = items;
        _atX = x;
        _atY = y;
        _cursor = Math.Max(0, NextEnabled(0, +1));
        OutsideClickCloses = true; // a press beside the menu dismisses it
    }

    private int NextEnabled(int from, int dir)
    {
        for (int i = from; i >= 0 && i < _items.Length; i += dir)
            if (_items[i].Enabled) return i;
        return -1;
    }

    private Rect MenuRect(Rect screen)
    {
        int w = 8;
        for (int i = 0; i < _items.Length; i++)
            w = Math.Max(w, _items[i].Label.Length + 4);
        w = Math.Min(w, Math.Max(1, screen.W - 2));
        int h = _items.Length + 2;
        int x = Math.Clamp(_atX, 0, Math.Max(0, screen.W - w));
        int y = Math.Clamp(_atY, 0, Math.Max(0, screen.H - h));
        return new Rect(x, y, w, h);
    }

    public override bool HitTest(int x, int y) => MenuRect(Rect).Contains(x, y);

    /// <summary>"Inside" for outside-press dismissal = the menu box, not
    /// the fullscreen overlay rect — same rule as Modal/DropdownPopup.</summary>
    internal override bool OwnsPoint(int x, int y) => MenuRect(Rect).Contains(x, y);

    public override bool HandleKey(KeyEvent ev)
    {
        switch (ev.Key)
        {
            case Key.Escape:
                App?.PopOverlay(this);
                return true;
            case Key.Up:
            {
                int i = NextEnabled(_cursor - 1, -1);
                if (i >= 0) _cursor = i;
                Invalidate();
                return true;
            }
            case Key.Down:
            {
                int i = NextEnabled(_cursor + 1, +1);
                if (i >= 0) _cursor = i;
                Invalidate();
                return true;
            }
            case Key.Enter:
            case Key.Space:
                Commit(_cursor);
                return true;
        }
        return true; // swallow everything else while the menu is open
    }

    private void Commit(int i)
    {
        if (i < 0 || i >= _items.Length || !_items[i].Enabled) return;
        var item = _items[i];
        App?.PopOverlay(this); // pop BEFORE side effects: an Act may push
        if (item.Msg is { } msg) Send(msg);
        item.Act?.Invoke();
    }

    public override bool OnMouse(MouseEvent ev)
    {
        var b = MenuRect(Rect);
        int i = ev.Y - b.Y - 1;
        if (ev.Kind == MouseKind.Motion && i >= 0 && i < _items.Length &&
            _items[i].Enabled && i != _cursor)
        {
            _cursor = i;
            Invalidate();
            return true;
        }
        if (ev.Kind == MouseKind.Release && i >= 0 && i < _items.Length)
        {
            Commit(i);
            return true;
        }
        return ev.Kind != MouseKind.Press || b.Contains(ev.X, ev.Y);
    }

    public override void Render(CellBuffer buffer)
    {
        var b = MenuRect(Rect);
        buffer.FillRect(b.X, b.Y, b.W, b.H,
            new Cell { Char = ' ', Fg = Theme.Get(ThemeRole.Text), Bg = Theme.Get(ThemeRole.PanelBg) });
        QxTui.Render.Box.Draw(buffer, b.X, b.Y, b.W, b.H, BoxStyles.Single,
            Theme.Get(ThemeRole.Border), "", Theme.Get(ThemeRole.Title));
        for (int i = 0; i < _items.Length; i++)
        {
            bool cur = i == _cursor && _items[i].Enabled;
            int fg = !_items[i].Enabled ? Theme.Get(ThemeRole.Faint)
                : cur ? 232 : Theme.Get(ThemeRole.Text);
            int bg = cur ? Theme.Get(ThemeRole.Accent) : Theme.Get(ThemeRole.PanelBg);
            buffer.WriteTextClipped(Clip, b.X + 1, b.Y + 1 + i,
                " " + QxTui.Render.Text.Ellipsize(_items[i].Label, b.W - 3) + " ",
                fg: fg, bg: bg, bold: cur);
        }
    }

    internal override void CollectPaintedRects(List<Rect> dst) => dst.Add(MenuRect(Rect));
}
