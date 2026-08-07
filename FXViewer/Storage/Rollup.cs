namespace FXViewer.Storage;

public enum Timeframe
{
    M1,
    M5,
    M15,
    M30,
    H1,
    H4,
    D1,
}

public readonly record struct AggCandle(long BucketUnixSeconds, int Min, int Max, int Avg, int Count, bool AnyApproximated)
{
    public DateTime TimeUtc => DateTimeOffset.FromUnixTimeSeconds(BucketUnixSeconds).UtcDateTime;
}

public static class Rollup
{
    public static int Minutes(Timeframe tf) => tf switch
    {
        Timeframe.M1 => 1,
        Timeframe.M5 => 5,
        Timeframe.M15 => 15,
        Timeframe.M30 => 30,
        Timeframe.H1 => 60,
        Timeframe.H4 => 240,
        Timeframe.D1 => 1440,
        _ => 1,
    };

    private sealed class Acc
    {
        public int Min;
        public int Max;
        public long AvgSum;
        public int Count;
        public bool AnyApprox;
    }

    public static int RoundAvg(long sum, int count) =>
        (int)Math.Round(sum / (double)count, MidpointRounding.AwayFromZero);

    public static List<AggCandle> Aggregate(IEnumerable<Candle> minutes, Timeframe tf)
    {
        long bucketSec = Minutes(tf) * 60L;
        var map = new SortedDictionary<long, Acc>();
        foreach (var c in minutes)
        {
            long bucket = c.MinuteUnixSeconds - (c.MinuteUnixSeconds % bucketSec);
            if (!map.TryGetValue(bucket, out var acc))
            {
                acc = new Acc { Min = c.Min, Max = c.Max };
                map[bucket] = acc;
            }
            if (c.Min < acc.Min) acc.Min = c.Min;
            if (c.Max > acc.Max) acc.Max = c.Max;
            acc.AvgSum += c.Avg;
            acc.Count++;
            if (c.AvgApproximated) acc.AnyApprox = true;
        }

        var result = new List<AggCandle>(map.Count);
        foreach (var (bucket, acc) in map)
        {
            int avg = RoundAvg(acc.AvgSum, acc.Count);
            result.Add(new AggCandle(bucket, acc.Min, acc.Max, avg, acc.Count, acc.AnyApprox));
        }
        return result;
    }
}
