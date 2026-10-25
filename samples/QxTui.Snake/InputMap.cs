using QxTui.Terminal;

namespace QxTui.Snake;

/// <summary>
/// Key → turn mapping. Three schemes, case-insensitive:
/// arrows, WASD, and vim-style hjkl (h← j↓ k↑ l→).
/// Extracted from Program so the mapping is unit-testable.
/// </summary>
internal static class InputMap
{
    public static Direction? ToDirection(KeyEvent ev)
    {
        switch (ev.Key)
        {
            case Key.Up: return Direction.Up;
            case Key.Down: return Direction.Down;
            case Key.Left: return Direction.Left;
            case Key.Right: return Direction.Right;
            case Key.Char:
                return ev.Char switch
                {
                    'w' or 'W' => Direction.Up,
                    's' or 'S' => Direction.Down,
                    'a' or 'A' => Direction.Left,
                    'd' or 'D' => Direction.Right,
                    'k' or 'K' => Direction.Up,
                    'j' or 'J' => Direction.Down,
                    'h' or 'H' => Direction.Left,
                    'l' or 'L' => Direction.Right,
                    _ => null,
                };
            default:
                return null;
        }
    }
}
