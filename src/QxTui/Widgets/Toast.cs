using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;

namespace QxTui.Widgets;

/// <summary>Semantic toast kind: owns a FULL contrast-checked color pair
/// for the whole box. Kinds must read identically in every theme, so no
/// theme-role indirection here — roles carry no contrast guarantee, which
/// is exactly how unreadable toasts happened.</summary>
public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Transient notification: a small floating box that pops itself
/// after a TTL. Clicks pass straight through (HitTest false). Open one via
/// <see cref="ToastExtensions.Toast"/> (single slot: a new toast replaces
/// the old).</summary>
public sealed class Toast : Widget
{
    private readonly string _text;
    private readonly ToastKind _kind;
    private readonly BoxStyle? _frame;
    private readonly bool _desktop;
    private readonly string? _desktopTitle;

    public Toast(string text, ToastKind kind = ToastKind.Info,
        int ms = 2500, BoxStyle? frame = null, bool desktop = false,
        string? desktopTitle = null)
    {
        _text = text;
        _kind = kind;
        _frame = frame;
        _desktop = desktop;
        _desktopTitle = desktopTitle;
        Tick(TimeSpan.FromMilliseconds(ms), _ => App?.PopOverlay(this));
    }

    /// <summary>OPT-IN desktop mirror: when the flag (or the app-wide
    /// <see cref="App.DesktopNotifications"/>) is on, showing the toast
    /// also fires a real OS notification — for results that arrive while
    /// the user watches another window.</summary>
    public override void OnMount(App app)
    {
        if (_desktop || app.DesktopNotifications)
            DesktopNotify.Send(_desktopTitle ?? app.DesktopNotifyTitle, _text);
    }

    /// <summary>(background, ink) pairs, chosen for contrast: near-black
    /// ink on bright fills, white ink on the saturated error red.</summary>
    public static (int Bg, int Fg) Colors(ToastKind kind) => kind switch
    {
        ToastKind.Success => (100, 232),
        ToastKind.Warning => (214, 232),
        ToastKind.Error => (196, 255),
        _ => (33, 255),
    };

    public override bool HitTest(int x, int y) => false; // never eats clicks

    /// <summary>Never owns a press: a toast must not shield opt-in
    /// modals below it from outside-press dismissal.</summary>
    internal override bool OwnsPoint(int x, int y) => false;

    private Rect Box(Rect screen)
    {
        int w = Math.Min(screen.W - 2, _text.Length + 4);
        int h = 3;
        int x = Math.Clamp((screen.W - w) / 2, 0, Math.Max(0, screen.W - w));
        int y = Math.Max(0, screen.H - h - 1);
        return new Rect(x, y, w, h);
    }

    public override void Render(CellBuffer buffer)
    {
        var b = Box(Rect);
        var (bg, fg) = Colors(_kind);
        if (_frame is BoxStyle style)
        {
            // framed variant: dark fill, border in the kind's saturated
            // color, ink in its contrast pair — rounding is the caller's
            // call (Rounded / Single / Double)
            buffer.FillRect(b.X, b.Y, b.W, b.H,
                new Cell { Char = ' ', Fg = fg, Bg = 236 });
            QxTui.Render.Box.Draw(buffer, b.X, b.Y, b.W, b.H, style, bg, "", bg);
            buffer.WriteTextClipped(Clip, b.X + 2, b.Y + 1,
                QxTui.Render.Text.Ellipsize(_text, b.W - 4),
                fg: fg, bg: 236, bold: true);
        }
        else
        {
            // filled variant: the kind's full color pair, edge to edge —
            // text cells MUST carry the kind bg too, or the default-bg
            // ink punches unreadable holes in the fill
            buffer.FillRect(b.X, b.Y, b.W, b.H,
                new Cell { Char = ' ', Fg = fg, Bg = bg });
            buffer.WriteTextClipped(Clip, b.X + 2, b.Y + 1,
                QxTui.Render.Text.Ellipsize(_text, b.W - 4),
                fg: fg, bg: bg, bold: true);
        }
    }

    internal override void CollectPaintedRects(List<Rect> dst) => dst.Add(Box(Rect));
}

/// <summary>App-level convenience: transient notifications without the
/// caller managing overlay lifetimes.</summary>
public static class ToastExtensions
{
    /// <summary>Show a toast (single slot: replaces any current toast).
    /// Clicks pass through it; it pops itself after <paramref name="ms"/>.
    /// <paramref name="frame"/> switches the filled look for a framed one
    /// (BoxStyles.Rounded for rounded corners, etc).</summary>
    public static void Toast(this App app, string text,
        ToastKind kind = ToastKind.Info, int ms = 2500, BoxStyle? frame = null,
        bool desktop = false, string? desktopTitle = null)
    {
        for (int i = app.OverlayCount - 1; i >= 0; i--)
            if (app.OverlayAt(i) is Toast t)
                app.PopOverlay(t);
        app.PushOverlay(new Toast(text, kind, ms, frame, desktop, desktopTitle));
    }
}
