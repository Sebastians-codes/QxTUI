using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>Titled frame panel: children lay out INSIDE the border ring
/// (ChildInset = 1), so hosted content can never paint the frame away —
/// the widget every sample used to hand-roll.
///
/// OPT-IN collapse (<see cref="Collapsible"/>, default false): the panel
/// draws a &lt; chip at the right of its title bar; clicking it (or
/// setting <see cref="IsCollapsed"/> from code / a key) collapses the
/// panel to a one-column rail whose &gt; chip expands it again. The rail
/// keeps the chip clickable, the expanded width is remembered, children
/// stay mounted (state survives), and a collapsed panel refuses resize
/// grabs. <see cref="CollapseChanged"/> lets the app react (move focus
/// out of the vanishing pane, persist the state, …).</summary>
public sealed class Panel : Widget
{
    private readonly Func<string>? _title;
    private int? _preCollapseWidth;
    private bool _chipHot;
    private bool _isCollapsed;

    public ThemeRole BorderRole = ThemeRole.Border;
    public ThemeRole TitleRole = ThemeRole.Title;

    private bool _collapsible;

    /// <summary>Opt-in: draw the &lt;/&gt; collapse chip and accept
    /// collapse requests. Default false — a panel is static unless its
    /// author says otherwise.</summary>
    public bool Collapsible
    {
        get => _collapsible;
        set
        {
            if (_collapsible == value) return;
            _collapsible = value;
            Clickable = value; // the chip is clicked (children still win hits)
            if (!value && _isCollapsed) // un-opt-in while railed: restore
            {
                _isCollapsed = false;
                ApplyCollapse();
            }
            Invalidate();
        }
    }

    /// <summary>Rail mode. Ignored while <see cref="Collapsible"/> is
    /// false. Setting it requests the relayout and fires
    /// <see cref="CollapseChanged"/>.</summary>
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (!Collapsible || _isCollapsed == value) return;
            _isCollapsed = value;
            ApplyCollapse();
            CollapseChanged?.Invoke(value);
        }
    }

    /// <summary>Fires after the rail/expand switch — apps move focus out
    /// of a pane that just vanished.</summary>
    public event Action<bool>? CollapseChanged;

    public Panel(string? title = null) : this(title is null ? null : () => title) { }

    public Panel(Func<string>? title)
    {
        _title = title;
        Node.ChildInset = 1;
        if (title is not null)
            Tick(TimeSpan.FromMilliseconds(250)); // reactive titles repaint
    }

    /// <summary>The cell (widget-local) carrying the collapse chip.</summary>
    private Point ChipCell() =>
        _isCollapsed ? new Point(0, 0) : new Point(Rect.W - 2, 0);

    private void ApplyCollapse()
    {
        if (_isCollapsed)
        {
            // remember the expanded width (explicit spec or live rect) so
            // expanding restores exactly what the user had — including a
            // width they right-dragged to
            _preCollapseWidth = Node.Width ?? (Rect.W > 1 ? Rect.W : null);
            Node.Width = 1;
        }
        else
        {
            if (_preCollapseWidth is int w) Node.Width = w;
            _preCollapseWidth = null;
        }
        App?.RequestLayout();
        Invalidate();
    }

    public override void OnRectChanged(Rect oldRect, Rect newRect)
    {
        // a panel collapsed before its first layout learns its real width
        // only once laid out — remember + rail it then
        if (_isCollapsed && newRect.W > 1 && Node.Width != 1)
        {
            _preCollapseWidth = Node.Width ?? newRect.W;
            Node.Width = 1;
            App?.RequestLayout();
        }
    }

    public override bool OnMouse(MouseEvent ev)
    {
        if (!Collapsible) return false;
        if (ev.Kind != MouseKind.Motion && ev.Kind != MouseKind.Press) return false;
        bool hot = ev.X - Rect.X == ChipCell().X && ev.Y - Rect.Y == ChipCell().Y;
        if (hot != _chipHot)
        {
            _chipHot = hot;
            Invalidate();
        }
        return false; // never consume: children and drags behave normally
    }

    public override void OnClick(Point local)
    {
        if (!Collapsible) return;
        if (local == ChipCell()) IsCollapsed = !_isCollapsed;
    }

    /// <summary>A collapsed rail must not invite resize grabs — there is
    /// nothing to size.</summary>
    internal override int ResizeEdgeAt(int x, int y) =>
        _isCollapsed ? 0 : base.ResizeEdgeAt(x, y);

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);

        int chipBg = Theme.Get(_chipHot ? ThemeRole.Title : ThemeRole.Accent);
        if (_isCollapsed)
        {
            // the rail IS the chip column: nothing else paints
            buffer.WriteTextClipped(Clip, r.X, r.Y, ">", fg: 232, bg: chipBg, bold: true);
            return;
        }

        Box.Draw(buffer, r.X, r.Y, r.W, r.H, BoxStyles.Single,
            Theme.Get(BorderRole), _title?.Invoke() ?? "", Theme.Get(TitleRole));
        if (Collapsible)
            buffer.WriteTextClipped(Clip, r.Right - 2, r.Y, "<", fg: 232, bg: chipBg, bold: true);
    }
}
