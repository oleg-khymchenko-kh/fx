using FXViewer.Storage;

namespace FXViewer.Chart;

public enum TradingCentralLevelKind
{
    Pivot,
    Target,
    Alternative,
}

public sealed record TradingCentralMark(
    long FromUnix, long ToUnix, int Value, TradingCentralLevelKind Kind, bool Up, string Session,
    int Number, TradingCentralSet Set, TradingCentralLevels Levels);

public sealed record TradingCentralMarker(
    int X, int Y, int HalfPx, int ColorArgb, bool Mirrored, TradingCentralMark Mark);

public static class TradingCentralMarks
{
    private const int PricePoints = 100000;
    private const long MinuteSeconds = 60;
    private const long DaySeconds = 86400;

    public static TradingCentralMark[] Build(
        IReadOnlyList<TradingCentralDay> days, string pair, int priceDiv)
    {
        string key = IndicatorSymbol.NameKey(pair);
        var found = new List<(long From, long Delivered, string Session, TradingCentralSet Set, TradingCentralLevels Levels)>();
        foreach (var day in days)
            foreach (var (session, set) in day.Sets())
            {
                if (set.MadeAtUnix <= 0) continue;
                var levels = set.Pairs.FirstOrDefault(
                    p => p.IsUsable() && IndicatorSymbol.NameKey(p.Pair) == key);
                if (levels == null) continue;
                long start = TradingCentralTimes.StartOf(set, levels);
                found.Add((start - start % MinuteSeconds, set.MadeAtUnix, session, set, levels));
            }
        found.Sort((a, b) => a.From != b.From ? a.From.CompareTo(b.From) : a.Delivered.CompareTo(b.Delivered));
        var views = new HashSet<string>(StringComparer.Ordinal);
        var sets = new List<(long From, long Delivered, string Session, TradingCentralSet Set, TradingCentralLevels Levels)>();
        foreach (var entry in found)
            if (entry.Levels.View.Length == 0 || views.Add(entry.Levels.View))
                sets.Add(entry);
        var marks = new List<TradingCentralMark>();
        for (int i = 0; i < sets.Count; i++)
        {
            var (from, delivered, session, set, levels) = sets[i];
            long to = DayEndAfter(Math.Max(from, delivered));
            if (i + 1 < sets.Count && sets[i + 1].From < to) to = sets[i + 1].From;
            if (to <= from) continue;
            bool up = levels.IsUp();
            marks.Add(new TradingCentralMark(from, to, Raw(levels.Pivot, priceDiv),
                TradingCentralLevelKind.Pivot, up, session, 0, set, levels));
            for (int t = 0; t < levels.Targets.Count; t++)
                if (levels.Targets[t] > 0)
                    marks.Add(new TradingCentralMark(from, to, Raw(levels.Targets[t], priceDiv),
                        TradingCentralLevelKind.Target, up, session, t + 1, set, levels));
            for (int a = 0; a < levels.Alternatives.Count; a++)
                if (levels.Alternatives[a] > 0)
                    marks.Add(new TradingCentralMark(from, to, Raw(levels.Alternatives[a], priceDiv),
                        TradingCentralLevelKind.Alternative, up, session, a + 1, set, levels));
        }
        return marks.ToArray();
    }

    public static long DayEndAfter(long unix)
    {
        var day = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.Date;
        for (int i = 0; i < 2; i++)
        {
            var date = day.AddDays(i);
            long close = new DateTimeOffset(date).ToUnixTimeSeconds()
                + SessionClock.AmericaCloseHourUtc(date) * SessionClock.HourSeconds;
            if (close > unix) return close;
        }
        return unix + DaySeconds;
    }

    public static TradingCentralMark? LastPivot(TradingCentralMark[] marks)
    {
        for (int i = marks.Length - 1; i >= 0; i--)
            if (marks[i].Kind == TradingCentralLevelKind.Pivot) return marks[i];
        return null;
    }

    public static int SetCount(TradingCentralMark[] marks)
    {
        int count = 0;
        foreach (var mark in marks)
            if (mark.Kind == TradingCentralLevelKind.Pivot) count++;
        return count;
    }

    private static int Raw(double price, int priceDiv) =>
        (int)Math.Round(price * PricePoints / Math.Max(1, priceDiv));
}
