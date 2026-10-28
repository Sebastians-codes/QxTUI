using QxTui.Core;
using QxTui.Layout;
using QxTui.SysMon;
using QxTui.Terminal;

// ---------------------------------------------------------------------------
// qxtop — a btop-style system monitor built on qxtui.
//
// Not a benchmark: a real application. Every panel samples /proc at its own
// rate (cpu 5Hz, net 10Hz, mem 4Hz, procs 1Hz, clock 1Hz) via the
// single AnimationScheduler clock, and each panel invalidates ONLY itself —
// the damage-isolation architecture doing actual work. Resize freely; the
// flexbox tree reflows and untouched panels keep their cached cells.
// ---------------------------------------------------------------------------

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("qxtop: Linux only (/proc based).");
    return 1;
}

var terminal = new PosixTerminal();

// App first: widgets need its scheduler. Root tree comes after, because the
// tree references widget nodes (SetRoot exists for exactly this).
var app = new App(terminal, new LayoutNode(), targetFps: 60);

var header = new Header();
var cpu = new Cpu();
var mem = new Mem();
var net = new Net();
var procs = new Procs();
var footer = new Footer();

// ---- flexbox layout ---------------------------------------------------------

var rowTop = new LayoutNode { Direction = FlexDirection.Row, Height = 12, Gap = 1 };
rowTop.Add(cpu.Node);
rowTop.Add(mem.Node);
cpu.Node.Grow = 3f;
mem.Node.Grow = 2f;

var root = new LayoutNode { Direction = FlexDirection.Column, Gap = 1 };
root.Add(header.Node);
root.Add(rowTop);
root.Add(net.Node);
root.Add(procs.Node);
root.Add(footer.Node);
header.Node.Height = 1;
net.Node.Height = 5;
procs.Node.Grow = 1f;
procs.Node.Basis = 8; // minimum presence on short terminals (shrinks others first)
footer.Node.Height = 1;

app.SetRoot(root);
app.AddWidget(header)
   .AddWidget(cpu)
   .AddWidget(mem)
   .AddWidget(net)
   .AddWidget(procs)
   .AddWidget(footer);

app.OnKey = ev =>
{
    switch (ev.Key)
    {
        case Key.Char when ev.Char is 'q' or '\u0003': // q or Ctrl-C
            app.Quit();
            return false;
        case Key.Char when ev.Char is 'p':
            procs.Paused = !procs.Paused;
            procs.Invalidate();
            return true;
        default:
            return true;
    }
};

using (app)
{
    app.Run();
}

return 0;
