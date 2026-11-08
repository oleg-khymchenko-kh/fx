namespace Fx.WeekPuzzle;

internal sealed class DayCard
{
    public DayCard(int index, DayShape shape)
    {
        Index = index;
        Shape = shape;
    }

    public int Index { get; }
    public DayShape Shape { get; }
    public string Id => Shape.Id;
    public double X { get; set; }
    public double Y { get; set; }
    public DayGroup? Group { get; internal set; }
    public bool Mirrored { get; set; }
    public WorldRect Rect => new(X, Y, Shape.Width, Shape.Height);
}

[Flags]
internal enum FrameEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

internal sealed class DayGroup
{
    public DayGroup(string name, string color)
    {
        Name = name;
        Color = color;
    }

    public string Name { get; internal set; }
    public string Color { get; }
    public List<DayCard> Cards { get; } = new();
    public List<string> Absent { get; } = new();
    public WorldRect? Frame { get; internal set; }
}

internal sealed record DayFrame(DayGroup Group, WorldRect Rect, int Count)
{
    public string Title => $"{Group.Name} ({Count})";
}

internal sealed record DayGroupState(string Name, string Color, List<string> Cards, double[]? Frame = null);

internal sealed record DaySnapshot(Dictionary<DayGroup, (List<DayCard> Cards, WorldRect? Frame)> Groups, HashSet<DayCard> Loose);

internal sealed class DayBoard
{
    public const double CardGap = 12 * DayCards.Scale;
    public const double BlockPad = 14 * DayCards.Scale;
    public const double BlockHeader = 44 * DayCards.Scale;
    public const double BlockGap = 80 * DayCards.Scale;
    public const double MinFrameWidth = 2 * BlockPad + 60 * DayCards.Scale;
    public const double MinFrameHeight = BlockHeader + BlockPad + 20 * DayCards.Scale;
    private const double BlockAspect = 1.5;
    private static readonly string[] Palette =
    {
        "#4FC3F7", "#FFB74D", "#81C784", "#F06292", "#BA68C8", "#FFD54F",
        "#4DB6AC", "#E57373", "#9575CD", "#AED581", "#64B5F6", "#FF8A65",
    };

    private readonly List<DayCard> _cards;
    private readonly Dictionary<string, DayCard> _byId;
    private readonly List<DayGroup> _groups = new();

    public DayBoard(IReadOnlyList<DayShape> shapes)
    {
        _cards = shapes.Select((s, i) => new DayCard(i, s)).ToList();
        _byId = _cards.ToDictionary(c => c.Id);
    }

    public IReadOnlyList<DayCard> Cards => _cards;

    public IReadOnlyList<DayGroup> Groups => _groups;

    public IEnumerable<DayCard> Loose => _cards.Where(c => c.Group is null);

    public WorldRect Bounds =>
        BoundsOf(_cards.Select(c => c.Rect).Concat(Frames().Select(f => f.Rect)).ToList());

    public DayCard? Find(string id) => _byId.GetValueOrDefault(id);

    public DayGroup? FindGroup(string name) =>
        _groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    public List<DayFrame> Frames(DayCard? except = null)
    {
        var frames = new List<DayFrame>();
        foreach (var group in _groups)
        {
            var cards = group.Cards.Where(c => c != except).ToList();
            if (FrameOf(group, cards) is { } rect) frames.Add(new DayFrame(group, rect, cards.Count));
        }
        return frames;
    }

    public DayFrame? FrameAt(double x, double y, DayCard? except = null) =>
        Frames(except).LastOrDefault(f => Contains(f.Rect, x, y));

    public WorldRect? FrameOf(DayGroup group) => FrameOf(group, group.Cards);

    private static WorldRect? FrameOf(DayGroup group, IReadOnlyList<DayCard> cards)
    {
        if (cards.Count == 0) return group.Frame;
        var fit = FrameOf(cards);
        return group.Frame is { } set ? BoundsOf(new[] { set, fit }) : fit;
    }

    public static WorldRect FrameOf(IEnumerable<DayCard> cards)
    {
        var b = BoundsOf(cards.Select(c => c.Rect).ToList());
        return new WorldRect(b.X - BlockPad, b.Y - BlockHeader, b.Width + 2 * BlockPad, b.Height + BlockHeader + BlockPad);
    }

    public static bool Contains(WorldRect r, double x, double y) =>
        x >= r.X && x <= r.X + r.Width && y >= r.Y && y <= r.Y + r.Height;

    public static WorldRect BoundsOf(IReadOnlyList<WorldRect> rects)
    {
        if (rects.Count == 0) return new WorldRect(0, 0, 1, 1);
        double x0 = rects.Min(r => r.X);
        double y0 = rects.Min(r => r.Y);
        double x1 = rects.Max(r => r.X + r.Width);
        double y1 = rects.Max(r => r.Y + r.Height);
        return new WorldRect(x0, y0, x1 - x0, y1 - y0);
    }

    public DayGroup Group(string name, IEnumerable<DayCard> cards, bool replace)
    {
        var group = FindGroup(name) ?? Create(name, null);
        var list = cards.ToList();
        if (replace)
        {
            foreach (var card in group.Cards) card.Group = null;
            group.Cards.Clear();
        }
        foreach (var card in list) Move(card, group);
        Prune();
        return group;
    }

    public DayGroup NewGroup(IEnumerable<DayCard> cards)
    {
        int n = _groups.Count + 1;
        while (FindGroup($"Group {n}") is not null) n++;
        return Group($"Group {n}", cards, false);
    }

    public void Ungroup(IEnumerable<DayCard> cards)
    {
        foreach (var card in cards.ToList()) Remove(card);
        Prune();
    }

    public void Rename(DayGroup group, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("group name is empty");
        if (FindGroup(name) is { } other && other != group) throw new ArgumentException($"group {name} already exists");
        group.Name = name;
    }

    public void Delete(DayGroup group)
    {
        foreach (var card in group.Cards) card.Group = null;
        _groups.Remove(group);
    }

    public void Clear()
    {
        foreach (var card in _cards) card.Group = null;
        _groups.Clear();
    }

    public void ReplaceAll(IEnumerable<(string Name, IReadOnlyList<DayCard> Cards)> groups)
    {
        Clear();
        foreach (var (name, cards) in groups) Group(name, cards, false);
    }

    public void Order(IEnumerable<string> names)
    {
        var first = names.Select(FindGroup).OfType<DayGroup>().Distinct().ToList();
        var rest = _groups.Where(g => !first.Contains(g)).ToList();
        _groups.Clear();
        _groups.AddRange(first);
        _groups.AddRange(rest);
    }

    public void MoveGroup(DayGroup group, double dx, double dy)
    {
        foreach (var card in group.Cards)
        {
            card.X += dx;
            card.Y += dy;
        }
        ShiftFrame(group, dx, dy);
    }

    public void ShiftFrame(DayGroup group, double dx, double dy)
    {
        if (group.Frame is { } f) group.Frame = f with { X = f.X + dx, Y = f.Y + dy };
    }

    public void SetFrame(DayGroup group, WorldRect rect) =>
        group.Frame = rect with { Width = Math.Max(rect.Width, MinFrameWidth), Height = Math.Max(rect.Height, MinFrameHeight) };

    public void FitFrame(DayGroup group) => group.Frame = null;

    public static WorldRect Resize(WorldRect start, FrameEdges edges, double dx, double dy)
    {
        double x0 = start.X, y0 = start.Y, x1 = start.X + start.Width, y1 = start.Y + start.Height;
        if (edges.HasFlag(FrameEdges.Left)) x0 = Math.Min(x0 + dx, x1 - MinFrameWidth);
        if (edges.HasFlag(FrameEdges.Right)) x1 = Math.Max(x1 + dx, x0 + MinFrameWidth);
        if (edges.HasFlag(FrameEdges.Top)) y0 = Math.Min(y0 + dy, y1 - MinFrameHeight);
        if (edges.HasFlag(FrameEdges.Bottom)) y1 = Math.Max(y1 + dy, y0 + MinFrameHeight);
        return new WorldRect(x0, y0, x1 - x0, y1 - y0);
    }

    public void Mirror(IEnumerable<DayCard> cards, bool mirrored)
    {
        foreach (var card in cards) card.Mirrored = mirrored;
    }

    public List<DayGroupState> Export() =>
        _groups.Select(g => new DayGroupState(g.Name, g.Color, g.Cards.Select(c => c.Id).Concat(g.Absent).ToList(),
            g.Frame is { } f ? new[] { Math.Round(f.X, 1), Math.Round(f.Y, 1), Math.Round(f.Width, 1), Math.Round(f.Height, 1) } : null)).ToList();

    public void Import(IEnumerable<DayGroupState> groups)
    {
        foreach (var state in groups)
        {
            if (string.IsNullOrWhiteSpace(state.Name) || FindGroup(state.Name) is not null) continue;
            var group = Create(state.Name, state.Color);
            if (state.Frame is [var x, var y, var w, var h] && new[] { x, y, w, h }.All(double.IsFinite) && w > 0 && h > 0)
                SetFrame(group, new WorldRect(x, y, w, h));
            foreach (string id in state.Cards ?? new List<string>())
            {
                if (Find(id) is { } card)
                {
                    if (card.Group is null) Move(card, group);
                }
                else if (!group.Absent.Contains(id))
                {
                    group.Absent.Add(id);
                }
            }
        }
        Prune();
    }

    public List<DayCard> ApplyPositions(IReadOnlyDictionary<string, double[]> positions)
    {
        var missing = new List<DayCard>();
        foreach (var card in _cards)
        {
            if (positions.TryGetValue(card.Id, out var p) && p.Length == 2 && double.IsFinite(p[0]) && double.IsFinite(p[1]))
            {
                card.X = p[0];
                card.Y = p[1];
            }
            else
            {
                missing.Add(card);
            }
        }
        return missing;
    }

    public DaySnapshot Snapshot() => new(
        _groups.ToDictionary(g => g, g => (g.Cards.ToList(), FrameOf(g))),
        Loose.ToHashSet());

    public void Tidy(DaySnapshot before)
    {
        foreach (var group in _groups)
            if (before.Groups.TryGetValue(group, out var old) && old.Frame is { } frame && !old.Cards.SequenceEqual(group.Cards))
                ArrangeGroup(group, frame.X, frame.Y);
        foreach (var group in _groups)
            if (!before.Groups.TryGetValue(group, out var old) || old.Frame is null)
            {
                var spot = FreeSpot(group.Cards);
                ArrangeGroup(group, spot.X, spot.Y);
            }
        var freed = Loose.Where(c => !before.Loose.Contains(c)).ToList();
        if (freed.Count > 0) PlaceLoose(freed);
    }

    public void Arrange()
    {
        foreach (var group in _groups) group.Frame = null;
        var parts = _groups.Select(g => g.Cards).ToList();
        var loose = Loose.ToList();
        if (loose.Count > 0) parts.Add(loose);
        var (cardWidth, avgHeight) = CardSize();
        var sizes = parts.Select(p => Measure(p, Columns(p.Count, cardWidth, avgHeight), cardWidth)).ToList();
        double area = sizes.Sum(s => (s.Width + BlockGap) * (s.Height + BlockGap));
        double maxWidth = Math.Max(sizes.Count > 0 ? sizes.Max(s => s.Width) : 0, Math.Sqrt(area) * BlockAspect);
        double x = 0, y = 0, shelf = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var (width, height) = sizes[i];
            if (x > 0 && x + width > maxWidth)
            {
                x = 0;
                y += shelf + BlockGap;
                shelf = 0;
            }
            Place(parts[i], Columns(parts[i].Count, cardWidth, avgHeight), cardWidth, x + BlockPad, y + BlockHeader);
            x += width + BlockGap;
            shelf = Math.Max(shelf, height);
        }
    }

    public void ArrangeGroup(DayGroup group, double frameX, double frameY)
    {
        var (cardWidth, avgHeight) = CardSize();
        if (group.Frame is { } set && FrameOf(group) is { } shown)
        {
            int fit = (int)Math.Floor((shown.Width - 2 * BlockPad + CardGap) / (cardWidth + CardGap));
            Place(group.Cards, Math.Clamp(fit, 1, Math.Max(1, group.Cards.Count)), cardWidth, frameX + BlockPad, frameY + BlockHeader);
            SetFrame(group, new WorldRect(frameX, frameY, shown.Width, set.Height));
            return;
        }
        Place(group.Cards, Columns(group.Cards.Count, cardWidth, avgHeight), cardWidth, frameX + BlockPad, frameY + BlockHeader);
    }

    public void PlaceLoose(IReadOnlyList<DayCard> cards)
    {
        var spot = FreeSpot(cards);
        var (cardWidth, avgHeight) = CardSize();
        Place(cards, Columns(cards.Count, cardWidth, avgHeight), cardWidth, spot.X + BlockPad, spot.Y + BlockHeader);
    }

    private WorldRect FreeSpot(IReadOnlyCollection<DayCard> moving)
    {
        var rest = _cards.Where(c => !moving.Contains(c)).ToList();
        if (rest.Count == 0) return new WorldRect(0, 0, 0, 0);
        var rects = rest.Select(c => c.Rect)
            .Concat(_groups.Select(g => FrameOf(g, g.Cards.Where(c => !moving.Contains(c)).ToList()))
                .Where(r => r.HasValue).Select(r => r!.Value))
            .ToList();
        var b = BoundsOf(rects);
        return new WorldRect(b.X, b.Y + b.Height + BlockGap, 0, 0);
    }

    private (double Width, double AvgHeight) CardSize()
    {
        double width = _cards.Count > 0 ? _cards.Max(c => c.Shape.Width) : 24 * DayCards.PxPerHour;
        return (width, _cards.Count > 0 ? _cards.Average(c => c.Shape.Height) : width);
    }

    private static int Columns(int count, double cardWidth, double avgHeight) =>
        Math.Clamp((int)Math.Ceiling(Math.Sqrt(count * BlockAspect * avgHeight / cardWidth)), 1, Math.Max(1, count));

    private static (double Width, double Height) Measure(IReadOnlyList<DayCard> cards, int columns, double cardWidth)
    {
        double rows = 0;
        for (int r = 0; r * columns < cards.Count; r++)
            rows += cards.Skip(r * columns).Take(columns).Max(c => c.Shape.Height) + CardGap;
        if (rows > 0) rows -= CardGap;
        return (2 * BlockPad + columns * cardWidth + (columns - 1) * CardGap, BlockHeader + rows + BlockPad);
    }

    private static void Place(IReadOnlyList<DayCard> cards, int columns, double cardWidth, double x0, double y0)
    {
        double y = y0;
        for (int r = 0; r * columns < cards.Count; r++)
        {
            var row = cards.Skip(r * columns).Take(columns).ToList();
            for (int k = 0; k < row.Count; k++)
            {
                row[k].X = x0 + k * (cardWidth + CardGap);
                row[k].Y = y;
            }
            y += row.Max(c => c.Shape.Height) + CardGap;
        }
    }

    private DayGroup Create(string name, string? color)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("group name is empty");
        color ??= Palette.FirstOrDefault(p => _groups.All(g => g.Color != p)) ?? Palette[_groups.Count % Palette.Length];
        var group = new DayGroup(name, color);
        _groups.Add(group);
        return group;
    }

    private static void Move(DayCard card, DayGroup group)
    {
        if (card.Group == group) return;
        card.Group?.Cards.Remove(card);
        group.Cards.Add(card);
        card.Group = group;
    }

    private static void Remove(DayCard card)
    {
        card.Group?.Cards.Remove(card);
        card.Group = null;
    }

    private void Prune() => _groups.RemoveAll(g => g.Cards.Count == 0 && g.Absent.Count == 0);
}
