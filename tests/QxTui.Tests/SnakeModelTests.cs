using QxTui.Terminal;
using QxTui.Snake;

namespace QxTui.Tests;

/// <summary>Pure snake rules (sample internals via InternalsVisibleTo).</summary>
public class SnakeModelTests
{
    [Fact]
    public void StartsAlive_WithFoodOnBoard()
    {
        var m = new SnakeModel(20, 10);
        Assert.True(m.Alive);
        Assert.Equal(3, m.Body.Count);
        Assert.True(m.Body[0].X >= 0 && m.Body[0].X < 20);
        Assert.DoesNotContain(m.Food, m.Body);
    }

    [Fact]
    public void StepsForward_HeadAdvances_TailFollows()
    {
        var m = new SnakeModel(20, 10);
        var head = m.Body[0];

        m.Step();

        Assert.Equal(new Point(head.X + 1, head.Y), m.Body[0]);
        Assert.Equal(3, m.Body.Count); // no growth without food
    }

    [Fact]
    public void Reversal_IsIgnored()
    {
        var m = new SnakeModel(20, 10);
        m.Turn(Direction.Left); // moving right: reversal must be ignored
        m.Step();
        Assert.Equal(Direction.Right, m.Dir);
        Assert.True(m.Alive);
    }

    [Fact]
    public void TurnThenStep_UsesNewDirection()
    {
        var m = new SnakeModel(20, 10);
        var head = m.Body[0];
        m.Turn(Direction.Up);
        m.Step();
        Assert.Equal(new Point(head.X, head.Y - 1), m.Body[0]);
    }

    [Fact]
    public void WallKills()
    {
        var m = new SnakeModel(20, 10);
        for (int i = 0; i < 20; i++)
            m.Step(); // march right into the wall

        Assert.False(m.Alive);
        Assert.False(m.Step()); // dead stays dead
    }

    [Fact]
    public void SelfCollisionKills()
    {
        var m = new SnakeModel(20, 20);

        // grow to length 6 by placing food straight ahead
        for (int i = 0; i < 3; i++)
        {
            var h = m.Body[0];
            m.PlaceFood(new Point(h.X + 1, h.Y));
            m.Step();
        }
        Assert.Equal(6, m.Body.Count);

        // tight 1x1 loop: with length 6 the fourth turn bites the body
        m.Turn(Direction.Down);  m.Step();
        m.Turn(Direction.Right); m.Step();
        m.Turn(Direction.Up);    m.Step();
        Assert.True(m.Alive);
        m.Turn(Direction.Left);  m.Step();
        Assert.False(m.Alive);
    }

    [Fact]
    public void Eating_GrowsAndScoresAndRespawnsFood()
    {
        var m = new SnakeModel(20, 10, seed: 7);
        var head = m.Body[0];

        m.PlaceFood(new Point(head.X + 3, head.Y));
        m.Step();
        m.Step();
        m.Step();

        Assert.Equal(1, m.Score);
        Assert.Equal(4, m.Body.Count); // grew by one
        Assert.DoesNotContain(m.Food, m.Body); // respawned elsewhere
    }

    [Fact]
    public void Resize_ClampsBodyAndFood()
    {
        var m = new SnakeModel(30, 20);
        for (int i = 0; i < 10; i++) m.Step(); // head near x=20

        m.Resize(10, 8);

        Assert.All(m.Body, p =>
        {
            Assert.InRange(p.X, 0, 9);
            Assert.InRange(p.Y, 0, 7);
        });
        Assert.InRange(m.Food.X, 0, 9);
        Assert.InRange(m.Food.Y, 0, 7);
    }

    [Fact]
    public void Restart_ResetsState()
    {
        var m = new SnakeModel(20, 10);
        for (int i = 0; i < 20; i++) m.Step();
        Assert.False(m.Alive);

        m.Restart(1);

        Assert.True(m.Alive);
        Assert.Equal(0, m.Score);
        Assert.Equal(3, m.Body.Count);
    }
}

public class SnakeInputBufferTests
{
    [Fact]
    public void MashedTurns_AreQueuedNotLost()
    {
        var m = new SnakeModel(20, 20);
        var h0 = m.Body[0];

        // two turns mashed inside ONE step interval
        m.Turn(Direction.Up);
        m.Turn(Direction.Left);

        m.Step();
        Assert.Equal(Direction.Up, m.Dir);
        Assert.Equal(new Point(h0.X, h0.Y - 1), m.Body[0]);

        m.Step();
        Assert.Equal(Direction.Left, m.Dir);
        Assert.Equal(new Point(h0.X - 1, h0.Y - 1), m.Body[0]);
    }

    [Fact]
    public void QueuedReversal_IsSkippedNotFatal()
    {
        var m = new SnakeModel(20, 20);

        m.Turn(Direction.Left);  // reversal of initial Right: must be skipped
        m.Turn(Direction.Up);    // ...and this one still applies
        m.Step();

        Assert.True(m.Alive);
        Assert.Equal(Direction.Up, m.Dir);
    }

    [Fact]
    public void QueueIsBounded_SpamDoesNotAccumulate()
    {
        var m = new SnakeModel(20, 20);
        for (int i = 0; i < 50; i++)
            m.Turn(i % 2 == 0 ? Direction.Up : Direction.Down);

        // at most MaxQueuedTurns survive: after enough steps the snake must
        // be heading in a stable direction, not oscillating for 50 steps
        for (int i = 0; i < 5; i++) m.Step();
        Assert.True(m.Alive);
    }

    [Fact]
    public void SameDirectionSpam_IsHarmless()
    {
        var m = new SnakeModel(20, 20);
        m.Turn(Direction.Right);
        m.Turn(Direction.Right);
        m.Turn(Direction.Right);
        m.Step();
        Assert.Equal(Direction.Right, m.Dir);
        Assert.True(m.Alive);
    }
}

public class InputMapTests
{
    private static Direction? Map(char c) =>
        QxTui.Snake.InputMap.ToDirection(KeyEvent.OfChar(c));

    [Theory]
    [InlineData('h', Direction.Left)]
    [InlineData('j', Direction.Down)]
    [InlineData('k', Direction.Up)]
    [InlineData('l', Direction.Right)]
    [InlineData('H', Direction.Left)]
    [InlineData('J', Direction.Down)]
    [InlineData('K', Direction.Up)]
    [InlineData('L', Direction.Right)]
    public void VimKeys_MapLikeVi(char c, Direction expected) =>
        Assert.Equal(expected, Map(c));

    [Theory]
    [InlineData('w', Direction.Up)]
    [InlineData('a', Direction.Left)]
    [InlineData('s', Direction.Down)]
    [InlineData('d', Direction.Right)]
    public void Wasd_StillWorks(char c, Direction expected) =>
        Assert.Equal(expected, Map(c));

    [Fact]
    public void Arrows_StillWork()
    {
        Assert.Equal(Direction.Up, QxTui.Snake.InputMap.ToDirection(KeyEvent.Of(QxTui.Terminal.Key.Up)));
        Assert.Equal(Direction.Down, QxTui.Snake.InputMap.ToDirection(KeyEvent.Of(QxTui.Terminal.Key.Down)));
        Assert.Equal(Direction.Left, QxTui.Snake.InputMap.ToDirection(KeyEvent.Of(QxTui.Terminal.Key.Left)));
        Assert.Equal(Direction.Right, QxTui.Snake.InputMap.ToDirection(KeyEvent.Of(QxTui.Terminal.Key.Right)));
    }

    [Fact]
    public void NonMovementKeys_AreNull()
    {
        Assert.Null(Map('q'));
        Assert.Null(Map(' '));
        Assert.Null(QxTui.Snake.InputMap.ToDirection(KeyEvent.Of(QxTui.Terminal.Key.Space)));
        Assert.Null(QxTui.Snake.InputMap.ToDirection(KeyEvent.Of(QxTui.Terminal.Key.Enter)));
    }
}
