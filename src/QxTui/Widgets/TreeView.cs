using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>
/// Expand/collapse tree with indentation guides, a selection cursor,
/// virtualized scrolling (wheel/keys/thumb-drag, ListView feel) and
/// keyboard semantics: Left collapses (or climbs to the parent), Right
/// expands (or descends into the first child), Enter activates a leaf
/// or toggles a parent, Space always toggles.
///
/// The flat visible-row list is rebuilt per render — trees are small;
/// only the visible slice ever paints, so deep collapsed data is free.
/// </summary>
public sealed class TreeView : Widget
{
    public TreeNode Root { get; }

    /// <summary>Fires on Enter over a leaf (events become messages).</summary>
    public event Action<TreeNode>? Activated;

    /// <summary>Fires after any expand/collapse.</summary>
    public event Action<TreeNode>? Toggled;

    public bool ShowScrollbar { get; set; } = true;

    /// <summary>Index of the first visible row.</summary>
    public int Offset { get; private set; }

    private int _selected; // index into the flattened visible rows
    private readonly List<TreeNode> _flat = [];
    private readonly List<int> _depth = [];

    private bool _dragThumb;
    private int _grabDelta, _grabDeltaPx, _lastRenderedOffset;

    public TreeView(TreeNode root)
    {
        Root = root;
        Focusable = true;
        Clickable = true;
        CapturesMouse = true; // thumb drags survive leaving the widget
    }

    /// <summary>The node under the cursor (null for an empty tree).</summary>
    public TreeNode? SelectedNode
    {
        get
        {
            Flatten();
            return _flat.Count == 0 ? null : _flat[Math.Clamp(_selected, 0, _flat.Count - 1)];
        }
    }

    private void Flatten()
    {
        _flat.Clear();
        _depth.Clear();
        void Walk(TreeNode n, int d)
        {
            _flat.Add(n);
            _depth.Add(d);
            if (n.Expanded)
                foreach (var c in n.Children) Walk(c, d + 1);
        }
        Walk(Root, 0);
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _flat.Count - 1));
    }

    private int TrackW => ShowScrollbar ? 1 : 0;

    public void ScrollTo(int index)
    {
        Flatten();
        Offset = Math.Clamp(index, 0, Math.Max(0, _flat.Count - Rect.H));
        Invalidate();
    }

    public void ScrollBy(int delta) => ScrollTo(Offset + delta);

    private void EnsureVisible()
    {
        Flatten();
        if (_selected < Offset) Offset = _selected;
        else if (_selected >= Offset + Rect.H) Offset = _selected - Rect.H + 1;
        Invalidate();
    }

    private void Toggle(TreeNode n)
    {
        if (!n.IsParent) return;
        n.Expanded = !n.Expanded;
        Toggled?.Invoke(n);
        EnsureVisible();
    }

    public override bool HandleKey(KeyEvent ev)
    {
        if (!Focused) return false;
        Flatten();
        if (_flat.Count == 0) return false;
        var node = _flat[_selected];
        switch (ev.Key)
        {
            case Key.Down:
                _selected = Math.Min(_flat.Count - 1, _selected + 1);
                EnsureVisible();
                return true;
            case Key.Up:
                _selected = Math.Max(0, _selected - 1);
                EnsureVisible();
                return true;
            case Key.Left:
                if (node.IsParent && node.Expanded) Toggle(node);
                else
                {
                    int p = ParentIndexOf(_selected);
                    if (p >= 0) { _selected = p; EnsureVisible(); }
                }
                return true;
            case Key.Right:
                if (node.IsParent && !node.Expanded) Toggle(node);
                else if (_selected + 1 < _flat.Count && _depth[_selected + 1] > _depth[_selected])
                {
                    _selected++; // first child
                    EnsureVisible();
                }
                return true;
            case Key.Home: _selected = 0; EnsureVisible(); return true;
            case Key.End: _selected = _flat.Count - 1; EnsureVisible(); return true;
            case Key.Enter:
                if (node.IsParent) Toggle(node);
                else Activated?.Invoke(node);
                return true;
            case Key.Space:
                Toggle(node);
                return true;
            default:
                return false;
        }
    }

    private int ParentIndexOf(int index)
    {
        int d = _depth[index];
        for (int i = index - 1; i >= 0; i--)
            if (_depth[i] == d - 1) return i;
        return -1;
    }

    public override bool OnMouse(MouseEvent ev)
    {
        var r = Rect;
        switch (ev.Kind)
        {
            case MouseKind.WheelUp:
                ScrollBy(-3);
                return true;

            case MouseKind.WheelDown:
                ScrollBy(3);
                return true;

            case MouseKind.Press:
                if (ShowScrollbar && ev.X >= r.Right - 1)
                {
                    int thumbY = ThumbY();
                    int thumbH = ThumbH();
                    int cellH = App?.CellPixelH ?? 0;
                    if (ev.Y >= thumbY && ev.Y < thumbY + thumbH)
                    {
                        _grabDelta = ev.Y - thumbY;
                        _grabDeltaPx = ev.Py >= 0 && cellH > 0
                            ? ev.Py - thumbY * cellH
                            : _grabDelta * cellH;
                    }
                    else
                    {
                        _grabDelta = thumbH / 2;
                        _grabDeltaPx = thumbH * cellH / 2;
                    }
                    _dragThumb = true;
                    DragTo(ev); // 1:1: view goes where the cursor is
                    return true;
                }
                return false; // body press handled in OnClick (row select/toggle)

            case MouseKind.Motion:
                if (_dragThumb)
                {
                    DragTo(ev);
                    return true;
                }
                return false;

            default: // release
                _dragThumb = false;
                return false;
        }
    }

    /// <summary>Click on a row: the glyph column toggles, anywhere else
    /// selects. Local coords from the App's Clickable path.</summary>
    public override void OnClick(Point local)
    {
        Flatten();
        int i = Offset + local.Y;
        if (i < 0 || i >= _flat.Count || local.X >= Rect.W - TrackW) return;
        _selected = i;
        if (local.X == _depth[i] * 2) Toggle(_flat[i]); // exactly on the glyph
        Invalidate();
    }

    private int ThumbH()
    {
        Flatten();
        int total = _flat.Count;
        if (total <= Rect.H || Rect.H <= 0) return Rect.H;
        return Math.Max(1, Rect.H * Rect.H / total);
    }

    private int ThumbY()
    {
        Flatten();
        int total = _flat.Count;
        if (total <= Rect.H) return Rect.Y;
        return Rect.Y + Offset * (Rect.H - ThumbH()) / Math.Max(1, total - Rect.H);
    }

    private void DragTo(MouseEvent ev)
    {
        Flatten();
        var r = Rect;
        int total = _flat.Count;
        if (total <= r.H) return;

        int cellH = App?.CellPixelH ?? 0;
        double trackH, thumbTop;
        if (ev.Py >= 0 && cellH > 0)
        {
            trackH = Math.Max(1, (r.H - ThumbH()) * cellH);
            thumbTop = ev.Py - r.Y * cellH - _grabDeltaPx;
        }
        else
        {
            trackH = Math.Max(1, r.H - ThumbH());
            thumbTop = ev.Y - r.Y - _grabDelta;
        }
        double frac = Math.Clamp(thumbTop / trackH, 0, 1);
        ScrollTo((int)Math.Round(frac * (total - r.H)));
    }

    public override void Render(CellBuffer buffer)
    {
        Flatten();
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);

        int total = _flat.Count;
        int contentW = Math.Max(1, r.W - TrackW);
        Offset = Math.Clamp(Offset, 0, Math.Max(0, total - r.H));

        // hardware scroll: whole-rect shift hint (no fixed header here)
        int deltaCells = Offset - _lastRenderedOffset;
        _lastRenderedOffset = Offset;
        if (deltaCells != 0)
            App?.ScrollHint(r, deltaCells);

        int panelBg = Theme.Get(ThemeRole.PanelBg);
        int last = Math.Min(total, Offset + r.H);
        for (int i = Offset; i < last; i++)
        {
            int y = r.Y + (i - Offset);
            var n = _flat[i];
            int d = _depth[i];
            bool sel = i == _selected;
            if (sel && Focused)
                buffer.FillRect(r.X, y, contentW, 1,
                    new Cell { Char = ' ', Fg = -1, Bg = panelBg });

            string glyph = n.IsParent ? (n.Expanded ? "▾" : "▸") : "·";
            int gx = r.X + d * 2;
            buffer.WriteTextClipped(Clip, gx, y, glyph,
                fg: Theme.Get(n.IsParent ? ThemeRole.Accent : ThemeRole.Faint),
                bg: sel && Focused ? panelBg : -1);
            buffer.WriteTextClipped(Clip, gx + 2, y,
                Text.Ellipsize(n.Label, Math.Max(0, contentW - d * 2 - 2)),
                fg: Theme.Get(sel ? ThemeRole.Title : ThemeRole.Text),
                bg: sel && Focused ? panelBg : -1,
                bold: sel && Focused);
        }

        if (!ShowScrollbar) return;
        VScroll.Draw(buffer, Clip, r.Right - 1, r.Y, r.H,
            Offset, r.H, total, Theme.Get(ThemeRole.Accent), Theme.Get(ThemeRole.Faint));
    }
}
