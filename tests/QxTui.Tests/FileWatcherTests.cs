using QxTui.Core;
using QxTui.Layout;

namespace QxTui.Tests;

public class FileWatcherTests
{
    private sealed record WatchHit;

    [Fact]
    public void BurstOfChanges_PostsOneCoalescedMessage()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qxtui-watch-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var term = new FakeTerminal(20, 6);
            using var app = new App(term, new LayoutNode(), targetFps: 60, asyncPresenting: false);
            int hits = 0;
            app.OnMessage<WatchHit>(_ => hits++);
            using var w = new FileWatcher(app, dir, () => new WatchHit(), quietMs: 100);

            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
            File.WriteAllText(Path.Combine(dir, "b.txt"), "y"); // same burst
            Thread.Sleep(400);                                  // quiet window passes
            app.RunFrame();                                     // drains the inbox

            Assert.Equal(1, hits); // coalesced: one message per quiet window
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
