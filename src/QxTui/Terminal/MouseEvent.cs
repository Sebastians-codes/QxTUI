namespace QxTui.Terminal;

public enum MouseKind
{
    Press,
    Release,
    Motion,
}

/// <summary>Terminal mouse event in 0-based cell coordinates.</summary>
public readonly record struct MouseEvent(int X, int Y, int Button, MouseKind Kind);
