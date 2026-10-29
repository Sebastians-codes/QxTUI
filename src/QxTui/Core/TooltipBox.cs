using QxTui.Layout;
using QxTui.Render;

namespace QxTui.Core;

/// <summary>Floating hover label: a small framed box near the hovered
/// widget, pushed by the App's hover tracking after <see cref="Widget.Tooltip"/>
/// widgets have been hovered for their delay. Never interactive — clicks
/// pass straight through (same contract as Toast), and it never owns a
/// press so it can't shield overlays below from outside-click dismissal.</summary>
public sealed class TooltipBox : Widget
{
    private readonly string _text;
    private readonly Widget _owner;
    private int _cursorX, _cursorY;

    /// <summary>The widget this tooltip describes — App keeps the box alive
    /// while hover re-enters its owner (no blink-on-motion).</summary>
    public Widget Owner => _owner;

    /// <summary>Where the box paints right now — public so placement is
    /// assertable (and authors can reason about it).</summary>
    public Rect Box => TipRect(Rect);

    /// <summary>Follow the pointer: App feeds every motion over the owner
    /// in, so a shown tooltip never lags behind the cursor.</summary>
    public void SetAnchor(int x, int y)
    {
        if (x == _cursorX && y == _cursorY) return;
        _cursorX = x;
        _cursorY = y;
        Invalidate();
    }

    public TooltipBox(string text, Widget owner, int cursorX, int cursorY)
    {
        _text = text;
        _owner = owner;
        _cursorX = cursorX;
        _cursorY = cursorY;
    }

    public override bool HitTest(int x, int y) => false; // never eats clicks
    internal override bool OwnsPoint(int x, int y) => false;

    private Rect TipRect(Rect screen)
    {
        int w = Math.Min(Math.Max(1, screen.W - 2), _text.Length + 4);
        int h = 3;
        // hug the POINTER: one row below the cursor cell, flipping above
        // it when the bottom edge is near. (Anchoring to the owner's box
        // read as "spawns randomly": on a tall widget the cursor sat
        // mid-table while the box sat at the table's top edge.)
        int y = _cursorY + 1 + h <= screen.H ? _cursorY + 1 : Math.Max(0, _cursorY - h);
        int x = Math.Clamp(_cursorX, 0, Math.Max(0, screen.W - w));
        return new Rect(x, y, w, h);
    }

    public override void Render(CellBuffer buffer)
    {
        var b = TipRect(Rect);
        buffer.FillRect(b.X, b.Y, b.W, b.H,
            new Cell { Char = ' ', Fg = Theme.Get(ThemeRole.Text), Bg = Theme.Get(ThemeRole.PanelBg) });
        QxTui.Render.Box.Draw(buffer, b.X, b.Y, b.W, b.H, BoxStyles.Single,
            Theme.Get(ThemeRole.Border), "", Theme.Get(ThemeRole.Title));
        buffer.WriteTextClipped(Clip, b.X + 2, b.Y + 1,
            QxTui.Render.Text.Ellipsize(_text, Math.Max(0, b.W - 4)),
            fg: Theme.Get(ThemeRole.Text), bg: Theme.Get(ThemeRole.PanelBg));
    }

    internal override void CollectPaintedRects(List<Rect> dst) => dst.Add(TipRect(Rect));
}
