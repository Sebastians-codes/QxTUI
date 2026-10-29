namespace QxTui.Layout;

public enum FlexDirection
{
    Row,
    Column,
}

public enum AlignItems
{
    Start,
    Center,
    End,
    Stretch,
}

/// <summary>Main-axis packing of children when free space remains after
/// flex distribution (grow absorbs free space first, like CSS).</summary>
public enum JustifyContent
{
    Start,
    Center,
    End,
    SpaceBetween,
    SpaceAround,
}

/// <summary>
/// A node in the flexbox layout tree (AGENT.md §2 Layout rules).
/// Pure data + pure math: no I/O, no global state, deterministic integer
/// cell arithmetic. After each pass, <see cref="RectChanged"/> tells whether
/// this node actually moved/resized — nodes whose rect did not change keep
/// their cached cells (content invalidation ≠ layout invalidation).
/// </summary>
public sealed class LayoutNode
{
    // ---- sizing inputs -------------------------------------------------

    /// <summary>Explicit width in cells. Takes precedence over everything.</summary>
    public int? Width { get; set; }

    /// <summary>Explicit height in cells.</summary>
    public int? Height { get; set; }

    /// <summary>Width as fraction (0..1) of the available width.</summary>
    public float? WidthPercent { get; set; }

    /// <summary>Height as fraction (0..1) of the available height.</summary>
    public float? HeightPercent { get; set; }

    /// <summary>Flex grow weight along the parent's main axis (0 = fixed).</summary>
    public float Grow { get; set; }

    /// <summary>Flex shrink weight along the parent's main axis.</summary>
    public float Shrink { get; set; } = 1f;

    /// <summary>Flex basis in cells along the main axis. null = auto (content).</summary>
    public float? Basis { get; set; }

    /// <summary>Hard floors/ceilings in cells, applied AFTER flex
    /// distribution: a MaxWidth caps even a growing child, a MinWidth
    /// floors even a shrinking one ("20% but never under 30 cols").
    /// Space a clamped child gives up is redistributed once to
    /// unconstrained siblings (deterministic single correction pass).</summary>
    public int? MinWidth { get; set; }
    public int? MaxWidth { get; set; }
    public int? MinHeight { get; set; }
    public int? MaxHeight { get; set; }

    internal int ClampWidth(int w)
    {
        if (MinWidth is int mn && w < mn) w = mn;
        if (MaxWidth is int mx && w > mx) w = mx;
        return w;
    }

    internal int ClampHeight(int h)
    {
        if (MinHeight is int mn && h < mn) h = mn;
        if (MaxHeight is int mx && h > mx) h = mx;
        return h;
    }

    // ---- container inputs ----------------------------------------------

    public FlexDirection Direction { get; set; } = FlexDirection.Column;
    public AlignItems Align { get; set; } = AlignItems.Start;

    /// <summary>Main-axis packing of leftover free space (after grow and
    /// min/max clamps). Ignored while any child can still grow.</summary>
    public JustifyContent Justify { get; set; } = JustifyContent.Start;

    public int Gap { get; set; }

    /// <summary>Reserve a border ring: children are laid out inside the
    /// rect shrunk by this inset on every side (panels with frames).</summary>
    public int ChildInset { get; set; }

    private List<LayoutNode>? _children;
    public IReadOnlyList<LayoutNode> Children => _children ??= [];

    /// <summary>Detach a child node (widget RemoveChild).</summary>
    public bool Remove(LayoutNode child) => _children?.Remove(child) ?? false;

    /// <summary>Leaf content measurement: (available size) → desired size.
    /// Containers ignore this; leaves without it fill their available space.</summary>
    public Func<Size, Size>? Measure { get; set; }

    // ---- outputs ---------------------------------------------------------

    public Rect Rect { get; internal set; }
    public Rect PreviousRect { get; private set; }

    /// <summary>True when this pass moved/resized the node.</summary>
    public bool RectChanged { get; internal set; }

    /// <summary>Arbitrary payload (widget attachment point).</summary>
    public object? Tag { get; set; }

    /// <summary>Parent node, set on Add. Needed for resize propagation:
    /// a resized child must grow its fixed-size ancestors to take space.</summary>
    public LayoutNode? Parent { get; private set; }

    internal LayoutNode? NextSiblingOf(LayoutNode n) => SiblingOf(n, +1);
    internal LayoutNode? PrevSiblingOf(LayoutNode n) => SiblingOf(n, -1);

    private LayoutNode? SiblingOf(LayoutNode n, int dir)
    {
        if (_children is null) return null;
        int i = _children.IndexOf(n);
        if (i < 0) return null;
        i += dir;
        return i >= 0 && i < _children.Count ? _children[i] : null;
    }

    public LayoutNode Add(LayoutNode child)
    {
        (_children ??= []).Add(child);
        child.Parent = this;
        return this;
    }

    // ---- the layout pass ---------------------------------------------------

    /// <summary>
    /// Root entry: resolve size for the root node within the viewport and
    /// lay out the whole subtree with absolute rects. Pure function of
    /// (tree, viewport) — same input, same rects.
    /// </summary>
    public static void Compute(LayoutNode root, int viewportWidth, int viewportHeight)
    {
        var available = new Size(viewportWidth, viewportHeight);

        int w = root.ResolveWidth(available);
        int h = root.ResolveHeight(available);
        w = Math.Clamp(w, 0, viewportWidth);
        h = Math.Clamp(h, 0, viewportHeight);

        root.SetRect(new Rect(0, 0, w, h));
        root.LayoutChildren();
    }

    internal void SetRect(Rect rect)
    {
        PreviousRect = Rect;
        Rect = rect;
        RectChanged = !rect.Equals(PreviousRect);
    }

    /// <summary>Walk the tree collecting nodes whose rect changed this pass.</summary>
    public void CollectChanged(List<LayoutNode> result)
    {
        if (RectChanged)
            result.Add(this);
        if (_children is null) return;
        for (int i = 0; i < _children.Count; i++)
            _children[i].CollectChanged(result);
    }

    internal int ResolveWidth(Size available) =>
        ClampWidth(Width is int w ? w
            : WidthPercent is float pw ? Math.Max(0, (int)(pw * available.Width))
            : IntrinsicWidth(available));

    internal int ResolveHeight(Size available) =>
        ClampHeight(Height is int h ? h
            : HeightPercent is float ph ? Math.Max(0, (int)(ph * available.Height))
            : IntrinsicHeight(available));

    private int IntrinsicWidth(Size available) =>
        Measure is { } m ? Math.Min(m(available).Width, available.Width) : available.Width;

    private int IntrinsicHeight(Size available) =>
        Measure is { } m ? Math.Min(m(available).Height, available.Height) : available.Height;

    /// <summary>Internal: overlays live outside the root layout tree, so
    /// hand-placed subtrees (modal bodies) lay themselves out via this.</summary>
    internal void LayoutChildren()
    {
        if (_children is null || _children.Count == 0)
            return;

        bool row = Direction == FlexDirection.Row;
        int inset = ChildInset;
        int innerX = Rect.X + inset;
        int innerY = Rect.Y + inset;
        int innerW = Math.Max(0, Rect.W - 2 * inset);
        int innerH = Math.Max(0, Rect.H - 2 * inset);
        int availableMain = row ? innerW : innerH;
        int availableCross = row ? innerH : innerW;
        int n = _children.Count;

        Span<int> mainSizes = n <= 64 ? stackalloc int[64] : new int[n]; // cold-path alloc OK for huge n
        mainSizes = mainSizes[..n];

        // 1) hypothetical main sizes: fixed > percent > basis > intrinsic
        float growSum = 0f;
        for (int i = 0; i < n; i++)
        {
            var child = _children[i];
            var avail = new Size(row ? availableMain : availableCross, row ? availableCross : availableMain);

            int? fixedMain = row ? child.Width : child.Height;
            float? pctMain = row ? child.WidthPercent : child.HeightPercent;
            float? basis = child.Basis;

            int hyp;
            if (fixedMain is int f)
                hyp = f;
            else if (basis is float b)
                hyp = Math.Max(0, (int)b);
            else if (pctMain is float p)
                hyp = Math.Max(0, (int)(p * availableMain));
            else if (child.Measure is { } m)
                hyp = Math.Clamp(row ? m(avail).Width : m(avail).Height, 0, availableMain);
            else if (child._children is { Count: > 0 })
                hyp = SumIntrinsicMain(child, row, availableCross); // shrink-to-fit containers
            else
                hyp = 0; // leaf without measure on main axis: flex decides

            mainSizes[i] = hyp;
            growSum += child.Grow;
        }

        // 2) distribute free space / shrink overflow
        int totalGap = Gap * (n - 1);
        int used = totalGap;
        for (int i = 0; i < n; i++) used += mainSizes[i];
        int free = availableMain - used;

        if (free > 0 && growSum > 0f)
        {
            int distributed = 0;
            for (int i = 0; i < n; i++)
            {
                var child = _children[i];
                if (child.Grow <= 0f) continue;
                int share = (int)(free * (child.Grow / growSum));
                mainSizes[i] += share;
                distributed += share;
            }
            // Remainder cells go to the earliest growing children (deterministic).
            int remainder = free - distributed;
            for (int i = 0; remainder > 0; i = (i + 1) % n)
            {
                if (_children[i].Grow > 0f)
                {
                    mainSizes[i]++;
                    remainder--;
                }
            }
        }
        else if (free < 0)
        {
            float shrinkSum = 0f;
            for (int i = 0; i < n; i++)
                shrinkSum += _children[i].Shrink * mainSizes[i];

            if (shrinkSum > 0f)
            {
                int deficit = -free;
                for (int i = 0; i < n; i++)
                {
                    int reduction = (int)(deficit * (_children[i].Shrink * mainSizes[i] / shrinkSum));
                    mainSizes[i] = Math.Max(0, mainSizes[i] - reduction);
                }
            }
            // Any leftover deficit: children simply overflow the viewport and
            // get clipped by the renderer bounds checks. Acceptable for v1.
        }

        // 2b) HARD min/max clamps on the flexed size, then one correction
        //     pass: space a clamped child gave up goes to unconstrained
        //     siblings (grow for surplus, shrink for deficit), re-clamped.
        //     Deterministic integer layout beats CSS violation looping.
        for (int i = 0; i < n; i++)
            mainSizes[i] = row ? _children[i].ClampWidth(mainSizes[i])
                               : _children[i].ClampHeight(mainSizes[i]);

        used = totalGap;
        for (int i = 0; i < n; i++) used += mainSizes[i];
        free = availableMain - used;

        if (free > 0)
        {
            float growSum2 = 0f;
            for (int i = 0; i < n; i++)
                if (_children[i].Grow > 0f && !AtMax(_children[i], mainSizes[i], row))
                    growSum2 += _children[i].Grow;
            if (growSum2 > 0f)
            {
                int distributed = 0;
                for (int i = 0; i < n; i++)
                {
                    var c = _children[i];
                    if (c.Grow <= 0f || AtMax(c, mainSizes[i], row)) continue;
                    int share = (int)(free * (c.Grow / growSum2));
                    mainSizes[i] += share;
                    distributed += share;
                }
                int rem = free - distributed;
                for (int i = 0; rem > 0; i = (i + 1) % n)
                    if (_children[i].Grow > 0f && !AtMax(_children[i], mainSizes[i], row))
                    { mainSizes[i]++; rem--; }
            }
        }
        else if (free < 0)
        {
            float shrinkSum2 = 0f;
            for (int i = 0; i < n; i++)
                if (!AtMin(_children[i], mainSizes[i], row))
                    shrinkSum2 += _children[i].Shrink * mainSizes[i];
            if (shrinkSum2 > 0f)
            {
                int deficit = -free;
                for (int i = 0; i < n; i++)
                {
                    if (AtMin(_children[i], mainSizes[i], row)) continue;
                    int reduction = (int)(deficit * (_children[i].Shrink * mainSizes[i] / shrinkSum2));
                    mainSizes[i] = Math.Max(0, mainSizes[i] - reduction);
                }
            }
        }
        for (int i = 0; i < n; i++)
            mainSizes[i] = row ? _children[i].ClampWidth(mainSizes[i])
                               : _children[i].ClampHeight(mainSizes[i]);

        // 2c) justify: pack the LEFTOVER free space (what grow and the
        //     clamps did not absorb). Remainder cells go to the earliest
        //     gaps — same determinism rule as grow distribution.
        int usedJ = totalGap;
        for (int i = 0; i < n; i++) usedJ += mainSizes[i];
        int freeJ = Math.Max(0, availableMain - usedJ);
        int lead = 0;
        int gaps = Math.Max(0, n - 1);
        Span<int> gapExtra = gaps <= 64 ? stackalloc int[64] : new int[gaps];
        gapExtra = gapExtra[..gaps];
        if (freeJ > 0)
        {
            switch (Justify)
            {
                case JustifyContent.Center: lead = freeJ / 2; break;
                case JustifyContent.End: lead = freeJ; break;
                case JustifyContent.SpaceBetween when gaps > 0:
                {
                    int baseG = freeJ / gaps, remG = freeJ % gaps;
                    for (int g = 0; g < gaps; g++) gapExtra[g] = baseG + (g < remG ? 1 : 0);
                    break;
                }
                case JustifyContent.SpaceAround:
                {
                    int between = freeJ / n;
                    lead = (freeJ - between * gaps) / 2;
                    for (int g = 0; g < gaps; g++) gapExtra[g] = between;
                    break;
                }
            }
        }

        // 3) cross sizes + placement
        int offsetMain = lead;
        for (int i = 0; i < n; i++)
        {
            var child = _children[i];
            int main = Math.Min(mainSizes[i], Math.Max(0, availableMain - offsetMain));

            // cross axis: fixed > percent > stretch > intrinsic, then the
            // same hard min/max clamps as the main axis
            int? fixedCross = row ? child.Height : child.Width;
            float? pctCross = row ? child.HeightPercent : child.WidthPercent;
            int cross;
            if (fixedCross is int fc)
                cross = fc;
            else if (pctCross is float pc)
                cross = Math.Max(0, (int)(pc * availableCross));
            else if (Align == AlignItems.Stretch)
                cross = availableCross;
            else
            {
                var avail = new Size(row ? main : availableCross, row ? availableCross : main);
                cross = row ? child.IntrinsicHeight(avail) : child.IntrinsicWidth(avail);
            }
            cross = row ? child.ClampHeight(cross) : child.ClampWidth(cross);
            cross = Math.Clamp(cross, 0, availableCross);

            int crossOffset = Align switch
            {
                AlignItems.Center => (availableCross - cross) / 2,
                AlignItems.End => availableCross - cross,
                _ => 0,
            };

            // When children overflow the parent (unreclaimed shrink deficit),
            // clamp the cursor to the parent edge so rects never escape the
            // parent — widgets may trust their rect is in-viewport.
            int posMain = Math.Min(offsetMain, Math.Max(0, availableMain));

            int cx = row ? innerX + posMain : innerX + crossOffset;
            int cy = row ? innerY + crossOffset : innerY + posMain;
            int cw = row ? main : cross;
            int ch = row ? cross : main;

            child.SetRect(new Rect(cx, cy, cw, ch));
            child.LayoutChildren();

            offsetMain += main + Gap + (i < gaps ? gapExtra[i] : 0);
        }
    }

    private static bool AtMax(LayoutNode c, int size, bool row) =>
        row ? c.MaxWidth is int mxW && size >= mxW
            : c.MaxHeight is int mxH && size >= mxH;

    private static bool AtMin(LayoutNode c, int size, bool row) =>
        row ? c.MinWidth is int mnW && size <= mnW
            : c.MinHeight is int mnH && size <= mnH;

    private static int SumIntrinsicMain(LayoutNode node, bool parentRow, int availableCross)
    {
        // Intrinsic size of a nested container WITHOUT an explicit main
        // size, measured along the PARENT's main axis (that is the axis
        // the hypothetical size feeds). A row-inside-a-column asks the
        // container for its HEIGHT — summing the container's own main
        // axis (widths, in that case) inflated the hypothetical size and
        // flipped the parent into the shrink path, zeroing every grow
        // sibling after it. Aligned axes sum (+gaps); crossed axes take
        // the max. ChildInset frames are part of the intrinsic size, and
        // nested containers recurse. Leaves without Measure stay 0 —
        // flex decides them.
        int inset2 = node.ChildInset * 2;
        if (node._children is not { Count: > 0 } kids)
            return inset2;

        bool aligned = (node.Direction == FlexDirection.Row) == parentRow;
        int sum = aligned ? node.Gap * (kids.Count - 1) : 0;
        int max = 0;
        foreach (var kid in kids)
        {
            int v = IntrinsicAlong(kid, parentRow, availableCross);
            if (aligned) sum += v;
            else max = Math.Max(max, v);
        }
        return (aligned ? sum : max) + inset2;
    }

    /// <summary>One child's intrinsic extent along the given axis:
    /// fixed &gt; basis (only meaningful along the child's OWN main axis)
    /// &gt; Measure &gt; nested container &gt; 0.</summary>
    private static int IntrinsicAlong(LayoutNode kid, bool alongRow, int availableCross)
    {
        int? fixedMain = alongRow ? kid.Width : kid.Height;
        if (fixedMain is int f) return f;

        bool kidAligned = (kid.Direction == FlexDirection.Row) == alongRow;
        if (kidAligned && kid.Basis is float b) return Math.Max(0, (int)b);

        if (kid.Measure is { } m)
        {
            var s = m(new Size(availableCross, availableCross));
            return alongRow ? s.Width : s.Height;
        }
        if (kid._children is { Count: > 0 })
            return SumIntrinsicMain(kid, alongRow, availableCross);
        return 0;
    }
}
