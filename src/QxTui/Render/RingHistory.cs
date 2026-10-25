namespace QxTui.Render;

/// <summary>
/// Fixed-capacity float ring buffer for time-series (sparklines, graphs).
/// Was sample-local in qxtop until it earned a place in the framework.
/// </summary>
public sealed class RingHistory(int capacity)
{
    private readonly float[] _data = new float[capacity];
    private int _head;

    public int Count { get; private set; }
    public int Capacity => capacity;

    public void Push(float value)
    {
        _data[_head] = value;
        _head = (_head + 1) % capacity;
        if (Count < capacity) Count++;
    }

    /// <summary>i-th oldest value (0 = oldest).</summary>
    public float this[int i] => _data[(_head - Count + i + capacity * 2) % capacity];

    /// <summary>Linear-interpolated read at a fractional index — lets
    /// sparklines scroll sub-cell smoothly between samples.</summary>
    public float AtInterp(float idx)
    {
        if (Count == 0) return 0f;
        if (idx <= 0f) return this[0];
        if (idx >= Count - 1) return this[Count - 1];
        int i = (int)idx;
        float t = idx - i;
        return this[i] * (1f - t) + this[i + 1] * t;
    }

    public float Max(float floor)
    {
        float m = floor;
        for (int i = 0; i < Count; i++)
            m = Math.Max(m, this[i]);
        return m;
    }

    public void Clear() => Count = 0;
}
