using System.Text;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Terminal;
using QxTui.Widgets;

namespace QxTui.Tests;

/// <summary>
/// The terminal must converge on the back buffer however the presenter's
/// writer interleaves with the frame loop. Each frame carries only a delta,
/// so a frame that never reaches the terminal must not lose its cells. The
/// writer is gated one write at a time, so these interleavings are exact.
/// </summary>
public class DropConvergenceTests
{
    private sealed class GatedScreen : ITerminal
    {
        private readonly SemaphoreSlim _permits = new(1000);
        private readonly char[,] _cells;
        private int _row, _col, _entered, _finished;

        public int Width { get; } = 20;
        public int Height { get; } = 3;
        public int Finished => Volatile.Read(ref _finished);

        public GatedScreen()
        {
            _cells = new char[Height, Width];
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                    _cells[y, x] = ' ';
        }

        public bool ConsumeResize() => false;
        public bool TryReadInput(out byte b) { b = 0; return false; }
        public void Enter() { }
        public void Exit() { }
        public void Dispose() { }

        /// <summary>From now on every write blocks until Release().</summary>
        public void Hold() { while (_permits.Wait(0)) { } }
        public void Release(int writes = 1) => _permits.Release(writes);
        public void ReleaseAll() => _permits.Release(1000);

        /// <summary>The writer has taken its nth frame (it is claimed).</summary>
        public void WaitForEntered(int n) =>
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref _entered) >= n,
                TimeSpan.FromSeconds(5)), $"writer never took frame {n}");

        public void WaitForFinished(int n) =>
            Assert.True(SpinWait.SpinUntil(() => Finished >= n,
                TimeSpan.FromSeconds(5)), $"writer never finished frame {n}");

        public void Write(ReadOnlySpan<byte> bytes)
        {
            var text = Encoding.UTF8.GetString(bytes);
            Interlocked.Increment(ref _entered);
            Assert.True(_permits.Wait(TimeSpan.FromSeconds(5)), "write never released");
            Apply(text);
            Interlocked.Increment(ref _finished);
        }

        private void Apply(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\x1b')
                {
                    if (_row < Height && _col < Width) _cells[_row, _col] = s[i];
                    _col++;
                    continue;
                }
                int end = i + 2;
                while (s[end] < '@' || s[end] > '~') end++;
                if (s[end] == 'H')
                {
                    var args = s[(i + 2)..end].Split(';');
                    _row = args[0].Length == 0 ? 0 : int.Parse(args[0]) - 1;
                    _col = args.Length > 1 ? int.Parse(args[1]) - 1 : 0;
                }
                i = end;
            }
        }

        public string Row(int y)
        {
            var b = new StringBuilder();
            for (int x = 0; x < Width; x++) b.Append(_cells[y, x]);
            return b.ToString().Trim(); // Label pads its text
        }
    }

    /// <summary>Three rows, one label each: a delta touching one row leaves
    /// the others' cells out of the frame.</summary>
    private sealed class Scene : IDisposable
    {
        public readonly GatedScreen Term = new();
        public readonly string[] Text = ["a1", "b1", "c1"];
        private readonly Label[] _labels = new Label[3];
        private readonly App _app;

        public Scene()
        {
            var root = new LayoutNode { Direction = FlexDirection.Column };
            for (int i = 0; i < 3; i++)
            {
                int row = i;
                _labels[i] = new Label(() => Text[row], ThemeRole.Text);
                _labels[i].Node.Height = 1;
                root.Add(_labels[i]);
            }
            _app = new App(Term, root, targetFps: 60); // async presenter
        }

        public void Set(int row, string text)
        {
            Text[row] = text;
            _labels[row].Invalidate();
            _app.RunFrame();
        }

        public void FirstFrame()
        {
            _app.RunFrame();
            Term.WaitForFinished(1);
        }

        public void AssertTerminalShowsScene()
        {
            for (int i = 0; i < 3; i++)
                Assert.Equal(Text[i], Term.Row(i));
        }

        public void Dispose() => _app.Dispose();
    }

    [Fact]
    public void LastFrameReplacesPending_ItsPredecessorsCellsStillArrive()
    {
        // the tab-switch freeze: the replaced frame's delta was gone, and
        // with nothing else to draw no later frame ever repaired it
        using var s = new Scene();
        s.FirstFrame();
        s.Term.Hold();

        s.Set(0, "a2");            // writer takes this one and stalls
        s.Term.WaitForEntered(2);
        s.Set(1, "b2");            // waits in the mailbox
        s.Set(2, "c2");            // replaces it; the scene is now idle

        s.Term.ReleaseAll();
        s.Term.WaitForFinished(3);

        s.AssertTerminalShowsScene();
    }

    [Fact]
    public void RepairDoesNotDiffAgainstAFrameOlderThanTheOneInFlight()
    {
        // a2 is in flight and row 0 reverts to a1. Measured against the
        // last FINISHED frame row 0 looks unchanged, so nothing overwrites
        // the a2 the writer is about to land
        using var s = new Scene();
        s.FirstFrame();
        s.Term.Hold();

        s.Set(0, "a2");            // in flight
        s.Term.WaitForEntered(2);
        s.Set(1, "b2");            // mailbox
        s.Set(1, "b3");            // drop
        s.Set(0, "a1");            // back to the finished frame's content

        s.Term.ReleaseAll();
        s.Term.WaitForFinished(3);

        s.AssertTerminalShowsScene();
    }

    [Fact]
    public void InFlightFramesSnapshot_SurvivesUntilItIsTheRepairBase()
    {
        // a2's snapshot must outlive its write: evicted while in flight,
        // the repair falls back to a frame that was itself dropped and
        // finds no difference to send
        using var s = new Scene();
        s.FirstFrame();
        s.Term.Hold();

        s.Set(0, "a2");            // in flight
        s.Term.WaitForEntered(2);
        s.Set(1, "b2");            // mailbox
        s.Set(2, "c2");            // replaces it: b2 never reaches the tty

        s.Term.Release();          // a2 lands; the writer takes the mailbox
        s.Term.WaitForEntered(3);
        s.Set(2, "c3");            // the next frame must carry b2 as well

        s.Term.ReleaseAll();
        s.Term.WaitForFinished(4);

        s.AssertTerminalShowsScene();
    }
}
