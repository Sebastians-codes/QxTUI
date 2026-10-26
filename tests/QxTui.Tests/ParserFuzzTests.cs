using QxTui.Terminal;

namespace QxTui.Tests;

/// <summary>
/// The parser must be chunk-invariant: a stream split across input drains
/// (routine at high fps) parses identically to one delivered whole — and
/// mouse/CSI bytes must NEVER leak into the ground state as keypresses
/// (the bug that typed wheel coordinates as tab hotkeys).
///
/// Exception, pinned rather than hidden: a chunk that ends on a LONE 0x1b
/// is indistinguishable from an Esc keypress without waiting forever, so
/// splits that isolate a sequence-leading ESC are excluded from the
/// invariance property (the app's lone-Esc flush handles them).
/// </summary>
public class ParserFuzzTests
{
    private static (List<KeyEvent> Keys, List<MouseEvent> Mouse) Drain(KeyParser p)
    {
        var keys = new List<KeyEvent>();
        var mouse = new List<MouseEvent>();
        while (p.TryReadKey(out var k)) keys.Add(k);
        while (p.TryReadMouse(out var m)) mouse.Add(m);
        return (keys, mouse);
    }

    private static (List<KeyEvent> Keys, List<MouseEvent> Mouse) RunChunked(
        byte[] data, int[] cuts)
    {
        var p = new KeyParser();
        var keys = new List<KeyEvent>();
        var mouse = new List<MouseEvent>();
        int start = 0;
        foreach (var cut in cuts.Append(data.Length))
        {
            for (int i = start; i < cut; i++) p.Feed(data[i]);
            if (p.HasPending) p.Flush();   // exactly what App does per frame
            var (k, m) = Drain(p);
            keys.AddRange(k);
            mouse.AddRange(m);
            start = cut;
        }
        p.Flush();
        var (fk, fm) = Drain(p);
        keys.AddRange(fk);
        mouse.AddRange(fm);
        return (keys, mouse);
    }

    public static IEnumerable<object[]> Sequences()
    {
        yield return new object[] { "\x1b[A" };
        yield return new object[] { "\x1b[6~" };
        yield return new object[] { "\x1b[<0;15;7M" };
        yield return new object[] { "\x1b[<0;15;7m" };
        yield return new object[] { "\x1b[<32;115;33M" };
        yield return new object[] { "\x1b[<65;60;32M" };
        yield return new object[] { "\x1b[16;10;20t" };
        yield return new object[] { "\x1b[4;800;1600t" };
        yield return new object[] { "abc \t\r\x7f" };
        yield return new object[] { "\x1b" };
        yield return new object[] { "\x1b\x1b[<65;1;1M" };
        yield return new object[] { "\x1b[<65;60;32M\x1b[<65;60;33Mq\x1b[B" };
    }

    [Theory]
    [MemberData(nameof(Sequences))]
    public void EverySplitPoint_IsChunkInvariant_OrIsolatedEsc(string seq)
    {
        var data = System.Text.Encoding.ASCII.GetBytes(seq);
        var golden = RunChunked(data, []);

        for (int cut = 1; cut < data.Length; cut++)
        {
            var got = RunChunked(data, [cut]);
            Assert.Equal(golden.Keys, got.Keys);
            Assert.Equal(golden.Mouse, got.Mouse);
        }
    }

    [Theory]
    [MemberData(nameof(Sequences))]
    public void MouseBytes_NeverBecomeKeys_AtAnySplit(string seq)
    {
        var data = System.Text.Encoding.ASCII.GetBytes(seq);
        // pure mouse streams only: combined streams may legitimately
        // contain ground keys ('q', arrows) which are not leaks
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                seq, @"^(\x1b\[<[0-9;]+[Mm])+$"))
            return;

        for (int cut = 1; cut < data.Length; cut++)
        {
            var (keys, _) = RunChunked(data, [cut]);
            Assert.Empty(keys); // digits/';'/'M' must never type themselves
        }
    }

    [Fact]
    public void RandomSoup_NeverThrows_AndRecovers()
    {
        var rng = new Random(0xC0FFEE);
        var buf = new byte[64];
        for (int iter = 0; iter < 300; iter++)
        {
            rng.NextBytes(buf);
            var p = new KeyParser();
            int pos = 0;
            while (pos < buf.Length)
            {
                int n = rng.Next(1, 8);
                for (int i = 0; i < n && pos < buf.Length; i++) p.Feed(buf[pos++]);
                if (p.HasPending) p.Flush();
                Drain(p);
            }
            p.Flush();
            Drain(p);

            // parser must always return to a usable state
            foreach (byte b in System.Text.Encoding.ASCII.GetBytes("\x1b[B"))
                p.Feed(b);
            var (keys, _) = Drain(p);
            Assert.Contains(keys, k => k.Key == Key.Down);
        }
    }

    [Fact]
    public void RandomThreeWaySplits_MatchGolden_OnCombinedStream()
    {
        var rng = new Random(0xBADC0DE);
        var stream = System.Text.Encoding.ASCII.GetBytes(
            "\x1b[<65;60;32Mhello\x1b[B\x1b[<0;9;9M\x1b[6~\x1b[16;8;16t");
        var golden = RunChunked(stream, []);

        for (int iter = 0; iter < 200; iter++)
        {
            int c1 = rng.Next(1, stream.Length);
            int c2 = rng.Next(c1, stream.Length);
            var got = RunChunked(stream, [c1, c2]);
            Assert.Equal(golden.Keys, got.Keys);
            Assert.Equal(golden.Mouse, got.Mouse);
        }
    }
}
