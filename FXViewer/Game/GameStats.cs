using System.Globalization;
using FXViewer.Chart;

namespace FXViewer.Game;

public sealed record GamePlayCount(int Plays, int Days);

public sealed record GameMonthStats(int Year, int Month, int Days, int Played, int Games);

public sealed record GameDateStats(DateOnly Date, int Games, long Seconds, double Pips, long OwnSeconds);

public sealed record GameTradeGroup(string Name, int Trades, int Wins, double Pips);

public sealed record GameDayResult(string Day, double Pips);

public sealed class GameStats
{
    public const string FirstPlay = "first play";
    public const string Replay = "replay";

    private const string LogTimeFormat = "yyyy-MM-dd HH:mm";

    private static readonly string[] ReasonOrder = { "take", "stop", "manual", "day end" };
    private static readonly string[] SideOrder = { "buy", "sell" };
    private static readonly string[] SessionOrder =
        { "Asia", "Asia + Europe", "Europe", "Europe + America", "America", "Night" };
    private static readonly string[] PlayOrder = { FirstPlay, Replay };

    private sealed record PlayedTrade(GameLogTrade Trade, bool Replay);

    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public int TradingDays { get; init; }
    public int PlayedDays { get; init; }
    public IReadOnlyList<GamePlayCount> PlayCounts { get; init; } = Array.Empty<GamePlayCount>();
    public IReadOnlyList<GameMonthStats> Months { get; init; } = Array.Empty<GameMonthStats>();
    public int Games { get; init; }
    public long TotalSeconds { get; init; }
    public double AverageSeconds { get; init; }
    public double MedianSeconds { get; init; }
    public long FastestSeconds { get; init; }
    public long SlowestSeconds { get; init; }
    public IReadOnlyList<GameDateStats> Dates { get; init; } = Array.Empty<GameDateStats>();
    public int OwnSessions { get; init; }
    public long PanelSeconds { get; init; }
    public long OwnSeconds { get; init; }
    public int Pauses { get; init; }
    public int OwnGames { get; init; }
    public double AverageGameOwn { get; init; }
    public double MedianGameOwn { get; init; }
    public int Trades { get; init; }
    public int Wins { get; init; }
    public double Pips { get; init; }
    public int ProfitGames { get; init; }
    public int LossGames { get; init; }
    public GameDayResult? Best { get; init; }
    public GameDayResult? Worst { get; init; }
    public double AverageHoldMinutes { get; init; }
    public double MedianHoldMinutes { get; init; }
    public IReadOnlyList<GameTradeGroup> Reasons { get; init; } = Array.Empty<GameTradeGroup>();
    public IReadOnlyList<GameTradeGroup> Sides { get; init; } = Array.Empty<GameTradeGroup>();
    public IReadOnlyList<GameTradeGroup> Pairs { get; init; } = Array.Empty<GameTradeGroup>();
    public IReadOnlyList<GameTradeGroup> Sessions { get; init; } = Array.Empty<GameTradeGroup>();
    public IReadOnlyList<GameTradeGroup> Plays { get; init; } = Array.Empty<GameTradeGroup>();

    public int NeverPlayed => TradingDays - PlayedDays;

    public int FlatGames => Games - ProfitGames - LossGames;

    public double LeftSeconds => NeverPlayed * AverageSeconds;

    public double AverageSessionOwn => OwnSessions == 0 ? 0 : (double)OwnSeconds / OwnSessions;

    public static GameStats Build(IReadOnlyList<GameLogEntry> log, IReadOnlyList<GameTimeSession> sessions,
        DateOnly from, DateOnly to, IReadOnlyList<DateOnly> days)
    {
        var plays = GameLogStore.PlayCounts(log);
        var counts = days.Select(d => plays.GetValueOrDefault(GameDayPicker.Key(d))).ToList();
        var seconds = log.Where(e => e.Seconds > 0).Select(e => (double)e.Seconds).Order().ToList();
        var own = log.Where(e => e.ActiveSeconds > 0).Select(e => (double)e.ActiveSeconds).Order().ToList();
        var trades = TradesOf(log);
        var holds = trades.Select(t => HoldMinutes(t.Trade)).OfType<double>().Order().ToList();
        var best = log.MaxBy(e => e.Pips);
        var worst = log.MinBy(e => e.Pips);
        return new GameStats
        {
            From = from,
            To = to,
            TradingDays = days.Count,
            PlayedDays = counts.Count(c => c > 0),
            PlayCounts = Enumerable.Range(0, counts.DefaultIfEmpty().Max() + 1)
                .Select(n => new GamePlayCount(n, counts.Count(c => c == n)))
                .ToList(),
            Months = days
                .GroupBy(d => (d.Year, d.Month))
                .Select(g => new GameMonthStats(g.Key.Year, g.Key.Month, g.Count(),
                    g.Count(d => plays.ContainsKey(GameDayPicker.Key(d))),
                    g.Sum(d => plays.GetValueOrDefault(GameDayPicker.Key(d)))))
                .ToList(),
            Games = log.Count,
            TotalSeconds = (long)seconds.Sum(),
            AverageSeconds = seconds.Count == 0 ? 0 : seconds.Average(),
            MedianSeconds = Median(seconds),
            FastestSeconds = (long)seconds.FirstOrDefault(),
            SlowestSeconds = (long)seconds.LastOrDefault(),
            Dates = DatesOf(log, sessions),
            OwnSessions = sessions.Count,
            PanelSeconds = sessions.Sum(s => Math.Max(0, s.Seconds)),
            OwnSeconds = sessions.Sum(s => Math.Max(0, s.ActiveSeconds)),
            Pauses = sessions.Sum(s => s.Pauses),
            OwnGames = own.Count,
            AverageGameOwn = own.Count == 0 ? 0 : own.Average(),
            MedianGameOwn = Median(own),
            Trades = trades.Count,
            Wins = trades.Count(t => t.Trade.Pips > 0),
            Pips = trades.Sum(t => t.Trade.Pips),
            ProfitGames = log.Count(e => e.Pips > 0),
            LossGames = log.Count(e => e.Pips < 0),
            Best = best == null ? null : new GameDayResult(best.Day, best.Pips),
            Worst = worst == null ? null : new GameDayResult(worst.Day, worst.Pips),
            AverageHoldMinutes = holds.Count == 0 ? 0 : holds.Average(),
            MedianHoldMinutes = Median(holds),
            Reasons = Groups(trades, t => t.Trade.Reason, ReasonOrder),
            Sides = Groups(trades, t => t.Trade.Side, SideOrder),
            Pairs = Groups(trades, t => t.Trade.Symbol, Array.Empty<string>()),
            Sessions = Groups(trades, t => SessionOf(t.Trade), SessionOrder),
            Plays = Groups(trades, t => t.Replay ? Replay : FirstPlay, PlayOrder),
        };
    }

    private static List<GameDateStats> DatesOf(IReadOnlyList<GameLogEntry> log,
        IReadOnlyList<GameTimeSession> sessions)
    {
        var byDate = new Dictionary<DateOnly, GameDateStats>();
        foreach (var entry in log)
        {
            if (entry.StartedAt == default) continue;
            var row = Row(byDate, entry.StartedAt);
            byDate[row.Date] = row with
            {
                Games = row.Games + 1,
                Seconds = row.Seconds + Math.Max(0, entry.Seconds),
                Pips = row.Pips + entry.Pips,
            };
        }
        foreach (var session in sessions)
        {
            if (session.StartedAt == default) continue;
            var row = Row(byDate, session.StartedAt);
            byDate[row.Date] = row with { OwnSeconds = row.OwnSeconds + Math.Max(0, session.ActiveSeconds) };
        }
        return byDate.Values.OrderByDescending(x => x.Date).ToList();
    }

    private static GameDateStats Row(Dictionary<DateOnly, GameDateStats> byDate, DateTimeOffset at)
    {
        var date = DateOnly.FromDateTime(at.DateTime);
        return byDate.TryGetValue(date, out var row) ? row : new GameDateStats(date, 0, 0, 0, 0);
    }

    private static List<PlayedTrade> TradesOf(IReadOnlyList<GameLogEntry> log)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var trades = new List<PlayedTrade>();
        foreach (var entry in log)
        {
            int before = seen.GetValueOrDefault(entry.Day);
            seen[entry.Day] = before + 1;
            foreach (var trade in entry.Trades) trades.Add(new PlayedTrade(trade, before > 0));
        }
        return trades;
    }

    private static List<GameTradeGroup> Groups(List<PlayedTrade> trades, Func<PlayedTrade, string> nameOf,
        string[] order) =>
        trades
            .GroupBy(nameOf, StringComparer.Ordinal)
            .Select(g => new GameTradeGroup(g.Key, g.Count(), g.Count(t => t.Trade.Pips > 0),
                g.Sum(t => t.Trade.Pips)))
            .OrderBy(g => Rank(order, g.Name))
            .ThenByDescending(g => g.Trades)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();

    private static int Rank(string[] order, string name)
    {
        int index = Array.IndexOf(order, name);
        return index < 0 ? order.Length : index;
    }

    private static string SessionOf(GameLogTrade trade) =>
        ParseUtc(trade.OpenUtc) is not { } open
            ? "?"
            : SessionClock.At(open) switch
            {
                ChartSession.AsiaEurope => "Asia + Europe",
                ChartSession.Europe => "Europe",
                ChartSession.Overlap => "Europe + America",
                ChartSession.America => "America",
                ChartSession.Closed => "Night",
                _ => "Asia",
            };

    private static double? HoldMinutes(GameLogTrade trade) =>
        ParseUtc(trade.OpenUtc) is { } open && ParseUtc(trade.CloseUtc) is { } close && close >= open
            ? (close - open) / 60.0
            : null;

    private static long? ParseUtc(string text) =>
        DateTime.TryParseExact(text, LogTimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
            ? new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds()
            : null;

    private static double Median(IReadOnlyList<double> sorted)
    {
        int n = sorted.Count;
        if (n == 0) return 0;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }
}
