using FXViewer.Chart;
using FXViewer.Storage;

namespace FXViewer.Compute;

public sealed record Levels2FindTarget(
    string Pair, int PipPoints, SeriesTransform? Transform = null, int PriceMul = 1, int PriceDigits = 5,
    IReadOnlyList<Candle>? Minutes = null);

public sealed record Levels2Tables(LevelSizeTable Left, LevelSizeTable Right)
{
    public static Levels2Tables Of(IEnumerable<LevelStep> left, IEnumerable<LevelStep> right) =>
        new(new LevelSizeTable(left), new LevelSizeTable(right));
}

public sealed record Levels2LevelRule(
    double ValuePoints, bool Required, double TolUpPips, double TolDownPips, int Sequence,
    long TimeUnix = 0, bool? Top = null, long? MinGapSeconds = null, long? MaxGapSeconds = null);

public sealed record Levels2SearchTarget(Levels2FindTarget Target, IReadOnlyList<Candle> Minutes);

public static class Levels2Search
{
    private const double ZoomStep = 1.15;
    private const int MaxGenericScan = 50_000;
    private const int MaxNodesPerStart = 20_000;

    private sealed record Point(int Rule, double Level, bool Top, bool Required, double Up, double Down,
        int Sequence, long TimeVirtual, long MinGap, long MaxGap);

    private sealed class Series
    {
        public required int[] High;
        public required int[] Low;
        public required long[] Virtual;
        public required long[] Unix;
        public required int[] NextHigher;
        public required int[] NextLower;

        public int Count => High.Length;
    }

    private sealed record Match(int Preferred, double Sim, double Pace, double Zoom, bool Mirrored,
        double Offset, int[] Minutes, int[] Values, int[] Points);

    private sealed class Pattern
    {
        public required Point[] Points;
        public required bool[] OptionalAfter;
        public required int FirstRequired;
        public required int Anchor;
        public required int PreferredTotal;
        public required double[] Zooms;
        public required long MinSpanSeconds;
    }

    private sealed class Context
    {
        public required Series Series;
        public required Pattern Pattern;
        public required int Limit;
        public readonly List<int>[] Candidates;
        public readonly int[] Minutes;
        public readonly int[] Values;
        public readonly int[] Points;
        public readonly List<double> Logs = new();
        public readonly List<Match> Found = new();
        public int Depth;
        public int Nodes;
        public int Sign;
        public double Zoom;
        public Match? Best;

        public Context(int pointCount)
        {
            Candidates = new List<int>[pointCount + 1];
            for (int i = 0; i < Candidates.Length; i++) Candidates[i] = new List<int>();
            Minutes = new int[pointCount];
            Values = new int[pointCount];
            Points = new int[pointCount];
        }
    }

    public static FindData Search(string template, Levels2FindTarget patternTarget,
        IReadOnlyList<Levels2SearchTarget> targets, long fragStartUnix, long fragEndUnix, int zoomPercent,
        int minSpanMinutes, IReadOnlyList<Levels2LevelRule> rules, IProgress<double>? progress,
        CancellationToken ct, bool searchReversed = true)
    {
        if (targets.Count == 0) throw new InvalidOperationException("Pick at least one pair to search in");
        foreach (var t in targets)
            if (t.Minutes.Count == 0)
                throw new InvalidOperationException($"Target symbol {t.Target.Pair} has no data");
        var w = WeekendCompressor.Instance;
        long vStart = SimilarSearch.FloorHour(w.ToVirtual(fragStartUnix));
        long vEnd = SimilarSearch.CeilHour(w.ToVirtual(fragEndUnix));
        int windowMinutes = (int)((vEnd - vStart) / 60);
        if (windowMinutes < SimilarSearch.MinWindowMinutes)
            throw new InvalidOperationException(
                $"Selection covers {windowMinutes} min of trading time, " +
                $"need at least {SimilarSearch.MinWindowMinutes} min");
        var pattern = BuildPattern(rules, zoomPercent, minSpanMinutes, w);
        var results = new List<FindResult>();
        bool anyHistory = false;
        int passes = searchReversed ? 2 : 1;
        for (int ti = 0; ti < targets.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var target = targets[ti];
            var minutes = target.Minutes as Candle[] ?? target.Minutes.ToArray();
            long vHistory = SimilarSearch.CeilHour(w.ToVirtual(minutes[0].MinuteUnixSeconds));
            if (vHistory >= vStart) continue;
            anyHistory = true;
            long axis = vHistory + vStart;
            for (int pass = 0; pass < passes; pass++)
            {
                bool reversed = pass == 1;
                var scanMinutes = reversed ? ShiftedSymbol.Shift(minutes, axis - 60, true).ToArray() : minutes;
                var series = Prepare(scanMinutes, w);
                int step = ti * passes + pass;
                var found = Scan(series, pattern, vHistory, vStart,
                    f => progress?.Report((step + f) / (targets.Count * passes)), ct);
                foreach (var r in Rank(found, series, pattern, target.Target.Pair))
                    results.Add(reversed ? Unreverse(r, axis, w) : r);
            }
        }
        if (!anyHistory) throw new InvalidOperationException("No history before the selection to search in");
        results.Sort(ResultOrder);
        if (results.Count > SimilarSearch.MaxResults)
            results.RemoveRange(SimilarSearch.MaxResults, results.Count - SimilarSearch.MaxResults);
        progress?.Report(1.0);
        long anchorUnix = pattern.Anchor < 0 ? 0 : rules[pattern.Points[pattern.Anchor].Rule].TimeUnix;
        return new FindData(FindStore.CurrentVersion, template, w.ToReal(vStart), w.ToRealEnd(vEnd),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), windowMinutes, results,
            targets.Select(t => t.Target.Pair).ToList(), 0, zoomPercent, 0,
            FindSearchTypes.Levels2, patternTarget.Pair, 0, 0, anchorUnix, minSpanMinutes);
    }

    private static int ResultOrder(FindResult a, FindResult b)
    {
        if (a.Preferred != b.Preferred) return b.Preferred.CompareTo(a.Preferred);
        if (a.Sim != b.Sim) return b.Sim.CompareTo(a.Sim);
        return b.Score.CompareTo(a.Score);
    }

    private static Pattern BuildPattern(IReadOnlyList<Levels2LevelRule> rules, int zoomPercent, int minSpanMinutes,
        WeekendCompressor w)
    {
        var order = Enumerable.Range(0, rules.Count)
            .OrderBy(i => rules[i].Sequence > 0 ? rules[i].Sequence : int.MaxValue)
            .ThenBy(i => rules[i].TimeUnix > 0 ? rules[i].TimeUnix : long.MaxValue)
            .ThenBy(i => i)
            .ToArray();
        if (order.Count(i => rules[i].Required) < 2)
            throw new InvalidOperationException("Mark at least two levels as Required");
        double pip = DensityProfile.PipPoints;
        var points = new Point[order.Length];
        for (int k = 0; k < order.Length; k++)
        {
            var rule = rules[order[k]];
            double prev = k > 0 ? rules[order[k - 1]].ValuePoints : double.NaN;
            double next = k + 1 < order.Length ? rules[order[k + 1]].ValuePoints : double.NaN;
            points[k] = new Point(order[k], rule.ValuePoints, rule.Top ?? InferTop(prev, rule.ValuePoints, next),
                rule.Required, Math.Max(0, rule.TolUpPips) * pip, Math.Max(0, rule.TolDownPips) * pip,
                Math.Max(0, rule.Sequence), rule.TimeUnix > 0 ? w.ToVirtual(rule.TimeUnix) : -1,
                Math.Max(0, rule.MinGapSeconds ?? 0),
                rule.MaxGapSeconds is long max && max >= 0 ? max : long.MaxValue);
        }
        var optionalAfter = new bool[points.Length];
        bool optional = true;
        for (int k = points.Length - 1; k >= 0; k--)
        {
            optionalAfter[k] = optional;
            if (points[k].Required) optional = false;
        }
        int anchor = -1;
        for (int k = 0; k < points.Length; k++)
            if (points[k].Required && points[k].TimeVirtual >= 0
                && (anchor < 0 || points[k].TimeVirtual > points[anchor].TimeVirtual))
                anchor = k;
        return new Pattern
        {
            Points = points,
            OptionalAfter = optionalAfter,
            FirstRequired = Array.FindIndex(points, p => p.Required),
            Anchor = anchor,
            PreferredTotal = points.Count(p => !p.Required),
            Zooms = ZoomGrid(zoomPercent),
            MinSpanSeconds = Math.Max(0, minSpanMinutes) * 60L,
        };
    }

    private static bool InferTop(double prev, double level, double next)
    {
        bool hasPrev = !double.IsNaN(prev);
        bool hasNext = !double.IsNaN(next);
        if (hasPrev && hasNext)
        {
            if (level >= prev && level >= next) return true;
            if (level <= prev && level <= next) return false;
            return next < level;
        }
        if (hasNext) return next < level;
        if (hasPrev) return prev < level;
        return true;
    }

    private static double[] ZoomGrid(int zoomPercent)
    {
        double maxZoom = 1 + Math.Max(0, zoomPercent) / 100.0;
        int steps = maxZoom <= 1 ? 0 : (int)Math.Ceiling(Math.Log(maxZoom) / Math.Log(ZoomStep) - 1e-9);
        var zooms = new double[2 * steps + 1];
        for (int i = -steps; i <= steps; i++)
            zooms[i + steps] = steps == 0 ? 1 : Math.Pow(maxZoom, (double)i / steps);
        return zooms;
    }

    private static Series Prepare(Candle[] minutes, WeekendCompressor w)
    {
        int n = minutes.Length;
        var high = new int[n];
        var low = new int[n];
        var virt = new long[n];
        var unix = new long[n];
        for (int i = 0; i < n; i++)
        {
            high[i] = minutes[i].Max;
            low[i] = minutes[i].Min;
            unix[i] = minutes[i].MinuteUnixSeconds;
            virt[i] = w.ToVirtual(unix[i]);
        }
        var nextHigher = new int[n];
        var nextLower = new int[n];
        var stack = new int[n];
        int top = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            while (top > 0 && high[stack[top - 1]] <= high[i]) top--;
            nextHigher[i] = top > 0 ? stack[top - 1] : n;
            stack[top++] = i;
        }
        top = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            while (top > 0 && low[stack[top - 1]] >= low[i]) top--;
            nextLower[i] = top > 0 ? stack[top - 1] : n;
            stack[top++] = i;
        }
        return new Series
        {
            High = high, Low = low, Virtual = virt, Unix = unix, NextHigher = nextHigher, NextLower = nextLower,
        };
    }

    private static List<Match> Scan(Series series, Pattern pattern, long vHistory, long vStart, Action<double> report,
        CancellationToken ct)
    {
        int first = LowerBound(series.Virtual, vHistory);
        int limit = LowerBound(series.Virtual, vStart);
        var all = new List<Match>();
        if (first >= limit) return all;
        int total = limit - first;
        int done = 0;
        int reportEvery = Math.Max(1, total / 200);
        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(first, limit, po,
            () => new Context(pattern.Points.Length) { Series = series, Pattern = pattern, Limit = limit },
            (s, _, ctx) =>
            {
                ScanStart(ctx, s);
                int d = Interlocked.Increment(ref done);
                if (d % reportEvery == 0) report((double)d / total);
                return ctx;
            },
            ctx =>
            {
                lock (all) all.AddRange(ctx.Found);
            });
        return all;
    }

    private static int LowerBound(long[] values, long value)
    {
        int lo = 0, hi = values.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (values[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static void ScanStart(Context ctx, int s)
    {
        var pattern = ctx.Pattern;
        var points = pattern.Points;
        ctx.Best = null;
        ctx.Nodes = 0;
        for (int sign = 1; sign >= -1; sign -= 2)
        {
            ctx.Sign = sign;
            for (int k0 = 0; k0 <= pattern.FirstRequired; k0++)
            {
                var p = points[k0];
                bool wantTop = sign > 0 ? p.Top : !p.Top;
                int x = wantTop ? ctx.Series.High[s] : ctx.Series.Low[s];
                if (!MayContinue(ctx, k0, s, x)) continue;
                foreach (double zoom in pattern.Zooms)
                {
                    ctx.Zoom = zoom;
                    double dLo = p.Level - p.Down - sign * zoom * x;
                    double dHi = p.Level + p.Up - sign * zoom * x;
                    ctx.Depth = 0;
                    Push(ctx, k0, s, x);
                    Extend(ctx, k0, s, x, dLo, dHi, p.Required ? 0 : 1);
                    ctx.Depth = 0;
                }
            }
        }
        if (ctx.Best != null) ctx.Found.Add(ctx.Best);
    }

    private static bool MayContinue(Context ctx, int k, int t, int x)
    {
        var points = ctx.Pattern.Points;
        var list = ctx.Candidates[^1];
        for (int j = k + 1; j < points.Length; j++)
        {
            var p = points[j];
            bool wantTop = ctx.Sign > 0 ? p.Top : !p.Top;
            double bLo = double.MaxValue, bHi = double.MinValue;
            foreach (double zoom in ctx.Pattern.Zooms)
            {
                var (lo, hi) = BandOf(ctx.Sign, zoom, p, points[k], x);
                bLo = Math.Min(bLo, lo);
                bHi = Math.Max(bHi, hi);
            }
            Collect(ctx.Series, t, x, wantTop, bLo, bHi, GapLimit(ctx, t, p), list);
            foreach (int u in list)
                if (ctx.Series.Virtual[u] - ctx.Series.Virtual[t] >= p.MinGap)
                    return true;
            if (p.Required) return false;
        }
        return true;
    }

    private static int GapLimit(Context ctx, int t, Point p)
    {
        if (p.MaxGap == long.MaxValue) return ctx.Limit;
        long last = ctx.Series.Virtual[t] + p.MaxGap;
        return Math.Min(ctx.Limit, LowerBound(ctx.Series.Virtual, last + 1));
    }

    private static (double Lo, double Hi) BandOf(int sign, double zoom, Point p, Point from, int x)
    {
        double dLo = from.Level - from.Down - sign * zoom * x;
        double dHi = from.Level + from.Up - sign * zoom * x;
        return Band(sign, zoom, p, dLo, dHi);
    }

    private static (double Lo, double Hi) Band(int sign, double zoom, Point p, double dLo, double dHi)
    {
        double lo = (p.Level - p.Down - dHi) / zoom;
        double hi = (p.Level + p.Up - dLo) / zoom;
        return sign > 0 ? (lo, hi) : (-hi, -lo);
    }

    private static void Push(Context ctx, int k, int minute, int value)
    {
        ctx.Points[ctx.Depth] = k;
        ctx.Minutes[ctx.Depth] = minute;
        ctx.Values[ctx.Depth] = value;
        ctx.Depth++;
    }

    private static void Extend(Context ctx, int k, int t, int x, double dLo, double dHi, int preferred)
    {
        if (++ctx.Nodes > MaxNodesPerStart) return;
        var pattern = ctx.Pattern;
        var points = pattern.Points;
        if (pattern.OptionalAfter[k]) Finish(ctx, dLo, dHi, preferred);
        var series = ctx.Series;
        for (int j = k + 1; j < points.Length; j++)
        {
            var p = points[j];
            bool wantTop = ctx.Sign > 0 ? p.Top : !p.Top;
            var (bLo, bHi) = Band(ctx.Sign, ctx.Zoom, p, dLo, dHi);
            if (bLo <= bHi)
            {
                var list = ctx.Candidates[ctx.Depth];
                Collect(series, t, x, wantTop, bLo, bHi, GapLimit(ctx, t, p), list);
                int depth = ctx.Depth;
                for (int c = 0; c < list.Count; c++)
                {
                    int u = list[c];
                    if (series.Virtual[u] - series.Virtual[t] < p.MinGap) continue;
                    int xu = wantTop ? series.High[u] : series.Low[u];
                    double scaled = ctx.Sign * ctx.Zoom * xu;
                    double nLo = Math.Max(dLo, p.Level - p.Down - scaled);
                    double nHi = Math.Min(dHi, p.Level + p.Up - scaled);
                    if (nLo > nHi + 1e-9) continue;
                    ctx.Depth = depth;
                    Push(ctx, j, u, xu);
                    Extend(ctx, j, u, xu, nLo, nHi, preferred + (p.Required ? 0 : 1));
                    if (ctx.Nodes > MaxNodesPerStart) break;
                }
                ctx.Depth = depth;
            }
            if (p.Required) break;
        }
    }

    private static void Finish(Context ctx, double dLo, double dHi, int preferred)
    {
        var series = ctx.Series;
        var pattern = ctx.Pattern;
        int count = ctx.Depth;
        long span = series.Virtual[ctx.Minutes[count - 1]] - series.Virtual[ctx.Minutes[0]];
        if (span < pattern.MinSpanSeconds) return;
        var (sim, pace) = TimeFit(ctx, count);
        var best = ctx.Best;
        if (best != null && (preferred < best.Preferred
            || (preferred == best.Preferred && (sim < best.Sim || (sim == best.Sim && pace <= best.Pace)))))
            return;
        ctx.Best = new Match(preferred, sim, pace, ctx.Zoom, ctx.Sign < 0, (dLo + dHi) / 2,
            ctx.Minutes.AsSpan(0, count).ToArray(), ctx.Values.AsSpan(0, count).ToArray(),
            ctx.Points.AsSpan(0, count).ToArray());
    }

    private static (double Sim, double Pace) TimeFit(Context ctx, int count)
    {
        var logs = ctx.Logs;
        logs.Clear();
        var points = ctx.Pattern.Points;
        var virt = ctx.Series.Virtual;
        for (int i = 1; i < count; i++)
        {
            var a = points[ctx.Points[i - 1]];
            var b = points[ctx.Points[i]];
            if (a.TimeVirtual < 0 || b.TimeVirtual < 0) continue;
            double template = b.TimeVirtual - a.TimeVirtual;
            double found = virt[ctx.Minutes[i]] - virt[ctx.Minutes[i - 1]];
            if (template <= 0 || found <= 0) continue;
            logs.Add(Math.Log(found / template));
        }
        if (logs.Count == 0) return (1, 1);
        double mean = logs.Average();
        double sum = 0;
        foreach (double l in logs) sum += (l - mean) * (l - mean);
        return (Math.Exp(-Math.Sqrt(sum / logs.Count)), Math.Exp(-Math.Abs(mean)));
    }

    private static int FirstAbove(Series s, int t, int value)
    {
        if (value == s.High[t]) return s.NextHigher[t];
        int c = t + 1;
        while (c < s.Count && s.High[c] <= value) c = s.NextHigher[c];
        return c;
    }

    private static int FirstBelow(Series s, int t, int value)
    {
        if (value == s.Low[t]) return s.NextLower[t];
        int c = t + 1;
        while (c < s.Count && s.Low[c] >= value) c = s.NextLower[c];
        return c;
    }

    private static void Collect(Series s, int t, int x, bool wantTop, double bLo, double bHi, int limit,
        List<int> output)
    {
        output.Clear();
        var high = s.High;
        var low = s.Low;
        if (!wantTop && bHi <= x)
        {
            int exit = Math.Min(limit, FirstAbove(s, t, x));
            for (int c = t + 1; c < exit; c = s.NextLower[c])
            {
                if (low[c] < bLo) return;
                if (low[c] <= bHi) output.Add(c);
            }
            return;
        }
        if (wantTop && bLo >= x)
        {
            int exit = Math.Min(limit, FirstBelow(s, t, x));
            for (int c = t + 1; c < exit; c = s.NextHigher[c])
            {
                if (high[c] > bHi) return;
                if (high[c] >= bLo) output.Add(c);
            }
            return;
        }
        double envHi = Math.Max(x, bHi);
        double envLo = Math.Min(x, bLo);
        int runMax = int.MinValue;
        int runMin = int.MaxValue;
        int end = (int)Math.Min(limit, (long)t + 1 + MaxGenericScan);
        for (int c = t + 1; c < end; c++)
        {
            int v = wantTop ? high[c] : low[c];
            if (v >= bLo && v <= bHi && runMax <= Math.Max(x, v) && runMin >= Math.Min(x, v)) output.Add(c);
            if (high[c] > runMax) runMax = high[c];
            if (low[c] < runMin) runMin = low[c];
            if (runMax > envHi || runMin < envLo) return;
        }
    }

    private static List<FindResult> Rank(List<Match> found, Series series, Pattern pattern, string pair)
    {
        found.Sort((a, b) =>
        {
            if (a.Preferred != b.Preferred) return b.Preferred.CompareTo(a.Preferred);
            if (a.Sim != b.Sim) return b.Sim.CompareTo(a.Sim);
            return b.Pace.CompareTo(a.Pace);
        });
        var results = new List<FindResult>();
        var kept = new List<(long First, long Last)>();
        foreach (var m in found)
        {
            if (results.Count >= SimilarSearch.MaxResults) break;
            long first = series.Virtual[m.Minutes[0]];
            long last = series.Virtual[m.Minutes[^1]] + 60;
            bool dup = false;
            foreach (var (kf, kl) in kept)
            {
                long overlap = Math.Min(last, kl) - Math.Max(first, kf);
                if (overlap * 2 > Math.Min(last - first, kl - kf))
                {
                    dup = true;
                    break;
                }
            }
            if (dup) continue;
            kept.Add((first, last));
            var keyPoints = new List<FindKeyPoint>(m.Minutes.Length);
            long alignUnix = 0;
            for (int i = 0; i < m.Minutes.Length; i++)
            {
                var p = pattern.Points[m.Points[i]];
                bool top = m.Mirrored ? !p.Top : p.Top;
                keyPoints.Add(new FindKeyPoint(series.Unix[m.Minutes[i]], m.Values[i], top, p.Required,
                    p.Sequence, p.Rule));
                if (m.Points[i] == pattern.Anchor) alignUnix = series.Unix[m.Minutes[i]];
            }
            results.Add(new FindResult(series.Unix[m.Minutes[0]], m.Sim, m.Pace, 0, m.Zoom, m.Mirrored,
                m.Minutes.Length, m.Offset, 0, pair,
                Preferred: m.Preferred, PreferredTotal: pattern.PreferredTotal,
                WindowMinutes: (int)((last - first) / 60), AlignUnix: alignUnix, KeyPoints: keyPoints));
        }
        return results;
    }

    private static FindResult Unreverse(FindResult r, long axis, WeekendCompressor w)
    {
        long windowSec = r.WindowMinutes * 60L;
        long Minute(long unix) => w.ToReal(axis - 60 - w.ToVirtual(unix));
        return r with
        {
            SourceUnix = w.ToReal(axis - w.ToVirtual(r.SourceUnix) - windowSec),
            AlignUnix = r.AlignUnix > 0 ? Minute(r.AlignUnix) : 0,
            KeyPoints = r.KeyPoints?
                .Select(p => p with { Unix = Minute(p.Unix) })
                .OrderBy(p => p.Unix)
                .ToList(),
            Reversed = true,
        };
    }

    public static bool TemplateLevels(Levels2Tables tables, IReadOnlyList<Candle> minutes,
        long fragStartUnix, long fragEndUnix, List<Level2> output)
    {
        output.Clear();
        if (minutes.Count == 0 || tables.Left.IsEmpty || tables.Right.IsEmpty) return false;
        var w = WeekendCompressor.Instance;
        long vStart = SimilarSearch.FloorHour(w.ToVirtual(fragStartUnix));
        long vEnd = SimilarSearch.CeilHour(w.ToVirtual(fragEndUnix));
        if ((vEnd - vStart) / 60 < SimilarSearch.MinWindowMinutes) return false;
        FindLevels(tables, minutes, vStart, vEnd, output);
        return true;
    }

    public static bool TemplateExtremes(Levels2Tables tables, IReadOnlyList<Candle> minutes,
        long fragStartUnix, long fragEndUnix, List<Level2> output)
    {
        output.Clear();
        if (minutes.Count == 0 || tables.Left.IsEmpty) return false;
        var w = WeekendCompressor.Instance;
        long vStart = SimilarSearch.FloorHour(w.ToVirtual(fragStartUnix));
        long vEnd = SimilarSearch.CeilHour(w.ToVirtual(fragEndUnix));
        if ((vEnd - vStart) / 60 < SimilarSearch.MinWindowMinutes) return false;
        FindLevels(tables, minutes, vStart, vEnd, output, 1, 1);
        return true;
    }

    private static void FindLevels(Levels2Tables tables, IReadOnlyList<Candle> minutes,
        long vStart, long vEnd, List<Level2> output, double? minApproach = null, double? minReversal = null)
    {
        var w = WeekendCompressor.Instance;
        var finder = new Levels2Finder();
        finder.Find(MinuteSequence.Of(minutes as Candle[] ?? minutes.ToArray()),
            w.ToReal(vStart), w.ToReal(vEnd) - 1, minApproach ?? tables.Left.MinPoints,
            minReversal ?? tables.Right.MinPoints, tables.Left.MaxPoints, output);
    }
}
