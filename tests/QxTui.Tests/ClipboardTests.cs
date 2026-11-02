using System.Text;
using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;
using Xunit;
using Clip = QxTui.Core.Clipboard;

namespace QxTui.Tests;

public class ClipboardEncodingTests
{
    [Fact]
    public void Osc52_IsSetClipboardWithBase64Payload() =>
        Assert.Equal("\u001b]52;c;aGk=\u0007", Encoding.ASCII.GetString(Clip.Osc52("hi")));

    [Fact]
    public void Osc52_CarriesNonAsciiAsUtf8()
    {
        const string text = "grön ✓ 日本\nsecond line";
        var sequence = Encoding.ASCII.GetString(Clip.Osc52(text));

        Assert.StartsWith("\u001b]52;c;", sequence);
        Assert.EndsWith("\u0007", sequence);
        var payload = sequence["\u001b]52;c;".Length..^1];
        Assert.Equal(text, Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
    }

    private static (string Command, string[] Arguments)? Choose(
        Clip.Platform platform, string[] env, string[] onPath) =>
        Clip.ChooseTool(platform, name => env.Contains(name) ? "set" : null, onPath.Contains);

    [Fact]
    public void Wayland_PrefersWlCopy() =>
        Assert.Equal("wl-copy",
            Choose(Clip.Platform.Linux, ["WAYLAND_DISPLAY", "DISPLAY"], ["wl-copy", "xclip"])!.Value.Command);

    [Fact]
    public void Wayland_WithoutWlCopy_FallsBackToXclip() =>
        Assert.Equal("xclip",
            Choose(Clip.Platform.Linux, ["WAYLAND_DISPLAY", "DISPLAY"], ["xclip"])!.Value.Command);

    [Fact]
    public void X11_UsesXclipThenXsel()
    {
        Assert.Equal(["-selection", "clipboard"],
            Choose(Clip.Platform.Linux, ["DISPLAY"], ["xclip", "xsel"])!.Value.Arguments);
        Assert.Equal("xsel", Choose(Clip.Platform.Linux, ["DISPLAY"], ["xsel"])!.Value.Command);
    }

    [Fact]
    public void LinuxWithoutDisplayOrTool_HasNoTool()
    {
        Assert.Null(Choose(Clip.Platform.Linux, [], ["wl-copy", "xclip"]));
        Assert.Null(Choose(Clip.Platform.Linux, ["DISPLAY"], []));
    }

    [Fact]
    public void MacOs_UsesPbcopyWhenPresent()
    {
        Assert.Equal("pbcopy", Choose(Clip.Platform.MacOS, [], ["pbcopy"])!.Value.Command);
        Assert.Null(Choose(Clip.Platform.MacOS, [], []));
    }

    [Fact]
    public void Windows_GoesThroughPowerShellAsUtf8()
    {
        var tool = Choose(Clip.Platform.Windows, [], [])!.Value;
        Assert.Equal("powershell", tool.Command);
        Assert.Contains(tool.Arguments, a => a.Contains("Set-Clipboard") && a.Contains("UTF8"));
    }
}

public class CopyRoutingTests
{
    private sealed class Probe : Widget
    {
        public string? Selection;
        public int SelectAllCalls;
        public bool HandlesCtrlC;

        public Probe() => Focusable = true;

        public override string? SelectedText => Selection;

        public override bool SelectAll()
        {
            SelectAllCalls++;
            Selection = "everything";
            return true;
        }

        public override bool HandleKey(KeyEvent ev) => HandlesCtrlC && ev.Char == '\u0003';
        public override void Render(CellBuffer buffer) { }
        public MenuItem[] Menu() => CopyMenu();
    }

    private sealed class Rig
    {
        public readonly Probe Probe = new();
        public readonly App App;
        public readonly FakeTerminal Term = new(40, 10);
        public readonly List<string> Copied = [];
        public readonly List<KeyEvent> Unhandled = [];

        public Rig()
        {
            Probe.Node.Grow = 1f;
            var root = new LayoutNode { Direction = FlexDirection.Column };
            root.Add(Probe);
            App = new App(Term, root, targetFps: 60, time: new VirtualTimeSource(), asyncPresenting: false)
            {
                ClipboardWriter = Copied.Add,
                OnKey = k => { Unhandled.Add(k); return true; },
            };
            App.Focus(Probe);
            App.RunFrame();
        }

        public void Press(byte b)
        {
            Term.QueueInput(b);
            App.RunFrame();
        }
    }

    [Fact]
    public void CtrlC_CopiesTheFocusedWidgetsSelection_AndIsConsumed()
    {
        var rig = new Rig();
        rig.Probe.Selection = "selected text";

        rig.Press(0x03);

        Assert.Equal(["selected text"], rig.Copied);
        Assert.Empty(rig.Unhandled);
    }

    [Fact]
    public void CtrlC_WithNothingSelected_StillReachesTheApp()
    {
        // apps read Ctrl+C as "interrupt": it must survive when there is nothing to copy
        var rig = new Rig();

        rig.Press(0x03);

        Assert.Empty(rig.Copied);
        Assert.Equal('\u0003', Assert.Single(rig.Unhandled).Char);
    }

    [Fact]
    public void CtrlC_AnEmptySelectionCopiesNothing()
    {
        var rig = new Rig();
        rig.Probe.Selection = "";

        rig.Press(0x03);

        Assert.Empty(rig.Copied);
        Assert.Single(rig.Unhandled);
    }

    [Fact]
    public void AWidgetThatHandlesCtrlCItself_Wins()
    {
        var rig = new Rig();
        rig.Probe.Selection = "x";
        rig.Probe.HandlesCtrlC = true;

        rig.Press(0x03);

        Assert.Empty(rig.Copied);
        Assert.Empty(rig.Unhandled);
    }

    [Fact]
    public void CtrlA_AsksTheFocusedWidgetToSelectAll()
    {
        var rig = new Rig();

        rig.Press(0x01);

        Assert.Equal(1, rig.Probe.SelectAllCalls);
        Assert.Empty(rig.Unhandled);
    }

    [Fact]
    public void CtrlA_OnAWidgetWithoutSelection_FallsThrough()
    {
        var rig = new Rig();
        rig.App.Focus(null);

        rig.Press(0x01);

        Assert.Equal(0, rig.Probe.SelectAllCalls);
        Assert.Single(rig.Unhandled);
    }

    [Fact]
    public void Copy_AnnouncesWithAToast_UnlessAskedNotTo()
    {
        var rig = new Rig();

        rig.App.Copy("abc", announce: false);
        Assert.Equal(0, rig.App.OverlayCount);

        rig.App.Copy("abc");
        Assert.Equal(1, rig.App.OverlayCount);
        Assert.Equal(["abc", "abc"], rig.Copied);
    }

    [Fact]
    public void Copy_OfNothing_DoesNothing()
    {
        var rig = new Rig();
        rig.App.Copy("");
        Assert.Empty(rig.Copied);
        Assert.Equal(0, rig.App.OverlayCount);
    }

    [Fact]
    public void CopyMenu_EnablesCopyOnlyWithASelection_AndCopiesIt()
    {
        var rig = new Rig();

        var none = rig.Probe.Menu();
        Assert.Equal(["copy", "select all"], none.Select(m => m.Label));
        Assert.False(none[0].Enabled);

        rig.Probe.Selection = "menu text";
        var some = rig.Probe.Menu();
        Assert.True(some[0].Enabled);
        some[0].Act!();
        Assert.Equal(["menu text"], rig.Copied);

        some[1].Act!();
        Assert.Equal(1, rig.Probe.SelectAllCalls);
    }
}
