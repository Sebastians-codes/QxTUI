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
