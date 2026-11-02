using QxTui.Terminal;

namespace QxTui.Tests;

/// <summary>Headless terminal double for driving App.RunFrame in tests.</summary>
internal sealed class FakeTerminal(int width = 80, int height = 24) : ITerminal
{
    private readonly Queue<byte> _input = new();

    public int Width { get; set; } = width;
    public int Height { get; set; } = height;
    public bool ResizePending { get; set; }
    public byte[]? LastFrame { get; private set; }

    /// <summary>Every Write, in order: for asserting what the app sent the terminal.</summary>
    public List<byte[]> Writes { get; } = [];

    /// <summary>Everything written so far, as text.</summary>
    public string AllOutput => System.Text.Encoding.UTF8.GetString(Writes.SelectMany(w => w).ToArray());
    public int WriteCount { get; private set; }
    public bool Entered { get; private set; }
    public bool Exited { get; private set; }

    public void QueueInput(byte b) => _input.Enqueue(b);

    public void SimulateResize(int width, int height)
    {
        Width = width;
        Height = height;
        ResizePending = true;
    }

    public bool ConsumeResize()
    {
        bool r = ResizePending;
        ResizePending = false;
        return r;
    }

    public bool TryReadInput(out byte b) => _input.TryDequeue(out b);

    public void Write(ReadOnlySpan<byte> bytes)
    {
        LastFrame = bytes.ToArray();
        Writes.Add(LastFrame);
        WriteCount++;
    }

    public void Enter() => Entered = true;
    public void Exit() => Exited = true;
    public void Dispose() { }
}
