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
var fps = new Signal<int>(120);

var hud = new HudWidget(score, alive, fps);
var game = new SnakeWidget(app.Scheduler, score, alive);
var help = new HelpWidget();

var root = new LayoutNode { Direction = FlexDirection.Column, Gap = 0 };
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
    if (InputMap.ToDirection(ev) is { } d)
    {
        game.Turn(d);
        return true;
    }

    // pause menu: fps control lives here
    if (game.Paused)
    {
        int[] presets = [30, 60, 120, 240];
        switch (ev.Key)
        {
            case Key.Char when ev.Char is 'f' or 'F':
            {
                int i = Array.IndexOf(presets, app.TargetFps);
                int next = presets[(i + 1) % presets.Length];
                app.SetTargetFps(next);
                fps.Set(next);
                return true;
            }
            case Key.Char when ev.Char is >= '1' and <= '4':
            {
                int next = presets[ev.Char - '1'];
                app.SetTargetFps(next);
                fps.Set(next);
                return true;
            }
        }
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
