using FXViewer.Chart;
using FXViewer.Storage;

namespace FXViewer.Compute;

public static class DollarIndexSymbol
{
    private const int PriceScale = 100000;
    private const int PipScale = 10;

    public static readonly string[] DefaultPairs =
        { "EURUSD", "GBPUSD", "AUDUSD", "NZDUSD", "USDCHF", "USDCAD" };

    public static IReadOnlyList<string> Effective(IReadOnlyList<string>? pairs) =>
        pairs == null || pairs.Count == 0 ? DefaultPairs : pairs;

    private static bool UsdIsBase(string pair) =>
        pair.StartsWith("USD", StringComparison.OrdinalIgnoreCase);

    private sealed class State
    {
        public bool Started;
        public double Acc;
        public readonly bool UsePips;
        public readonly double[] LastPrice;
        public int Written;
        public long LastUnix;

        public State(int pairCount, bool usePips)
        {
            LastPrice = new double[pairCount];
            UsePips = usePips;
        }
    }

    private static string FinalText(State st) =>
        st.UsePips ? $"{st.Acc:+0.0;-0.0} pips" : $"{Math.Exp(st.Acc):F5}";

    public static (int Years, int Minutes) Generate(
        CandleDatabase db, string targetSymbol, IReadOnlyList<string>? pairs,
        long startUnix, long endUnix, string method, string algorithm, Func<string, int> pipPoints,
        Action<string>? log = null, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var use = Effective(pairs);
        db.DeleteSymbol(targetSymbol);
        long end = endUnix > 0 ? endUnix : long.MaxValue;
        var st = new State(use.Count, IndexAlgorithms.IsPips(algorithm));
        int years = RunRange(db, use, startUnix, end, IndexMethods.IsAverage(method),
            pipPoints, st, DbWriter(db, targetSymbol), ct, progress);
        db.FlushAll();
        progress?.Report(1.0);
        if (st.Written == 0)
        {
            log?.Invoke($"{targetSymbol}: no minutes where all {use.Count} pairs have data in the range");
            return (years, 0);
        }
        log?.Invoke(
            $"{targetSymbol}: {st.Written:N0} minutes written, " +
            $"{UnixToUtc(st.LastUnix):yyyy-MM-dd HH:mm} last, final {FinalText(st)} " +
            $"({method}, {algorithm}, {string.Join(" ", use)})");
        return (years, st.Written);
    }

    public static int Refresh(
        CandleDatabase db, string targetSymbol, IReadOnlyList<string>? pairs,
        long startUnix, long endUnix, string method, string algorithm, Func<string, int> pipPoints,
        Action<string>? log = null, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var use = Effective(pairs);
        var lastTarget = db.LastFilledMinuteUtc(targetSymbol);
        if (lastTarget == null)
            return Generate(db, targetSymbol, use, startUnix, endUnix, method, algorithm, pipPoints,
                log, ct, progress).Minutes;

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

        var st = Resume(db, targetSymbol, use, lastTargetUnix, IndexAlgorithms.IsPips(algorithm));
        if (st == null)
            return Generate(db, targetSymbol, use, startUnix, endUnix, method, algorithm, pipPoints,
                log, ct, progress).Minutes;

        RunRange(db, use, lastTargetUnix + 60, to, IndexMethods.IsAverage(method),
            pipPoints, st, DbWriter(db, targetSymbol), ct, progress);
        db.FlushAll();
        progress?.Report(1.0);
        log?.Invoke($"{targetSymbol}: refreshed {st.Written:N0} minutes, final {FinalText(st)}");
        return st.Written;
    }

    public static Candle[] Compute(
        CandleDatabase db, IReadOnlyList<string>? pairs, long startUnix, long endUnix,
        string method, string algorithm, Func<string, int> pipPoints, CancellationToken ct = default)
    {
        var use = Effective(pairs);
        var result = new List<Candle>();
        var st = new State(use.Count, IndexAlgorithms.IsPips(algorithm));
        RunRange(db, use, startUnix, endUnix > 0 ? endUnix : long.MaxValue, IndexMethods.IsAverage(method),
            pipPoints, st, (t, v) => result.Add(Flat(t, v)), ct, null);
        return result.ToArray();
    }

    internal static Candle Flat(long unix, int value) => new(unix, value, value, value, false);

    private static Action<long, int> DbWriter(CandleDatabase db, string targetSymbol) =>
        (t, v) => db.WriteMinute(targetSymbol, UnixToUtc(t), v, v, v, false);

    public static List<Candle> Tail(
        CandleDatabase db, string targetSymbol, IReadOnlyList<string>? pairs, long endUnix,
        string method, string algorithm, Func<string, int> pipPoints,
        IReadOnlyDictionary<string, Candle[]> livePairs)
    {
        var tail = new List<Candle>();
        var use = Effective(pairs);
        var lastTarget = db.LastFilledMinuteUtc(targetSymbol);
        if (lastTarget == null) return tail;
        long lastTargetUnix = ((DateTimeOffset)lastTarget.Value).ToUnixTimeSeconds();
        long to = Math.Min(endUnix > 0 ? endUnix : long.MaxValue, NowMinuteUnix());
        if (to <= lastTargetUnix) return tail;
        var st = Resume(db, targetSymbol, use, lastTargetUnix, IndexAlgorithms.IsPips(algorithm));
        if (st == null) return tail;

        var lists = new IReadOnlyList<Candle>[use.Count];
        for (int i = 0; i < use.Count; i++)
            lists[i] = WithLive(
                db.ReadRange(use[i], UnixToUtc(lastTargetUnix + 60), UnixToUtc(to)),
                livePairs.GetValueOrDefault(use[i]), lastTargetUnix, to);
        Step(lists, use.Select(UsdIsBase).ToArray(), IndexMethods.IsAverage(method),
            use.Select(p => (double)pipPoints(p)).ToArray(), st,
            (t, v) => tail.Add(Flat(t, v)), CancellationToken.None);
        return tail;
    }

    internal static long NowMinuteUnix()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return now - now % 60;
    }

    internal static Candle[] WithLive(
        List<Candle> stored, IReadOnlyList<Candle>? live, long afterUnix, long toUnix)
    {
        if (live == null || live.Count == 0) return stored.ToArray();
        var extra = live
            .Where(c => c.MinuteUnixSeconds > afterUnix && c.MinuteUnixSeconds <= toUnix)
            .ToArray();
        return CandleHistory.MergeByTime(stored.ToArray(), extra);
    }

    private static State? Resume(
        CandleDatabase db, string targetSymbol, IReadOnlyList<string> pairs, long lastTargetUnix, bool usePips)
    {
        var targetCandle = ReadAt(db, targetSymbol, lastTargetUnix);
        if (targetCandle is not { Avg: > 0 }) return null;
        var st = new State(pairs.Count, usePips)
        {
            Started = true,
            LastUnix = lastTargetUnix,
            Acc = usePips
                ? (targetCandle.Value.Avg - PriceScale) / (double)PipScale
                : Math.Log(targetCandle.Value.Avg / (double)PriceScale),
        };
        for (int i = 0; i < pairs.Count; i++)
        {
            var c = ReadAt(db, pairs[i], lastTargetUnix);
            if (c is not { Avg: > 0 }) return null;
            st.LastPrice[i] = c.Value.Avg;
        }
        return st;
    }

    private static int RunRange(
        CandleDatabase db, IReadOnlyList<string> pairs,
        long fromUnix, long toUnix, bool useAverage, Func<string, int> pipPoints,
        State st, Action<long, int> write, CancellationToken ct, IProgress<double>? progress)
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
        var lists = new IReadOnlyList<Candle>[pairs.Count];
        var usdBase = pairs.Select(UsdIsBase).ToArray();
        var pip = pairs.Select(p => (double)pipPoints(p)).ToArray();
        int done = 0;
        foreach (int year in span)
        {
            ct.ThrowIfCancellationRequested();
            long yearFrom = Math.Max(from, YearStartUnix(year));
            long yearTo = Math.Min(to, YearStartUnix(year + 1) - 60);
            for (int i = 0; i < pairs.Count; i++)
                lists[i] = db.ReadRange(pairs[i], UnixToUtc(yearFrom), UnixToUtc(yearTo));
            Step(lists, usdBase, useAverage, pip, st, write, ct);
            done++;
            progress?.Report((double)done / span.Count);
        }
        return span.Count;
    }

    private static void Step(
        IReadOnlyList<Candle>[] lists, bool[] usdBase, bool useAverage,
        double[] pip, State st, Action<long, int> write, CancellationToken ct)
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
                st.Acc = 0;
                for (int i = 0; i < vals.Length; i++) st.LastPrice[i] = vals[i];
                WriteStep(t, st, write);
                continue;
            }

            for (int i = 0; i < vals.Length; i++)
            {
                double r = st.UsePips
                    ? (vals[i] - st.LastPrice[i]) / pip[i]
                    : Math.Log(vals[i] / st.LastPrice[i]);
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
            st.Acc += u;
            WriteStep(t, st, write);
        }
    }

    private static void WriteStep(long unix, State st, Action<long, int> write)
    {
        int v = (int)Math.Round(
            st.UsePips ? PriceScale + st.Acc * PipScale : Math.Exp(st.Acc) * PriceScale,
            MidpointRounding.AwayFromZero);
        write(unix, v);
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
