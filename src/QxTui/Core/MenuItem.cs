
namespace QxTui.Core;

/// <summary>One context-menu row: either an intent (<c>Msg</c>: the menu
/// emits, the app decides) or app policy (<c>Act</c>: runs on commit) —
/// the same two-channel convention as the modal's ChipAction.</summary>
public sealed record MenuItem(string Label, object? Msg = null, Action? Act = null,
    bool Enabled = true);
