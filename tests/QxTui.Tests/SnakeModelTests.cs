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
