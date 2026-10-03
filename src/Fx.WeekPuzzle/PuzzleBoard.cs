namespace Fx.WeekPuzzle;

internal readonly record struct WeekCandle(double Open, double High, double Low, double Close)
{
    public double Body => Close - Open;

    public double Range => High - Low;
}

internal sealed class PuzzleCard
{
    public PuzzleCard(int index, double width, double height, double leftTick, double rightTick, WeekCandle candle = default)
    {
        Index = index;
        Width = width;
        Height = height;
        LeftTick = leftTick;
        RightTick = rightTick;
        Candle = candle;
    }

    public int Index { get; }
    public double Width { get; }
    public double Height { get; }
    public double LeftTick { get; }
    public double RightTick { get; }
    public WeekCandle Candle { get; }
    public bool Loose => Left is null && Right is null;
    public double X { get; set; }
    public double Y { get; set; }
    public PuzzleCard? Left { get; internal set; }
    public PuzzleCard? Right { get; internal set; }

    public PuzzleCard Head
    {
        get
        {
            var c = this;
            while (c.Left is not null) c = c.Left;
            return c;
        }
    }

    public PuzzleCard Tail
    {
        get
        {
            var c = this;
            while (c.Right is not null) c = c.Right;
            return c;
        }
    }

    public List<PuzzleCard> Chain()
    {
        var chain = new List<PuzzleCard>();
        for (var c = Head; c is not null; c = c.Right) chain.Add(c);
        return chain;
    }
}

internal sealed record PuzzleLink(PuzzleCard Left, PuzzleCard Right)
{
    public bool Correct => Right.Index == Left.Index + 1;
}

internal readonly record struct WorldRect(double X, double Y, double Width, double Height);

internal sealed class PuzzleBoard
{
    private readonly List<PuzzleCard> _cards;

    public PuzzleBoard(IEnumerable<PuzzleCard> cards)
    {
        _cards = cards.ToList();
    }

    public IReadOnlyList<PuzzleCard> Cards => _cards;

    public IEnumerable<PuzzleLink> Links =>
        _cards.Where(c => c.Right is not null).Select(c => new PuzzleLink(c, c.Right!));

    public int CorrectLinks => Links.Count(l => l.Correct);

    public int ChainCount => _cards.Count(c => c.Left is null);

    public bool Solved => _cards.Count > 0 && CorrectLinks == _cards.Count - 1;

    public WorldRect Bounds => BoundsOf(_cards);

    private static WorldRect BoundsOf(IReadOnlyList<PuzzleCard> cards)
    {
        if (cards.Count == 0) return new WorldRect(0, 0, 1, 1);
        double x0 = cards.Min(c => c.X);
        double y0 = cards.Min(c => c.Y);
        double x1 = cards.Max(c => c.X + c.Width);
        double y1 = cards.Max(c => c.Y + c.Height);
        return new WorldRect(x0, y0, x1 - x0, y1 - y0);
    }

    public void Sort(double bigMove, double gap, int rowCards)
    {
        var loose = _cards.Where(c => c.Loose).ToList();
        if (loose.Count == 0) return;
        var connected = _cards.Where(c => !c.Loose).ToList();
        double x0 = 0, y = 0;
        if (connected.Count > 0)
        {
            var b = BoundsOf(connected);
            x0 = b.X;
            y = b.Y + b.Height + 4 * gap;
        }
        var groups = new List<IEnumerable<PuzzleCard>>
        {
            loose.Where(c => c.Candle.Body >= bigMove).OrderByDescending(c => c.Candle.Body),
            loose.Where(c => Math.Abs(c.Candle.Body) < bigMove).OrderByDescending(c => c.Candle.Range),
            loose.Where(c => c.Candle.Body <= -bigMove).OrderBy(c => c.Candle.Body),
        };
        foreach (var group in groups)
        {
            int n = 0;
            double x = x0, rowHeight = 0;
            foreach (var card in group)
            {
                if (n > 0 && n % rowCards == 0)
                {
                    y += rowHeight + gap;
                    x = x0;
                    rowHeight = 0;
                }
                card.X = x;
                card.Y = y;
                x += card.Width + gap;
                rowHeight = Math.Max(rowHeight, card.Height);
                n++;
            }
            if (n > 0) y += rowHeight + 4 * gap;
        }
    }

    public void Scatter(Random random, double gap)
    {
        int count = _cards.Count;
        if (count == 0) return;
        double cellWidth = _cards.Max(c => c.Width) + 2 * gap;
        double cellHeight = _cards.Max(c => c.Height) + 2 * gap;
        int columns = Math.Max(1, (int)Math.Round(Math.Sqrt(1.7 * count * cellHeight / cellWidth)));
        var order = Enumerable.Range(0, count).OrderBy(_ => random.Next()).ToList();
        for (int k = 0; k < count; k++)
        {
            var card = _cards[order[k]];
            card.Left = null;
            card.Right = null;
            card.X = k % columns * cellWidth + gap / 2 + random.NextDouble() * (cellWidth - gap - card.Width);
            card.Y = k / columns * cellHeight + gap / 2 + random.NextDouble() * (cellHeight - gap - card.Height);
        }
    }

    public PuzzleCard? HintFor(PuzzleCard card)
    {
        if (card.Right is null && card.Index + 1 < _cards.Count) return _cards[card.Index + 1];
        if (card.Left is null && card.Index > 0) return _cards[card.Index - 1];
        return null;
    }

    public void MoveChain(PuzzleCard card, double dx, double dy)
    {
        foreach (var c in card.Chain())
        {
            c.X += dx;
            c.Y += dy;
        }
    }

    public void Detach(PuzzleCard card)
    {
        if (card.Left is not null) card.Left.Right = null;
        if (card.Right is not null) card.Right.Left = null;
        card.Left = null;
        card.Right = null;
    }

    public PuzzleLink? TrySnap(PuzzleCard card, double tolerance)
    {
        var chain = card.Chain();
        var head = chain[0];
        var tail = chain[^1];
        PuzzleLink? best = null;
        double bestDistance = tolerance;
        double bestDx = 0, bestDy = 0;
        foreach (var other in _cards)
        {
            if (chain.Contains(other)) continue;
            if (other.Right is null)
            {
                double dx = other.X + other.Width - head.X;
                double dy = other.Y + other.RightTick - (head.Y + head.LeftTick);
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = new PuzzleLink(other, head);
                    bestDx = dx;
                    bestDy = dy;
                }
            }
            if (other.Left is null)
            {
                double dx = other.X - (tail.X + tail.Width);
                double dy = other.Y + other.LeftTick - (tail.Y + tail.RightTick);
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = new PuzzleLink(tail, other);
                    bestDx = dx;
                    bestDy = dy;
                }
            }
        }
        if (best is null) return null;
        MoveChain(card, bestDx, bestDy);
        best.Left.Right = best.Right;
        best.Right.Left = best.Left;
        return best;
    }
}
