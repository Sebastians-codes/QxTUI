using QxTui.Core;
using QxTui.Layout;
using QxTui.Render;
using QxTui.Terminal;

namespace QxTui.Widgets;

/// <summary>One-line text prompt as an overlay (commit message, new
/// branch name, worktree spec). Enter submits and closes, Esc cancels.
/// The hosted TextField gets keys via delegation — overlays receive
/// keys before anything else — and takes app focus at mount so the
/// field's own Focused gate passes too.</summary>
public sealed class Prompt : Widget
{
    private readonly TextField _field;
    private readonly Action<string> _submit;
    private readonly string _title;

    public Prompt(string title, string placeholder, Action<string> submit)
    {
        _title = title;
        _submit = submit;
        _field = new TextField { Placeholder = placeholder };
        AddChild(_field);
    }

    private Rect BoxRect(Rect screen)
    {
        int w = Math.Min(screen.W - 8, Math.Max(40, _title.Length + 10));
        return new Rect((screen.W - w) / 2, screen.H / 2 - 2, w, 3);
    }

    public override bool HitTest(int x, int y) => BoxRect(Rect).Contains(x, y);
    internal override bool OwnsPoint(int x, int y) => BoxRect(Rect).Contains(x, y);

    /// <summary>The field owns the keyboard while the prompt is open.</summary>
    public override void OnMount(App app) => app.Focus(_field);

    public override bool HandleKey(KeyEvent ev)
    {
        if (ev.Key == Key.Escape)
        {
            App?.PopOverlay(this);
            return true;
        }
        if (ev.Key == Key.Enter)
        {
            string text = _field.Text;
            App?.PopOverlay(this);
            if (text.Trim().Length > 0) _submit(text);
            return true;
        }
        return _field.HandleKey(ev); // typing, cursor moves, backspace…
    }

    public override void Render(CellBuffer buffer)
    {
        var b = BoxRect(Rect);
        buffer.FillRect(b.X, b.Y, b.W, b.H,
            new Cell { Char = ' ', Fg = Theme.Get(ThemeRole.Text), Bg = Theme.Get(ThemeRole.PanelBg) });
        Box.Draw(buffer, b.X, b.Y, b.W, b.H, BoxStyles.Single,
            Theme.Get(ThemeRole.Border), _title, Theme.Get(ThemeRole.Title));
        var inner = new Rect(b.X + 2, b.Y + 1, b.W - 4, 1);
        if (!_field.Rect.Equals(inner))
        {
            _field.Node.SetRect(inner);
            _field.Invalidate();
        }
    }

    internal override void CollectPaintedRects(List<Rect> dst) => dst.Add(BoxRect(Rect));
}
