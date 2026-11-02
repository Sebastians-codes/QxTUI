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

/// <summary>Turns console mouse records into the SGR mouse reports KeyParser
/// reads (<c>ESC [ &lt; b ; x ; y M|m</c>). Modern terminals deliver mouse
/// input as those sequences inside key records already; a console that
/// doesn't (classic conhost, or one that never saw the mouse-mode request)
/// hands over MOUSE_EVENT records instead, which would otherwise be dropped.
/// Pure and stateful only in the held-button mask, so it is testable off
/// Windows.</summary>
internal sealed class WindowsMouseDecoder(Action<byte> emit)
{
    private const uint LeftButton = 0x0001;   // FROM_LEFT_1ST_BUTTON_PRESSED
    private const uint RightButton = 0x0002;  // RIGHTMOST_BUTTON_PRESSED
    private const uint MiddleButton = 0x0004; // FROM_LEFT_2ND_BUTTON_PRESSED
    private const uint ButtonMask = LeftButton | RightButton | MiddleButton;

    private const uint Moved = 0x0001;        // MOUSE_MOVED
    private const uint Wheeled = 0x0004;      // MOUSE_WHEELED
    private const uint HorizontalWheeled = 0x0008;

    private const uint RightAlt = 0x0001;
    private const uint LeftAlt = 0x0002;
    private const uint RightCtrl = 0x0004;
    private const uint LeftCtrl = 0x0008;
    private const uint ShiftPressed = 0x0010;

    private const int WheelNotch = 120;

    private uint _held;

    /// <param name="column">0-based cell column relative to the window.</param>
    /// <param name="row">0-based cell row relative to the window.</param>
    /// <param name="buttonState">dwButtonState; the high word is the wheel delta.</param>
    /// <param name="controlState">dwControlKeyState (modifiers).</param>
    /// <param name="eventFlags">dwEventFlags (moved, wheeled, ...).</param>
    public void OnMouse(int column, int row, uint buttonState, uint controlState, uint eventFlags)
    {
        int modifiers = ((controlState & ShiftPressed) != 0 ? 4 : 0)
                      | ((controlState & (LeftAlt | RightAlt)) != 0 ? 8 : 0)
                      | ((controlState & (LeftCtrl | RightCtrl)) != 0 ? 16 : 0);
        int x = Math.Max(0, column) + 1;
        int y = Math.Max(0, row) + 1;

        if ((eventFlags & Wheeled) != 0)
        {
            int delta = (short)(buttonState >> 16);
            int notches = Math.Max(1, Math.Abs(delta) / WheelNotch);
            for (int i = 0; i < notches; i++)
                Report((delta > 0 ? 64 : 65) | modifiers, x, y, 'M');
            return;
        }
        if ((eventFlags & HorizontalWheeled) != 0)
            return; // KeyParser has no horizontal wheel

        uint now = buttonState & ButtonMask;
        uint changed = now ^ _held;
        _held = now;

        ReportChange(changed, now, LeftButton, 0, modifiers, x, y);
        ReportChange(changed, now, MiddleButton, 1, modifiers, x, y);
        ReportChange(changed, now, RightButton, 2, modifiers, x, y);

        if ((eventFlags & Moved) != 0 && changed == 0)
        {
            // drag reports the held button; plain hover is "no button" (3)
            int button = (now & LeftButton) != 0 ? 0
                       : (now & MiddleButton) != 0 ? 1
                       : (now & RightButton) != 0 ? 2
                       : 3;
            Report(32 | button | modifiers, x, y, 'M');
        }
    }

    private void ReportChange(uint changed, uint now, uint bit, int code, int modifiers, int x, int y)
    {
        if ((changed & bit) != 0)
            Report(code | modifiers, x, y, (now & bit) != 0 ? 'M' : 'm');
    }

    private void Report(int code, int x, int y, char final)
    {
        foreach (char c in $"\u001b[<{code};{x};{y}{final}")
            emit((byte)c);
    }
}
