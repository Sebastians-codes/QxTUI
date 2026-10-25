using System.Buffers;

namespace QxTui.Terminal;

/// <summary>
/// Dedicated frame writer with a single-slot, latest-wins mailbox.
///
/// Why: the tty output queue is only a few KB. When the terminal emulator
/// (whose redraw cost grows with window size) hasn't drained it, a blocking
/// write() stalls the frame loop and drags fps down to the emulator's pace —
/// exactly the size-dependent fps this exists to eliminate. With the mailbox,
/// the render loop produces frames at target rate and NEVER blocks on the
/// tty; the writer thread absorbs the backpressure. Intermediate frames are
/// dropped (latest wins) — "render the latest, never queue" (AGENT.md §2).
/// </summary>
public sealed class FramePresenter : IDisposable
{
    private readonly Action<ReadOnlySpan<byte>> _sink;
    private readonly object _gate = new();
    private readonly Thread _thread;

    private byte[]? _buffer;
    private int _length;
    private bool _hasFrame;
    private bool _stopped;

    /// <summary>Frames actually handed to the terminal (emulator-limited).</summary>
    public long PresentedCount => Volatile.Read(ref _presented);
    private long _presented;

    /// <summary>Frames dropped because a newer frame replaced them.</summary>
    public long DroppedCount => Volatile.Read(ref _dropped);
    private long _dropped;

    public FramePresenter(Action<ReadOnlySpan<byte>> sink)
    {
        _sink = sink;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "qxtui-presenter",
        };
        _thread.Start();
    }

    /// <summary>Hand a frame to the writer. Never blocks on the terminal:
    /// if the previous frame is still waiting, it is dropped (latest wins).</summary>
    public void Submit(ReadOnlySpan<byte> frame)
    {
        lock (_gate)
        {
            if (_stopped)
                return;

            if (_hasFrame)
                Interlocked.Increment(ref _dropped);

            if (_buffer is null || _buffer.Length < frame.Length)
            {
                if (_buffer is not null)
                    ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(frame.Length, 4096));
            }

            frame.CopyTo(_buffer);
            _length = frame.Length;
            _hasFrame = true;
            Monitor.PulseAll(_gate);
        }
    }

    private void Loop()
    {
        while (true)
        {
            byte[] buffer;
            int length;

            lock (_gate)
            {
                while (!_hasFrame && !_stopped)
                    Monitor.Wait(_gate);

                if (!_hasFrame)
                    return; // stopped with nothing pending

                buffer = _buffer!;
                length = _length;
                _buffer = null;
                _hasFrame = false;
            }

            // Blocking write happens HERE, off the frame loop.
            _sink(buffer.AsSpan(0, length));
            Interlocked.Increment(ref _presented);

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Stop the writer thread. Idempotent. Pending frame is flushed
    /// first so exit sequences keep their ordering.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            _stopped = true;
            Monitor.PulseAll(_gate);
        }
        _thread.Join(1000);

        lock (_gate)
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
            }
        }
    }
}
