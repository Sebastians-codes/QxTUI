using QxTui.Core;


namespace QxTui.Widgets;

/// <summary>Tree model node: a label (static or reactive), an optional Tag
/// payload, and children. Expanded defaults true — a fresh tree shows
/// everything; collapse is a view concern the user drives.</summary>
public sealed class TreeNode
{
    private readonly Func<string> _label;
    private List<TreeNode>? _kids;

    public TreeNode(string label) : this(() => label) { }

    public TreeNode(Func<string> label) => _label = label;

    public string Label => _label();
    public object? Tag { get; set; }

    /// <summary>Optional semantic color for the label — data widgets
    /// colour their states (git: modified/staged/deleted/untracked).
    /// null = the tree's default text role.</summary>
    public ThemeRole? Role { get; set; }

    public bool Expanded { get; set; } = true;

    public IReadOnlyList<TreeNode> Children => _kids ??= [];
    public bool IsParent => _kids is { Count: > 0 };

    public TreeNode Add(TreeNode child)
    {
        (_kids ??= []).Add(child);
        return this;
    }

    public TreeNode Add(string label) => Add(new TreeNode(label));
}
