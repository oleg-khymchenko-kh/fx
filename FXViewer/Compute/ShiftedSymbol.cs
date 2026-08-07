using FXViewer.Chart;
using FXViewer.Storage;

namespace FXViewer.Compute;

public static class ShiftedSymbol
{
    public static long VirtualDelta(long sourceTimeUnix, long chartTimeUnix)
    {
        var w = WeekendCompressor.Instance;
        return w.ToVirtual(chartTimeUnix) - w.ToVirtual(sourceTimeUnix);
    }

    public static long FlipBase(Candle[] candles)
    {
        int mn = int.MaxValue;
        int mx = int.MinValue;
        foreach (var c in candles)
        {
            if (c.Min < mn) mn = c.Min;
            if (c.Max > mx) mx = c.Max;
        }
        return mn > mx ? 0 : (long)mn + mx;
    }

    public static long Flip(Candle[] candles)
    {
        long flipBase = FlipBase(candles);
        FlipAround(candles, flipBase);
        return flipBase;
    }

    public static void FlipAround(Candle[] candles, long flipBase)
    {
        for (int i = 0; i < candles.Length; i++)
        {
            var c = candles[i];
            candles[i] = new Candle(c.MinuteUnixSeconds,
                (int)(flipBase - c.Max), (int)(flipBase - c.Min), (int)(flipBase - c.Avg),
                c.AvgApproximated);
        }
    }

    public static Candle[] Restamp(IReadOnlyList<Candle> candles, long virtualDelta)
    {
        var w = WeekendCompressor.Instance;
        var result = new Candle[candles.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var c = candles[i];
            result[i] = c with
            {
                MinuteUnixSeconds = w.ToReal(w.ToVirtual(c.MinuteUnixSeconds) + virtualDelta),
            };
        }
        return result;
    }

    public static List<Candle> Shift(IReadOnlyList<Candle> candles, long virtualDelta)
    {
        var w = WeekendCompressor.Instance;
        var result = new List<Candle>(candles.Count);
        long prev = long.MinValue;
        foreach (var c in candles)
        {
            long t = w.ToReal(w.ToVirtual(c.MinuteUnixSeconds) + virtualDelta);
            if (t <= prev) continue;
            prev = t;
            result.Add(c with { MinuteUnixSeconds = t });
        }
        return result;
    }
}
