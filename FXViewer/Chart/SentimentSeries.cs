using System.IO;
using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly record struct SentimentSample(
    long FromUnix, long UntilUnix, int StepMinutes, float LongByLots, float LongByPositions,
    float ShortLots, float LongLots, int ShortPositions, int LongPositions);

public sealed class SentimentSeries
{
    public const int CarryBars = 4;

    public static readonly SentimentSeries Empty = new(Array.Empty<SentimentSample>());

    public SentimentSample[] Samples { get; }

    public int Count => Samples.Length;

    public SentimentSample? Last => Samples.Length > 0 ? Samples[^1] : null;

    private SentimentSeries(SentimentSample[] samples)
    {
        Samples = samples;
    }

    public static SentimentSeries Load(string folder)
    {
        if (!Directory.Exists(folder)) return Empty;
        var timeframes = new List<(int StepMinutes, IReadOnlyList<SentimentPoint> Points)>();
        foreach (var tf in SentimentTimeframes.All)
        {
            string path = Path.Combine(folder, tf + ".csv");
            if (!File.Exists(path)) continue;
            var points = SentimentStore.Load(path);
            if (points.Count > 0) timeframes.Add((SentimentTimeframes.Minutes(tf), points));
        }
        return Build(timeframes);
    }

    private readonly record struct RawPoint(long Time, int Step, SentimentPoint Point);

    public static SentimentSeries Build(
        IEnumerable<(int StepMinutes, IReadOnlyList<SentimentPoint> Points)> timeframes)
    {
        var items = new List<RawPoint>();
        foreach (var (step, points) in timeframes)
        {
            if (step <= 0) continue;
            foreach (var p in points)
            {
                if (double.IsNaN(p.LongShareByLots) && double.IsNaN(p.LongShareByPositions)) continue;
                items.Add(new RawPoint(p.TimeUnix, step, p));
            }
        }
        if (items.Count == 0) return Empty;
        items.Sort((a, b) =>
        {
            int byTime = a.Time.CompareTo(b.Time);
            return byTime != 0 ? byTime : a.Step.CompareTo(b.Step);
        });
        var kept = new List<RawPoint>(items.Count);
        for (int i = 0; i < items.Count; i++)
            if (!CoveredByFiner(items, i)) kept.Add(items[i]);
        var samples = new SentimentSample[kept.Count];
        for (int i = 0; i < kept.Count; i++)
        {
            var (time, step, p) = kept[i];
            long carry = time + (long)step * 60 * CarryBars;
            long until = i + 1 < kept.Count ? Math.Min(kept[i + 1].Time, carry) : carry;
            double lots = p.LongShareByLots;
            double positions = p.LongShareByPositions;
            if (double.IsNaN(lots)) lots = positions;
            samples[i] = new SentimentSample(time, until, step, (float)lots, (float)positions,
                (float)p.ShortLots, (float)p.LongLots, p.ShortPositions, p.LongPositions);
        }
        return new SentimentSeries(samples);
    }

    private static bool CoveredByFiner(List<RawPoint> items, int index)
    {
        var p = items[index];
        long spanEnd = p.Time + (long)p.Step * 60;
        int j = FirstAtOrAfter(items, p.Time);
        for (; j < items.Count && items[j].Time < spanEnd; j++)
        {
            if (j == index) continue;
            if (items[j].Step < p.Step) return true;
        }
        return false;
    }

    private static int FirstAtOrAfter(List<RawPoint> items, long time)
    {
        int lo = 0;
        int hi = items.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (items[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public int LastBefore(long unix)
    {
        int lo = 0;
        int hi = Samples.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Samples[mid].FromUnix < unix) lo = mid + 1;
            else hi = mid;
        }
        return lo - 1;
    }
}

public readonly record struct SentimentColumn(float Lots, float Positions)
{
    public static readonly SentimentColumn None = new(float.NaN, float.NaN);

    public bool HasData => !float.IsNaN(Lots);
}

public static class SentimentColumns
{
    public static SentimentColumn[] Build(SentimentSeries data, long columnSeconds, long firstBucket,
        int count, WeekendCompressor? map, long maxUnix = long.MaxValue)
    {
        var columns = new SentimentColumn[count];
        Array.Fill(columns, SentimentColumn.None);
        if (count <= 0 || data.Count == 0) return columns;
        int run = ChartColumns.MinuteRun(columnSeconds);
        if (run > 1)
        {
            long minuteFirst = ChartColumns.MinuteBucket(firstBucket, run);
            var minutes = Build(data, ChartColumns.MinuteSeconds, minuteFirst,
                ChartColumns.MinuteCount(count, run), map, maxUnix);
            return ChartColumns.Expand(minutes, minuteFirst, run, firstBucket, count, SentimentColumn.None);
        }
        var edges = ChartColumns.ColumnEdges(map, columnSeconds, firstBucket, count, maxUnix);
        var samples = data.Samples;
        int i = data.LastBefore(edges[1]);
        for (int col = 0; col < count; col++)
        {
            long lo = edges[col];
            long hi = edges[col + 1];
            if (lo >= maxUnix) break;
            while (i + 1 < samples.Length && samples[i + 1].FromUnix < hi) i++;
            if (i < 0 || samples[i].UntilUnix <= lo) continue;
            columns[col] = new SentimentColumn(samples[i].LongByLots, samples[i].LongByPositions);
        }
        return columns;
    }
}
