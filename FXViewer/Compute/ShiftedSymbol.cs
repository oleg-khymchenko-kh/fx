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

    public static bool AnchorsBroken(long sourceTimeUnix, long chartTimeUnix)
    {
        long first = WeekendCompressor.Instance.FirstGapStart;
        return sourceTimeUnix < first || chartTimeUnix < first;
    }

    public static long DefaultAnchorUnix()
    {
        var now = DateTime.UtcNow;
        var day = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        long unix = new DateTimeOffset(day).ToUnixTimeSeconds();
        var w = WeekendCompressor.Instance;
        return w.InGap(unix) ? w.ToRealEnd(w.ToVirtual(unix)) : unix;
    }

    public static (long Source, long Chart) Rebase(
        long sourceTimeUnix, long chartTimeUnix, long anchorUnix)
    {
        var w = WeekendCompressor.Instance;
        long delta = VirtualDelta(sourceTimeUnix, chartTimeUnix);
        long chart = w.ToReal(w.ToVirtual(anchorUnix) + delta);
        return chart < w.FirstGapStart ? (anchorUnix, anchorUnix) : (anchorUnix, chart);
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
            candles[i] = c with
            {
                Min = (int)(flipBase - c.Max),
                Max = (int)(flipBase - c.Min),
                Avg = (int)(flipBase - c.Avg),
            };
        }
    }

    public static Candle[] Retarget(IReadOnlyList<Candle> display, SeriesTransform from,
        bool mirror, long? fixedBase, out long mirrorBase)
    {
        mirrorBase = 0;
        var result = new Candle[display.Count];
        for (int i = 0; i < result.Length; i++) result[i] = display[i];
        if (!mirror)
        {
            if (from.Mirror) FlipAround(result, from.MirrorBase);
            return result;
        }
        if (result.Length == 0)
        {
            mirrorBase = fixedBase ?? 0;
            return result;
        }
        long span = FlipBase(result);
        mirrorBase = fixedBase ?? (from.Mirror ? 2 * from.MirrorBase - span : span);
        if (!from.Mirror)
        {
            FlipAround(result, mirrorBase);
            return result;
        }
        long shift = mirrorBase - from.MirrorBase;
        if (shift == 0) return result;
        for (int i = 0; i < result.Length; i++)
        {
            var c = result[i];
            result[i] = c with
            {
                Min = (int)(c.Min + shift),
                Max = (int)(c.Max + shift),
                Avg = (int)(c.Avg + shift),
            };
        }
        return result;
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
