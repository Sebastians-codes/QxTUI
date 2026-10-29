
namespace QxTui.Widgets;

/// <summary>How a cell's text sits inside its column.</summary>
public enum ColumnAlign { Left, Right, Center }

/// <summary>Column spec: a fixed <paramref name="Width"/>, or a
/// <paramref name="Weight"/>-proportional share of the leftover space.</summary>
public sealed record TableColumn(string Header, int? Width = null, float Weight = 1f,
    ColumnAlign Align = ColumnAlign.Left);
