using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Snake;
using QxTui.Terminal;

// ---------------------------------------------------------------------------
// qxtui snake — a real game on the framework.
//
// Fixed-step simulation (speed ramps with score) on the scheduler clock,
// parsed arrow-key input, pause/restart states, flexbox layout. The HUD is
// signal-driven: it re-renders only when score or alive-state changes while
// the playfield ticks away beside it.
// ---------------------------------------------------------------------------

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("qxtui snake: Linux only for now.");
    return 1;
}

var terminal = new PosixTerminal();
var app = new App(terminal, new LayoutNode(), targetFps: 165);

var score = new Signal<int>(0);
var alive = new Signal<bool>(true);

var hud = new HudWidget(score, alive);
var game = new SnakeWidget(app.Scheduler, score, alive);
var help = new HelpWidget();

var root = new LayoutNode { Direction = FlexDirection.Column, Gap = 1 };
root.Add(hud.Node);
root.Add(game.Node);
root.Add(help.Node);
hud.Node.Height = 1;
game.Node.Grow = 1f;
game.Node.Basis = 10;
help.Node.Height = 1;

app.SetRoot(root);
app.AddWidget(hud).AddWidget(game).AddWidget(help);

app.OnKey = ev =>
{
    Direction? turn = ev.Key switch
    {
        Key.Up => Direction.Up,
        Key.Down => Direction.Down,
        Key.Left => Direction.Left,
        Key.Right => Direction.Right,
        Key.Char when ev.Char is 'w' or 'W' => Direction.Up,
        Key.Char when ev.Char is 's' or 'S' => Direction.Down,
        Key.Char when ev.Char is 'a' or 'A' => Direction.Left,
        Key.Char when ev.Char is 'd' or 'D' => Direction.Right,
        _ => null,
    };

    if (turn is { } d)
    {
        game.Turn(d);
        return true;
    }

    switch (ev.Key)
    {
        case Key.Space:
            game.TogglePause();
            return true;
        case Key.Enter:
            if (!game.Model.Alive) game.Restart();
            return true;
        case Key.Escape:
        case Key.Char when ev.Char is 'q':
            app.Quit();
            return false;
        default:
            return true;
    }
};

using (app)
{
    app.Run();
}

return 0;
