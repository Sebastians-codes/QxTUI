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

var hud = new Hud(() => themeNames[themeIdx], app.TargetFpsSignal);
var ball = new Ball(speed);
var staticPanel = new StaticPanel();
var stats = new StatsPanel(staticPanel, ball);
stats.MaxRenderHz = 10; // per-component cap demo: HUD text doesn't need 120Hz

var wrapDemo = new Paragraph(() =>
    "wrapping: 日本語のテキスト flows by 2-col runes, emoji 👍 = 2 cols, " +
    "ZWJ family 👨‍👩‍ = one glyph, combining é = zero — resize to reflow.");

var speedLabel = new Label(speed, v =>
{
    float m = Ball.MultFor(v);
    return $"speed {(m < 10 ? m.ToString("F1") : m.ToString("F0"))}x";
}, bg: ThemeRole.PanelBg); // opaque strip: ball must not show through
var speedSlider = new Slider(speed, ThemeRole.Warning);

var sineSpark = new Sparkline(sineHist, ThemeRole.Accent, scaleMax: 100f);
var walkSpark = new Sparkline(walkHist, ThemeRole.Success, autoFloor: 10f);

// feeder invalidates its dependents (kit widgets are draw-only)
var feederWired = new Feeder(sineHist, walkHist);

bool filledOn = false;
var filledDemo = new Button(
    () => filledOn ? " filled: on " : " filled: off ",
    () => filledOn = !filledOn,
    style: ButtonStyle.Filled)
{
    // persistent state color: green when on, amber when off —
    // on top of the momentary pressed inversion
    RoleProvider = () => filledOn ? ThemeRole.Success : ThemeRole.Warning,
};

var help = new Label(() =>
    " t: theme  [ ]: fps  1-4/click tabs  o/click: popup  Tab: focus  q: quit ", fg: ThemeRole.Text, bg: ThemeRole.Faint);

string[] PopupLines() =>
[
    $"theme:   {themeNames[themeIdx]}",
    $"target:  {app.TargetFps} fps",
    $"bytes/f: {app.Stats.LastBytesWritten:N0}",
    $"mouse:   {(app.CellPixelH > 0 ? $"pixel {app.CellPixelW}x{app.CellPixelH}" : "cell")}",
    $"hwscroll: {(app.HardwareScroll ? "on" : "off")} (auto per terminal)",
    $"term:    {app.TerminalName ?? "unknown (no XTVERSION reply)"}",
    "",
    "modals swallow all keys;",
    "esc / [x] closes; outside",
    "click closes THIS one (opt-in).",
];

// the one resizable component in the playground: the popup spawned by
// the open-popup button — everything else keeps Resizable = false.
// The fps chips are PURE INTENT (ChipAction.Msg): the modal knows
// nothing about this program; the update function at the top of this
// file is the only place that interprets the messages.
Modal MakePopup()
{
    var m = new Modal("popup", PopupLines, dim: false,
    chips:
    [
        new("30fps", Msg: new FpsMsg(30)),
        new("60fps", Msg: new FpsMsg(60)),
        new("120fps", Msg: new FpsMsg(120)),
        new("240fps", Msg: new FpsMsg(240)),
        new("480fps", Msg: new FpsMsg(480)),
        new("1000fps", Msg: new FpsMsg(1000)),
        new("stack", Act: () => app.PushOverlay(MakeConfirmModal())),
        new("rooms", Act: () =>
        {
            // offsets keep the boxes disjoint: overlapping floating
            // popups paint over each other's bodies every frame
            foreach (var (hz, ox, oy) in new[] { (8, -40, -10), (30, 0, 0), (120, 40, 10) })
                app.PushOverlay(MakeBallRoomModal(hz, ox, oy));
        }),
        new("hwscroll", Act: () =>
        {
            app.HardwareScroll = !app.HardwareScroll;
            app.InvalidateAll();
        }),
    ]);
    m.Resizable = true; // the one resizable component in the playground
    m.OutsideClickCloses = true; // demo the opt-in outside-press dismissal
    return m;
}

// popover-on-popover: confirm modal opens a third level with fps settings
// stacked levels don't dim: the base is already dimmed by level 1 —
// dimming a dimmed scene is just murk
Modal MakeConfirmModal() => new("confirm",
    () =>
    [
        "a second modal on top",
        "of the first — esc pops",
        "one level at a time.",
    ],
    dim: false,
    chips:
    [
        new("fps settings", Act: () => app.PushOverlay(MakeFpsModal())),
        new("ball room", Act: () => app.PushOverlay(MakeBallRoomModal())),
        new("close", Act: () => app.PopOverlay()),
    ]);

// a LIVE ball sim inside the overlay stack: overlays repaint every
// frame, so animated bodies run at full rate; the slider drives both
// balls (shared signal)
Modal MakeBallRoomModal(int hz = 0, int ox = 0, int oy = 0)
{
    Ball? roomBall = null;
    var m = new Modal(hz > 0 ? $"ball {hz}hz" : "ball room",
        () =>
        [
            hz > 0
                ? $"steps: {roomBall?.Steps ?? 0} @ {hz} Hz"
                : "a live ball sim inside",
            hz > 0
                ? "— its own refresh window"
                : "the overlay stack — the",
            hz > 0
                ? "inside the overlay stack."
                : "slider drives BOTH balls.",
        ],
        dim: false,
        chips: [new("close", Act: () => app.PopOverlay())])
    { BodyRows = 10, OffsetX = ox, OffsetY = oy };
    var room = new Container();
    room.Node.Direction = FlexDirection.Column;
    var b2 = new Ball(speed, tickHz: hz > 0 ? hz : null);
    b2.Node.Grow = 1;
    room.AddChild(b2);
    roomBall = b2;
    var srow = new Container();
    srow.Node.Height = 1;
    srow.AddChild(new Slider(speed));
    room.AddChild(srow);
    m.Body = room;
    return m;
}

Modal MakeFpsModal() => new("fps settings",
    () =>
    [
        "third level: popovers on",
        "popovers on popovers.",
        $"current target: {app.RequestedFps} fps",
        $"effective: {app.TargetFps} fps (may adapt down)",
        $"presented: {app.Stats.PresentedFps.ToString("F0")} fps",
    ],
    dim: false,
    chips:
    [
        new("30", Msg: new FpsMsg(30)),
        new("120", Msg: new FpsMsg(120)),
        new("240", Msg: new FpsMsg(240)),
        new("1000", Msg: new FpsMsg(1000)),
    ]);

Button? openBtn = null;
openBtn = new Button(() => " open popup ", style: ButtonStyle.Rounded)
{
    EmitOnClick = new OpenPopupMsg(), // pure intent: no app reference
};

var themeBtn = new Button(() => " theme ", style: ButtonStyle.Outline)
{
    EmitOnClick = new ThemeMsg(),
};



// ---- the whole layout as ONE expression (the DSL showcase) ------------------

// widget tree: the slider row lives INSIDE the ball panel — mounted,
// laid out and rendered via the parent, not the root list
ball.Node.Direction = FlexDirection.Column;
var ballSpacer = new Container();
ball.AddChild(ballSpacer);
ballSpacer.Node.Grow = 1f;

var sliderRow = new Container();
sliderRow.Node.Direction = FlexDirection.Row;
sliderRow.Node.Gap = 1;
ball.AddChild(sliderRow);
sliderRow.Node.Height = 1;
sliderRow.AddChild(speedLabel);
speedLabel.Node.Width = 16;
sliderRow.AddChild(speedSlider);
speedSlider.Node.Grow = 1f;

// ---- ball page: the sim + a multi-rate row ---------------------------------
// three capped balls side by side: each repaints at its own Hz while the
// page around them runs at full rate — independent refresh windows on
// the BASE scene (overlays can't do per-body rates: stacked floaters
// must repaint opaquely every frame to stay z-correct)
var ballPage = new Container();
ballPage.Node.Direction = FlexDirection.Column;
ballPage.Node.Gap = 1;
ball.Node.Grow = 1f;
ballPage.AddChild(ball);
var rateRow = new Container();
rateRow.Node.Direction = FlexDirection.Row;
rateRow.Node.Gap = 1;
rateRow.Node.Height = 9;
ballPage.AddChild(rateRow);
foreach (var hz in new[] { 8, 30, 120 })
{
    var col = new Container();
    col.Node.Direction = FlexDirection.Column;
    col.Node.Grow = 1f;
    rateRow.AddChild(col);
    var cap = hz;
    Ball? capped = null;
    col.AddChild(new Label(() =>
        // renders pause off-tab BY DESIGN (detached pages cost zero —
        // the damage-isolation contract); steps keep running because
        // ticks survive detach. Showing both makes the pause honest.
        $"capped {cap} Hz · {capped?.RenderCount ?? 0} renders"
        + $" · {capped?.Steps ?? 0} steps"));
    col.Children[col.Children.Count - 1].Node.Height = 1;
    var cb = new Ball(speed, tickHz: cap) { MaxRenderHz = cap };
    cb.Node.Grow = 1f;
    col.AddChild(cb);
    capped = cb;
}

// ---- SPA-style pages behind a Router ---------------------------------------

var widgetsPage = new Container();
widgetsPage.Node.Direction = FlexDirection.Row;
widgetsPage.Node.Gap = 1;

var barCol = new Container();
barCol.Node.Direction = FlexDirection.Column;
barCol.Node.Gap = 1;
widgetsPage.AddChild(barCol);
barCol.Node.Grow = 2f;
void AddBar(Label l, Bar b)
{
    barCol.AddChild(l); l.Node.Height = 1;
    barCol.AddChild(b); b.Node.Height = 1;
}
AddBar(new Label(linear, v => $"linear · 3s · {v:F2}"), new Bar(linear, ThemeRole.Accent));
AddBar(new Label(cubic, v => $"out-cubic · 2s · {v:F2}"), new Bar(cubic, ThemeRole.Success));
AddBar(new Label(back, v => $"out-back · 2.5s · {v:F2} (clamps)"), new Bar(back, ThemeRole.Warning));

var sparkCol = new Container();
sparkCol.Node.Direction = FlexDirection.Column;
sparkCol.Node.Gap = 1;
widgetsPage.AddChild(sparkCol);
sparkCol.Node.Grow = 2f;
var sineLabel = new Label(() => "sine · 20Hz feed · fixed scale");
var walkLabel = new Label(() => "random walk · auto scale");
sparkCol.AddChild(sineLabel); sineLabel.Node.Height = 1;
sparkCol.AddChild(sineSpark); sineSpark.Node.Height = 2;
sparkCol.AddChild(walkLabel); walkLabel.Node.Height = 1;
sparkCol.AddChild(walkSpark); walkSpark.Node.Height = 2;

// ---- kit controls showcase: the new everyday controls, bound to signals
var controlsCol = new Container();
controlsCol.Node.Direction = FlexDirection.Column;
controlsCol.Node.Gap = 1;
controlsCol.Node.Grow = 2f;
widgetsPage.AddChild(controlsCol);

var darkMode = new Signal<bool>(true);
var mode = new Signal<int>(1);
var choice = new Signal<int>(0);
var load = new Signal<float>(0.35f);
app.Scheduler.Animate(load, 1f,
    new Transition(TimeSpan.FromSeconds(4), Easing.Linear, RepeatMode.Reverse));

var controlsPanel = new Panel("controls");
controlsPanel.Node.Grow = 1f;
controlsCol.AddChild(controlsPanel);
var controlStack = new Container();
controlStack.Node.Direction = FlexDirection.Column;
controlStack.Node.Gap = 0; // dense gallery: fits short terminals too
controlsPanel.AddChild(controlStack);
controlStack.AddChild(new Checkbox(darkMode, "dark mode"));
controlStack.AddChild(new Segmented(mode, "day", "night", "auto"));
controlStack.AddChild(new Dropdown(choice, "alpha", "beta", "gamma"));
controlStack.AddChild(new Separator());
var busyRow = new Container();
busyRow.Node.Direction = FlexDirection.Row;
busyRow.Node.Gap = 1;
busyRow.Node.Height = 1;
controlStack.AddChild(busyRow);
busyRow.AddChild(new Spinner());
var loadBar = new ProgressBar(load);
busyRow.AddChild(loadBar);
loadBar.Node.Grow = 1f;
controlStack.AddChild(new Button(() => " toast ", style: ButtonStyle.Rounded)
{
    EmitOnClick = new ToastMsg("saved ✓"),
});

var textPage = new Container();
textPage.Node.Direction = FlexDirection.Column;
textPage.Node.Gap = 1;
var input = new TextField { Placeholder = "type here… (Tab leaves the field)" };
string submitted = "";
var echo = new Label(() =>
    submitted.Length > 0 ? $"submitted: {submitted}" : $"you typed: {input.Text}");
input.Changed += t => { submitted = ""; echo.Invalidate(); };
input.Submitted += t => { submitted = t; echo.Invalidate(); };

var textTitle = new Label(() => "text wrapping + unicode widths");
var ellipDemo = new Label(
    () => "ellipsize: the quick brown fox jumps over the lazy dog and keeps going…");
textPage.AddChild(textTitle); textTitle.Node.Height = 1;
textPage.AddChild(input); input.Node.Height = 1;
textPage.AddChild(echo); echo.Node.Height = 1;
textPage.AddChild(wrapDemo); wrapDemo.Node.Grow = 1f;
textPage.AddChild(ellipDemo); ellipDemo.Node.Height = 1;

// ---- page 4: virtualized list (10k rows, only visible ones paint) --------

var listPage = new Container();
listPage.Node.Direction = FlexDirection.Column;
listPage.Node.Gap = 1;

var listHint = new Label(() =>
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
      .Register("ball", () => ballPage)
      .Register("list", () => listPage);
router.Switch("widgets");

var tabBar = new TabBar(router, ["widgets", "text", "ball", "list"]);

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

// authoring API: the update function — the ONE place intents become state.
// Components (chips, buttons) and keys emit the same message vocabulary;
// nothing below here mutates app state directly anymore.
void CycleTheme()
{
    themeIdx = (themeIdx + 1) % themes.Length;
    app.Theme = themes[themeIdx];
    app.InvalidateAll(); // theme change = full repaint, by design
}

app.OnMessage<FpsMsg>(m => app.SetTargetFps(m.Value));
app.OnMessage<ThemeMsg>(_ => CycleTheme());
app.OnMessage<OpenPopupMsg>(_ => app.PushOverlay(MakePopup()));
app.OnMessage<ToastMsg>(m => app.Toast(m.Text, ToastKind.Success));

int[] fpsPresets = [30, 60, 120, 240, 480, 1000];

app.OnKey = ev =>
{
    switch (ev.Key)
    {
        // composition-root intents: keys speak the same message vocabulary
        // as the components, so every state change flows through ONE update fn
        case Key.Char when ev.Char is 't' or 'T':
            app.Send(new ThemeMsg());
            return true;

        case Key.Char when ev.Char is ']':
        case Key.Char when ev.Char is '[':
        {
            int i = Array.IndexOf(fpsPresets, app.TargetFps);
            i = ev.Char == ']' ? Math.Min(i + 1, fpsPresets.Length - 1) : Math.Max(i - 1, 0);
            app.Send(new FpsMsg(fpsPresets[i]));
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
            app.Send(new OpenPopupMsg()); // floating, no dim
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

// ---- intents (authoring API) ------------------------------------------------
// The vocabulary components speak. A chip/button emits a message without
// knowing this program exists; the update function registered at the top
// is the single interpretation. New feature = new message + new handler,
// never a closure wired through the component that emits it.
internal record FpsMsg(int Value);
internal record ThemeMsg;
internal record OpenPopupMsg;
internal record ToastMsg(string Text);
