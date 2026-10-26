namespace QxTui.Terminal;

/// <summary>
/// Per-terminal feature gates keyed off the XTVERSION identity. Defaults
/// stay conservative: a feature whose misbehavior corrupts the screen
/// (hardware scroll) is OFF unless the terminal is on the proven list.
/// Observed evidence, not vendor claims: foot's SU/SD under load produced
/// tinted artifacts and at least one crash; the listed terminals conform
/// in the scroll_integrity_probe sense of the word.
/// </summary>
public static class TerminalCaps
{
    public static bool HardwareScrollSafe(string? name)
    {
        if (name is null) return false;
        foreach (var safe in SafeList)
            if (name.Contains(safe, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static readonly string[] SafeList =
        ["xterm", "kitty", "wezterm", "alacritty", "contour", "ghostty"];
}
