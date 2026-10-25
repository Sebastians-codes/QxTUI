using QxTui.Animation;
using QxTui.Core;
using QxTui.Layout;
using QxTui.Playground;
using System.Globalization;
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

app.Scheduler.Animate(linear, 1f, new Transition(TimeSpan.FromSeconds(3), Easing.Linear, RepeatMode.Reverse));
app.Scheduler.Animate(cubic, 1f, new Transition(TimeSpan.FromSeconds(2), Easing.OutCubic, RepeatMode.Reverse));
app.Scheduler.Animate(back, 1f, new Transition(TimeSpan.FromSeconds(2.5), Easing.OutBack, RepeatMode.Reverse));

// ---- themes: framework defaults + one defined right here -------------------

var amber = new Theme(
    Border: 238, Title: 214, Text: 222, Dim: 244, Faint: 236,
    Accent: 214, Success: 100, Warning: 214, Danger: 196, PanelBg: 233);
Theme[] themes = [Theme.Night, Theme.Mono, amber, Theme.NightRgb];
string[] themeNames = ["Night", "Mono", "Amber", "NightRGB"];
int themeIdx = 0;

// ---- widgets ---------------------------------------------------------------

var sineHist = new RingHistory(120);
var walkHist = new RingHistory(120);

var speed = new Signal<float>(0.5f); // mid-travel = exactly 1.0x

var hud = new HudWidget(() => themeNames[themeIdx], app.TargetFpsSignal);
var ball = new BallWidget(speed);
var staticPanel = new StaticPanelWidget();
var stats = new StatsPanelWidget(staticPanel, ball);

var wrapDemo = new TextWidget(() =>
    "wrapping: 日本語のテキスト flows by 2-col runes, emoji 👍 = 2 cols, " +
    "ZWJ family 👨‍👩‍ = one glyph, combining é = zero — resize to reflow.");

var speedLabel = new LabelWidget(speed, v =>
{
    float m = BallWidget.MultFor(v);
    return $"speed {(m < 10 ? m.ToString("F1") : m.ToString("F0"))}x";
}, bg: ThemeRole.PanelBg); // opaque strip: ball must not show through
var speedSlider = new SliderWidget(speed, ThemeRole.Warning);

var sineSpark = new SparklineWidget(sineHist, ThemeRole.Accent, scaleMax: 100f);
var walkSpark = new SparklineWidget(walkHist, ThemeRole.Success, autoFloor: 10f);

// feeder invalidates its dependents (kit widgets are draw-only)
var feederWired = new FeederWidget(sineHist, walkHist);

bool filledOn = false;
var filledDemo = new ButtonWidget(
    () => filledOn ? " filled: on " : " filled: off ",
    () => filledOn = !filledOn,
    style: ButtonStyle.Filled)
{
    // persistent state color: green when on, amber when off —
    // on top of the momentary pressed inversion
    RoleProvider = () => filledOn ? ThemeRole.Success : ThemeRole.Warning,
};

var help = new LabelWidget(() =>
    " t: theme  [ ]: fps  1-4/click tabs  o/click: popup  Tab: focus  q: quit ", fg: ThemeRole.Text, bg: ThemeRole.Faint);

string[] PopupLines() =>
[
    $"theme:   {themeNames[themeIdx]}",
    $"target:  {app.TargetFps} fps",
    $"bytes/f: {app.Stats.LastBytesWritten:N0}",
    $"mouse:   {(app.CellPixelH > 0 ? $"pixel {app.CellPixelW}x{app.CellPixelH}" : "cell")}",
    $"hwscroll: {(app.HardwareScroll ? "on" : "off")} (experimental)",
    "",
    "modals swallow all keys;",
    "esc / [x] / dim click closes.",
];

void SetFps(int v) => app.SetTargetFps(v); // TargetFpsSignal updates HUDs

ModalWidget MakePopup() => new("popup", PopupLines, dim: false,
    actions:
    [
        ("30fps", () => SetFps(30)),
        ("60fps", () => SetFps(60)),
        ("120fps", () => SetFps(120)),
        ("240fps", () => SetFps(240)),
        ("480fps", () => SetFps(480)),
        ("1000fps", () => SetFps(1000)),
        ("stack", () => app.PushOverlay(MakeConfirmModal())),
        ("hwscroll", () =>
        {
            app.HardwareScroll = !app.HardwareScroll;
            app.InvalidateAll();
        }),
    ]);

// popover-on-popover: confirm modal opens a third level with fps settings
ModalWidget MakeConfirmModal() => new("confirm",
    () =>
    [
        "a second modal on top",
        "of the first — esc pops",
        "one level at a time.",
    ],
    dim: true,
    actions:
    [
        ("fps settings", () => app.PushOverlay(MakeFpsModal())),
        ("close", () => app.PopOverlay()),
    ]);

ModalWidget MakeFpsModal() => new("fps settings",
    () =>
    [
        "third level: popovers on",
        "popovers on popovers.",
        $"current target: {app.TargetFps} fps",
    ],
    dim: true,
    actions:
    [
        ("30", () => SetFps(30)),
        ("120", () => SetFps(120)),
        ("240", () => SetFps(240)),
        ("1000", () => SetFps(1000)),
    ]);

ButtonWidget? openBtn = null;
openBtn = new ButtonWidget(() => " open popup ", () => app.PushOverlay(MakePopup()),
    style: ButtonStyle.Rounded);

var themeBtn = new ButtonWidget(() => " theme ", () =>
{
    themeIdx = (themeIdx + 1) % themes.Length;
    app.Theme = themes[themeIdx];
    app.InvalidateAll();
}, style: ButtonStyle.Outline);



// ---- the whole layout as ONE expression (the DSL showcase) ------------------

// widget tree: the slider row lives INSIDE the ball panel — mounted,
// laid out and rendered via the parent, not the root list
ball.Node.Direction = FlexDirection.Column;
var ballSpacer = new ContainerWidget();
ball.AddChild(ballSpacer);
ballSpacer.Node.Grow = 1f;

var sliderRow = new ContainerWidget();
sliderRow.Node.Direction = FlexDirection.Row;
sliderRow.Node.Gap = 1;
ball.AddChild(sliderRow);
sliderRow.Node.Height = 1;
sliderRow.AddChild(speedLabel);
speedLabel.Node.Width = 16;
sliderRow.AddChild(speedSlider);
speedSlider.Node.Grow = 1f;

// ---- SPA-style pages behind a Router ---------------------------------------

var widgetsPage = new ContainerWidget();
widgetsPage.Node.Direction = FlexDirection.Row;
widgetsPage.Node.Gap = 1;

var barCol = new ContainerWidget();
barCol.Node.Direction = FlexDirection.Column;
barCol.Node.Gap = 1;
widgetsPage.AddChild(barCol);
barCol.Node.Grow = 2f;
void AddBar(LabelWidget l, BarWidget b)
{
    barCol.AddChild(l); l.Node.Height = 1;
    barCol.AddChild(b); b.Node.Height = 1;
}
AddBar(new LabelWidget(linear, v => $"linear · 3s · {v:F2}"), new BarWidget(linear, ThemeRole.Accent));
AddBar(new LabelWidget(cubic, v => $"out-cubic · 2s · {v:F2}"), new BarWidget(cubic, ThemeRole.Success));
AddBar(new LabelWidget(back, v => $"out-back · 2.5s · {v:F2} (clamps)"), new BarWidget(back, ThemeRole.Warning));

var sparkCol = new ContainerWidget();
sparkCol.Node.Direction = FlexDirection.Column;
sparkCol.Node.Gap = 1;
widgetsPage.AddChild(sparkCol);
sparkCol.Node.Grow = 2f;
var sineLabel = new LabelWidget(() => "sine · 20Hz feed · fixed scale");
var walkLabel = new LabelWidget(() => "random walk · auto scale");
sparkCol.AddChild(sineLabel); sineLabel.Node.Height = 1;
sparkCol.AddChild(sineSpark); sineSpark.Node.Height = 2;
sparkCol.AddChild(walkLabel); walkLabel.Node.Height = 1;
sparkCol.AddChild(walkSpark); walkSpark.Node.Height = 2;

var textPage = new ContainerWidget();
textPage.Node.Direction = FlexDirection.Column;
textPage.Node.Gap = 1;
var input = new TextFieldWidget { Placeholder = "type here… (Tab leaves the field)" };
string submitted = "";
var echo = new LabelWidget(() =>
    submitted.Length > 0 ? $"submitted: {submitted}" : $"you typed: {input.Text}");
input.Changed += t => { submitted = ""; echo.Invalidate(); };
input.Submitted += t => { submitted = t; echo.Invalidate(); };

var textTitle = new LabelWidget(() => "text wrapping + unicode widths");
var ellipDemo = new LabelWidget(
    () => "ellipsize: the quick brown fox jumps over the lazy dog and keeps going…");
textPage.AddChild(textTitle); textTitle.Node.Height = 1;
textPage.AddChild(input); input.Node.Height = 1;
textPage.AddChild(echo); echo.Node.Height = 1;
textPage.AddChild(wrapDemo); wrapDemo.Node.Grow = 1f;
textPage.AddChild(ellipDemo); ellipDemo.Node.Height = 1;

// ---- page 4: virtualized list (10k rows, only visible ones paint) --------

var listPage = new ContainerWidget();
listPage.Node.Direction = FlexDirection.Column;
listPage.Node.Gap = 1;

var listHint = new LabelWidget(() =>
    "10,000 rows — only the visible slice paints. wheel / drag-scroll " +
    "(thumb is pixel-fine where the terminal reports pixels), click or " +
    "Tab to focus, then arrows / PgUp / PgDn / Home / End");
var bigList = new ListView(() => 10_000)
{
    RenderRow = (buffer, clip, rect, i, selected) =>
    {
        var theme = app.Theme;
        if (selected)
            buffer.FillRect(rect.X, rect.Y, rect.W, rect.H,
                new Cell { Char = ' ', Fg = -1, Bg = theme.Get(ThemeRole.PanelBg) });
        buffer.WriteTextClipped(clip, rect.X + 1, rect.Y,
            string.Create(CultureInfo.InvariantCulture,
                $"row {i,5}  value {(i * 7) % 97,3}  {new string('#', 1 + i % 40)}"),
            fg: selected ? theme.Get(ThemeRole.Title) : theme.Get(ThemeRole.Text));
    },
};
listPage.AddChild(listHint);
listHint.Node.Height = 1;
listPage.AddChild(bigList);
bigList.Node.Grow = 1f;

var router = new Router();
router.Register("widgets", () => widgetsPage)
      .Register("text", () => textPage)
      .Register("ball", () => ball)
      .Register("list", () => listPage);
router.Switch("widgets");

var tabBar = new TabBarWidget(router, ["widgets", "text", "ball", "list"]);

app.SetRoot(UI.Column(1,
    hud.Height(1),
    tabBar.Height(1),
    router.Grow(1),
    UI.Row(1, staticPanel.Grow(1), stats.Grow(1),
        UI.Column(1, themeBtn.Height(3), filledDemo.Height(3), openBtn.Height(3)).Width(16))
    .Height(11), // three 3-row buttons + gaps: below this they'd crush
    help.Height(1),
    feederWired.Height(0))); // data-only widget: zero-height tree slot keeps it mounted

// no AddWidget calls needed: SetRoot walks the DSL tree and mounts
// every widget it finds (roots AND children)

// ---- keys: theme cycle + runtime fps control --------------------------------

int[] fpsPresets = [30, 60, 120, 240, 480, 1000];

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
            return true;
        }

        case Key.Char when ev.Char is '1':
            router.Switch("widgets");
            return true;
        case Key.Char when ev.Char is '2':
            router.Switch("text");
            return true;
        case Key.Char when ev.Char is '3':
            router.Switch("ball");
            return true;
        case Key.Char when ev.Char is '4':
            router.Switch("list");
            return true;

        case Key.Char when ev.Char is 'o' or 'O':
            app.PushOverlay(MakePopup()); // floating, no dim
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
