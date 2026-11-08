using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly record struct BigPlayersEvent(
    long TimeUnix, int StepMinutes, float BuyOpen, float BuyClose, float SellOpen, float SellClose)
{
    public float Buy => BuyOpen + BuyClose;

    public float Sell => SellOpen + SellClose;
}

public sealed class BigPlayersSeries
{
    public const int MassPositionsPerMinute = 100;
    public const int MaxStepMinutes = 60;

    public static readonly BigPlayersSeries Empty = new(Array.Empty<BigPlayersEvent>(), 0);

    public BigPlayersEvent[] Events { get; }

    public double MinLots { get; }

    public int Count => Events.Length;

    private BigPlayersSeries(BigPlayersEvent[] events, double minLots)
    {
        Events = events;
        MinLots = minLots;
    }

    public static BigPlayersSeries Build(SentimentSeries sentiment, double minLots)
    {
        var samples = sentiment.Samples;
        var events = new List<BigPlayersEvent>();
        for (int i = 1; i < samples.Length; i++)
        {
            var prev = samples[i - 1];
            var cur = samples[i];
            if (cur.StepMinutes != prev.StepMinutes || cur.StepMinutes > MaxStepMinutes) continue;
            if (cur.FromUnix - prev.FromUnix != (long)cur.StepMinutes * 60) continue;
            long massCap = (long)MassPositionsPerMinute * cur.StepMinutes;
            var (longOpened, longClosed) = SideFlow(
                prev.LongLots, prev.LongPositions, cur.LongLots, cur.LongPositions, massCap, minLots);
            var (shortOpened, shortClosed) = SideFlow(
                prev.ShortLots, prev.ShortPositions, cur.ShortLots, cur.ShortPositions, massCap, minLots);
            if (longOpened + longClosed + shortOpened + shortClosed <= 0) continue;
            events.Add(new BigPlayersEvent(cur.FromUnix, cur.StepMinutes,
                longOpened, shortClosed, shortOpened, longClosed));
        }
        return new BigPlayersSeries(events.ToArray(), minLots);
    }

    private static (float Opened, float Closed) SideFlow(
        float lots0, int positions0, float lots1, int positions1, long massCap, double minLots)
    {
        if (positions0 <= 0) return (0, 0);
        double deltaLots = lots1 - lots0;
        long deltaPositions = (long)positions1 - positions0;
        if (Math.Abs(deltaPositions) >= massCap) return (0, 0);
        double typical = lots0 / positions0;
        double excess = Math.Abs(deltaLots) - Math.Abs(deltaPositions) * typical;
        if (excess < minLots) return (0, 0);
        return deltaLots > 0 ? ((float)excess, 0f) : (0f, (float)excess);
    }

    public int FirstAtOrAfter(long unix)
    {
        int lo = 0;
        int hi = Events.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Events[mid].TimeUnix < unix) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}

public readonly record struct BigPlayersColumn(float BuyOpen, float BuyClose, float SellOpen, float SellClose)
{
    public static readonly BigPlayersColumn None = default;

    public float Buy => BuyOpen + BuyClose;

    public float Sell => SellOpen + SellClose;

    public bool HasData => Buy > 0 || Sell > 0;

    public BigPlayersColumn Add(BigPlayersEvent e) => new(
        BuyOpen + e.BuyOpen, BuyClose + e.BuyClose, SellOpen + e.SellOpen, SellClose + e.SellClose);
}

public static class BigPlayersColumns
{
    public static BigPlayersColumn[] Build(BigPlayersSeries data, long columnSeconds, long firstBucket,
        int count, WeekendCompressor? map, long maxUnix = long.MaxValue)
    {
        var columns = new BigPlayersColumn[count];
        if (count <= 0 || data.Count == 0) return columns;
        int run = ChartColumns.MinuteRun(columnSeconds);
        if (run > 1)
        {
            long minuteFirst = ChartColumns.MinuteBucket(firstBucket, run);
            var minutes = Build(data, ChartColumns.MinuteSeconds, minuteFirst,
                ChartColumns.MinuteCount(count, run), map, maxUnix);
            return ChartColumns.Expand(minutes, minuteFirst, run, firstBucket, count, BigPlayersColumn.None);
        }
        var edges = ChartColumns.ColumnEdges(map, columnSeconds, firstBucket, count, maxUnix);
        var events = data.Events;
        int i = data.FirstAtOrAfter(edges[0]);
        for (int col = 0; col < count && i < events.Length; col++)
        {
            long hi = edges[col + 1];
            if (edges[col] >= maxUnix) break;
            for (; i < events.Length && events[i].TimeUnix < hi; i++)
            {
                if (events[i].TimeUnix >= maxUnix) return columns;
                columns[col] = columns[col].Add(events[i]);
            }
        }
        return columns;
    }

    public static OrderBookSides BuildProfile(BigPlayersSeries data, Candle[] minutes, long fromUnix, long toUnix)
    {
        var empty = new DensityHistogram(0, Array.Empty<double>(), 0, 0);
        if (data.Count == 0 || minutes.Length == 0 || toUnix < fromUnix) return new OrderBookSides(empty, empty);
        var buy = new Dictionary<int, double>();
        var sell = new Dictionary<int, double>();
        int lo = int.MaxValue;
        int hi = int.MinValue;
        var events = data.Events;
        for (int i = data.FirstAtOrAfter(fromUnix); i < events.Length && events[i].TimeUnix <= toUnix; i++)
        {
            var e = events[i];
            if (OrderBookProfile.PricePointsAt(minutes, e.TimeUnix) is not { } price) continue;
            int pip = DensityProfile.PipLevel(price);
            if (pip < lo) lo = pip;
            if (pip > hi) hi = pip;
            if (e.Buy > 0) buy[pip] = buy.GetValueOrDefault(pip) + e.Buy;
            if (e.Sell > 0) sell[pip] = sell.GetValueOrDefault(pip) + e.Sell;
        }
        if (lo > hi) return new OrderBookSides(empty, empty);
        var buyCounts = new double[hi - lo + 1];
        var sellCounts = new double[hi - lo + 1];
        double max = 0;
        foreach (var (pip, lots) in buy)
        {
            buyCounts[pip - lo] = lots;
            if (lots > max) max = lots;
        }
        foreach (var (pip, lots) in sell)
        {
            sellCounts[pip - lo] = lots;
            if (lots > max) max = lots;
        }
        return new OrderBookSides(
            new DensityHistogram(lo, buyCounts, max, 0),
            new DensityHistogram(lo, sellCounts, max, 0));
    }
}
