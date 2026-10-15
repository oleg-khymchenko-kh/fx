using FXViewer.Storage;

namespace FXViewer.Compute;

public static class FlatSeriesSync
{
    public sealed record Plan(string Symbol, long StoredLastUnix, Candle[] Writes, long[] Clears)
    {
        public bool Empty => Writes.Length == 0 && Clears.Length == 0;
    }

    public static (int Written, int Cleared) Apply(
        CandleDatabase db, string symbol, IReadOnlyList<Candle> computed)
    {
        var plan = Diff(db, symbol, computed);
        Write(db, plan);
        return (plan.Writes.Length, plan.Clears.Length);
    }

    public static Plan Diff(CandleDatabase db, string symbol, IReadOnlyList<Candle> computed)
    {
        var stored = new List<Candle>();
        foreach (int year in db.ExistingYears(symbol))
            stored.AddRange(db.ReadRange(symbol,
                new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(year, 12, 31, 23, 59, 0, DateTimeKind.Utc), includeWide: true));
        var writes = new List<Candle>();
        var clears = new List<long>();
        int i = 0;
        int j = 0;
        while (i < computed.Count || j < stored.Count)
        {
            long tc = i < computed.Count ? computed[i].MinuteUnixSeconds : long.MaxValue;
            long ts = j < stored.Count ? stored[j].MinuteUnixSeconds : long.MaxValue;
            if (ts < tc)
            {
                clears.Add(ts);
                j++;
                continue;
            }
            var c = computed[i];
            if (ts != tc || stored[j].Min != c.Min || stored[j].Max != c.Max || stored[j].Avg != c.Avg)
                writes.Add(c);
            if (ts == tc) j++;
            i++;
        }
        long storedLast = stored.Count > 0 ? stored[^1].MinuteUnixSeconds : 0;
        return new Plan(symbol, storedLast, writes.ToArray(), clears.ToArray());
    }

    public static bool StillMatches(CandleDatabase db, Plan plan)
    {
        var last = db.LastFilledMinuteUtc(plan.Symbol);
        long lastUnix = last == null ? 0 : ((DateTimeOffset)last.Value).ToUnixTimeSeconds();
        return lastUnix == plan.StoredLastUnix;
    }

    public static void Replace(CandleDatabase db, string symbol, IReadOnlyList<Candle> values)
    {
        db.DeleteSymbol(symbol);
        foreach (var c in values)
            db.WriteMinute(symbol, UnixToUtc(c.MinuteUnixSeconds), c.Min, c.Max, c.Avg, false);
        db.FlushSymbol(symbol);
    }

    public static void Write(CandleDatabase db, Plan plan)
    {
        if (plan.Empty) return;
        foreach (long unix in plan.Clears) db.ClearMinute(plan.Symbol, UnixToUtc(unix));
        foreach (var c in plan.Writes)
            db.WriteMinute(plan.Symbol, UnixToUtc(c.MinuteUnixSeconds), c.Min, c.Max, c.Avg, false);
        db.FlushSymbol(plan.Symbol);
    }

    private static DateTime UnixToUtc(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
}
