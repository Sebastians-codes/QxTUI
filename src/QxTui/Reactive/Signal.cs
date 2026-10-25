namespace QxTui.Reactive;

/// <summary>
/// A versioned observable value. Writing an equal value does NOT bump the
/// version and does NOT notify subscribers — this is what keeps clean UI clean
/// (AGENT.md §2 Reactivity rules).
/// </summary>
public sealed class Signal<T>
{
    private T _value;
    private Action<T>? _subscribers;

    public Signal(T initial)
    {
        _value = initial;
    }

    /// <summary>Monotonically increasing version. Bumped only on real changes.</summary>
    public long Version { get; private set; }

    public T Value
    {
        get => _value;
        set => Set(value);
    }

    public void Set(T value)
    {
        if (EqualityComparer<T>.Default.Equals(_value, value))
            return;

        _value = value;
        Version++;
        Notify(value);
    }

    /// <summary>
    /// Subscribe to changes. Returns an IDisposable that unsubscribes.
    /// Hot path: notification must not allocate per write (multicast delegate
    /// combination happens only on subscribe/unsubscribe).
    /// </summary>
    public IDisposable Subscribe(Action<T> onChange)
    {
        _subscribers += onChange;
        return new Unsubscriber(this, onChange);
    }

    private void Notify(T value) => _subscribers?.Invoke(value);

    private sealed class Unsubscriber(Signal<T> signal, Action<T> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                signal._subscribers -= handler;
        }
    }
}
