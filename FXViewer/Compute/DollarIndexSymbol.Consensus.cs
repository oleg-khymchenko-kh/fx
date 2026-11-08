using System.IO;
using Fx.Consensus;
using FXViewer.Storage;

namespace FXViewer.Compute;

public static partial class DollarIndexSymbol
{
    private const string ConsensusMarkerFile = "consensus.key";
    private static readonly long ConsensusLookbackSeconds = (long)CausalIndex.Lookback * 60 * 7 / 5 + 14 * 86400L;
    private static readonly long ConsensusPreStartSeconds =
        CausalIndex.FloorWeeks * CausalIndex.WeekSeconds + ConsensusLookbackSeconds;
    private static readonly Dictionary<string, CausalIndex.FloorBook> ConsensusBooks = new();
    private static readonly Dictionary<string, ConsensusPart> ConsensusPrefixes = new();

    private sealed record ConsensusRun(Candle[] Stored, Candle[] New, string Summary);

    private sealed record ConsensusPart(long LoadFrom, long LastStored, long[] Times, double[][] Prices, int[] Q);

    private sealed record ConsensusVariant(double[] Weights, ConsensusIndex.Options Options, string Name);

    private static ConsensusVariant VariantOf(IReadOnlyList<string> pairs, string method)
    {
        bool v2 = IndexMethods.IsConsensus2(method);
        return new ConsensusVariant(
            pairs.Select(p => ConsensusIndex.PairWeight(p, v2)).ToArray(),
            v2 ? ConsensusIndex.Options.Anchored : ConsensusIndex.Options.Default,
            v2 ? "heavy+returns" : "ranked");
    }

    private static string DataKey(IReadOnlyList<string> pairs, long startUnix, bool usePips) =>
        $"{string.Join(",", pairs.Select(p => p.ToUpperInvariant()))}|{startUnix}|{(usePips ? "pips" : "percent")}";

    private static string MarkerOf(IReadOnlyList<string> pairs, string method, long startUnix, bool usePips) =>
        $"causal v{CausalIndex.Version}|{VariantOf(pairs, method).Name}|{DataKey(pairs, startUnix, usePips)}";

    private static string MarkerPath(CandleDatabase db, string symbol) =>
        Path.Combine(db.SymbolDirectory(symbol), ConsensusMarkerFile);

    private static string? ReadMarker(CandleDatabase db, string symbol)
    {
        try
        {
            var path = MarkerPath(db, symbol);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void MarkStored(CandleDatabase db, string symbol, IReadOnlyList<string>? pairs, long startUnix,
        string method, string algorithm)
    {
        if (!IndexMethods.IsConsensus(method)) return;
        ForgetConsensus(symbol);
        var dir = db.SymbolDirectory(symbol);
        if (!Directory.Exists(dir)) return;
        var path = Path.Combine(dir, ConsensusMarkerFile);
        var temp = path + ".tmp";
        File.WriteAllText(temp, MarkerOf(Effective(pairs), method, startUnix, IndexAlgorithms.IsPips(algorithm)));
        File.Move(temp, path, true);
    }

    private static void ForgetConsensus(string symbol)
    {
        string suffix = "|" + symbol.ToUpperInvariant();
        lock (ConsensusPrefixes)
            foreach (var k in ConsensusPrefixes.Keys.Where(k => k.EndsWith(suffix, StringComparison.Ordinal)).ToList())
                ConsensusPrefixes.Remove(k);
    }

    private static CausalIndex.FloorBook FloorBookOf(string key, bool fresh)
    {
        lock (ConsensusBooks)
        {
            if (fresh || !ConsensusBooks.TryGetValue(key, out var book))
            {
                book = new CausalIndex.FloorBook();
                ConsensusBooks[key] = book;
            }
            return book;
        }
    }

    private static long ConsensusOrigin(CandleDatabase db, IReadOnlyList<string> pairs, long startUnix)
    {
        int minYear = int.MaxValue;
        foreach (var pair in pairs)
            foreach (var y in db.ExistingYears(pair))
                minYear = Math.Min(minYear, y);
        return minYear == int.MaxValue
            ? long.MaxValue
            : Math.Max(startUnix - ConsensusPreStartSeconds, YearStartUnix(minYear));
    }

    private static Candle[] ComputeConsensus(
        CandleDatabase db, IReadOnlyList<string> pairs, string method, long startUnix, long endUnix, bool usePips,
        Func<string, int> pipPoints, CancellationToken ct, Action<string>? log, IProgress<double>? progress,
        string? storedSymbol)
    {
        var run = RunConsensus(db, pairs, method, startUnix, endUnix, usePips, pipPoints, ct, progress, storedSymbol,
            null, true);
        if (run == null) return [];
        log?.Invoke($"{method} index{(storedSymbol != null ? " " + storedSymbol : "")}: {run.Summary}");
        if (run.Stored.Length == 0) return run.New;
        var all = new Candle[run.Stored.Length + run.New.Length];
        run.Stored.CopyTo(all, 0);
        run.New.CopyTo(all, run.Stored.Length);
        return all;
    }

    private static List<Candle> TailConsensus(
        CandleDatabase db, string targetSymbol, IReadOnlyList<string> pairs, string method, long startUnix,
        long endUnix, bool usePips, Func<string, int> pipPoints, IReadOnlyDictionary<string, Candle[]> livePairs)
    {
        if (db.LastFilledMinuteUtc(targetSymbol) == null) return new List<Candle>();
        if (ReadMarker(db, targetSymbol) != MarkerOf(pairs, method, startUnix, usePips)) return new List<Candle>();
        long to = Math.Min(endUnix > 0 ? endUnix : long.MaxValue, NowMinuteUnix());
        var run = RunConsensus(db, pairs, method, startUnix, to, usePips, pipPoints, CancellationToken.None, null,
            targetSymbol, livePairs, false);
        return run == null ? new List<Candle>() : run.New.ToList();
    }

    private static ConsensusRun? RunConsensus(
        CandleDatabase db, IReadOnlyList<string> pairs, string method, long startUnix, long endUnix, bool usePips,
        Func<string, int> pipPoints, CancellationToken ct, IProgress<double>? progress, string? storedSymbol,
        IReadOnlyDictionary<string, Candle[]>? livePairs, bool returnStored)
    {
        if (pairs.Count == 0) return null;
        long origin = ConsensusOrigin(db, pairs, startUnix);
        if (origin == long.MaxValue) return null;
        string dataKey = DataKey(pairs, startUnix, usePips);
        string marker = MarkerOf(pairs, method, startUnix, usePips);
        var variant = VariantOf(pairs, method);

        long lastStored = 0;
        if (storedSymbol != null && db.LastFilledMinuteUtc(storedSymbol) is { } last)
            lastStored = ((DateTimeOffset)last).ToUnixTimeSeconds();
        bool sameAlgorithm = storedSymbol != null && ReadMarker(db, storedSymbol) == marker;
        bool fromScratch = lastStored < startUnix || !sameAlgorithm;
        if (!fromScratch && lastStored >= endUnix)
            return new ConsensusRun(returnStored ? ReadStored(db, storedSymbol!, startUnix, lastStored) : [], [],
                "up to date");

        var book = FloorBookOf(dataKey, fromScratch);
        long loadFrom = origin;
        if (!fromScratch)
        {
            long firstNewUnix = lastStored + 60;
            long week = CausalIndex.WeekOf(firstNewUnix);
            bool booked;
            lock (book) booked = CausalIndex.HasWeeks(book, week - CausalIndex.FloorWeeks, week - 1);
            long need = booked
                ? firstNewUnix - ConsensusLookbackSeconds
                : CausalIndex.WeekOrigin + (week - CausalIndex.FloorWeeks) * CausalIndex.WeekSeconds
                  - ConsensusLookbackSeconds;
            loadFrom = Math.Max(origin, need);
        }
        var pips = pairs.Select(p => (double)pipPoints(p)).ToArray();

        var prefix = fromScratch
            ? new ConsensusPart(loadFrom, lastStored, [], pairs.Select(_ => Array.Empty<double>()).ToArray(), [])
            : StoredPart(db, pairs, pips, storedSymbol!, marker, startUnix, loadFrom, lastStored, livePairs != null, ct);
        var (suffixTimes, suffixPrices) = ReadGrid(db, pairs, pips,
            fromScratch ? loadFrom : Math.Max(loadFrom, lastStored + 60), endUnix, fromScratch ? 0 : lastStored,
            livePairs, ct);
        progress?.Report(0.1);

        int np = prefix.Times.Length;
        int n = np + suffixTimes.Count;
        var series = new CausalIndex.Series
        {
            Times = new long[n],
            Prices = new double[pairs.Count][],
            UsdBase = pairs.Select(UsdIsBase).ToArray(),
            Pip = pips,
            Pips = usePips,
            Weights = variant.Weights,
        };
        prefix.Times.CopyTo(series.Times, 0);
        suffixTimes.CopyTo(series.Times, np);
        for (int i = 0; i < pairs.Count; i++)
        {
            series.Prices[i] = new double[n];
            prefix.Prices[i].CopyTo(series.Prices[i], 0);
            suffixPrices[i].CopyTo(series.Prices[i], np);
        }
        var q = new int[n];
        prefix.Q.CopyTo(q, 0);

        int startIdx = Array.FindIndex(series.Times, u => u >= startUnix);
        if (startIdx < 0) return new ConsensusRun([], [], "no minutes after the start");
        int knownFrom = startIdx;
        int firstNew = startIdx;
        if (!fromScratch)
        {
            if (startUnix < loadFrom) startIdx = -1;
            knownFrom = Math.Max(0, startIdx);
            firstNew = np;
        }
        Candle[] storedPart = returnStored && !fromScratch ? ReadStored(db, storedSymbol!, startUnix, lastStored) : [];
        if (firstNew >= n) return new ConsensusRun(storedPart, [], "up to date");

        CausalIndex.Stats stats;
        lock (book)
            CausalIndex.Run(series, variant.Options, startIdx, q, knownFrom, firstNew, book, ct,
                out stats, v => progress?.Report(0.1 + 0.9 * v), null, loadFrom == origin);
        var fresh = new Candle[n - firstNew];
        for (int t = firstNew; t < n; t++) fresh[t - firstNew] = Flat(series.Times[t], q[t]);
        string mode = !fromScratch ? "appended"
            : storedSymbol != null && lastStored >= startUnix ? "rebuilt from scratch (stored values are from another algorithm or setup)"
            : "from scratch";
        return new ConsensusRun(storedPart, fresh, $"{mode}, {n:N0} grid minutes loaded, {stats.Summary}");
    }

    private static ConsensusPart StoredPart(
        CandleDatabase db, IReadOnlyList<string> pairs, double[] pips, string storedSymbol, string marker,
        long startUnix, long loadFrom, long lastStored, bool cache, CancellationToken ct)
    {
        string cacheKey = marker + "|" + storedSymbol.ToUpperInvariant();
        if (cache)
            lock (ConsensusPrefixes)
                if (ConsensusPrefixes.TryGetValue(cacheKey, out var hit)
                    && hit.LoadFrom == loadFrom && hit.LastStored == lastStored)
                    return hit;

        var (times, prices) = ReadGrid(db, pairs, pips, loadFrom, lastStored, lastStored, null, ct);
        var known = new Dictionary<long, int>();
        foreach (var c in ReadStored(db, storedSymbol, Math.Max(startUnix, loadFrom), lastStored))
            known[c.MinuteUnixSeconds] = c.Avg;
        var keep = new List<int>(times.Count);
        for (int t = 0; t < times.Count; t++)
            if (times[t] < startUnix || known.ContainsKey(times[t])) keep.Add(t);
        var part = new ConsensusPart(loadFrom, lastStored,
            keep.Select(t => times[t]).ToArray(),
            prices.Select(p => keep.Select(t => p[t]).ToArray()).ToArray(),
            keep.Select(t => times[t] < startUnix ? 0 : known[times[t]]).ToArray());
        if (cache)
            lock (ConsensusPrefixes)
                ConsensusPrefixes[cacheKey] = part;
        return part;
    }

    private static (List<long> Times, List<double>[] Prices) ReadGrid(
        CandleDatabase db, IReadOnlyList<string> pairs, double[] pips, long fromUnix, long toUnix, long liveAfter,
        IReadOnlyDictionary<string, Candle[]>? livePairs, CancellationToken ct)
    {
        var times = new List<long>();
        var prices = new List<double>[pairs.Count];
        for (int i = 0; i < pairs.Count; i++) prices[i] = new List<double>();
        if (toUnix < fromUnix) return (times, prices);
        var lists = new List<Candle>[pairs.Count];
        int lastYear = DateTimeOffset.FromUnixTimeSeconds(Math.Min(toUnix, NowMinuteUnix() + 86400)).Year;
        for (int year = DateTimeOffset.FromUnixTimeSeconds(fromUnix).Year; year <= lastYear; year++)
        {
            long yearFrom = Math.Max(fromUnix, YearStartUnix(year));
            long yearTo = Math.Min(toUnix, YearStartUnix(year + 1) - 60);
            if (yearTo < yearFrom) continue;
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < pairs.Count; i++)
            {
                var stored = db.ReadRange(pairs[i], UnixToUtc(yearFrom), UnixToUtc(yearTo), includeWide: true);
                lists[i] = livePairs == null
                    ? stored
                    : WithLive(stored, livePairs.GetValueOrDefault(pairs[i]), Math.Max(liveAfter, yearFrom - 60),
                        yearTo).ToList();
            }
            MergeCommon(lists, pips, times, prices);
        }
        return (times, prices);
    }

    private static Candle[] ReadStored(CandleDatabase db, string symbol, long fromUnix, long toUnix) =>
        db.ReadRange(symbol, UnixToUtc(fromUnix), UnixToUtc(toUnix))
            .Where(c => c.Avg > 0)
            .Select(c => Flat(c.MinuteUnixSeconds, c.Avg))
            .ToArray();

    private static void MergeCommon(List<Candle>[] lists, double[] pips, List<long> times, List<double>[] prices)
    {
        var idx = new int[lists.Length];
        var current = new double[lists.Length];
        while (true)
        {
            long t = long.MaxValue;
            for (int i = 0; i < lists.Length; i++)
                if (idx[i] < lists[i].Count && lists[i][idx[i]].MinuteUnixSeconds < t)
                    t = lists[i][idx[i]].MinuteUnixSeconds;
            if (t == long.MaxValue) break;

            bool all = true;
            for (int i = 0; i < lists.Length; i++)
                if (idx[i] >= lists[i].Count || lists[i][idx[i]].MinuteUnixSeconds != t
                    || !TryEffectivePrice(lists[i][idx[i]], pips[i], out current[i]))
                    all = false;
            if (all)
            {
                times.Add(t);
                for (int i = 0; i < lists.Length; i++) prices[i].Add(current[i]);
            }
            for (int i = 0; i < lists.Length; i++)
                if (idx[i] < lists[i].Count && lists[i][idx[i]].MinuteUnixSeconds == t)
                    idx[i]++;
        }
    }
}
