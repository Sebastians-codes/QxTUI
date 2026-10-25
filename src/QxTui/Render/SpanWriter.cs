using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace QxTui.Render;

/// <summary>
/// Zero-allocation byte writer over IBufferWriter&lt;byte&gt;. Buffers writes
/// into rented memory and advances the writer in chunks. Used by the renderer
/// hot path (AGENT.md §2: one write per frame, no allocations).
/// </summary>
internal struct SpanWriter
{
    private const int ChunkSize = 8192;

    private readonly IBufferWriter<byte> _output;
    private Memory<byte> _buffer;
    private int _pos;

    public int TotalWritten;

    public SpanWriter(IBufferWriter<byte> output)
    {
        _output = output;
        _buffer = output.GetMemory(ChunkSize);
        _pos = 0;
        TotalWritten = 0;
    }

    private void Ensure(int count)
    {
        if (_pos + count <= _buffer.Length)
            return;

        if (_pos > 0)
        {
            _output.Advance(_pos);
            TotalWritten += _pos;
            _pos = 0;
        }
        _buffer = _output.GetMemory(Math.Max(count, ChunkSize));
    }

    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.Span.Slice(_pos));
        _pos += bytes.Length;
    }

    public void WriteInt(int value)
    {
        Ensure(11);
        if (!Utf8Formatter.TryFormat(value, _buffer.Span.Slice(_pos), out int written))
            throw new InvalidOperationException("int format failed");
        _pos += written;
    }

    public void WriteText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return;
        Ensure(text.Length * 4); // worst case UTF-8 expansion (surrogates → 4 bytes)
        int written = Encoding.UTF8.GetBytes(text, _buffer.Span.Slice(_pos));
        _pos += written;
    }

    /// <summary>Flush any pending bytes to the underlying writer.</summary>
    public void Commit()
    {
        if (_pos > 0)
        {
            _output.Advance(_pos);
            TotalWritten += _pos;
            _pos = 0;
        }
    }
}
