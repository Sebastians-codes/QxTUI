namespace QxTui.Terminal;

public enum MouseKind
{
    Press,
    Release,
    Motion,
    WheelUp,
    WheelDown,
}

/// <summary>Terminal mouse event in 0-based cell coordinates. Px/Py are the
/// raw SGR coordinates in PIXELS when the terminal honors SGR pixel mode
/// (?1016) and the app knows the cell size — sub-cell drag resolution —
/// otherwise -1. Shift/Ctrl/Alt are the SGR modifier bits (+4/+16/+8) —
/// what makes shift-click ranges and ctrl-click toggles possible.</summary>
public readonly record struct MouseEvent(int X, int Y, int Button, MouseKind Kind,
    int Px = -1, int Py = -1,
    bool Shift = false, bool Ctrl = false, bool Alt = false);
