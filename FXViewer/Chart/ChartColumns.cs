using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly record struct ColumnAggregate(int Min, int Max, int Avg, bool HasData);

public readonly record struct ChartSeries(ColumnAggregate[] Columns, int MinutesPerColumn, long FirstBucket);

public static class ChartColumns
{
    private const int ParallelChunkCandles = 64_000;

    private static readonly (int Threshold, int Quantum)[] SnapBands =
    {
        (5760, 1440),
        (960, 240),
        (240, 60),
        (60, 15),
    };

    public static int Quantum(int k)
    {
        foreach (var (threshold, quantum) in SnapBands)
            if (k >= threshold) return quantum;
        return 1;
    }

    public static int SnapK(int k) => k - k % Quantum(k);

    private static int SnapKUp(int k)
    {
        int rem = k % Quantum(k);
        return rem == 0 ? k : k + Quantum(k) - rem;
    }

    public static int FitK(long firstUnix, long lastUnix, int maxColumns)
    {
        long totalMinutes = (lastUnix - firstUnix) / 60 + 1;
        int k = SnapKUp((int)Math.Max(1, (totalMinutes + maxColumns - 1) / maxColumns));
        while (lastUnix / (k * 60L) - firstUnix / (k * 60L) + 1 > maxColumns) k = SnapKUp(k + 1);
        return k;
    }

    public static long[] ColumnEdges(WeekendCompressor? map, int k, long firstBucket, int count)
    {
        long bucketSec = k * 60L;
        var edges = new long[count + 1];
        for (int i = 0; i <= count; i++)
        {
            long v = (firstBucket + i) * bucketSec;
            edges[i] = map == null ? v : map.ToReal(v);
        }
        return edges;
    }

    public static ChartSeries BuildView(CandleHistory history, int k, long firstBucket, int count,
        WeekendCompressor? map = null)
    {
        ChartSeries series;
        int level = LevelFor(k, map);
        if (map != null)
        {
            var columns = new ColumnAggregate[count];
            var edges = ColumnEdges(map, k, firstBucket, count);
            if (level >= 0) FillEdges(history.Levels[level], edges, columns);
            else FillEdges(history.Minutes, edges, columns);
            series = new ChartSeries(columns, k, firstBucket);
        }
        else if (level >= 0)
        {
            var columns = new ColumnAggregate[count];
            FillView(history.Levels[level], k, firstBucket, count, columns);
            series = new ChartSeries(columns, k, firstBucket);
        }
        else
        {
            series = BuildView(history.Minutes, k, firstBucket, count);
        }
        MergeLive(history, series.Columns, k, firstBucket, map);
        return series;
    }

    public static int LevelFor(int k, WeekendCompressor? map)
    {
        var levels = CandleHistory.LevelMinutes;
        for (int i = levels.Length - 1; i >= 0; i--)
        {
            if (k % levels[i] != 0) continue;
            if (map != null && 3600 % (levels[i] * 60) != 0) continue;
            return i;
        }
        return -1;
    }

    private static void FillEdges(AggBlock[] blocks, long[] edges, ColumnAggregate[] columns)
    {
        int i = LowerBound(blocks, edges[0]);
        for (int col = 0; col < columns.Length && i < blocks.Length; col++)
        {
            long hi = edges[col + 1];
            int mn = int.MaxValue;
            int mx = int.MinValue;
            long sum = 0;
            int n = 0;
            for (; i < blocks.Length && blocks[i].StartUnixSeconds < hi; i++)
            {
                var b = blocks[i];
                if (b.Min < mn) mn = b.Min;
                if (b.Max > mx) mx = b.Max;
                sum += b.AvgSum;
                n += b.Count;
            }
            if (n > 0) columns[col] = new ColumnAggregate(mn, mx, Rollup.RoundAvg(sum, n), true);
        }
    }

    private static void FillEdges(Candle[] minutes, long[] edges, ColumnAggregate[] columns)
    {
        int i = LowerBound(minutes, edges[0]);
        for (int col = 0; col < columns.Length && i < minutes.Length; col++)
        {
            long hi = edges[col + 1];
            int mn = int.MaxValue;
            int mx = int.MinValue;
            long sum = 0;
            int n = 0;
            for (; i < minutes.Length && minutes[i].MinuteUnixSeconds < hi; i++)
            {
                var c = minutes[i];
                if (c.Min < mn) mn = c.Min;
                if (c.Max > mx) mx = c.Max;
                sum += c.Avg;
                n++;
            }
            if (n > 0) columns[col] = new ColumnAggregate(mn, mx, Rollup.RoundAvg(sum, n), true);
        }
    }

    private static void MergeLive(CandleHistory history, ColumnAggregate[] columns, int k, long firstBucket,
        WeekendCompressor? map)
    {
        var live = history.Live;
        if (live.Length == 0) return;
        long bucketSec = k * 60L;
        long lastBucketExcl = firstBucket + columns.Length;
        long recomputed = long.MinValue;
        foreach (var c in live)
        {
            long t = map == null ? c.MinuteUnixSeconds : map.ToVirtual(c.MinuteUnixSeconds);
            long bucket = t / bucketSec;
            if (bucket < firstBucket || bucket >= lastBucketExcl) continue;
            if (bucket == recomputed) continue;
            recomputed = bucket;
            long lo = map == null ? bucket * bucketSec : map.ToReal(bucket * bucketSec);
            long hi = map == null ? lo + bucketSec : map.ToReal((bucket + 1) * bucketSec);
            columns[bucket - firstBucket] = RecomputeColumn(history.Minutes, live, lo, hi);
        }
    }

    private static ColumnAggregate RecomputeColumn(Candle[] minutes, Candle[] live, long lo, long hi)
    {
        int mn = int.MaxValue;
        int mx = int.MinValue;
        long sum = 0;
        int n = 0;
        for (int i = LowerBound(minutes, lo); i < minutes.Length && minutes[i].MinuteUnixSeconds < hi; i++)
        {
            var c = minutes[i];
            if (c.Min < mn) mn = c.Min;
            if (c.Max > mx) mx = c.Max;
            sum += c.Avg;
            n++;
        }
        foreach (var c in live)
        {
            if (c.MinuteUnixSeconds < lo || c.MinuteUnixSeconds >= hi) continue;
            if (c.Min < mn) mn = c.Min;
            if (c.Max > mx) mx = c.Max;
            sum += c.Avg;
            n++;
        }
        return n == 0 ? default : new ColumnAggregate(mn, mx, Rollup.RoundAvg(sum, n), true);
    }

    public static ChartSeries BuildView(Candle[] minutes, int k, long firstBucket, int count)
    {
        var columns = new ColumnAggregate[count];
        long bucketSec = k * 60L;
        long lo = firstBucket * bucketSec;
        int start = LowerBound(minutes, lo);
        int end = LowerBound(minutes, (firstBucket + count) * bucketSec);
        int chunks = Math.Clamp((end - start) / ParallelChunkCandles, 1, Environment.ProcessorCount);
        if (chunks <= 1)
        {
            FillColumns(minutes, start, end, lo, bucketSec, columns);
        }
        else
        {
            int colsPerChunk = (count + chunks - 1) / chunks;
            Parallel.For(0, chunks, ci =>
            {
                int colFrom = ci * colsPerChunk;
                int colTo = Math.Min(count, colFrom + colsPerChunk);
                if (colFrom >= colTo) return;
                int from = LowerBound(minutes, lo + colFrom * bucketSec);
                int to = LowerBound(minutes, lo + colTo * bucketSec);
                FillColumns(minutes, from, to, lo, bucketSec, columns);
            });
        }
        return new ChartSeries(columns, k, firstBucket);
    }

    private static void FillView(AggBlock[] blocks, int k, long firstBucket, int count, ColumnAggregate[] columns)
    {
        long bucketSec = k * 60L;
        long lo = firstBucket * bucketSec;
        int start = LowerBound(blocks, lo);
        int end = LowerBound(blocks, (firstBucket + count) * bucketSec);
        int chunks = Math.Clamp((end - start) / ParallelChunkCandles, 1, Environment.ProcessorCount);
        if (chunks <= 1)
        {
            FillBlocks(blocks, start, end, lo, bucketSec, columns);
        }
        else
        {
            int colsPerChunk = (count + chunks - 1) / chunks;
            Parallel.For(0, chunks, ci =>
            {
                int colFrom = ci * colsPerChunk;
                int colTo = Math.Min(count, colFrom + colsPerChunk);
                if (colFrom >= colTo) return;
                int from = LowerBound(blocks, lo + colFrom * bucketSec);
                int to = LowerBound(blocks, lo + colTo * bucketSec);
                FillBlocks(blocks, from, to, lo, bucketSec, columns);
            });
        }
    }

    private static void FillBlocks(AggBlock[] blocks, int from, int to,
        long lo, long bucketSec, ColumnAggregate[] columns)
    {
        int col = 0;
        long colEnd = long.MinValue;
        int mn = 0;
        int mx = 0;
        int n = 0;
        long sum = 0;
        for (int i = from; i < to; i++)
        {
            var b = blocks[i];
            if (b.StartUnixSeconds >= colEnd)
            {
                if (n > 0) columns[col] = new ColumnAggregate(mn, mx, Rollup.RoundAvg(sum, n), true);
                col = (int)((b.StartUnixSeconds - lo) / bucketSec);
                colEnd = lo + (col + 1) * bucketSec;
                mn = b.Min;
                mx = b.Max;
                sum = 0;
                n = 0;
            }
            else
            {
                if (b.Min < mn) mn = b.Min;
                if (b.Max > mx) mx = b.Max;
            }
            sum += b.AvgSum;
            n += b.Count;
        }
        if (n > 0) columns[col] = new ColumnAggregate(mn, mx, Rollup.RoundAvg(sum, n), true);
    }

    private static int LowerBound(AggBlock[] blocks, long unixSeconds)
    {
        int lo = 0;
        int hi = blocks.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (blocks[mid].StartUnixSeconds < unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static void FillColumns(Candle[] minutes, int from, int to,
        long lo, long bucketSec, ColumnAggregate[] columns)
    {
        int col = 0;
        long colEnd = long.MinValue;
        int mn = 0;
        int mx = 0;
        int n = 0;
        long sum = 0;
        for (int i = from; i < to; i++)
        {
            var c = minutes[i];
            if (c.MinuteUnixSeconds >= colEnd)
            {
                if (n > 0) columns[col] = new ColumnAggregate(mn, mx, Rollup.RoundAvg(sum, n), true);
                col = (int)((c.MinuteUnixSeconds - lo) / bucketSec);
                colEnd = lo + (col + 1) * bucketSec;
                mn = c.Min;
                mx = c.Max;
                sum = 0;
                n = 0;
            }
            else
            {
                if (c.Min < mn) mn = c.Min;
                if (c.Max > mx) mx = c.Max;
            }
            sum += c.Avg;
            n++;
        }
        if (n > 0) columns[col] = new ColumnAggregate(mn, mx, Rollup.RoundAvg(sum, n), true);
    }

    public static ColumnAggregate NearestColumn(Candle[] minutes, int k, long bucket, WeekendCompressor? map)
    {
        if (minutes.Length == 0) return default;
        long bucketSec = k * 60L;
        long lo = map == null ? bucket * bucketSec : map.ToReal(bucket * bucketSec);
        long hi = map == null ? lo + bucketSec : map.ToReal((bucket + 1) * bucketSec);
        int idx = LowerBound(minutes, lo);
        if (idx < minutes.Length && minutes[idx].MinuteUnixSeconds < hi)
            return RecomputeColumn(minutes, Array.Empty<Candle>(), lo, hi);
        long? left = idx > 0 ? BucketOf(minutes[idx - 1].MinuteUnixSeconds, bucketSec, map) : null;
        long? right = idx < minutes.Length ? BucketOf(minutes[idx].MinuteUnixSeconds, bucketSec, map) : null;
        long chosen = left == null ? right!.Value
            : right == null ? left.Value
            : bucket - left.Value <= right.Value - bucket ? left.Value : right.Value;
        long clo = map == null ? chosen * bucketSec : map.ToReal(chosen * bucketSec);
        long chi = map == null ? clo + bucketSec : map.ToReal((chosen + 1) * bucketSec);
        return RecomputeColumn(minutes, Array.Empty<Candle>(), clo, chi);
    }

    private static long BucketOf(long unixSeconds, long bucketSec, WeekendCompressor? map) =>
        (map == null ? unixSeconds : map.ToVirtual(unixSeconds)) / bucketSec;

    private static int LowerBound(Candle[] minutes, long unixSeconds)
    {
        int lo = 0;
        int hi = minutes.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (minutes[mid].MinuteUnixSeconds < unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
