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

    // ---- container inputs ----------------------------------------------

    public FlexDirection Direction { get; set; } = FlexDirection.Column;
    public AlignItems Align { get; set; } = AlignItems.Start;
    public int Gap { get; set; }

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

    public LayoutNode Add(LayoutNode child)
    {
        (_children ??= []).Add(child);
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

    internal int ResolveWidth(Size available)
    {
        if (Width is int w) return w;
        if (WidthPercent is float pw) return Math.Max(0, (int)(pw * available.Width));
        return IntrinsicWidth(available);
    }

    internal int ResolveHeight(Size available)
    {
        if (Height is int h) return h;
        if (HeightPercent is float ph) return Math.Max(0, (int)(ph * available.Height));
        return IntrinsicHeight(available);
    }

    private int IntrinsicWidth(Size available) =>
        Measure is { } m ? Math.Min(m(available).Width, available.Width) : available.Width;

    private int IntrinsicHeight(Size available) =>
        Measure is { } m ? Math.Min(m(available).Height, available.Height) : available.Height;

    private void LayoutChildren()
    {
        if (_children is null || _children.Count == 0)
            return;

        bool row = Direction == FlexDirection.Row;
        int availableMain = row ? Rect.W : Rect.H;
        int availableCross = row ? Rect.H : Rect.W;
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

        // 3) cross sizes + placement
        int offsetMain = 0;
        for (int i = 0; i < n; i++)
        {
            var child = _children[i];
            int main = Math.Min(mainSizes[i], Math.Max(0, availableMain - offsetMain));

            // cross axis: fixed > percent > stretch > intrinsic
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

            int cx = row ? Rect.X + posMain : Rect.X + crossOffset;
            int cy = row ? Rect.Y + crossOffset : Rect.Y + posMain;
            int cw = row ? main : cross;
            int ch = row ? cross : main;

            child.SetRect(new Rect(cx, cy, cw, ch));
            child.LayoutChildren();

            offsetMain += main + Gap;
        }
    }

    private static int SumIntrinsicMain(LayoutNode node, bool parentRow, int availableCross)
    {
        // Shrink-to-fit for nested containers without explicit main size:
        // lay out hypothetically in a scratch pass is overkill for v1 — sum
        // children's intrinsic mains + gaps, clamped to available.
        if (node._children is not { Count: > 0 } kids)
            return 0;

        bool row = node.Direction == FlexDirection.Row;
        int total = node.Gap * (kids.Count - 1);
        foreach (var kid in kids)
        {
            int? fixedMain = row ? kid.Width : kid.Height;
            if (fixedMain is int f) { total += f; continue; }
            if (kid.Basis is float b) { total += (int)b; continue; }
            if (kid.Measure is { } m)
            {
                var s = m(new Size(availableCross, availableCross));
                total += row ? s.Width : s.Height;
            }
        }
        return total;
    }
}
