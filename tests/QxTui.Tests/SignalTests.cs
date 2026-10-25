using QxTui.Reactive;

namespace QxTui.Tests;

public class SignalTests
{
    [Fact]
    public void EqualWrite_DoesNotBumpVersionOrNotify()
    {
        var signal = new Signal<float>(0.5f);
        int notifications = 0;
        using var sub = signal.Subscribe(_ => notifications++);

        signal.Set(0.5f);

        Assert.Equal(0, signal.Version);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void RealWrite_BumpsVersionAndNotifies()
    {
        var signal = new Signal<int>(1);
        var seen = new List<int>();
        using var sub = signal.Subscribe(seen.Add);

        signal.Set(2);
        signal.Set(3);

        Assert.Equal(2, signal.Version);
        Assert.Equal([2, 3], seen);
        Assert.Equal(3, signal.Value);
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        var signal = new Signal<int>(0);
        int notifications = 0;
        var sub = signal.Subscribe(_ => notifications++);

        signal.Set(1);
        sub.Dispose();
        signal.Set(2);

        Assert.Equal(1, notifications);
        Assert.Equal(2, signal.Version);
    }

    [Fact]
    public void DoubleDispose_IsSafe()
    {
        var signal = new Signal<int>(0);
        var sub = signal.Subscribe(_ => { });
        sub.Dispose();
        sub.Dispose(); // must not throw or corrupt
        signal.Set(1);
        Assert.Equal(1, signal.Version);
    }
}
