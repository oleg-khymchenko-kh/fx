using FXViewer.Storage;

namespace FXViewer.Compute;

public static class DollarIndexSymbol
{
    private const int PriceScale = 100000;

    public static readonly string[] DefaultPairs =
        { "EURUSD", "GBPUSD", "AUDUSD", "NZDUSD", "USDCHF", "USDCAD" };

    public static IReadOnlyList<string> Effective(IReadOnlyList<string>? pairs) =>
        pairs == null || pairs.Count == 0 ? DefaultPairs : pairs;

    private static bool UsdIsBase(string pair) =>
        pair.StartsWith("USD", StringComparison.OrdinalIgnoreCase);

    private sealed class State
    {
        public bool Started;
        public double LogIndex;
        public readonly double[] LastPrice;
        public int Written;
        public long LastUnix;

        public State(int pairCount) => LastPrice = new double[pairCount];
    }

    public static (int Years, int Minutes) Generate(
        CandleDatabase db, string targetSymbol, IReadOnlyList<string>? pairs,
        long startUnix, long endUnix, string method,
        Action<string>? log = null, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var use = Effective(pairs);
        db.DeleteSymbol(targetSymbol);
        long end = endUnix > 0 ? endUnix : long.MaxValue;
        var st = new State(use.Count);
        int years = RunRange(db, targetSymbol, use, startUnix, end, IndexMethods.IsAverage(method), st, ct,
            progress);
        db.FlushAll();
        progress?.Report(1.0);
        if (st.Written == 0)
        {
            log?.Invoke($"{targetSymbol}: no minutes where all {use.Count} pairs have data in the range");
            return (years, 0);
        }
        log?.Invoke(
            $"{targetSymbol}: {st.Written:N0} minutes written, " +
            $"{UnixToUtc(st.LastUnix):yyyy-MM-dd HH:mm} last, final {Math.Exp(st.LogIndex):F5} " +
            $"({method}, {string.Join(" ", use)})");
        return (years, st.Written);
    }

    public static int Refresh(
        CandleDatabase db, string targetSymbol, IReadOnlyList<string>? pairs,
        long startUnix, long endUnix, string method,
        Action<string>? log = null, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var use = Effective(pairs);
        var lastTarget = db.LastFilledMinuteUtc(targetSymbol);
        if (lastTarget == null)
            return Generate(db, targetSymbol, use, startUnix, endUnix, method, log, ct, progress).Minutes;

        long lastTargetUnix = ((DateTimeOffset)lastTarget.Value).ToUnixTimeSeconds();
        long end = endUnix > 0 ? endUnix : long.MaxValue;
        long limit = long.MaxValue;
        foreach (var pair in use)
        {
            var lastPair = db.LastFilledMinuteUtc(pair);
            if (lastPair == null)
            {
                progress?.Report(1.0);
                log?.Invoke($"{pair}: no data");
                return 0;
            }
            limit = Math.Min(limit, ((DateTimeOffset)lastPair.Value).ToUnixTimeSeconds());
        }
        long to = Math.Min(limit, end);
        if (to <= lastTargetUnix)
        {
            progress?.Report(1.0);
            log?.Invoke($"{targetSymbol}: up to date");
            return 0;
        }

        var st = new State(use.Count) { Started = true, LastUnix = lastTargetUnix };
        var targetCandle = ReadAt(db, targetSymbol, lastTargetUnix);
        if (targetCandle is not { Avg: > 0 })
            return Generate(db, targetSymbol, use, startUnix, endUnix, method, log, ct, progress).Minutes;
        st.LogIndex = Math.Log(targetCandle.Value.Avg / (double)PriceScale);
        for (int i = 0; i < use.Count; i++)
        {
            var c = ReadAt(db, use[i], lastTargetUnix);
            if (c is not { Avg: > 0 })
                return Generate(db, targetSymbol, use, startUnix, endUnix, method, log, ct, progress).Minutes;
            st.LastPrice[i] = c.Value.Avg;
        }

        RunRange(db, targetSymbol, use, lastTargetUnix + 60, to, IndexMethods.IsAverage(method), st, ct,
            progress);
        db.FlushAll();
        progress?.Report(1.0);
        log?.Invoke($"{targetSymbol}: refreshed {st.Written:N0} minutes, final {Math.Exp(st.LogIndex):F5}");
        return st.Written;
    }

    private static int RunRange(
        CandleDatabase db, string targetSymbol, IReadOnlyList<string> pairs,
        long fromUnix, long toUnix, bool useAverage,
        State st, CancellationToken ct, IProgress<double>? progress)
    {
        var years = new SortedSet<int>();
        foreach (var pair in pairs)
            foreach (var y in db.ExistingYears(pair))
                years.Add(y);
        if (years.Count == 0) return 0;

        long floor = YearStartUnix(years.Min);
        long from = Math.Max(fromUnix, floor);
        long ceiling = YearStartUnix(years.Max + 1) - 60;
        long to = Math.Min(toUnix, ceiling);
        if (to < from) return 0;

        var span = years.Where(y => YearStartUnix(y + 1) > from && YearStartUnix(y) <= to).ToList();
        var lists = new List<Candle>[pairs.Count];
        var usdBase = pairs.Select(UsdIsBase).ToArray();
        int done = 0;
        foreach (int year in span)
        {
            ct.ThrowIfCancellationRequested();
            long yearFrom = Math.Max(from, YearStartUnix(year));
            long yearTo = Math.Min(to, YearStartUnix(year + 1) - 60);
            for (int i = 0; i < pairs.Count; i++)
                lists[i] = db.ReadRange(pairs[i], UnixToUtc(yearFrom), UnixToUtc(yearTo));
            Step(db, targetSymbol, lists, usdBase, useAverage, st, ct);
            done++;
            progress?.Report((double)done / span.Count);
        }
        return span.Count;
    }

    private static void Step(
        CandleDatabase db, string targetSymbol, List<Candle>[] lists, bool[] usdBase, bool useAverage,
        State st, CancellationToken ct)
    {
        var idx = new int[lists.Length];
        var vals = new double[lists.Length];
        var s = new double[lists.Length];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long t = long.MaxValue;
            for (int i = 0; i < lists.Length; i++)
                if (idx[i] < lists[i].Count && lists[i][idx[i]].MinuteUnixSeconds < t)
                    t = lists[i][idx[i]].MinuteUnixSeconds;
            if (t == long.MaxValue) break;

            int present = 0;
            for (int i = 0; i < lists.Length; i++)
            {
                vals[i] = 0;
                if (idx[i] < lists[i].Count && lists[i][idx[i]].MinuteUnixSeconds == t)
                {
                    if (lists[i][idx[i]].Avg > 0)
                    {
                        vals[i] = lists[i][idx[i]].Avg;
                        present++;
                    }
                    idx[i]++;
                }
            }
            if (present < lists.Length) continue;

            if (!st.Started)
            {
                st.Started = true;
                st.LogIndex = 0;
                for (int i = 0; i < vals.Length; i++) st.LastPrice[i] = vals[i];
                WriteStep(db, targetSymbol, t, st);
                continue;
            }

            for (int i = 0; i < vals.Length; i++)
            {
                double r = Math.Log(vals[i] / st.LastPrice[i]);
                s[i] = usdBase[i] ? r : -r;
                st.LastPrice[i] = vals[i];
            }
            double u;
            if (useAverage)
            {
                double sum = 0;
                for (int i = 0; i < s.Length; i++) sum += s[i];
                u = sum / s.Length;
            }
            else
            {
                Array.Sort(s);
                u = s.Length % 2 == 1
                    ? s[s.Length / 2]
                    : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2.0;
            }
            st.LogIndex += u;
            WriteStep(db, targetSymbol, t, st);
        }
    }

    private static void WriteStep(CandleDatabase db, string targetSymbol, long unix, State st)
    {
        int v = (int)Math.Round(Math.Exp(st.LogIndex) * PriceScale, MidpointRounding.AwayFromZero);
        db.WriteMinute(targetSymbol, UnixToUtc(unix), v, v, v, false);
        st.Written++;
        st.LastUnix = unix;
    }

    private static Candle? ReadAt(CandleDatabase db, string symbol, long unix)
    {
        var utc = UnixToUtc(unix);
        var candles = db.ReadRange(symbol, utc, utc);
        return candles.Count > 0 ? candles[0] : null;
    }

    private static long YearStartUnix(int year) =>
        new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static DateTime UnixToUtc(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
}
