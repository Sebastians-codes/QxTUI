using QxTui.Core;
using QxTui.Layout;
using QxTui.Widgets;
using Xunit;

namespace QxTui.Tests;

public class CollapseProbe
{
    [Fact]
    public void CollapsedSubtree_DescendantRectsGoZero()
    {
        var label = new Label(() => "tick", ThemeRole.Text);
        label.Node.Height = 1;
        var viewA = new Container();
        viewA.AddChild(label);
        var viewB = new Container();
        var other = new Label(() => "b", ThemeRole.Text);
        viewB.AddChild(other);
        var stack = new Container();
        stack.AddChild(viewA);
        stack.AddChild(viewB);
        var root = new LayoutNode { Direction = FlexDirection.Column };
        root.Add(stack);
        var term = new FakeTerminal(40, 12);
        var app = new App(term, root, targetFps: 60, asyncPresenting: false);
        stack.Node.Grow = 1f; viewA.Node.Grow = 1f; viewB.Node.Grow = 1f;
        app.RunFrame();
        Assert.True(label.Rect.H > 0);
        viewA.Collapsed = true;
        app.RunFrame();
        Assert.True(label.Rect.W == 0 && label.Rect.H == 0);
    }
}
