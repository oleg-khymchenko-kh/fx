using FXViewer.Storage;

namespace FXViewer.Compute;

public static class CurrencyIndexSymbol
{
    private const int PriceScale = 100000;
    private const int PipScale = 10;

    public static bool UsdIsBase(string pair) =>
        pair.StartsWith("USD", StringComparison.OrdinalIgnoreCase);

    public static string CurrencyOf(string pair)
    {
        var key = IndicatorSymbol.NameKey(pair);
        if (key.Length < 6) return key;
        return UsdIsBase(key) ? key.Substring(3, 3) : key.Substring(0, 3);
    }

    private sealed class State
    {
        public bool Started;
        public double Acc;
        public bool UsePips;
        public double LastIndex;
        public double LastPair;
        public int Written;
        public long LastUnix;
    }

    private static string FinalText(State st) =>
        st.UsePips ? $"{st.Acc:+0.0;-0.0} pips" : $"{Math.Exp(st.Acc):F5}";

    public static (int Years, int Minutes) Generate(
        CandleDatabase db, string targetSymbol, string indexSymbol, string pair,
        string algorithm, int pairPipPoints,
        Action<string>? log = null, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        db.DeleteSymbol(targetSymbol);
        var st = new State { UsePips = IndexAlgorithms.IsPips(algorithm) };
        int years = RunRange(db, targetSymbol, indexSymbol, pair, pairPipPoints, 0, long.MaxValue,
            st, ct, progress);
        db.FlushAll();
        progress?.Report(1.0);
        if (st.Written == 0)
        {
            log?.Invoke($"{targetSymbol}: no minutes where both {indexSymbol} and {pair} have data");
            return (years, 0);
        }
        log?.Invoke(
            $"{targetSymbol}: {st.Written:N0} minutes written, " +
            $"{UnixToUtc(st.LastUnix):yyyy-MM-dd HH:mm} last, final {FinalText(st)} " +
            $"({CurrencyOf(pair)} from {pair} and {indexSymbol}, {algorithm})");
        return (years, st.Written);
    }

    public static int Refresh(
        CandleDatabase db, string targetSymbol, string indexSymbol, string pair,
        string algorithm, int pairPipPoints, long endUnix,
        Action<string>? log = null, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var lastTarget = db.LastFilledMinuteUtc(targetSymbol);
        if (lastTarget == null)
            return Generate(db, targetSymbol, indexSymbol, pair, algorithm, pairPipPoints,
                log, ct, progress).Minutes;

        long lastTargetUnix = ((DateTimeOffset)lastTarget.Value).ToUnixTimeSeconds();
        var lastIndex = db.LastFilledMinuteUtc(indexSymbol);
        var lastPair = db.LastFilledMinuteUtc(pair);
        if (lastIndex == null || lastPair == null)
        {
            progress?.Report(1.0);
            log?.Invoke($"{(lastIndex == null ? indexSymbol : pair)}: no data");
            return 0;
        }
        long to = Math.Min(
            ((DateTimeOffset)lastIndex.Value).ToUnixTimeSeconds(),
            ((DateTimeOffset)lastPair.Value).ToUnixTimeSeconds());
        if (endUnix > 0) to = Math.Min(to, endUnix);
        if (to <= lastTargetUnix)
        {
            progress?.Report(1.0);
            log?.Invoke($"{targetSymbol}: up to date");
            return 0;
        }

        var st = Resume(db, targetSymbol, indexSymbol, pair, lastTargetUnix, IndexAlgorithms.IsPips(algorithm));
        if (st == null)
            return Generate(db, targetSymbol, indexSymbol, pair, algorithm, pairPipPoints,
                log, ct, progress).Minutes;

        RunRange(db, targetSymbol, indexSymbol, pair, pairPipPoints, lastTargetUnix + 60, to,
            st, ct, progress);
        db.FlushAll();
        progress?.Report(1.0);
        log?.Invoke($"{targetSymbol}: refreshed {st.Written:N0} minutes, final {FinalText(st)}");
        return st.Written;
    }

    public static Candle[] Compute(
        CandleDatabase db, Candle[] indexCandles, string pair, string algorithm, int pairPipPoints,
        long endUnix, CancellationToken ct = default)
    {
        var result = new List<Candle>();
        var st = new State { UsePips = IndexAlgorithms.IsPips(algorithm) };
        long end = endUnix > 0 ? endUnix : long.MaxValue;
        bool usdBase = UsdIsBase(pair);
        Action<long, int> write = (t, v) => result.Add(DollarIndexSymbol.Flat(t, v));
        int i = 0;
        while (i < indexCandles.Length && indexCandles[i].MinuteUnixSeconds <= end)
        {
            ct.ThrowIfCancellationRequested();
            long from = indexCandles[i].MinuteUnixSeconds;
            long yearTo = Math.Min(end, YearStartUnix(UnixToUtc(from).Year + 1) - 60);
            int k = i;
            while (k < indexCandles.Length && indexCandles[k].MinuteUnixSeconds <= yearTo) k++;
            var pairCandles = db.ReadRange(pair, UnixToUtc(from), UnixToUtc(yearTo));
            Step(new ArraySegment<Candle>(indexCandles, i, k - i), pairCandles, usdBase, pairPipPoints,
                st, write, ct);
            i = k;
        }
        return result.ToArray();
    }

    public static List<Candle> Tail(
        CandleDatabase db, string targetSymbol, string indexSymbol, string pair,
        string algorithm, int pairPipPoints, IReadOnlyList<Candle> indexTail, IReadOnlyList<Candle>? pairLive)
    {
        var tail = new List<Candle>();
        var lastTarget = db.LastFilledMinuteUtc(targetSymbol);
        if (lastTarget == null) return tail;
        long lastTargetUnix = ((DateTimeOffset)lastTarget.Value).ToUnixTimeSeconds();
        long to = DollarIndexSymbol.NowMinuteUnix();
        if (to <= lastTargetUnix) return tail;
        var st = Resume(db, targetSymbol, indexSymbol, pair, lastTargetUnix, IndexAlgorithms.IsPips(algorithm));
        if (st == null) return tail;

        var from = UnixToUtc(lastTargetUnix + 60);
        var indexCandles = DollarIndexSymbol.WithLive(
            db.ReadRange(indexSymbol, from, UnixToUtc(to)), indexTail, lastTargetUnix, to);
        var pairCandles = DollarIndexSymbol.WithLive(
            db.ReadRange(pair, from, UnixToUtc(to)), pairLive, lastTargetUnix, to);
        Step(indexCandles, pairCandles, UsdIsBase(pair), pairPipPoints, st,
            (t, v) => tail.Add(DollarIndexSymbol.Flat(t, v)), CancellationToken.None);
        return tail;
    }

    private static State? Resume(
        CandleDatabase db, string targetSymbol, string indexSymbol, string pair, long lastTargetUnix,
        bool usePips)
    {
        var targetCandle = ReadAt(db, targetSymbol, lastTargetUnix);
        var indexCandle = ReadAt(db, indexSymbol, lastTargetUnix);
        var pairCandle = ReadAt(db, pair, lastTargetUnix);
        if (targetCandle is not { Avg: > 0 } || indexCandle is not { Avg: > 0 }
            || pairCandle is not { Avg: > 0 })
            return null;
        return new State
        {
            Started = true,
            LastUnix = lastTargetUnix,
            UsePips = usePips,
            Acc = usePips
                ? (targetCandle.Value.Avg - PriceScale) / (double)PipScale
                : Math.Log(targetCandle.Value.Avg / (double)PriceScale),
            LastIndex = indexCandle.Value.Avg,
            LastPair = pairCandle.Value.Avg,
        };
    }

    private static int RunRange(
        CandleDatabase db, string targetSymbol, string indexSymbol, string pair, int pairPipPoints,
        long fromUnix, long toUnix, State st, CancellationToken ct, IProgress<double>? progress)
    {
        var years = db.ExistingYears(indexSymbol);
        if (years.Count == 0) return 0;

        long from = Math.Max(fromUnix, YearStartUnix(years[0]));
        long to = Math.Min(toUnix, YearStartUnix(years[^1] + 1) - 60);
        if (to < from) return 0;

        var span = years.Where(y => YearStartUnix(y + 1) > from && YearStartUnix(y) <= to).ToList();
        bool usdBase = UsdIsBase(pair);
        Action<long, int> write = (t, v) => db.WriteMinute(targetSymbol, UnixToUtc(t), v, v, v, false);
        int done = 0;
        foreach (int year in span)
        {
            ct.ThrowIfCancellationRequested();
            long yearFrom = Math.Max(from, YearStartUnix(year));
            long yearTo = Math.Min(to, YearStartUnix(year + 1) - 60);
            var indexCandles = db.ReadRange(indexSymbol, UnixToUtc(yearFrom), UnixToUtc(yearTo));
            var pairCandles = db.ReadRange(pair, UnixToUtc(yearFrom), UnixToUtc(yearTo));
            Step(indexCandles, pairCandles, usdBase, pairPipPoints, st, write, ct);
            done++;
            progress?.Report((double)done / span.Count);
        }
        return span.Count;
    }

    private static void Step(
        IReadOnlyList<Candle> indexCandles, IReadOnlyList<Candle> pairCandles,
        bool usdBase, int pairPipPoints, State st, Action<long, int> write, CancellationToken ct)
    {
        int i = 0;
        int j = 0;
        while (i < indexCandles.Count && j < pairCandles.Count)
        {
            ct.ThrowIfCancellationRequested();
            long ti = indexCandles[i].MinuteUnixSeconds;
            long tj = pairCandles[j].MinuteUnixSeconds;
            if (ti < tj) { i++; continue; }
            if (tj < ti) { j++; continue; }

            double indexValue = indexCandles[i].Avg;
            double pairValue = pairCandles[j].Avg;
            i++;
            j++;
            if (indexValue <= 0 || pairValue <= 0) continue;

            if (!st.Started)
            {
                st.Started = true;
                st.Acc = 0;
                st.LastIndex = indexValue;
                st.LastPair = pairValue;
                WriteStep(ti, st, write);
                continue;
            }

            double u = st.UsePips
                ? (indexValue - st.LastIndex) / PipScale
                : Math.Log(indexValue / st.LastIndex);
            double r = st.UsePips
                ? (pairValue - st.LastPair) / pairPipPoints
                : Math.Log(pairValue / st.LastPair);
            st.LastIndex = indexValue;
            st.LastPair = pairValue;
            st.Acc += usdBase ? u - r : u + r;
            WriteStep(ti, st, write);
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
