namespace QxTui.Terminal;

public enum Key
{
    None,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    Delete,
    PageUp,
    PageDown,
    Backspace,
    Tab,
    Enter,
    Space,
    Escape,
    Char, // printable: see KeyEvent.Char
}

public readonly record struct KeyEvent(Key Key, char Char)
{
    public static KeyEvent Of(Key key) => new(key, '\0');
    public static KeyEvent OfChar(char c) => new(Key.Char, c);
}

/// <summary>
/// Incremental ANSI input parser: feed raw tty bytes, drain key events and
/// mouse events from separate queues. Handles plain chars, Ctrl-C, CR/LF,
/// CSI/SS3 escape sequences for arrows, and SGR mouse reports
/// (\x1b[&lt;btn;x;y M/m) when the terminal is in mouse mode.
/// A lone ESC followed by an unrelated byte yields Escape AND re-processes
/// that byte; Flush() releases a trailing ESC at end of stream.
/// </summary>
public sealed class KeyParser
{
    private enum State { Ground, Esc, Csi, Ss3 }

    private readonly Queue<KeyEvent> _keys = new(4);
    private readonly Queue<MouseEvent> _mouse = new(4);
    private readonly byte[] _params = new byte[48];
    private int _plen;
    private int _param0;
    private bool _isMouse;
    private State _state = State.Ground;

    public void Feed(byte b)
    {
        switch (_state)
        {
            case State.Ground:
                Ground(b);
                break;

            case State.Esc:
                switch (b)
                {
                    case (byte)'[':
                        _state = State.Csi;
                        _plen = 0;
                        _param0 = 0;
                        _isMouse = false;
                        break;
                    case (byte)'O':
                        _state = State.Ss3;
                        break;
                    default:
                        _state = State.Ground;
                        _keys.Enqueue(KeyEvent.Of(Key.Escape));
                        Ground(b);
                        break;
                }
                break;

            case State.Csi:
                if (b == (byte)'<' && _plen == 0)
                {
                    _isMouse = true;
                    break;
                }
                if (b >= 0x30 && b <= 0x3f)
                {
                    if (_plen < _params.Length)
                        _params[_plen++] = b;
                    if (b >= '0' && b <= '9' && _plen <= 3)
                        _param0 = _param0 * 10 + (b - '0'); // first numeric param
                    break;
                }

                _state = State.Ground;
                if (b is < 0x40 or > 0x7e)
                    break; // malformed: drop

                if (_isMouse)
                    ParseMouse(b);
                else
                {
                    var ev = Translate(b, _param0);
                    if (ev.Key != Key.None)
                        _keys.Enqueue(ev);
                }
                break;

            case State.Ss3:
                _state = State.Ground;
                if (b is >= 0x40 and <= 0x7e)
                {
                    var ev = Translate(b, 0);
                    if (ev.Key != Key.None)
                        _keys.Enqueue(ev);
                }
                break;
        }
    }

    /// <summary>End of stream: release any pending lone ESC.</summary>
    public void Flush()
    {
        if (_state == State.Esc)
            _keys.Enqueue(KeyEvent.Of(Key.Escape));
        _state = State.Ground;
    }

    /// <summary>True when a partial escape sequence is buffered. Apps flush
    /// at end of frame so a LONE Esc keypress doesn't wait forever for
    /// sequence bytes that will never come (arrows arrive in one batch).</summary>
    public bool HasPending => _state != State.Ground;

    public bool TryReadKey(out KeyEvent ev) => _keys.TryDequeue(out ev);
    public bool TryReadMouse(out MouseEvent ev) => _mouse.TryDequeue(out ev);

    private void ParseMouse(byte final)
    {
        // params: btn;x;y  (SGR, 1-based coords)
        int btn = 0, x = 0, y = 0;
        int field = 0, acc = 0;
        for (int i = 0; i <= _plen; i++)
        {
            if (i < _plen && _params[i] >= '0' && _params[i] <= '9')
            {
                acc = acc * 10 + (_params[i] - '0');
                continue;
            }
            switch (field)
            {
                case 0: btn = acc; break;
                case 1: x = acc; break;
                case 2: y = acc; break;
            }
            field++;
            acc = 0;
        }

        MouseKind kind;
        if (final == (byte)'m')
            kind = MouseKind.Release;
        else if ((btn & 64) != 0)
            kind = (btn & 1) == 0 ? MouseKind.WheelUp : MouseKind.WheelDown;
        else if ((btn & 32) != 0)
            kind = MouseKind.Motion;
        else
            kind = MouseKind.Press;

        _mouse.Enqueue(new MouseEvent(x - 1, y - 1, btn & 3, kind));
    }

    private void Ground(byte b)
    {
        switch (b)
        {
            case 0x1b:
                _state = State.Esc;
                break;
            case 13: // CR
            case 10: // LF
                _keys.Enqueue(KeyEvent.Of(Key.Enter));
                break;
            case 32:
                _keys.Enqueue(KeyEvent.Of(Key.Space));
                break;
            case 9:
                _keys.Enqueue(KeyEvent.Of(Key.Tab));
                break;
            case 0x7f:
            case 0x08:
                _keys.Enqueue(KeyEvent.Of(Key.Backspace));
                break;
            case 3: // Ctrl-C
                _keys.Enqueue(KeyEvent.OfChar('c'));
                break;
            default:
                _keys.Enqueue(KeyEvent.OfChar((char)b));
                break;
        }
    }

    private static KeyEvent Translate(byte final, int param0) => final switch
    {
        (byte)'A' => KeyEvent.Of(Key.Up),
        (byte)'B' => KeyEvent.Of(Key.Down),
        (byte)'C' => KeyEvent.Of(Key.Right),
        (byte)'D' => KeyEvent.Of(Key.Left),
        (byte)'H' => KeyEvent.Of(Key.Home),
        (byte)'F' => KeyEvent.Of(Key.End),
        (byte)'~' => param0 switch
        {
            1 or 7 => KeyEvent.Of(Key.Home),
            3 => KeyEvent.Of(Key.Delete),
            4 or 8 => KeyEvent.Of(Key.End),
            5 => KeyEvent.Of(Key.PageUp),
            6 => KeyEvent.Of(Key.PageDown),
            _ => KeyEvent.Of(Key.None),
        },
        _ => KeyEvent.Of(Key.None),
    };
}
