using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly record struct ColumnAggregate(int Min, int Max, int Avg, bool HasData);

public readonly record struct ChartSeries(ColumnAggregate[] Columns, long ColumnSeconds, long FirstBucket);

public static class ChartColumns
{
    public const long MinuteSeconds = 60;

    private const int ParallelChunkCandles = 64_000;

    private static readonly (long Threshold, long Quantum)[] SnapBands =
    {
        (5760 * MinuteSeconds, 1440 * MinuteSeconds),
        (960 * MinuteSeconds, 240 * MinuteSeconds),
        (240 * MinuteSeconds, 60 * MinuteSeconds),
        (60 * MinuteSeconds, 15 * MinuteSeconds),
    };

    public static readonly long[] SubMinuteSteps = { 60, 30, 20, 15, 12, 10, 6, 5, 4, 3, 2, 1 };

    public static long Quantum(long columnSeconds)
    {
        foreach (var (threshold, quantum) in SnapBands)
            if (columnSeconds >= threshold) return quantum;
        return MinuteSeconds;
    }

    public static long Snap(long columnSeconds) =>
        columnSeconds <= MinuteSeconds
            ? SubMinuteSteps[StepIndex(columnSeconds)]
            : columnSeconds - columnSeconds % Quantum(columnSeconds);

    public static long Finer(long columnSeconds)
    {
        if (columnSeconds > MinuteSeconds) return columnSeconds;
        int i = StepIndex(columnSeconds);
        return SubMinuteSteps[Math.Min(i + 1, SubMinuteSteps.Length - 1)];
    }

    public static long Coarser(long columnSeconds)
    {
        if (columnSeconds >= MinuteSeconds) return columnSeconds;
        int i = StepIndex(columnSeconds);
        return SubMinuteSteps[Math.Max(i - 1, 0)];
    }

    private static int StepIndex(long columnSeconds)
    {
        int best = 0;
        long bestDiff = long.MaxValue;
        for (int i = 0; i < SubMinuteSteps.Length; i++)
        {
            long diff = Math.Abs(SubMinuteSteps[i] - columnSeconds);
            if (diff >= bestDiff) continue;
            bestDiff = diff;
            best = i;
        }
        return best;
    }

    public static long Zoomed(long columnSeconds, bool zoomIn, double step)
    {
        if (zoomIn && columnSeconds <= MinuteSeconds) return Finer(columnSeconds);
        if (!zoomIn && columnSeconds < MinuteSeconds) return Coarser(columnSeconds);
        long scaled = (long)Math.Round(zoomIn ? columnSeconds / step : columnSeconds * step);
        if (scaled == columnSeconds) scaled = zoomIn ? columnSeconds - 1 : columnSeconds + 1;
        long next = Snap(Math.Max(MinuteSeconds, scaled));
        if (next != columnSeconds) return next;
        return zoomIn
            ? Math.Max(MinuteSeconds, Snap(columnSeconds - Quantum(columnSeconds)))
            : columnSeconds + Quantum(columnSeconds);
    }

    private static long SnapUp(long columnSeconds)
    {
        if (columnSeconds <= MinuteSeconds) return MinuteSeconds;
        long quantum = Quantum(columnSeconds);
        long rem = columnSeconds % quantum;
        return rem == 0 ? columnSeconds : columnSeconds + quantum - rem;
    }

    public static long FitColumnSeconds(long firstUnix, long lastUnix, int maxColumns)
    {
        long total = lastUnix - firstUnix + MinuteSeconds;
        long columnSeconds = SnapUp(Math.Max(MinuteSeconds, (total + maxColumns - 1) / maxColumns));
        while (lastUnix / columnSeconds - firstUnix / columnSeconds + 1 > maxColumns)
            columnSeconds = SnapUp(columnSeconds + 1);
        return columnSeconds;
    }

    public static int MinuteRun(long columnSeconds) =>
        columnSeconds > 0 && columnSeconds < MinuteSeconds ? (int)(MinuteSeconds / columnSeconds) : 1;

    public static long MinuteBucket(long bucket, int run) =>
        run <= 1 ? bucket : bucket >= 0 ? bucket / run : (bucket - run + 1) / run;

    public static int MinuteCount(int count, int run) => count / run + 2;

    public static T[] Expand<T>(T[] source, long sourceFirst, int run, long firstBucket, int count, T empty)
    {
        var result = new T[count];
        Array.Fill(result, empty);
        for (int i = 0; i < count; i++)
        {
            long index = MinuteBucket(firstBucket + i, run) - sourceFirst;
            if ((ulong)index < (ulong)source.Length) result[i] = source[(int)index];
        }
        return result;
    }

    public static bool[] ExpandOnce(bool[] source, long sourceFirst, int run, long firstBucket, int count)
    {
        var result = new bool[count];
        long prevMinute = long.MinValue;
        for (int i = 0; i < count; i++)
        {
            long minute = MinuteBucket(firstBucket + i, run);
            long index = minute - sourceFirst;
            result[i] = minute != prevMinute && (ulong)index < (ulong)source.Length && source[(int)index];
            prevMinute = minute;
        }
        return result;
    }

    public static long[] ColumnEdges(WeekendCompressor? map, long columnSeconds, long firstBucket, int count,
        long maxUnix = long.MaxValue)
    {
        var edges = new long[count + 1];
        for (int i = 0; i <= count; i++)
        {
            long v = (firstBucket + i) * columnSeconds;
            long real = map == null ? v : map.ToReal(v);
            edges[i] = real > maxUnix ? maxUnix : real;
        }
        return edges;
    }

    public static ChartSeries BuildView(CandleHistory history, long columnSeconds, long firstBucket, int count,
        WeekendCompressor? map = null, long maxUnix = long.MaxValue)
    {
        int run = MinuteRun(columnSeconds);
        if (run > 1)
        {
            long minuteFirst = MinuteBucket(firstBucket, run);
            var minuteView = BuildView(
                history, MinuteSeconds, minuteFirst, MinuteCount(count, run), map, maxUnix);
            return new ChartSeries(
                Expand(minuteView.Columns, minuteFirst, run, firstBucket, count, default),
                columnSeconds, firstBucket);
        }
        ChartSeries series;
        int level = LevelFor(columnSeconds, map);
        bool cut = maxUnix != long.MaxValue;
        if (map != null || cut)
        {
            var columns = new ColumnAggregate[count];
            var edges = ColumnEdges(map, columnSeconds, firstBucket, count, maxUnix);
            if (level >= 0) FillEdges(history.Levels[level], edges, columns);
            else FillEdges(history.Minutes, edges, columns);
            series = new ChartSeries(columns, columnSeconds, firstBucket);
            if (cut && level >= 0) FixCutColumn(history, columns, edges, maxUnix);
        }
        else if (level >= 0)
        {
            var columns = new ColumnAggregate[count];
            FillView(history.Levels[level], columnSeconds, firstBucket, count, columns);
            series = new ChartSeries(columns, columnSeconds, firstBucket);
        }
        else
        {
            series = BuildView(history.Minutes, columnSeconds, firstBucket, count);
        }
        MergeLive(history, series.Columns, columnSeconds, firstBucket, map, maxUnix);
        return series;
    }

    private static void FixCutColumn(CandleHistory history, ColumnAggregate[] columns, long[] edges,
        long maxUnix)
    {
        for (int col = 0; col < columns.Length; col++)
        {
            if (edges[col] >= maxUnix) return;
            if (edges[col + 1] < maxUnix) continue;
            columns[col] = RecomputeColumn(history.Minutes, history.Live, edges[col], maxUnix);
            return;
        }
    }

    public static (ChartSeries View, int[] Chosen, bool[] FullRange) BuildLine(CandleHistory history,
        long columnSeconds, long firstBucket, int count, WeekendCompressor? map, int lookback,
        int noiseThreshold, long maxUnix = long.MaxValue, CandleHistory? extension = null)
    {
        int run = MinuteRun(columnSeconds);
        if (run == 1)
        {
            var view = BuildExtendedView(history, extension, columnSeconds, firstBucket, count, map, maxUnix);
            var (values, fullRange) = LineDecimator.ChooseValues(view.Columns, lookback, noiseThreshold);
            return (view, values, fullRange);
        }
        long minuteFirst = MinuteBucket(firstBucket, run);
        var minutes = BuildExtendedView(
            history, extension, MinuteSeconds, minuteFirst, MinuteCount(count, run), map, maxUnix);
        var (minuteChosen, minuteFullRange) = LineDecimator.ChooseValues(minutes.Columns, lookback, noiseThreshold);
        return (
            new ChartSeries(
                Expand(minutes.Columns, minuteFirst, run, firstBucket, count, default),
                columnSeconds, firstBucket),
            Expand(minuteChosen, minuteFirst, run, firstBucket, count, 0),
            ExpandOnce(minuteFullRange, minuteFirst, run, firstBucket, count));
    }

    private static ChartSeries BuildExtendedView(CandleHistory history, CandleHistory? extension,
        long columnSeconds, long firstBucket, int count, WeekendCompressor? map, long maxUnix)
    {
        if (extension == null || extension.Minutes.Length == 0)
            return BuildView(history, columnSeconds, firstBucket, count, map, maxUnix);
        long extensionFrom = extension.Minutes[0].MinuteUnixSeconds;
        var view = BuildView(history, columnSeconds, firstBucket, count, map, Math.Min(maxUnix, extensionFrom));
        var tail = BuildView(extension, columnSeconds, firstBucket, count, map);
        var columns = view.Columns;
        for (int i = 0; i < columns.Length; i++)
        {
            var t = tail.Columns[i];
            if (!t.HasData) continue;
            var c = columns[i];
            columns[i] = c.HasData
                ? new ColumnAggregate(Math.Min(c.Min, t.Min), Math.Max(c.Max, t.Max), t.Avg, true)
                : t;
        }
        return view;
    }

    public static int LevelFor(long columnSeconds, WeekendCompressor? map)
    {
        if (columnSeconds < MinuteSeconds || columnSeconds % MinuteSeconds != 0) return -1;
        long minutes = columnSeconds / MinuteSeconds;
        var levels = CandleHistory.LevelMinutes;
        for (int i = levels.Length - 1; i >= 0; i--)
        {
            if (minutes % levels[i] != 0) continue;
            if (map != null && 3600 % (levels[i] * MinuteSeconds) != 0) continue;
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

    private static void MergeLive(CandleHistory history, ColumnAggregate[] columns, long columnSeconds,
        long firstBucket, WeekendCompressor? map, long maxUnix = long.MaxValue)
    {
        var live = history.Live;
        if (live.Length == 0) return;
        long lastBucketExcl = firstBucket + columns.Length;
        long recomputed = long.MinValue;
        foreach (var c in live)
        {
            if (c.MinuteUnixSeconds >= maxUnix) break;
            long t = map == null ? c.MinuteUnixSeconds : map.ToVirtual(c.MinuteUnixSeconds);
            long bucket = t / columnSeconds;
            if (bucket < firstBucket || bucket >= lastBucketExcl) continue;
            if (bucket == recomputed) continue;
            recomputed = bucket;
            long lo = map == null ? bucket * columnSeconds : map.ToReal(bucket * columnSeconds);
            long hi = map == null ? lo + columnSeconds : map.ToReal((bucket + 1) * columnSeconds);
            columns[bucket - firstBucket] = RecomputeColumn(history.Minutes, live, lo, Math.Min(hi, maxUnix));
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

    public static ChartSeries BuildView(Candle[] minutes, long columnSeconds, long firstBucket, int count)
    {
        var columns = new ColumnAggregate[count];
        long lo = firstBucket * columnSeconds;
        int start = LowerBound(minutes, lo);
        int end = LowerBound(minutes, (firstBucket + count) * columnSeconds);
        int chunks = Math.Clamp((end - start) / ParallelChunkCandles, 1, Environment.ProcessorCount);
        if (chunks <= 1)
        {
            FillColumns(minutes, start, end, lo, columnSeconds, columns);
        }
        else
        {
            int colsPerChunk = (count + chunks - 1) / chunks;
            Parallel.For(0, chunks, ci =>
            {
                int colFrom = ci * colsPerChunk;
                int colTo = Math.Min(count, colFrom + colsPerChunk);
                if (colFrom >= colTo) return;
                int from = LowerBound(minutes, lo + colFrom * columnSeconds);
                int to = LowerBound(minutes, lo + colTo * columnSeconds);
                FillColumns(minutes, from, to, lo, columnSeconds, columns);
            });
        }
        return new ChartSeries(columns, columnSeconds, firstBucket);
    }

    private static void FillView(AggBlock[] blocks, long columnSeconds, long firstBucket, int count,
        ColumnAggregate[] columns)
    {
        long lo = firstBucket * columnSeconds;
        int start = LowerBound(blocks, lo);
        int end = LowerBound(blocks, (firstBucket + count) * columnSeconds);
        int chunks = Math.Clamp((end - start) / ParallelChunkCandles, 1, Environment.ProcessorCount);
        if (chunks <= 1)
        {
            FillBlocks(blocks, start, end, lo, columnSeconds, columns);
        }
        else
        {
            int colsPerChunk = (count + chunks - 1) / chunks;
            Parallel.For(0, chunks, ci =>
            {
                int colFrom = ci * colsPerChunk;
                int colTo = Math.Min(count, colFrom + colsPerChunk);
                if (colFrom >= colTo) return;
                int from = LowerBound(blocks, lo + colFrom * columnSeconds);
                int to = LowerBound(blocks, lo + colTo * columnSeconds);
                FillBlocks(blocks, from, to, lo, columnSeconds, columns);
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
