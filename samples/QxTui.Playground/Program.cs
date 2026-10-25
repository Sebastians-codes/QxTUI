using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Playground;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;
using QxTui.Widgets;

// ---------------------------------------------------------------------------
// qxtui playground — every framework feature, labeled, in one screen.
//
//   layout DSL        the whole tree is ONE expression (UI.Column/Row + fluent)
//   themes            t cycles Night → Mono → Amber; every widget recolors
//   widget kit        Label/Bar/Sparkline widgets + Box/Bar/Sparkline primitives
//   Tick / OnSignal   declared in ctors, auto-mounted, auto-disposed
//   transitions       three easings on signals, declarative, looping
//   square pixels     half-block bouncing ball with a fading trail
//   fps control       [ and ] retarget 30/60/120/240 at runtime
//   damage isolation  static panel renders once; stats prove it live
// ---------------------------------------------------------------------------

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("qxtui playground: Linux only for now.");
    return 1;
}

var terminal = new PosixTerminal();
var app = new App(terminal, new LayoutNode(), targetFps: 120);

// ---- signals + declarative transitions (one scheduler clock) ---------------

var linear = new Signal<float>(0f);
var cubic = new Signal<float>(0f);
var back = new Signal<float>(0f);
var fpsSig = new Signal<int>(120);

app.Scheduler.Animate(linear, 1f, new Transition(TimeSpan.FromSeconds(3), Easing.Linear, RepeatMode.Reverse));
app.Scheduler.Animate(cubic, 1f, new Transition(TimeSpan.FromSeconds(2), Easing.OutCubic, RepeatMode.Reverse));
app.Scheduler.Animate(back, 1f, new Transition(TimeSpan.FromSeconds(2.5), Easing.OutBack, RepeatMode.Reverse));

// ---- themes: framework defaults + one defined right here -------------------

var amber = new Theme(
    Border: 238, Title: 214, Text: 222, Dim: 244, Faint: 236,
    Accent: 214, Success: 100, Warning: 214, Danger: 196, PanelBg: 233);
Theme[] themes = [Theme.Night, Theme.Mono, amber];
string[] themeNames = ["Night", "Mono", "Amber"];
int themeIdx = 0;

// ---- widgets ---------------------------------------------------------------

var sineHist = new RingHistory(120);
var walkHist = new RingHistory(120);

var hud = new HudWidget(() => themeNames[themeIdx], fpsSig);
var ball = new BallWidget();
var staticPanel = new StaticPanelWidget();
var stats = new StatsPanelWidget(staticPanel, ball);

var sineSpark = new SparklineWidget(sineHist, ThemeRole.Accent, scaleMax: 100f);
var walkSpark = new SparklineWidget(walkHist, ThemeRole.Success, autoFloor: 10f);

// feeder invalidates its dependents (kit widgets are draw-only)
var feederWired = new FeederWidget(sineHist, walkHist, onChanged: () =>
{
    sineSpark.Invalidate();
    walkSpark.Invalidate();
});

var help = new LabelWidget(() =>
    " t: theme   [ ]: fps   o/click: popup   q: quit ", fg: ThemeRole.Text, bg: ThemeRole.Faint);

ButtonWidget? openBtn = null;
openBtn = new ButtonWidget(() => " open popup ", () => app.PushOverlay(new ModalWidget(
    "popup",
    () =>
    [
        $"theme:   {themeNames[themeIdx]}",
        $"target:  {app.TargetFps} fps",
        $"bytes/f: {app.Stats.LastBytesWritten:N0}",
        "",
        "modals swallow all keys;",
        "esc / [x] / dim click closes.",
    ])));

// ---- the whole layout as ONE expression (the DSL showcase) ------------------

app.SetRoot(UI.Column(1,
    hud.Height(1),
    UI.Row(1,
        UI.Column(1,
            new LabelWidget(linear, v => $"linear · 3s · {v:F2}").Height(1),
            new BarWidget(linear, ThemeRole.Accent).Height(1),
            new LabelWidget(cubic, v => $"out-cubic · 2s · {v:F2}").Height(1),
            new BarWidget(cubic, ThemeRole.Success).Height(1),
            new LabelWidget(back, v => $"out-back · 2.5s · {v:F2} (clamps)").Height(1),
            new BarWidget(back, ThemeRole.Warning).Height(1))
        .Grow(2),
        UI.Column(1,
            new LabelWidget(() => "sine · 20Hz feed · fixed scale").Height(1),
            sineSpark.Height(2),
            new LabelWidget(() => "random walk · auto scale").Height(1),
            walkSpark.Height(2))
        .Grow(2),
        ball.Grow(3))
    .Grow(1),
    UI.Row(1, staticPanel.Grow(1), stats.Grow(1), openBtn.Width(16)).Height(9),
    help.Height(1)));

app.AddWidget(hud)
   .AddWidget(feederWired)
   .AddWidget(sineSpark)
   .AddWidget(walkSpark)
   .AddWidget(ball)
   .AddWidget(staticPanel)
   .AddWidget(stats)
   .AddWidget(help);

// ---- keys: theme cycle + runtime fps control --------------------------------

int[] fpsPresets = [30, 60, 120, 240];

app.OnKey = ev =>
{
    switch (ev.Key)
    {
        case Key.Char when ev.Char is 't' or 'T':
            themeIdx = (themeIdx + 1) % themes.Length;
            app.Theme = themes[themeIdx];
            app.InvalidateAll(); // theme change = full repaint, by design
            return true;

        case Key.Char when ev.Char is ']':
        case Key.Char when ev.Char is '[':
        {
            int i = Array.IndexOf(fpsPresets, app.TargetFps);
            i = ev.Char == ']' ? Math.Min(i + 1, fpsPresets.Length - 1) : Math.Max(i - 1, 0);
            app.SetTargetFps(fpsPresets[i]);
            fpsSig.Set(fpsPresets[i]);
            return true;
        }

        case Key.Char when ev.Char is 'o' or 'O':
            openBtn!.OnClick(new QxTui.Layout.Point(0, 0));
            return true;

        case Key.Char when ev.Char is 'q':
        case Key.Escape:
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
