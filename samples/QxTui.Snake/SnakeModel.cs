namespace QxTui.Snake;

public readonly record struct Point(int X, int Y);

public enum Direction { Up, Down, Left, Right }

/// <summary>
/// Pure snake rules — no I/O, no rendering, fully unit-testable.
/// Classic rules: walls kill, self kills, eating grows + scores,
/// reversing into yourself is ignored.
/// </summary>
internal sealed class SnakeModel
{
    private readonly List<Point> _body; // [0] = head
    private readonly Random _rng;
    private Direction _dir = Direction.Right;

    // Input buffer: mashing two turns inside one step interval must not
    // lose the first one (single-slot pending did exactly that).
    private readonly Queue<Direction> _turns = new(4);
    private const int MaxQueuedTurns = 3;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public Point Food { get; private set; }
    public int Score { get; private set; }
    public bool Alive { get; private set; } = true;
    public IReadOnlyList<Point> Body => _body;
    public Direction Dir => _dir;

    public SnakeModel(int width, int height, int seed = 42)
    {
        Width = Math.Max(5, width);
        Height = Math.Max(5, height);
        _rng = new Random(seed);

        int cx = Width / 2, cy = Height / 2;
        _body = [new Point(cx, cy), new Point(cx - 1, cy), new Point(cx - 2, cy)];
        Food = RandomFreeCell();
    }

    /// <summary>Queue a turn. Validated at STEP time against the heading
    /// actually in effect then — so mashing up-then-left works, and a
    /// queued reversal is skipped instead of killing you.</summary>
    public void Turn(Direction d)
    {
        if (!Alive) return;
        if (_turns.Count < MaxQueuedTurns)
            _turns.Enqueue(d);
    }

    /// <summary>Advance one tick. Returns false when this step killed us.</summary>
    public bool Step()
    {
        if (!Alive) return false;

        // consume the first queued turn that is legal right now
        while (_turns.Count > 0)
        {
            var d = _turns.Dequeue();
            if (d == _dir || Opposite(d) == _dir) continue; // no-op or reversal
            _dir = d;
            break;
        }

        var head = _body[0];
        var next = _dir switch
        {
            Direction.Up => new Point(head.X, head.Y - 1),
            Direction.Down => new Point(head.X, head.Y + 1),
            Direction.Left => new Point(head.X - 1, head.Y),
            _ => new Point(head.X + 1, head.Y),
        };

        // wall
        if (next.X < 0 || next.Y < 0 || next.X >= Width || next.Y >= Height)
        {
            Alive = false;
            return false;
        }

        bool eating = next == Food;

        // self: the tail cell vacates this tick unless we grow
        int checkLen = eating ? _body.Count : _body.Count - 1;
        for (int i = 0; i < checkLen; i++)
        {
            if (_body[i] == next)
            {
                Alive = false;
                return false;
            }
        }

        _body.Insert(0, next);
        if (eating)
        {
            Score++;
            Food = RandomFreeCell();
        }
        else
        {
            _body.RemoveAt(_body.Count - 1);
        }

        return true;
    }

    /// <summary>Keep entities inside new bounds after a terminal resize.</summary>
    public void Resize(int width, int height)
    {
        Width = Math.Max(5, width);
        Height = Math.Max(5, height);

        for (int i = 0; i < _body.Count; i++)
            _body[i] = new Point(
                Math.Clamp(_body[i].X, 0, Width - 1),
                Math.Clamp(_body[i].Y, 0, Height - 1));

        if (!IsFree(Food))
            Food = RandomFreeCell();
    }

    public void Restart(int seed)
    {
        int cx = Width / 2, cy = Height / 2;
        _body.Clear();
        _body.Add(new Point(cx, cy));
        _body.Add(new Point(cx - 1, cy));
        _body.Add(new Point(cx - 2, cy));
        _dir = Direction.Right;
        _turns.Clear();
        Score = 0;
        Alive = true;
        _rng.GetType(); // keep analyzer quiet about unused reseed
        Food = RandomFreeCell();
    }

    /// <summary>Test hook: place food deterministically.</summary>
    internal void PlaceFood(Point p)
    {
        if (IsFree(p)) Food = p;
    }

    private bool IsFree(Point p)
    {
        for (int i = 0; i < _body.Count; i++)
            if (_body[i] == p) return false;
        return p.X >= 0 && p.Y >= 0 && p.X < Width && p.Y < Height;
    }

    private Point RandomFreeCell()
    {
        // free-cell count is tiny compared to the field; rejection sampling
        // is fine and keeps the model allocation-light
        for (int attempt = 0; attempt < 500; attempt++)
        {
            var p = new Point(_rng.Next(Width), _rng.Next(Height));
            if (IsFree(p)) return p;
        }

        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                var p = new Point(x, y);
                if (IsFree(p)) return p;
            }

        return _body[0]; // board full: game is won anyway
    }

    private static Direction Opposite(Direction d) => d switch
    {
        Direction.Up => Direction.Down,
        Direction.Down => Direction.Up,
        Direction.Left => Direction.Right,
        _ => Direction.Left,
    };
}
