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
/// Incremental ANSI key parser: feed raw tty bytes, drain complete key
/// events. Handles plain chars, Ctrl-C, CR/LF, and CSI/SS3 escape sequences
/// for arrows (terminals send \x1b[A .. \x1b[D or \x1bOA .. \x1bOD).
/// A lone ESC followed by an unrelated byte yields Escape AND re-processes
/// that byte; Flush() releases a trailing ESC at end of stream.
/// Allocation-light (one internal queue), stateful: one instance per stream.
/// </summary>
public sealed class KeyParser
{
    private enum State { Ground, Esc, Csi, Ss3 }

    private readonly Queue<KeyEvent> _out = new(4);
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
                        break;
                    case (byte)'O':
                        _state = State.Ss3;
                        break;
                    default:
                        // lone ESC: emit it, then re-process this byte fresh
                        _state = State.Ground;
                        _out.Enqueue(KeyEvent.Of(Key.Escape));
                        Ground(b);
                        break;
                }
                break;

            case State.Csi:
            case State.Ss3:
                if (b >= 0x30 && b <= 0x3f)
                    break; // parameter byte (digits, ;, ?)

                _state = State.Ground;
                if (b is >= 0x40 and <= 0x7e)
                {
                    var ev = Translate(b);
                    if (ev.Key != Key.None)
                        _out.Enqueue(ev);
                }
                break;
        }
    }

    /// <summary>End of stream: release any pending lone ESC.</summary>
    public void Flush()
    {
        if (_state == State.Esc)
            _out.Enqueue(KeyEvent.Of(Key.Escape));
        _state = State.Ground;
    }

    public bool TryRead(out KeyEvent ev) => _out.TryDequeue(out ev);

    private void Ground(byte b)
    {
        switch (b)
        {
            case 0x1b:
                _state = State.Esc;
                break;
            case 13: // CR
            case 10: // LF
                _out.Enqueue(KeyEvent.Of(Key.Enter));
                break;
            case 32:
                _out.Enqueue(KeyEvent.Of(Key.Space));
                break;
            case 9:
                _out.Enqueue(KeyEvent.Of(Key.Tab));
                break;
            case 0x7f:
            case 0x08:
                _out.Enqueue(KeyEvent.Of(Key.Backspace));
                break;
            case 3: // Ctrl-C
                _out.Enqueue(KeyEvent.OfChar('c'));
                break;
            default:
                _out.Enqueue(KeyEvent.OfChar((char)b));
                break;
        }
    }

    private static KeyEvent Translate(byte final) => final switch
    {
        (byte)'A' => KeyEvent.Of(Key.Up),
        (byte)'B' => KeyEvent.Of(Key.Down),
        (byte)'C' => KeyEvent.Of(Key.Right),
        (byte)'D' => KeyEvent.Of(Key.Left),
        (byte)'H' => KeyEvent.Of(Key.Home),
        (byte)'F' => KeyEvent.Of(Key.End),
        (byte)'~' => KeyEvent.Of(Key.Delete), // reached with params like 3~
        _ => KeyEvent.Of(Key.None),
    };
}
