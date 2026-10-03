namespace FXViewer.Game;

public static class GameTimeSpans
{
    public const double JoinSeconds = 2;

    public static void Add(List<GameTimeSpan> spans, string mode, DateTimeOffset from, DateTimeOffset to)
    {
        from = Whole(from);
        to = Whole(to);
        if (to <= from) return;
        if (spans.Count > 0)
        {
            var last = spans[^1];
            if (last.Mode == mode && from >= last.From && (from - last.To).TotalSeconds <= JoinSeconds)
            {
                if (to > last.To) last.To = to;
                return;
            }
        }
        spans.Add(new GameTimeSpan { Mode = mode, From = from, To = to });
    }

    public static IEnumerable<GameTimeSpan> Of(IEnumerable<GameTimeSession> sessions,
        IReadOnlyDictionary<string, List<GameLogEntry>> logs, DateTimeOffset since)
    {
        foreach (var session in sessions)
        {
            if (session.EndedAt < since) continue;
            var spans = session.Spans.Count > 0 ? session.Spans : SpansFromGames(session, logs);
            foreach (var span in spans) yield return span;
        }
    }

    public static double Seconds(IEnumerable<GameTimeSpan> spans, string mode, DateTimeOffset from,
        DateTimeOffset to)
    {
        double seconds = 0;
        foreach (var span in spans)
        {
            if (span.Mode != mode) continue;
            var start = span.From > from ? span.From : from;
            var end = span.To < to ? span.To : to;
            if (end > start) seconds += (end - start).TotalSeconds;
        }
        return seconds;
    }

    private static List<GameTimeSpan> SpansFromGames(GameTimeSession session,
        IReadOnlyDictionary<string, List<GameLogEntry>> logs)
    {
        var spans = new List<GameTimeSpan>();
        if (session.ActiveSeconds <= 0 || session.EndedAt == default) return spans;
        var games = logs
            .Select(log => (Mode: log.Key, Count: log.Value.Count(e => EndedInside(e, session))))
            .Where(x => x.Count > 0)
            .ToList();
        int total = games.Sum(x => x.Count);
        foreach (var (mode, count) in games)
            spans.Add(new GameTimeSpan
            {
                Mode = mode,
                From = session.EndedAt.AddSeconds(-session.ActiveSeconds * (double)count / total),
                To = session.EndedAt,
            });
        return spans;
    }

    private static bool EndedInside(GameLogEntry entry, GameTimeSession session) =>
        entry.EndedAt >= session.StartedAt && entry.EndedAt <= session.EndedAt;

    private static DateTimeOffset Whole(DateTimeOffset time)
    {
        long rest = time.Ticks % TimeSpan.TicksPerSecond;
        long ticks = time.Ticks - rest + (rest >= TimeSpan.TicksPerSecond / 2 ? TimeSpan.TicksPerSecond : 0);
        return new DateTimeOffset(ticks, time.Offset);
    }
}
