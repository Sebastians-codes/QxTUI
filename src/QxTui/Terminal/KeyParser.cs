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
    private enum State { Ground, Esc, Csi, Ss3, Dcs, DcsEsc }

    private readonly Queue<KeyEvent> _keys = new(4);
    private readonly Queue<MouseEvent> _mouse = new(4);
    private readonly byte[] _params = new byte[48];
    private int _plen;
    private int _param0;
    private bool _isMouse;
    private bool _paramDone;
    private long _escSince;
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
                        _paramDone = false;
                        _isMouse = false;
                        break;
                    case (byte)'O':
                        _state = State.Ss3;
                        break;
                    case (byte)'P':
                        _dcsLen = 0;
                        _state = State.Dcs; // XTVERSION et al: DCS ... ST
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
                    // first numeric param ONLY: stop at the first ';'
                    // (the old _plen<=3 heuristic read "4;800" as 48)
                    if (b >= '0' && b <= '9' && !_paramDone)
                        _param0 = _param0 * 10 + (b - '0');
                    else if (b == ';')
                        _paramDone = true;
                    break;
                }

                _state = State.Ground;
                if (b is < 0x40 or > 0x7e)
                    break; // malformed: drop

                if (_isMouse)
                    ParseMouse(b);
                else if (b == (byte)'t' && _param0 == 16)
                    ParseCellSize();   // CSI 16 ; w ; h t
                else if (b == (byte)'t' && _param0 == 4)
                    ParseWindowSize(); // CSI 4 ; h ; w t (fallback probe)
                else
                {
                    var ev = Translate(b, _param0);
                    if (ev.Key != Key.None)
                        _keys.Enqueue(ev);
                }
                break;

            case State.Dcs:
                if (b == 0x18 || b == 0x1a)
                    _state = State.Ground; // CAN/SUB: VT-standard abort
                else if (b == 0x1b)
                    _state = State.DcsEsc;
                else if (_dcsLen < _dcsBuf.Length)
                    _dcsBuf[_dcsLen++] = (char)b;
                break;

            case State.DcsEsc:
                if (b == (byte)'\\')
                {
                    FinalizeDcs();       // ST: end of device string
                    _state = State.Ground;
                }
                else if (b == 0x18 || b == 0x1a)
                    _state = State.Ground;
                else
                    _state = State.Dcs;  // lone ESC inside the string
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

    /// <summary>End of frame: release a lone ESC only once it has been
    /// pending longer than <see cref="EscSettleMs"/>. A sequence
    /// introducer split across drains completes in microseconds; two
    /// idle frames used to be enough to leak its remainder as ground
    /// keys (wheel coordinates as tab hotkeys). Time, not frame count,
    /// is the discriminator. Cost: a real Esc press lands ~50ms late.</summary>
    public const int EscSettleMs = 50;

    public void Flush()
    {
        if (_state == State.Esc)
        {
            if (Environment.TickCount64 - _escSince >= EscSettleMs)
            {
                _keys.Enqueue(KeyEvent.Of(Key.Escape));
                _state = State.Ground;
            }
            return;
        }
        if (_state != State.Ground)
            return; // mid-sequence: keep state, wait for the rest
    }

    /// <summary>True when a LONE Esc is buffered. Apps flush at end of frame
    /// so an Esc keypress doesn't wait for sequence bytes that will never
    /// come. Mid-SEQUENCE pending (Csi/Sgr mouse) must NOT be flushed:
    /// resetting there leaks the sequence's remaining bytes as ground
    /// keys — wheel coordinates typed as digits literally switched tabs
    /// ('3' in a y coordinate = ball tab).</summary>
    public bool HasPending => _state == State.Esc;

    public bool TryReadKey(out KeyEvent ev) => _keys.TryDequeue(out ev);
    public bool TryReadMouse(out MouseEvent ev) => _mouse.TryDequeue(out ev);

    private readonly char[] _dcsBuf = new char[96];
    private int _dcsLen;

    /// <summary>Terminal identity from the XTVERSION reply (DCS > | name ST),
    /// e.g. "foot 1.14.0". Null until the terminal answers.</summary>
    public string? TerminalName { get; private set; }

    private void FinalizeDcs()
    {
        if (_dcsLen > 2 && _dcsBuf[0] == '>' && _dcsBuf[1] == '|')
            TerminalName = new string(_dcsBuf, 2, _dcsLen - 2);
    }

    /// <summary>Cell size in pixels (CSI 16 t reply). Enables pixel-precision
    /// mouse math: 0 until the terminal answers the query.</summary>
    public int CellPixW { get; private set; }
    public int CellPixH { get; private set; }

    private void ParseCellSize()
    {
        int w = 0, h = 0, field = 0, acc = 0;
        for (int i = 0; i <= _plen; i++)
        {
            if (i < _plen && _params[i] >= '0' && _params[i] <= '9')
            {
                acc = acc * 10 + (_params[i] - '0');
                continue;
            }
            if (field == 1) w = acc;
            else if (field == 2) h = acc;
            field++;
            acc = 0;
        }
        if (w > 0 && h > 0)
        {
            CellPixW = w;
            CellPixH = h;
        }
    }

    /// <summary>Window size in pixels (CSI 14 t reply). Divided by the
    /// terminal's rows/cols this yields the cell size when CSI 16 t is
    /// unanswered (foot ignores 16 t but answers 14 t).</summary>
    public int WinPixW { get; private set; }
    public int WinPixH { get; private set; }

    private void ParseWindowSize()
    {
        int h = 0, w = 0, field = 0, acc = 0;
        for (int i = 0; i <= _plen; i++)
        {
            if (i < _plen && _params[i] >= '0' && _params[i] <= '9')
            {
                acc = acc * 10 + (_params[i] - '0');
                continue;
            }
            if (field == 1) h = acc;
            else if (field == 2) w = acc;
            field++;
            acc = 0;
        }
        if (w > 0 && h > 0)
        {
            WinPixW = w;
            WinPixH = h;
        }
    }

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

        _mouse.Enqueue(new MouseEvent(x - 1, y - 1, btn & 3, kind, x - 1, y - 1));
    }

    private void Ground(byte b)
    {
        switch (b)
        {
            case 0x1b:
                if (_state == State.Ground)
                    _escSince = Environment.TickCount64;
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
            case 3: // Ctrl-C: keep it distinguishable from a plain 'c' so
                    // OnKey handlers can treat it as interrupt (ETX char)
                _keys.Enqueue(KeyEvent.OfChar('\u0003'));
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
