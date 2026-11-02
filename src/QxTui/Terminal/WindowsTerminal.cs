using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace QxTui.Terminal;

/// <summary>
/// Windows console terminal (Windows 10 1809+). CONIN$/CONOUT$ are the
/// /dev/tty equivalent, so redirected stdio does not matter. Raw mode via
/// console modes, input and resize both arrive as console input records on
/// one background reader. Output is the same ANSI as everywhere else.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTerminal : ITerminal
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint CpUtf8 = 65001;
    private const uint WaitObject0 = 0;
    private const uint ReaderPollMs = 50;
    private const uint TimerResolutionMs = 1;

    private const ushort KeyEvent = 0x0001;
    private const ushort MouseEventRecord = 0x0002;
    private const ushort WindowBufferSizeEvent = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public Coord Size;
        public Coord CursorPosition;
        public ushort Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    // INPUT_RECORD is a tagged union, 20 bytes. Only the key member is read;
    // the others just need the record to stay the right size.
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(8)] public ushort RepeatCount;
        [FieldOffset(10)] public ushort VirtualKeyCode;
        [FieldOffset(12)] public ushort VirtualScanCode;
        [FieldOffset(14)] public ushort UnicodeChar;
        [FieldOffset(16)] public uint ControlKeyState;

        // MOUSE_EVENT_RECORD overlays the same 16 bytes after the tag
        [FieldOffset(4)] public short MouseX;
        [FieldOffset(6)] public short MouseY;
        [FieldOffset(8)] public uint ButtonState;
        [FieldOffset(12)] public uint MouseControlKeyState;
        [FieldOffset(16)] public uint MouseEventFlags;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(SafeFileHandle console, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(SafeFileHandle console, uint mode);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCP(uint codePage);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfo(SafeFileHandle output,
        out ConsoleScreenBufferInfo info);

    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleInput(SafeFileHandle input,
        [Out] InputRecord[] buffer, uint length, out uint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool WriteFile(SafeFileHandle file, byte* buffer, int length,
        out int written, IntPtr overlapped);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);

    private readonly SafeFileHandle _in;
    private readonly SafeFileHandle _out;
    private readonly uint _savedInputMode;
    private readonly uint _savedOutputMode;
    private readonly uint _savedInputCodePage;
    private readonly uint _savedOutputCodePage;
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte> _input = new();
    private readonly WindowsInputDecoder _decoder;
    private readonly WindowsMouseDecoder _mouse;
    private readonly StreamWriter? _inputLog;
    private int _windowLeft;
    private int _windowTop;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _readerThread;
    private int _resized;
    private bool _entered;
    private int _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public WindowsTerminal()
    {
        _in = OpenConsole("CONIN$");
        _out = OpenConsole("CONOUT$");

        if (!GetConsoleMode(_in, out _savedInputMode) || !GetConsoleMode(_out, out _savedOutputMode))
            throw new InvalidOperationException(
                $"Could not read the console modes (error {Marshal.GetLastPInvokeError()}).");
        _savedInputCodePage = GetConsoleCP();
        _savedOutputCodePage = GetConsoleOutputCP();

        QuerySize();
        _decoder = new WindowsInputDecoder(_input.Enqueue);
        _mouse = new WindowsMouseDecoder(_input.Enqueue);
        if (Environment.GetEnvironmentVariable("QXTUI_INPUT_LOG") is { Length: > 0 } logPath)
        {
            try { _inputLog = new StreamWriter(logPath, append: true) { AutoFlush = true }; }
            catch (Exception) { /* diagnostics must never stop the app */ }
        }

        _readerThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "qxtui-input",
        };
        _readerThread.Start();
    }

    private static SafeFileHandle OpenConsole(string name)
    {
        var handle = CreateFile(name, GenericRead | GenericWrite, FileShareReadWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new InvalidOperationException(
                $"No console attached: could not open {name} (error {Marshal.GetLastPInvokeError()}).");
        return handle;
    }

    public void Enter()
    {
        if (!SetConsoleMode(_out, WindowsConsoleModes.VtOutput(_savedOutputMode)))
            throw new PlatformNotSupportedException(
                "This console has no ANSI support; qxtui needs Windows 10 1809 or later.");
        if (!SetConsoleMode(_in, WindowsConsoleModes.RawInput(_savedInputMode)))
        {
            SetConsoleMode(_out, _savedOutputMode);
            throw new PlatformNotSupportedException(
                "This console has no virtual terminal input; qxtui needs Windows 10 1809 or later.");
        }
        SetConsoleCP(CpUtf8);
        SetConsoleOutputCP(CpUtf8);

        // The default ~15.6ms timer tick makes Thread.Sleep overshoot a whole
        // 60fps frame; the frame pacer relies on sleeps being close to exact
        timeBeginPeriod(TimerResolutionMs);
        _entered = true;

        Write(TerminalSequences.Enter);
    }

    public void Exit()
    {
        if (!_entered)
            return;
        _entered = false;

        Write(TerminalSequences.Exit);
        timeEndPeriod(TimerResolutionMs);
        SetConsoleCP(_savedInputCodePage);
        SetConsoleOutputCP(_savedOutputCodePage);
        SetConsoleMode(_in, _savedInputMode);
        SetConsoleMode(_out, _savedOutputMode);
    }

    public bool ConsumeResize()
    {
        if (Interlocked.Exchange(ref _resized, 0) == 1)
        {
            QuerySize();
            return true;
        }
        return false;
    }

    public bool TryReadInput(out byte b) => _input.TryDequeue(out b);

    public unsafe void Write(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* start = bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                if (!WriteFile(_out, start + offset, bytes.Length - offset, out int written, IntPtr.Zero))
                    throw new IOException(
                        $"Console write failed (error {Marshal.GetLastPInvokeError()}).");
                if (written == 0)
                    return; // the console took nothing: don't spin on it
                offset += written;
            }
        }
    }

    // The window rect, not the buffer size: the buffer includes scrollback
    private void QuerySize()
    {
        if (GetConsoleScreenBufferInfo(_out, out var info))
        {
            int width = info.Window.Right - info.Window.Left + 1;
            int height = info.Window.Bottom - info.Window.Top + 1;
            _windowLeft = info.Window.Left;
            _windowTop = info.Window.Top;
            if (width > 0 && height > 0)
            {
                Width = width;
                Height = height;
                return;
            }
        }
        Width = 80;
        Height = 24;
    }

    // Waits with a timeout before each read because a blocked
    // ReadConsoleInput can't be interrupted by closing the handle, which is
    // how PosixTerminal stops its reader. Input still wakes the wait at once.
    private void ReadLoop()
    {
        var records = new InputRecord[64];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (WaitForSingleObject(_in, ReaderPollMs) != WaitObject0)
                    continue;
                if (!ReadConsoleInput(_in, records, (uint)records.Length, out uint read))
                    break;

                for (int i = 0; i < read; i++)
                {
                    ref readonly var record = ref records[i];
                    _inputLog?.WriteLine(WindowsInputLog.Describe(record.EventType, record.KeyDown != 0,
                        record.VirtualKeyCode, record.UnicodeChar, record.MouseX, record.MouseY,
                        record.ButtonState, record.MouseControlKeyState, record.MouseEventFlags));
                    if (record.EventType == KeyEvent)
                        _decoder.OnKey(record.KeyDown != 0, record.RepeatCount,
                            record.VirtualKeyCode, (char)record.UnicodeChar);
                    else if (record.EventType == MouseEventRecord)
                        // record coordinates are buffer cells; KeyParser wants window cells
                        _mouse.OnMouse(record.MouseX - _windowLeft, record.MouseY - _windowTop,
                            record.ButtonState, record.MouseControlKeyState, record.MouseEventFlags);
                    else if (record.EventType == WindowBufferSizeEvent)
                        Volatile.Write(ref _resized, 1);
                }
            }
        }
        catch (Exception)
        {
            // console gone / handle disposed during shutdown
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return; // idempotent — App owns the terminal and may dispose it

        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _readerThread.Join(500); } catch { /* best effort */ }
        _in.Dispose();
        _out.Dispose();
        _inputLog?.Dispose();
        _cts.Dispose();
    }
}
