using FXViewer.Chart;
using FXViewer.Storage;

namespace FXViewer.Compute;

public sealed record ZigZagPatternPoint(
    double Pips, double Minutes, double UpPips, double DownPips, double LeftMinutes,
    double RightMinutes, bool Zoom = false);

public sealed record ZigZagFindTarget(string Name, string Source, int PipPoints, PivotPoint[] Points);

public sealed record ZigZagSearchTarget(ZigZagFindTarget Target, IReadOnlyList<Candle> Minutes);

public static class ZigZagSearch
{
    private const double MaxDurationFactor = 2.0;

    public static FindData Search(string template, IReadOnlyList<Candle> templateMinutes,
        IReadOnlyList<ZigZagSearchTarget> targets, string patternSource,
        IReadOnlyList<ZigZagPatternPoint> pattern, long fragStartUnix, long fragEndUnix,
        long anchorUnix, int zoomPercent, IProgress<double>? progress, CancellationToken ct)
    {
        if (templateMinutes.Count == 0) throw new InvalidOperationException("Template symbol has no data");
        if (targets.Count == 0) throw new InvalidOperationException("Pick at least one ZigZag indicator");
        foreach (var t in targets)
            if (t.Minutes.Count == 0)
                throw new InvalidOperationException($"Target symbol {t.Target.Source} has no data");
        if (pattern.Count < 2) throw new InvalidOperationException("Pattern needs at least two points");

        var zoomIdx = new List<int>();
        for (int i = 0; i < pattern.Count; i++)
            if (pattern[i].Zoom)
                zoomIdx.Add(i);
        if (zoomIdx.Count != 2)
            throw new InvalidOperationException("Mark exactly two zoom points");
        int zoomA = zoomIdx[0];
        int zoomB = zoomIdx[1];
        if (pattern[zoomA].Pips == pattern[zoomB].Pips)
            throw new InvalidOperationException("The two zoom points must sit at different pip levels");
        double patternDur = pattern[^1].Minutes;

        var w = WeekendCompressor.Instance;
        long vStart = SimilarSearch.FloorHour(w.ToVirtual(fragStartUnix));
        long vEnd = SimilarSearch.CeilHour(w.ToVirtual(fragEndUnix));
        int windowMinutes = (int)((vEnd - vStart) / 60);
        if (windowMinutes < SimilarSearch.MinWindowMinutes)
            throw new InvalidOperationException(
                $"Selection covers {windowMinutes} min of trading time, " +
                $"need at least {SimilarSearch.MinWindowMinutes} min");
        long anchorOffsetSec = Math.Max(0, w.ToVirtual(anchorUnix) - vStart);

        int bucketMinutes = SimilarSearch.BucketMinutesFor(windowMinutes);
        long bucketSec = bucketMinutes * 60L;
        long vBase = SimilarSearch.FloorHour(w.ToVirtual(templateMinutes[0].MinuteUnixSeconds));
        long vTop = Math.Max(w.ToVirtual(templateMinutes[^1].MinuteUnixSeconds) + 60, vEnd);
        foreach (var t in targets)
        {
            vBase = Math.Min(vBase,
                SimilarSearch.FloorHour(w.ToVirtual(t.Minutes[0].MinuteUnixSeconds)));
            vTop = Math.Max(vTop, w.ToVirtual(t.Minutes[^1].MinuteUnixSeconds) + 60);
        }
        int totalMinutes = (int)((vTop - vBase) / 60);

        var templatePoints = SimilarSearch.BuildPoints(templateMinutes, vBase, totalMinutes, 0, bucketMinutes);
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
        var ai = fragIdx.ToArray();
        var av = fragVal.ToArray();
        int minOverlap = (int)Math.Ceiling(len * SimilarSearch.MinOverlapShare);
        if (fragIdx.Count < minOverlap)
            throw new InvalidOperationException(
                $"Selection has too little data: {fragIdx.Count}/{len} points");
        double maxZoom = 1 + Math.Max(0, zoomPercent) / 100.0;
        double minZoom = 1 / maxZoom;

        int n = pattern.Count;
        var results = new List<FindResult>();
        var pairPoints = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        for (int ti = 0; ti < targets.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var target = targets[ti].Target;
            if (!pairPoints.TryGetValue(target.Source, out var targetPoints))
            {
                targetPoints = SimilarSearch.BuildPoints(
                    targets[ti].Minutes, vBase, totalMinutes, 0, bucketMinutes);
                pairPoints[target.Source] = targetPoints;
            }
            var pivots = target.Points;
            if (pivots.Length < n) continue;
            var vMinute = new long[pivots.Length];
            for (int i = 0; i < pivots.Length; i++)
                vMinute[i] = w.ToVirtual(pivots[i].UnixSeconds) / 60;

            int candCount = 0;
            while (candCount + n <= pivots.Length
                && pivots[candCount + n - 1].UnixSeconds < fragStartUnix)
                candCount++;

            for (int j = 0; j < candCount; j++)
            {
                ct.ThrowIfCancellationRequested();
                if (j % 256 == 0)
                    progress?.Report((ti + (double)j / candCount) / targets.Count);
                double candDur = vMinute[j + n - 1] - vMinute[j];
                if (patternDur > 0 && candDur > MaxDurationFactor * patternDur) continue;
                long alignedStart = vMinute[j] * 60 - anchorOffsetSec;
                if (alignedStart < vBase) continue;
                double timeFit = TimeFit(vMinute, j, pattern);
                int p = (int)Math.Round((double)(alignedStart - vBase) / bucketSec);
                for (int mirror = 0; mirror < 2; mirror++)
                {
                    if (!PipsFit(pivots, j, pattern, target.PipPoints, zoomA, zoomB, minZoom, maxZoom,
                        mirror == 1, out double zoom)) continue;
                    var m = SimilarSearch.ScoreWindow(targetPoints, p, ai, av, len, minOverlap,
                        minZoom, maxZoom, mirror == 0, mirror == 1, fragB0);
                    results.Add(new FindResult(w.ToReal(alignedStart), m.Sim, m.Score, m.Rho, zoom,
                        mirror == 1, m.Overlap, m.MeanA, m.MeanB, target.Source, timeFit));
                }
            }
        }
        if (results.Count == 0 && targets.All(t => t.Target.Points.Length < n))
            throw new InvalidOperationException("Every target has fewer points than the pattern");
        results.Sort((a, b) =>
        {
            int c = b.TimeFit.CompareTo(a.TimeFit);
            return c != 0 ? c : b.Sim.CompareTo(a.Sim);
        });
        if (results.Count > SimilarSearch.MaxResults)
            results.RemoveRange(SimilarSearch.MaxResults, results.Count - SimilarSearch.MaxResults);
        progress?.Report(1.0);
        return new FindData(FindStore.CurrentVersion, template, w.ToReal(vStart), w.ToRealEnd(vEnd),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), windowMinutes, results,
            targets.Select(t => t.Target.Name).ToList(), 0, zoomPercent, 0,
            FindSearchTypes.ZigZag, patternSource);
    }

    private static bool PipsFit(PivotPoint[] pivots, int j, IReadOnlyList<ZigZagPatternPoint> pattern,
        int pipPoints, int zoomA, int zoomB, double minZoom, double maxZoom, bool mirror,
        out double zoom)
    {
        zoom = 0;
        double cA = Delta(pivots, j, zoomA, pipPoints);
        double cB = Delta(pivots, j, zoomB, pipPoints);
        double pA = mirror ? -pattern[zoomA].Pips : pattern[zoomA].Pips;
        double pB = mirror ? -pattern[zoomB].Pips : pattern[zoomB].Pips;
        double z = (cB - cA) / (pB - pA);
        if (z < minZoom || z > maxZoom) return false;
        double shift = cA - z * pA;
        for (int i = 0; i < pattern.Count; i++)
        {
            if (i == zoomA || i == zoomB) continue;
            double c = Delta(pivots, j, i, pipPoints);
            double t = shift + z * (mirror ? -pattern[i].Pips : pattern[i].Pips);
            double above = mirror ? pattern[i].DownPips : pattern[i].UpPips;
            double below = mirror ? pattern[i].UpPips : pattern[i].DownPips;
            if (c < t - below || c > t + above) return false;
        }
        zoom = z;
        return true;
    }

    private static double Delta(PivotPoint[] pivots, int j, int i, int pipPoints) =>
        i == 0 ? 0 : (pivots[j + i].Value - pivots[j].Value) / (double)pipPoints;

    private static double TimeFit(long[] vMinute, int j, IReadOnlyList<ZigZagPatternPoint> pattern)
    {
        double total = 0;
        for (int i = 0; i < pattern.Count; i++)
        {
            double m = vMinute[j + i] - vMinute[j];
            double lo = pattern[i].Minutes - pattern[i].LeftMinutes;
            double hi = pattern[i].Minutes + pattern[i].RightMinutes;
            if (m < lo) total += (lo - m) / Math.Max(1, pattern[i].LeftMinutes);
            else if (m > hi) total += (m - hi) / Math.Max(1, pattern[i].RightMinutes);
        }
        return 1 / (1 + total / pattern.Count);
    }
}
