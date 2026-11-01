using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace QxTui.Terminal;

/// <summary>The parts of `stty` handling that differ per OS. Pure, so both
/// variants are testable on any machine.</summary>
internal static class PosixStty
{
    /// <summary>GNU stty names the device with -F, BSD stty (macOS) with -f.</summary>
    internal static string Arguments(bool bsd, string args) =>
        $"{(bsd ? "-f" : "-F")} /dev/tty {args}";

    /// <summary>Parses `stty size` output ("rows cols"); null if malformed.</summary>
    internal static (int Rows, int Cols)? ParseSize(string output)
    {
        var parts = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out int rows) &&
            int.TryParse(parts[1], out int cols) && rows > 0 && cols > 0)
            return (rows, cols);
        return null;
    }
}

/// <summary>
/// POSIX terminal (Linux and macOS).
/// Raw mode + alternate screen via stty/ANSI, size via TIOCGWINSZ ioctl on
/// Linux and `stty size` on macOS, resize via SIGWINCH, input via a
/// background reader into a lock-free queue.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
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
        Write(TerminalSequences.Enter);
    }

    public void Exit()
    {
        Write(TerminalSequences.Exit);
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
        if (TryQuerySize() is var (rows, cols))
        {
            Width = cols;
            Height = rows;
        }
        else
        {
            Width = 80;
            Height = 24;
        }
    }

    // macOS gets stty instead of the ioctl: the request code differs there,
    // and ioctl is variadic, so this P/Invoke would pass its pointer in the
    // wrong place on Apple Silicon. A spawn is fine at resize frequency.
    private (int Rows, int Cols)? TryQuerySize()
    {
        if (OperatingSystem.IsMacOS())
            return PosixStty.ParseSize(Stty("size"));

        WinSize ws = default;
        if (ioctl(_ttyHandle.DangerousGetHandle().ToInt32(), TIOCGWINSZ, ref ws) == 0 && ws.Col > 0 && ws.Row > 0)
            return (ws.Row, ws.Col);
        return null;
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

    private static string Stty(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("stty",
            PosixStty.Arguments(OperatingSystem.IsMacOS(), args))
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
