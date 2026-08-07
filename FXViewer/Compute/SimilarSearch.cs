using FXViewer.Chart;
using FXViewer.Storage;

namespace FXViewer.Compute;

public sealed record SearchParams(int SmoothMinutes, int ZoomPercent, int StepMinutes);

public sealed record SearchTarget(string Symbol, IReadOnlyList<Candle> Minutes);

public static class SimilarSearch
{
    public const int MinWindowMinutes = 120;
    internal const double MinOverlapShare = 0.6;
    internal const int MaxResults = 100;

    internal readonly record struct Match(
        double Sim, double Score, double Rho, double Zoom, double MeanA, double MeanB, int Overlap,
        bool Mirrored);

    public static FindData Search(string template, IReadOnlyList<Candle> templateMinutes,
        IReadOnlyList<SearchTarget> targets, long fragStartUnix, long fragEndUnix, SearchParams p,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (templateMinutes.Count == 0) throw new InvalidOperationException("Template symbol has no data");
        if (targets.Count == 0) throw new InvalidOperationException("Pick at least one target symbol");
        foreach (var t in targets)
            if (t.Minutes.Count == 0)
                throw new InvalidOperationException($"Target symbol {t.Symbol} has no data");

        var w = WeekendCompressor.Instance;
        long vStart = FloorHour(w.ToVirtual(fragStartUnix));
        long vEnd = CeilHour(w.ToVirtual(fragEndUnix));
        int windowMinutes = (int)((vEnd - vStart) / 60);
        if (windowMinutes < MinWindowMinutes)
            throw new InvalidOperationException(
                $"Selection covers {windowMinutes} min of trading time, need at least {MinWindowMinutes} min");

        int bucketMinutes = BucketMinutesFor(windowMinutes);
        long bucketSec = bucketMinutes * 60L;
        long vBase = FloorHour(w.ToVirtual(templateMinutes[0].MinuteUnixSeconds));
        long vTop = Math.Max(w.ToVirtual(templateMinutes[^1].MinuteUnixSeconds) + 60, vEnd);
        foreach (var t in targets)
        {
            vBase = Math.Min(vBase, FloorHour(w.ToVirtual(t.Minutes[0].MinuteUnixSeconds)));
            vTop = Math.Max(vTop, w.ToVirtual(t.Minutes[^1].MinuteUnixSeconds) + 60);
        }
        int totalMinutes = (int)((vTop - vBase) / 60);
        int halfWindowMinutes = Math.Max(0, p.SmoothMinutes / 2);

        var templatePoints = BuildPoints(templateMinutes, vBase, totalMinutes, halfWindowMinutes, bucketMinutes);
        ct.ThrowIfCancellationRequested();

        int len = windowMinutes / bucketMinutes;
        int fragB0 = (int)((vStart - vBase) / bucketSec);
        var fragIdx = new List<int>(len);
        var fragVal = new List<double>(len);
        for (int i = 0; i < len; i++)
        {
            int idx = fragB0 + i;
            if (idx < 0 || idx >= templatePoints.Length || double.IsNaN(templatePoints[idx])) continue;
            fragIdx.Add(i);
            fragVal.Add(templatePoints[idx]);
        }
        int minOverlap = (int)Math.Ceiling(len * MinOverlapShare);
        if (fragIdx.Count < minOverlap)
            throw new InvalidOperationException(
                $"Selection has too little data: {fragIdx.Count}/{len} points");

        int stepB = Math.Max(1, (int)Math.Round((double)p.StepMinutes / bucketMinutes));
        int candCount = fragB0 >= len ? (fragB0 - len) / stepB + 1 : 0;
        if (candCount <= 0)
            throw new InvalidOperationException("No history before the selection to search in");

        double maxZoom = 1 + Math.Max(0, p.ZoomPercent) / 100.0;
        double minZoom = 1 / maxZoom;
        var ai = fragIdx.ToArray();
        var av = fragVal.ToArray();
        var results = new List<FindResult>();
        for (int ti = 0; ti < targets.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var target = targets[ti];
            var points = BuildPoints(target.Minutes, vBase, totalMinutes, halfWindowMinutes, bucketMinutes);
            var matches = ScanCandidates(points, ai, av, len, minOverlap, candCount, stepB, minZoom, maxZoom,
                f => progress?.Report((ti + f) / targets.Count), ct);
            results.AddRange(Rank(matches, target.Symbol, len, stepB, vBase, bucketSec));
        }
        results.Sort((a, b) => b.Sim.CompareTo(a.Sim));
        if (results.Count > MaxResults) results.RemoveRange(MaxResults, results.Count - MaxResults);
        progress?.Report(1.0);
        return new FindData(FindStore.CurrentVersion, template, w.ToReal(vStart), w.ToRealEnd(vEnd),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), windowMinutes, results,
            targets.Select(t => t.Symbol).ToList(), p.SmoothMinutes, p.ZoomPercent, p.StepMinutes);
    }

    private static Match[] ScanCandidates(double[] points, int[] ai, double[] av, int len, int minOverlap,
        int candCount, int stepB, double minZoom, double maxZoom, Action<double> report, CancellationToken ct)
    {
        var matches = new Match[candCount];
        int done = 0;
        int reportEvery = Math.Max(1, candCount / 200);
        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, candCount, po, k =>
        {
            matches[k] = ScoreWindow(points, k * stepB, ai, av, len, minOverlap, minZoom, maxZoom,
                true, true);
            int d = Interlocked.Increment(ref done);
            if (d % reportEvery == 0) report((double)d / candCount);
        });
        return matches;
    }

    internal static Match ScoreWindow(double[] points, int p, int[] ai, double[] av, int len,
        int minOverlap, double minZoom, double maxZoom, bool straight, bool mirrored,
        int maxIdx = int.MaxValue)
    {
        int n = 0;
        int lim = Math.Min(points.Length, maxIdx);
        double sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
        for (int j = 0; j < ai.Length; j++)
        {
            int idx = p + ai[j];
            if (idx < 0) continue;
            if (idx >= lim) break;
            double vb = points[idx];
            if (double.IsNaN(vb)) continue;
            double va = av[j];
            n++;
            sumA += va;
            sumB += vb;
            sumAA += va * va;
            sumBB += vb * vb;
            sumAB += va * vb;
        }
        if (n == 0) return default;
        double meanA = sumA / n;
        double meanB = sumB / n;
        var best = new Match(0, 0, 0, 1, meanA, meanB, n, false);
        if (n < minOverlap) return best;
        double sxx = sumAA - n * meanA * meanA;
        double syy = sumBB - n * meanB * meanB;
        double sxy = sumAB - n * meanA * meanB;
        if (sxx <= 0 || syy <= 0) return best;
        double rho = sxy / Math.Sqrt(sxx * syy);
        for (int mirror = 0; mirror < 2; mirror++)
        {
            if (mirror == 0 ? !straight : !mirrored) continue;
            double sxyM = mirror == 0 ? sxy : -sxy;
            double zoom = Math.Clamp(sxyM / syy, minZoom, maxZoom);
            double residual = sxx - 2 * zoom * sxyM + zoom * zoom * syy;
            double score = 1 - residual / sxx;
            double sim = score * Math.Sqrt((double)n / len);
            if (sim > best.Sim)
                best = new Match(sim, score, mirror == 0 ? rho : -rho, zoom, meanA, meanB, n, mirror == 1);
        }
        return best;
    }

    private static List<FindResult> Rank(Match[] matches, string symbol, int len, int stepB, long vBase,
        long bucketSec)
    {
        var w = WeekendCompressor.Instance;
        var order = Enumerable.Range(0, matches.Length)
            .Where(k => matches[k].Sim > 0)
            .OrderByDescending(k => matches[k].Sim)
            .ToArray();
        var results = new List<FindResult>();
        var keptPos = new List<int>();
        foreach (var k in order)
        {
            if (results.Count >= MaxResults) break;
            int p = k * stepB;
            bool dup = false;
            foreach (var q in keptPos)
                if (Math.Abs(p - q) < len)
                {
                    dup = true;
                    break;
                }
            if (dup) continue;
            keptPos.Add(p);
            var m = matches[k];
            results.Add(new FindResult(w.ToReal(vBase + p * bucketSec), m.Sim, m.Score, m.Rho, m.Zoom,
                m.Mirrored, m.Overlap, m.MeanA, m.MeanB, symbol));
        }
        return results;
    }

    internal static int BucketMinutesFor(int windowMinutes) =>
        windowMinutes >= 3 * 1440 ? 15 : windowMinutes >= 720 ? 5 : 1;

    internal static double[] BuildPoints(IReadOnlyList<Candle> minutes, long vBase, int totalMinutes,
        int halfWindowMinutes, int bucketMinutes)
    {
        var w = WeekendCompressor.Instance;
        var minuteVals = new double[totalMinutes];
        Array.Fill(minuteVals, double.NaN);
        foreach (var c in minutes)
        {
            int idx = (int)((w.ToVirtual(c.MinuteUnixSeconds) - vBase) / 60);
            if (idx >= 0 && idx < totalMinutes && double.IsNaN(minuteVals[idx])) minuteVals[idx] = c.Avg;
        }
        return Downsample(Smooth(minuteVals, halfWindowMinutes), bucketMinutes);
    }

    private static double[] Smooth(double[] minute, int halfWindowMinutes)
    {
        int n = minute.Length;
        if (halfWindowMinutes <= 0) return minute;
        var sum = new double[n + 1];
        var cnt = new int[n + 1];
        for (int i = 0; i < n; i++)
        {
            bool has = !double.IsNaN(minute[i]);
            sum[i + 1] = sum[i] + (has ? minute[i] : 0);
            cnt[i + 1] = cnt[i] + (has ? 1 : 0);
        }
        int minSamples = halfWindowMinutes + 1;
        var smoothed = new double[n];
        for (int i = 0; i < n; i++)
        {
            int lo = Math.Max(0, i - halfWindowMinutes);
            int hi = Math.Min(n - 1, i + halfWindowMinutes);
            int c = cnt[hi + 1] - cnt[lo];
            smoothed[i] = c >= minSamples ? (sum[hi + 1] - sum[lo]) / c : double.NaN;
        }
        return smoothed;
    }

    private static double[] Downsample(double[] smoothed, int bucketMinutes)
    {
        if (bucketMinutes == 1) return smoothed;
        int count = (smoothed.Length + bucketMinutes - 1) / bucketMinutes;
        var points = new double[count];
        for (int p = 0; p < count; p++)
        {
            double acc = 0;
            int c = 0;
            int end = Math.Min(smoothed.Length, (p + 1) * bucketMinutes);
            for (int i = p * bucketMinutes; i < end; i++)
            {
                if (double.IsNaN(smoothed[i])) continue;
                acc += smoothed[i];
                c++;
            }
            points[p] = c > 0 ? acc / c : double.NaN;
        }
        return points;
    }

    internal static long FloorHour(long v) => v - v % 3600;

    internal static long CeilHour(long v)
    {
        long m = v % 3600;
        return m == 0 ? v : v + 3600 - m;
    }
}
