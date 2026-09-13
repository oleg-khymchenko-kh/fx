namespace FXViewer.Game;

public static class GameActions
{
    public const string Market = "market";
    public const string Order = "order";
    public const string Cancel = "cancel";
    public const string Close = "close";
    public const string DayEnd = "dayend";
}

public sealed class GameAction
{
    public string Kind { get; set; } = GameActions.Market;
    public string Symbol { get; set; } = "";
    public bool Buy { get; set; }
    public long AtUnix { get; set; }
    public int Id { get; set; }
    public int Target { get; set; }
    public int Price { get; set; }
    public double StopPips { get; set; }
    public double TakePips { get; set; }

    public GameAction Clone() => (GameAction)MemberwiseClone();
}

public sealed class GameState
{
    public const double DefaultPips = 20;
    public const double MaxPips = 10000;

    public bool Playing { get; set; }
    public long StartUnix { get; set; }
    public long TimeUnix { get; set; }
    public double StopPips { get; set; } = DefaultPips;
    public double TakePips { get; set; } = DefaultPips;
    public int NextId { get; set; } = 1;
    public List<GameAction> Actions { get; set; } = new();
    public string Day { get; set; } = "";
    public long EndUnix { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public bool Finished { get; set; }
    public bool NotesOpen { get; set; }
    public bool FutureOpen { get; set; }
    public bool Replay { get; set; }
    public double ActiveSeconds { get; set; }

    public bool IsDayGame() => Day.Length > 0;

    public GameState Clone() => new()
    {
        Playing = Playing,
        StartUnix = StartUnix,
        TimeUnix = TimeUnix,
        StopPips = StopPips,
        TakePips = TakePips,
        NextId = NextId,
        Actions = Actions.Select(a => a.Clone()).ToList(),
        Day = Day,
        EndUnix = EndUnix,
        StartedAt = StartedAt,
        EndedAt = EndedAt,
        Finished = Finished,
        NotesOpen = NotesOpen,
        FutureOpen = FutureOpen,
        Replay = Replay,
        ActiveSeconds = ActiveSeconds,
    };

    public static double ClampPips(double pips) =>
        !double.IsFinite(pips) || pips <= 0 ? 0 : Math.Min(pips, MaxPips);

    public int Add(GameAction action)
    {
        action.Id = NextId++;
        int index = Actions.Count;
        while (index > 0 && Actions[index - 1].AtUnix > action.AtUnix) index--;
        Actions.Insert(index, action);
        return action.Id;
    }
}
