using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;

namespace QxTui.Widgets;

public enum ButtonStyle
{
    /// <summary>Solid fill, dark bold label.</summary>
    Filled,
    /// <summary>Single-line box outline.</summary>
    Outline,
    /// <summary>Rounded-corner outline.</summary>
    Rounded,
}

/// <summary>Clickable labeled button in three styles, all hover-reactive:
/// the pointer brightens border/fill/label — the "this is clickable"
/// affordance. Click target is the whole rect.</summary>
public sealed class Button : Widget
{
    private readonly Func<string> _label;
    private readonly Action? _onClick;
    private readonly ThemeRole _fg;
    private readonly ButtonStyle _style;

    /// <summary>Optional persistent state color (e.g. a toggle: green when
    /// on, warning when off). Overrides <c>fg</c> while provided.</summary>
    public Func<ThemeRole>? RoleProvider { get; set; }

    /// <summary>Intent emitted on click (authoring API): a button that only
    /// knows "I was clicked, this is my message" drops into any app — the
    /// update function decides what the message means. Composes with
    /// <c>onClick</c> (message first, then the callback).</summary>
    public object? EmitOnClick { get; set; }

    public Button(Func<string> label, Action? onClick,
        ThemeRole fg = ThemeRole.Accent, ButtonStyle style = ButtonStyle.Rounded)
    {
        _label = label;
        _onClick = onClick;
        _fg = fg;
        _style = style;
        Clickable = true;
        Node.Height = 1; // sane default: a bare button is one row, not zero
    }

    /// <summary>Pure-intent button: no callback at all, just EmitOnClick.</summary>
    public Button(Func<string> label,
        ThemeRole fg = ThemeRole.Accent, ButtonStyle style = ButtonStyle.Rounded)
        : this(label, null, fg, style) { }

    public override void OnClick(Point local)
    {
        if (EmitOnClick is { } msg) Send(msg);
        _onClick?.Invoke();
    }

    public override void Render(CellBuffer buffer)
    {
        var r = Rect;
        buffer.FillRect(r.X, r.Y, r.W, r.H, Cell.Empty);

        // reactive states: idle → hovered (brighter) → pressed/flashing (inverted)
        bool down = Pressed || FlashActive;
        ThemeRole role = RoleProvider?.Invoke() ?? _fg;
        int baseCol = Theme.Get(role);
        int hot = Theme.Get(Hovered ? ThemeRole.Title : role);
        int fill = down ? Theme.Get(ThemeRole.Faint)
                 : Hovered ? Theme.Get(ThemeRole.Title)
                 : baseCol;
        int textOnFill = down ? hot : 232;

        var label = _label();
        int lx = r.X + Math.Max(1, (r.W - label.Length) / 2);
        int ly = r.Y + r.H / 2;

        switch (_style)
        {
            case ButtonStyle.Filled:
                for (int y = r.Y; y < r.Bottom; y++)
                    for (int x = r.X; x < r.Right; x++)
                        buffer.WriteTextClipped(Clip, x, y, " ", fg: textOnFill, bg: fill);
                buffer.WriteTextClipped(Clip, lx, ly, label, fg: textOnFill, bg: fill, bold: true);
                break;

            case ButtonStyle.Outline:
            default:
                Box.Draw(buffer, r.X, r.Y, r.W, r.H,
                    _style == ButtonStyle.Outline ? BoxStyles.Single : BoxStyles.Rounded, hot);
                if (down)
                {
                    // pressed indicator: invert the LABEL only (dark text on
                    // a bright chip) while the border brightens — a crisp
                    // push-down, not a gray wash over the whole button
                    buffer.WriteTextClipped(Clip, lx, ly, label,
                        fg: 232, bg: hot, bold: true);
                }
                else
                {
                    buffer.WriteTextClipped(Clip, lx, ly, label, fg: hot, bold: true);
                }
                break;
        }
    }
}
