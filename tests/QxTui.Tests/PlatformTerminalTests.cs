using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using QxTui.Terminal;
using Xunit;

namespace QxTui.Tests;

public class WindowsInputDecoderTests
{
    private const ushort VkMenu = 0x12;
    private const ushort VkA = 0x41;

    private static string Decode(params (bool Down, ushort Repeat, ushort Vk, char Ch)[] records)
    {
        var bytes = new List<byte>();
        var decoder = new WindowsInputDecoder(bytes.Add);
        foreach (var r in records)
            decoder.OnKey(r.Down, r.Repeat, r.Vk, r.Ch);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    [Fact]
    public void KeyDownCharacterPassesThrough() =>
        Assert.Equal("a", Decode((true, 1, VkA, 'a')));

    [Fact]
    public void KeyUpIsDropped() =>
        Assert.Equal("a", Decode((true, 1, VkA, 'a'), (false, 1, VkA, 'a')));

    [Fact]
    public void AltNumpadCompositionArrivesOnAltRelease() =>
        Assert.Equal("é", Decode((false, 1, VkMenu, 'é')));

    [Fact]
    public void BareModifierProducesNothing() =>
        Assert.Equal("", Decode((true, 1, 0x10, '\0')));

    [Fact]
    public void RepeatCountRepeatsTheCharacter() =>
        Assert.Equal("aaa", Decode((true, 3, VkA, 'a')));

    [Fact]
    public void ZeroRepeatCountStillDelivers() =>
        Assert.Equal("a", Decode((true, 0, VkA, 'a')));

    [Fact]
    public void NonAsciiBecomesUtf8()
    {
        var bytes = new List<byte>();
        new WindowsInputDecoder(bytes.Add).OnKey(true, 1, 0, 'ö');
        Assert.Equal(new byte[] { 0xC3, 0xB6 }, bytes);
    }

    [Fact]
    public void SurrogatePairAcrossTwoRecordsBecomesOneCharacter() =>
        Assert.Equal("😀", Decode((true, 1, 0, '\uD83D'), (true, 1, 0, '\uDE00')));

    [Fact]
    public void RepeatedSurrogatePairRepeatsTheCharacter() =>
        Assert.Equal("😀😀", Decode((true, 2, 0, '\uD83D'), (true, 2, 0, '\uDE00')));

    [Fact]
    public void LoneHighSurrogateBecomesReplacement() =>
        Assert.Equal("�a", Decode((true, 1, 0, '\uD83D'), (true, 1, VkA, 'a')));

    [Fact]
    public void LoneLowSurrogateBecomesReplacement() =>
        Assert.Equal("�", Decode((true, 1, 0, '\uDE00')));

    [Fact]
    public void AnsiArrowSequenceSurvivesForKeyParser() =>
        Assert.Equal("\x1b[A", Decode((true, 1, 0, '\x1b'), (true, 1, 0, '['), (true, 1, 0, 'A')));
}

public class WindowsConsoleModeTests
{
    [Fact]
    public void RawInputTurnsOffCookedBehaviourAndQuickEdit()
    {
        const uint cooked = 0x01 | 0x02 | 0x04 | 0x40 | 0x20 | 0x80;
        uint raw = WindowsConsoleModes.RawInput(cooked);

        Assert.Equal(0u, raw & (WindowsConsoleModes.EnableProcessedInput
                                | WindowsConsoleModes.EnableLineInput
                                | WindowsConsoleModes.EnableEchoInput
                                | WindowsConsoleModes.EnableQuickEditMode));
        Assert.NotEqual(0u, raw & WindowsConsoleModes.EnableVirtualTerminalInput);
        Assert.NotEqual(0u, raw & WindowsConsoleModes.EnableExtendedFlags);
        Assert.NotEqual(0u, raw & WindowsConsoleModes.EnableWindowInput);
    }

    [Fact]
    public void VtOutputKeepsExistingFlags()
    {
        const uint wrapAtEol = 0x02;
        uint mode = WindowsConsoleModes.VtOutput(wrapAtEol);
        Assert.Equal(wrapAtEol | WindowsConsoleModes.EnableProcessedOutput
                     | WindowsConsoleModes.EnableVirtualTerminalProcessing, mode);
    }

    // The sizes the Win32 headers define. A wrong size here would corrupt
    // every console read, and only on Windows, so pin it where tests can run.
    [Theory]
    [InlineData("InputRecord", 20)]
    [InlineData("ConsoleScreenBufferInfo", 22)]
    public void NativeStructsMatchWin32Layout(string name, int size)
    {
        var type = typeof(WindowsTerminal).GetNestedType(name, BindingFlags.NonPublic)!;
        Assert.Equal(size, Marshal.SizeOf(type));
    }
}

public class PosixSttyTests
{
    [Fact]
    public void GnuSttyUsesUpperCaseDeviceFlag() =>
        Assert.Equal("-F /dev/tty raw -echo", PosixStty.Arguments(bsd: false, "raw -echo"));

    [Fact]
    public void BsdSttyUsesLowerCaseDeviceFlag() =>
        Assert.Equal("-f /dev/tty size", PosixStty.Arguments(bsd: true, "size"));

    [Theory]
    [InlineData("24 80", 24, 80)]
    [InlineData("  50   132\n", 50, 132)]
    public void ParsesSttySize(string output, int rows, int cols) =>
        Assert.Equal((rows, cols), PosixStty.ParseSize(output));

    [Theory]
    [InlineData("")]
    [InlineData("0 0")]
    [InlineData("24")]
    [InlineData("stty: not a tty")]
    public void MalformedSttySizeIsNull(string output) =>
        Assert.Null(PosixStty.ParseSize(output));
}

public class WindowsMouseDecoderTests
{
    private const uint Left = 0x1;
    private const uint Right = 0x2;
    private const uint Moved = 0x1;
    private const uint Wheeled = 0x4;

    private static (WindowsMouseDecoder Decoder, List<byte> Bytes) Make()
    {
        var bytes = new List<byte>();
        return (new WindowsMouseDecoder(bytes.Add), bytes);
    }

    private static string Text(List<byte> bytes) => Encoding.ASCII.GetString(bytes.ToArray());

    [Fact]
    public void PressAndReleaseAreSgrReportsWithOneBasedCells()
    {
        var (d, bytes) = Make();
        d.OnMouse(4, 2, Left, 0, 0);
        d.OnMouse(4, 2, 0, 0, 0);
        Assert.Equal("\u001b[<0;5;3M\u001b[<0;5;3m", Text(bytes));
    }

    [Fact]
    public void RightButtonIsCodeTwo()
    {
        var (d, bytes) = Make();
        d.OnMouse(0, 0, Right, 0, 0);
        Assert.Equal("\u001b[<2;1;1M", Text(bytes));
    }

    [Fact]
    public void MovingWithAButtonHeldIsADrag()
    {
        var (d, bytes) = Make();
        d.OnMouse(1, 1, Left, 0, 0);
        bytes.Clear();
        d.OnMouse(2, 1, Left, 0, Moved);
        Assert.Equal("\u001b[<32;3;2M", Text(bytes));
    }

    [Fact]
    public void MovingWithNoButtonIsHover()
    {
        var (d, bytes) = Make();
        d.OnMouse(7, 3, 0, 0, Moved);
        Assert.Equal("\u001b[<35;8;4M", Text(bytes));
    }

    [Fact]
    public void WheelDeltaSignPicksDirectionAndNotchesRepeat()
    {
        var (d, bytes) = Make();
        d.OnMouse(0, 0, unchecked((uint)(120 << 16)), 0, Wheeled);
        d.OnMouse(0, 0, unchecked((uint)((ushort)-240 << 16)), 0, Wheeled);
        Assert.Equal("\u001b[<64;1;1M\u001b[<65;1;1M\u001b[<65;1;1M", Text(bytes));
    }

    [Fact]
    public void ModifiersMapToSgrBits()
    {
        var (d, bytes) = Make();
        d.OnMouse(0, 0, Left, 0x10 | 0x08, 0); // shift + left ctrl
        Assert.Equal("\u001b[<20;1;1M", Text(bytes));
    }

    [Fact]
    public void ReportsRoundTripThroughKeyParser()
    {
        var parser = new KeyParser();
        var (d, bytes) = Make();
        d.OnMouse(9, 4, Left, 0, 0);
        d.OnMouse(12, 4, Left, 0, Moved);
        d.OnMouse(12, 4, 0, 0, 0);
        foreach (var b in bytes)
            parser.Feed(b);

        Assert.True(parser.TryReadMouse(out var press));
        Assert.Equal((MouseKind.Press, 9, 4, 0), (press.Kind, press.X, press.Y, press.Button));
        Assert.True(parser.TryReadMouse(out var drag));
        Assert.Equal((MouseKind.Motion, 12), (drag.Kind, drag.X));
        Assert.True(parser.TryReadMouse(out var release));
        Assert.Equal(MouseKind.Release, release.Kind);
    }
}
