# Authoring apps with qxtui

qxtui separates an app into four layers, each with exactly one owner:

```
intent    messages (Send / Post / Effect results)   → App.OnMessage<T> / App.Update
state     Signal<T> (+ animations writing signals)  → widgets subscribe (OnSignal)
render    dirty widgets paint into their layer      → Widget.Render(CellBuffer)
pixels    compose → diff → ANSI → writer thread     → framework, never you
```

The rule of thumb: **events become messages, messages become signal writes,
signal writes become repaints.** Nothing skips a layer.

## 1. Messages (intent)

Declare your vocabulary as records — usually at the bottom of `Program.cs`:

```csharp
internal record FpsMsg(int Value);
internal record ThemeMsg;
internal record OpenPopupMsg;
```

One update function interprets them. Either per type:

```csharp
app.OnMessage<FpsMsg>(m => app.SetTargetFps(m.Value));
```

or catch-all, switch-style (typed handlers win when both match):

```csharp
app.Update(msg => msg switch
{
    FpsMsg f => Set(f.Value),
    ThemeMsg => CycleTheme(),
    _        => false,                 // not mine → UnhandledMessages++
});
```

Emitters never reference the app:

```csharp
// inside any widget:
Send(new FpsMsg(30));                                  // protected, returns "handled?"
// kit components, declaratively:
new ButtonWidget(() => " theme ") { EmitOnClick = new ThemeMsg() };
new ModalWidget("popup", lines, dim: false, chips:
    [ new("30fps", Msg: new FpsMsg(30)),               // pure intent
      new("stack", Act: () => ...) ]);                 // closure = app policy
```

`app.Stats.UnhandledMessages` counts intents nobody handled — keep it at 0;
it is also a fine test assertion.

**Scoped intent:** the channel is app-global. Two instances of a reusable
component emitting the same message type collide; put identity in the record
(`record CloseMsg(Widget Owner)`) and filter in the handler.

## 2. Effects (off-thread work)

The frame loop must never block. Anything slow (file, network, `/proc` walk)
runs as an effect; its result comes back as an ordinary message:

```csharp
app.OnMessage<LoadMsg>(m => app.Effect(async ct =>
{
    var text = await File.ReadAllTextAsync(m.Path, ct);   // thread pool
    return new LoadedMsg(text);                            // posted, drained next frame
}));
```

- Exceptions become `EffectFault(Exception)` messages — handle or watch the
  unhandled counter; the loop never dies.
- `app.Post(msg)` is the raw thread-safe ingress (effects use it internally);
  it is the ONLY supported way to touch the app from another thread.
- Guard re-entrancy yourself when a scan may outlive its timer
  (`Interlocked.Exchange(ref _busy, 1)`), see `samples/QxTui.SysMon`
  `ProcWidget` for the full pattern: tick → effect → message → repaint.

## 3. Lifecycle

```csharp
public override void OnMount(App app) { }          // attached; app live
public override void OnDetach() { }                // detached (also cached pages)
public override void OnRectChanged(Rect o, Rect n) // rect actually moved
```

`Tick(interval)` and `OnSignal(sig)` declared in constructors materialize at
mount and dispose with the widget. `OnDetach` fires before `Dispose` and also
for `dispose:false` detaches (router-cached pages), so pause/resume logic
belongs there, not in `Dispose`.

## 4. State and repaints

- `Signal<T>`: versioned; writing an equal value repaints nothing.
- `OnSignal(s)` / `OnSignal(s, cb)` subscribe; `Tick(hz)` periodic invalidate.
- `MaxRenderHz` caps a component's repaint rate without dropping invalidations.
- Animations: `app.Scheduler.Animate(signal, to, Transition(dur, easing))`.

## 5. Layout and windows

- DSL: `UI.Column(gap, a.Grow(1), b.Height(3))`; fluent on widgets too.
- Sizes: fixed `Width/Height`, `WidthPercent/HeightPercent` (fraction of the
  parent's inner space), `Grow`/`Shrink`/`Basis` for flex contests.
- Windows: `app.PushOverlay(modal)`; floating popups (`dim:false`) coexist;
  `OutsideClickCloses` opts into dismiss-on-outside-press; title bar drags,
  right-drag resizes (`Resizable = true`) come free. Z-order is structural
  (per-overlay layers), so moving/resizing/closing never ghosts.

## 6. Testing

Logic: no terminal at all — `app.Send(msg)`, assert signals.
UI: `FakeTerminal` + `app.RunFrame()`; queue SGR mouse bytes for clicks;
assert `app.Back` cells or the exact emitted bytes. Effects: pump frames
until the result message lands. See `tests/QxTui.Tests/AuthoringApiTests.cs`.

## Worked examples

- `samples/QxTui.Playground/Program.cs` — messages end-to-end (chips,
  buttons, keys all emit; one update function mutates state).
- `samples/QxTui.SysMon/Widgets.cs` — `ProcWidget`: 1Hz effect sampling
  `/proc` off-thread, result as message, virtualized list repaint.
