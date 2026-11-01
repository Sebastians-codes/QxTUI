using System.Text;

namespace QxTui.Terminal;

/// <summary>Console mode math. Pure, so it is testable off Windows.</summary>
internal static class WindowsConsoleModes
{
    internal const uint EnableProcessedInput = 0x0001;
    internal const uint EnableLineInput = 0x0002;
    internal const uint EnableEchoInput = 0x0004;
    internal const uint EnableWindowInput = 0x0008;
    internal const uint EnableMouseInput = 0x0010;
    internal const uint EnableQuickEditMode = 0x0040;
    internal const uint EnableExtendedFlags = 0x0080;
    internal const uint EnableVirtualTerminalInput = 0x0200;

    internal const uint EnableProcessedOutput = 0x0001;
    internal const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <summary>The `stty raw -echo` equivalent. Processed input off makes
    /// Ctrl+C arrive as byte 0x03, as in raw mode on POSIX. Quick-edit off
    /// (which needs extended flags) or conhost swallows the mouse.</summary>
    internal static uint RawInput(uint saved) =>
        (saved & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput | EnableQuickEditMode))
        | EnableExtendedFlags | EnableWindowInput | EnableMouseInput | EnableVirtualTerminalInput;

    internal static uint VtOutput(uint saved) =>
        saved | EnableProcessedOutput | EnableVirtualTerminalProcessing;
}

/// <summary>Turns console key records into the UTF-8 byte stream that
/// <see cref="ITerminal"/> promises and KeyParser consumes. With virtual
/// terminal input on, arrows, mouse reports and terminal replies all arrive
/// as characters in these records, one record per UTF-16 code unit, so a
/// non-BMP character spans two records.</summary>
internal sealed class WindowsInputDecoder(Action<byte> emit)
{
    private const ushort VkMenu = 0x12;
    private const char Replacement = '�';

    private char _pendingHigh;

    public void OnKey(bool keyDown, ushort repeat, ushort virtualKey, char ch)
    {
        if (ch == '\0')
            return; // bare modifier or similar: no character to deliver

        // Characters arrive on key-down, except Alt+numpad composition,
        // which delivers its character on the release of Alt
        if (!keyDown && virtualKey != VkMenu)
            return;

        int count = Math.Max((int)repeat, 1);

        if (char.IsHighSurrogate(ch))
        {
            FlushPendingHigh();
            _pendingHigh = ch;
            return;
        }

        if (char.IsLowSurrogate(ch))
        {
            if (_pendingHigh == '\0')
            {
                EmitRune(new Rune(Replacement), count);
                return;
            }
            var rune = new Rune(_pendingHigh, ch);
            _pendingHigh = '\0';
            EmitRune(rune, count);
            return;
        }

        FlushPendingHigh();
        EmitRune(new Rune(ch), count);
    }

    private void FlushPendingHigh()
    {
        if (_pendingHigh == '\0')
            return;
        _pendingHigh = '\0';
        EmitRune(new Rune(Replacement), 1);
    }

    private void EmitRune(Rune rune, int count)
    {
        Span<byte> utf8 = stackalloc byte[4];
        int length = rune.EncodeToUtf8(utf8);
        for (int i = 0; i < count; i++)
            for (int j = 0; j < length; j++)
                emit(utf8[j]);
    }
}
