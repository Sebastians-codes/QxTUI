using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;

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
