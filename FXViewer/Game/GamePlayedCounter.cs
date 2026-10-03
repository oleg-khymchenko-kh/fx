using System.Globalization;

namespace FXViewer.Game;

public sealed record GamePlayedPeriod(int Games, double OwnSeconds);

public sealed record GamePlayedCounts(GamePlayedPeriod Today, GamePlayedPeriod LastHour)
{
    public string Text(string mode, Func<double, string> clock)
    {
        string name = GameModes.Title(mode);
        string[] labels = { name + " games", name + " own time" };
        string[] today = { Number(Today.Games), clock(Today.OwnSeconds) };
        string[] hour = { Number(LastHour.Games), clock(LastHour.OwnSeconds) };
        int labelWidth = labels.Max(x => x.Length);
        int todayWidth = today.Max(x => x.Length);
        int hourWidth = hour.Max(x => x.Length);
        return string.Join("\n", Enumerable.Range(0, labels.Length).Select(i =>
            $"{labels[i].PadRight(labelWidth)}  today {today[i].PadLeft(todayWidth)}   " +
            $"last hour {hour[i].PadLeft(hourWidth)}"));
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

public static class GamePlayedCounter
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    public static GamePlayedCounts Count(string mode, IReadOnlyDictionary<string, List<GameLogEntry>> logs,
        IEnumerable<GameTimeSession> sessions, IEnumerable<GameTimeSpan> running, DateTimeOffset now,
        DateTimeOffset dayStart)
    {
        var hourAgo = now - Hour;
        var since = dayStart < hourAgo ? dayStart : hourAgo;
        var spans = GameTimeSpans.Of(sessions, logs, since).Concat(running).ToList();
        int today = 0;
        int lastHour = 0;
        if (logs.TryGetValue(mode, out var games))
            foreach (var entry in games)
            {
                var played = PlayedAt(entry);
                if (played == default || played > now) continue;
                if (played >= dayStart) today++;
                if (played > hourAgo) lastHour++;
            }
        return new GamePlayedCounts(
            new GamePlayedPeriod(today, GameTimeSpans.Seconds(spans, mode, dayStart, now)),
            new GamePlayedPeriod(lastHour, GameTimeSpans.Seconds(spans, mode, hourAgo, now)));
    }

    private static DateTimeOffset PlayedAt(GameLogEntry entry) =>
        entry.EndedAt != default ? entry.EndedAt : entry.StartedAt;
}
