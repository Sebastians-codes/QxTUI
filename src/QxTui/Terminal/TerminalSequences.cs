namespace QxTui.Terminal;

/// <summary>The escape sequences that open and close a TUI session. Every
/// platform speaks the same ANSI here; only raw mode differs.</summary>
internal static class TerminalSequences
{
    // pixel mode (?1016) is enabled LATER, only once the CSI 16 t reply
    // proves the terminal can answer it — pixel coords misread as cells
    // would break all input otherwise
    public static ReadOnlySpan<byte> Enter =>
        "\x1b[?1049h"u8 + // alternate screen
        "\x1b[?25l"u8 +   // hide cursor
        "\x1b[?1003h"u8 + // mouse press/release + any-motion (hover)
        "\x1b[?1006h"u8 + // SGR encoding (unlimited coordinates)
        "\x1b[16t"u8 +    // cell size in px
        "\x1b[14t"u8 +    // window size in px (fallback cell probe)
        "\x1b[>q"u8 +     // XTVERSION: who are we talking to?
        "\x1b[2J"u8;      // clear

    public static ReadOnlySpan<byte> Exit =>
        "\x1b[?1003l"u8 + // mouse off
        "\x1b[?1016l"u8 +
        "\x1b[?1006l"u8 +
        "\x1b[?25h"u8 +   // show cursor
        "\x1b[?1049l"u8;  // leave alternate screen
}
