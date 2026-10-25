using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace QxTui.Terminal;

/// <summary>Terminal abstraction so the app loop is testable headless.</summary>
public interface ITerminal : IDisposable
{
    int Width { get; }
    int Height { get; }
    /// <summary>True once since the last call when a resize occurred.</summary>
    bool ConsumeResize();
    /// <summary>Drain pending input; returns plain bytes ('q', 'r', ...). PoC scope.</summary>
    bool TryReadInput(out byte b);
    void Write(ReadOnlySpan<byte> bytes);
    void Enter();
    void Exit();
}

/// <summary>
/// POSIX terminal (Linux first — AGENT.md §5 PoC scope).
/// Raw mode + alternate screen via stty/ANSI, size via TIOCGWINSZ ioctl,
/// resize via SIGWINCH, input via a background reader into a lock-free queue.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PosixTerminal : ITerminal
{
    private const int TIOCGWINSZ = 0x5413; // Linux (x64/arm64)

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Row, Col, XPixel, YPixel;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref WinSize data);

    private readonly FileStream _ttyIn;
    private readonly FileStream _ttyOut;
    private readonly SafeHandle _ttyHandle;
    private readonly string _savedSttyState;
    private readonly ConcurrentQueue<byte> _input = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _readerThread;
    private readonly PosixSignalRegistration _sigwinch;
    private int _resized;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public PosixTerminal()
    {
        _ttyIn = new FileStream("/dev/tty", FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, FileOptions.Asynchronous);
        _ttyOut = new FileStream("/dev/tty", FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
            bufferSize: 65536);
        _ttyHandle = _ttyIn.SafeFileHandle;

        _savedSttyState = Stty("-g");
        QuerySize();

        _sigwinch = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ =>
        {
            Volatile.Write(ref _resized, 1);
        });

        _readerThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "qxtui-input",
        };
        _readerThread.Start();
    }

    public void Enter()
    {
        Stty("raw -echo");
        WriteRaw("\x1b[?1049h"); // alternate screen
        WriteRaw("\x1b[?25l");    // hide cursor
        WriteRaw("\x1b[?1003h"); // mouse press/release + any-motion (hover)
        WriteRaw("\x1b[?1006h"); // SGR encoding (unlimited coordinates)
        // pixel mode (?1016) is enabled LATER, only once the CSI 16 t reply
        // proves the terminal can answer it — pixel coords misread as cells
        // would break all input otherwise
        WriteRaw("\x1b[16t");    // cell size in px
        WriteRaw("\x1b[2J");      // clear
    }

    public void Exit()
    {
        WriteRaw("\x1b[?1003l"); // mouse off
        WriteRaw("\x1b[?1016l");
        WriteRaw("\x1b[?1006l");
        WriteRaw("\x1b[?25h");    // show cursor
        WriteRaw("\x1b[?1049l");  // leave alternate screen
        Stty(_savedSttyState);
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

    public void Write(ReadOnlySpan<byte> bytes)
    {
        _ttyOut.Write(bytes);
        _ttyOut.Flush();
    }

    private void QuerySize()
    {
        WinSize ws = default;
        if (ioctl(_ttyHandle.DangerousGetHandle().ToInt32(), TIOCGWINSZ, ref ws) == 0 && ws.Col > 0 && ws.Row > 0)
        {
            Width = ws.Col;
            Height = ws.Row;
        }
        else
        {
            Width = 80;
            Height = 24;
        }
    }

    // Blocking reads on a dedicated thread: async tty reads can miss epoll
    // wakeups and silently drop keys under load; a kernel blocking read
    // wakes on every byte. Closing the stream unblocks it on dispose.
    private void ReadLoop()
    {
        byte[] buf = new byte[256];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int read = _ttyIn.Read(buf, 0, buf.Length);
                if (read <= 0)
                    break;
                for (int i = 0; i < read; i++)
                    _input.Enqueue(buf[i]);
            }
        }
        catch (Exception)
        {
            // tty closed / stream disposed during shutdown
        }
    }

    private void WriteRaw(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        _ttyOut.Write(bytes, 0, bytes.Length);
        _ttyOut.Flush();
    }

    private static string Stty(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("stty", $"-F /dev/tty {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("stty not available");
        string output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return output;
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return; // idempotent — App owns the terminal and may dispose it

        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _ttyIn.Dispose(); } catch { /* unblocks the reader */ }
        try { _readerThread.Join(500); } catch { /* best effort */ }
        _sigwinch.Dispose();
        _ttyOut.Dispose();
        _cts.Dispose();
    }
}
