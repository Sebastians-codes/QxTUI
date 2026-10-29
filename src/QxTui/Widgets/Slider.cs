using QxTui.Core;
using QxTui.Layout;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>
/// Horizontal slider bound to a Signal&lt;float&gt; in [0,1].
/// Click to jump, drag to slide (mouse capture keeps the drag alive even
/// when the pointer leaves the row). Knob brightens on hover/press.
/// </summary>
public sealed class Slider : Widget
{
    private readonly Signal<float> _value;
    private readonly ThemeRole _fg;
    private bool _dragging;

    public Slider(Signal<float> value, ThemeRole fg = ThemeRole.Accent)
    {
        _value = value;
        _fg = fg;
        Clickable = true;
        CapturesMouse = true;
        OnSignal(value); // external writes repaint too
    }

    public float Value => _value.Value;

    /// <summary>Value change per wheel notch.</summary>
    public float WheelStep { get; set; } = 0.05f;

    public override bool OnMouse(MouseEvent ev)
    {
        switch (ev.Kind)
        {
            case MouseKind.Press:
                _dragging = true;
                SetFromX(ev.X);
                return true;
            case MouseKind.Motion:
                if (_dragging) SetFromX(ev.X);
                return _dragging;
            case MouseKind.WheelUp:
                Nudge(+WheelStep); // hover + scroll adjusts, no click needed
                return true;
            case MouseKind.WheelDown:
                Nudge(-WheelStep);
                return true;
            default: // release
                _dragging = false;
                return true;
        }
    }

    private void Nudge(float delta) =>
        _value.Set(Math.Clamp(_value.Value + delta, 0f, 1f));

    private void SetFromX(int x)
    {
        var r = Rect;
        int track = Math.Max(1, r.W - 3); // 1-cell inset each side + knob
        float frac = Math.Clamp((x - r.X - 1) / (float)track, 0f, 1f);
        _value.Set(frac);
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        // opaque: a transparent slider row lets whatever is behind the
        // panel (or the terminal wallpaper) show through — widgets that
        // form a UI strip must own their background
        buffer.FillRect(r.X, r.Y, r.W, r.H,
            new Cell { Char = ' ', Fg = -1, Bg = Theme.Get(ThemeRole.PanelBg) });
        int y = r.Y + r.H / 2;
        int track = Math.Max(1, r.W - 3);
        int pos = r.X + 1 + (int)(_value.Value * track);

        int col = Theme.Get(_fg);
        int hot = Theme.Get(Hovered || Pressed ? ThemeRole.Title : _fg);

        int panelBg = Theme.Get(ThemeRole.PanelBg);
        for (int x = r.X + 1; x <= r.X + 1 + track; x++)
            buffer.WriteTextClipped(Clip, x, y, "─",
                fg: x <= pos ? col : Theme.Get(ThemeRole.Faint), bg: panelBg);

        buffer.WriteTextClipped(Clip, pos, y, "█", fg: hot, bg: panelBg, bold: true);
    }
}
