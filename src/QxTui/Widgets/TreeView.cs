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
    private TreeNode _root;

    /// <summary>The tree model. Swapping it resets cursor and scroll —
    /// real data arrives asynchronously (Effect → message), so a tree
    /// must accept a new model long after mount.</summary>
    public TreeNode Root
    {
        get => _root;
        set
        {
            _root = value;
            _selected = 0;
            _anchor = 0;
            _selection.Clear();
            Offset = 0;
            _lastRenderedOffset = 0;
            Invalidate();
        }
    }

    /// <summary>Fires on Enter over a leaf (events become messages).</summary>
    public event Action<TreeNode>? Activated;

    /// <summary>Fires after any expand/collapse.</summary>
    public event Action<TreeNode>? Toggled;

    /// <summary>Fires when the cursor actually MOVES via user navigation
    /// (keys or row click) — what detail panes follow.</summary>
    public event Action<TreeNode>? SelectionChanged;

    public bool ShowScrollbar { get; set; } = true;

    /// <summary>Index of the first visible row.</summary>
    public int Offset { get; private set; }

    private int _selected; // index into the flattened visible rows
    private int _anchor;   // shift-range start (last plain/ctrl click)
    private readonly HashSet<int> _selection = []; // flat indices, multi-select
    private readonly List<TreeNode> _flat = [];
    private readonly List<int> _depth = [];

    private bool _dragThumb;
    private int _grabDelta, _grabDeltaPx, _lastRenderedOffset;

    public TreeView(TreeNode root)
    {
        _root = root; // direct: the setter wouldn't satisfy _root's CS8618 analysis
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

    /// <summary>The multi-selection in visual order, with every selected
    /// folder expanded to all its descendants (selecting a directory
    /// means selecting everything under it). Plain click selects one,
    /// CTRL-click toggles membership, SHIFT-click selects the range from
    /// the anchor. Staging/deleting acts on this set; the cursor
    /// (SelectedNode) is what diffs and Enter follow.</summary>
    public IReadOnlyList<TreeNode> SelectedNodes
    {
        get
        {
            Flatten();
            var list = new List<TreeNode>();
            var seen = new HashSet<TreeNode>();
            void AddWithDescendants(TreeNode n)
            {
                if (!seen.Add(n)) return; // parent+child both selected: once
                list.Add(n);
                foreach (var c in n.Children) AddWithDescendants(c);
            }
            for (int i = 0; i < _flat.Count; i++)
                if (_selection.Contains(i)) AddWithDescendants(_flat[i]);
            return list;
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
        // expand/collapse shifts flat indices: prune what fell off the end
        // rather than pretending a mapping VSCode-style
        _selection.RemoveWhere(i => i >= _flat.Count);
        _anchor = Math.Clamp(_anchor, 0, Math.Max(0, _flat.Count - 1));
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

    /// <summary>Drop the multi-selection AND the cursor highlight — e.g.
    /// after an operation consumed the selection (staging).</summary>
    public void ClearSelection()
    {
        _selection.Clear();
        _anchor = _selected;
        Invalidate();
    }

    /// <summary>Select exactly these nodes (multi-select set) and move the
    /// cursor to the last of them. Used to carry a selection across model
    /// rebuilds (match nodes by identity or tag before calling).</summary>
    public void SelectNodes(IReadOnlyList<TreeNode> nodes)
    {
        Flatten();
        _selection.Clear();
        foreach (var n in nodes)
        {
            int i = _flat.IndexOf(n);
            if (i >= 0) _selection.Add(i);
        }
        if (nodes.Count > 0)
        {
            int c = _flat.IndexOf(nodes[^1]);
            if (c >= 0)
            {
                _selected = c;
                _anchor = c;
            }
        }
        Invalidate();
    }

    /// <summary>Clamp → dedupe → notify: the single funnel for user
    /// navigation so SelectionChanged fires exactly on real moves.</summary>
    private void MoveSelection(int i, bool resetSet = true)
    {
        Flatten();
        i = Math.Clamp(i, 0, Math.Max(0, _flat.Count - 1));
        if (i == _selected) return;
        _selected = i;
        if (resetSet)
        {
            // keyboard navigation selects as it moves (single), like the
            // click gesture — the cursor row is always part of the set so
            // something is always visibly selected
            _selection.Clear();
            _selection.Add(i);
            _anchor = i;
        }
        SelectionChanged?.Invoke(_flat[i]);
        EnsureVisible();
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
                MoveSelection(_selected + 1);
                return true;
            case Key.Up:
                MoveSelection(_selected - 1);
                return true;
            case Key.Left:
                if (node.IsParent && node.Expanded) Toggle(node);
                else
                {
                    int p = ParentIndexOf(_selected);
                    if (p >= 0) MoveSelection(p);
                }
                return true;
            case Key.Right:
                if (node.IsParent && !node.Expanded) Toggle(node);
                else if (_selected + 1 < _flat.Count && _depth[_selected + 1] > _depth[_selected])
                    MoveSelection(_selected + 1); // first child
                return true;
            case Key.Home: MoveSelection(0); return true;
            case Key.End: MoveSelection(_flat.Count - 1); return true;
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
                // body press: the selection gesture lives HERE because the
                // modifiers (shift/ctrl) are on the mouse event, which
                // OnClick never sees
                Flatten();
                int row = Offset + (ev.Y - r.Y);
                if (row >= 0 && row < _flat.Count && ev.X < r.W - TrackW)
                {
                    if (ev.Ctrl)
                    {
                        MoveSelection(row, resetSet: false);
                        if (!_selection.Remove(row)) _selection.Add(row);
                        _anchor = row;
                    }
                    else if (ev.Shift)
                    {
                        int lo = Math.Min(_anchor, row);
                        int hi = Math.Max(_anchor, row);
                        _selection.Clear();
                        for (int k = lo; k <= hi; k++) _selection.Add(k);
                        MoveSelection(row, resetSet: false);
                    }
                    else
                    {
                        _selection.Clear();
                        _selection.Add(row);
                        _anchor = row;
                        MoveSelection(row);
                    }
                    Invalidate();
                }
                return false; // let the App focus us (Clickable path)

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

    /// <summary>Click on a row's glyph column toggles expand/collapse —
    /// row SELECTION happens on press (it needs the modifier keys, which
    /// OnClick never sees).</summary>
    public override void OnClick(Point local)
    {
        Flatten();
        int i = Offset + local.Y;
        if (i < 0 || i >= _flat.Count || local.X >= Rect.W - TrackW) return;
        if (local.X == _depth[i] * 2) Toggle(_flat[i]); // exactly on the glyph
    }

    /// <summary>The node under the point is the element identity: fast
    /// clicks on two different rows never form a double.</summary>
    internal override object? ClickElementAt(Point local)
    {
        Flatten();
        int i = Offset + local.Y;
        return i >= 0 && i < _flat.Count ? _flat[i] : null;
    }

    /// <summary>Double-click opens leaves (the file-manager gesture);
    /// parents are already toggled by their glyph on single click.</summary>
    public override void OnDoubleClick(Point local)
    {
        if (SelectedNode is { IsParent: false } leaf)
            Activated?.Invoke(leaf);
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
        // rows covered by a selected DIRECTORY get marked too: what you
        // see marked is exactly what staging the selection would touch
        var marked = new HashSet<int>(_selection);
        int markDepth = -1;
        for (int i = Offset; i < Math.Min(total, Offset + r.H); i++)
        {
            if (_selection.Contains(i)) { markDepth = _depth[i]; continue; }
            if (markDepth >= 0)
            {
                if (_depth[i] > markDepth) marked.Add(i);
                else markDepth = -1;
            }
        }
        int last = Math.Min(total, Offset + r.H);
        for (int i = Offset; i < last; i++)
        {
            int y = r.Y + (i - Offset);
            var n = _flat[i];
            int d = _depth[i];
            bool cur = i == _selected;
            bool inSet = marked.Contains(i);
            // LOUD selection: the cursor row is a solid accent bar with
            // dark ink; extra multi-selected rows get a panel-bg fill and
            // an accent ▌ edge marker — no squinting at dark-on-dark
            int accent = Theme.Get(ThemeRole.Accent);
            bool curBar = cur && Focused;
            int rowBg = curBar ? accent : inSet ? panelBg : -1;
            if (rowBg != -1)
                buffer.FillRect(r.X, y, contentW, 1,
                    new Cell { Char = ' ', Fg = -1, Bg = rowBg });
            if (inSet && !curBar)
                buffer.WriteTextClipped(Clip, r.X, y, "▌", fg: accent, bg: panelBg);

            string glyph = n.IsParent ? (n.Expanded ? "▾" : "▸") : "·";
            int gx = r.X + d * 2;
            buffer.WriteTextClipped(Clip, gx, y, glyph,
                fg: curBar ? 232 : Theme.Get(n.IsParent ? ThemeRole.Accent : ThemeRole.Faint),
                bg: rowBg);
            // per-node semantic color (git status etc) beats the default
            // text role — except on the accent bar, where dark ink wins
            int fg = curBar ? 232 : Theme.Get(n.Role ?? (cur ? ThemeRole.Title : ThemeRole.Text));
            buffer.WriteTextClipped(Clip, gx + 2, y,
                Text.Ellipsize(n.Label, Math.Max(0, contentW - d * 2 - 2)),
                fg: fg,
                bg: rowBg,
                bold: cur);
        }

        if (!ShowScrollbar) return;
        VScroll.Draw(buffer, Clip, r.Right - 1, r.Y, r.H,
            Offset, r.H, total, Theme.Get(ThemeRole.Accent), Theme.Get(ThemeRole.Faint));
    }
}
