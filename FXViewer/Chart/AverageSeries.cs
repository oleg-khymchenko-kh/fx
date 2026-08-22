using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly record struct AverageDiff(int NewFrom, int NewToExcl, int OldFrom, int OldToExcl);

public static class AverageSeries
{
    public static Candle[] Compute(Candle[] parent, int windowBars, bool fromFuture, bool volumeWeighted) =>
        ComputeRange(parent, windowBars, fromFuture, volumeWeighted, 0, parent.Length);

    public static Candle[] ComputeRange(
        Candle[] parent, int windowBars, bool fromFuture, bool volumeWeighted, int from, int toExcl)
    {
        int n = parent.Length;
        from = Math.Clamp(from, 0, n);
        toExcl = Math.Clamp(toExcl, from, n);
        var result = new Candle[toExcl - from];
        if (result.Length == 0) return result;
        int w = Math.Max(1, windowBars);
        var sums = new WindowSums();
        if (fromFuture)
        {
            int seedEnd = (int)Math.Min(n - 1, (long)(toExcl - 1) + w - 1);
            for (int j = seedEnd; j >= toExcl; j--) sums.Add(parent[j]);
            for (int i = toExcl - 1; i >= from; i--)
            {
                sums.Add(parent[i]);
                if (sums.Count > w) sums.Remove(parent[i + w]);
                result[i - from] = Flat(parent[i].MinuteUnixSeconds, sums, volumeWeighted);
            }
        }
        else
        {
            int seedStart = Math.Max(0, from - (w - 1));
            for (int j = seedStart; j < from; j++) sums.Add(parent[j]);
            for (int i = from; i < toExcl; i++)
            {
                sums.Add(parent[i]);
                if (sums.Count > w) sums.Remove(parent[i - w]);
                result[i - from] = Flat(parent[i].MinuteUnixSeconds, sums, volumeWeighted);
            }
        }
        return result;
    }

    public static AverageDiff? Diff(
        Candle[] oldParent, Candle[] newParent, int windowBars, bool fromFuture, bool volumeWeighted)
    {
        int n0 = oldParent.Length;
        int n1 = newParent.Length;
        int limit = Math.Min(n0, n1);
        int prefix = 0;
        while (prefix < limit && Same(oldParent[prefix], newParent[prefix], volumeWeighted)) prefix++;
        int suffix = 0;
        while (suffix < limit - prefix
               && Same(oldParent[n0 - 1 - suffix], newParent[n1 - 1 - suffix], volumeWeighted))
            suffix++;
        if (n0 == n1 && prefix == n1) return null;
        int w = Math.Max(1, windowBars);
        int newFrom = fromFuture ? (int)Math.Max(0, (long)prefix - (w - 1)) : prefix;
        int newToExcl = fromFuture
            ? n1 - suffix
            : (int)Math.Min(n1, (long)(n1 - suffix) + (w - 1));
        return new AverageDiff(newFrom, newToExcl, newFrom, newToExcl - (n1 - n0));
    }

    public static Candle[] LiveTail(
        Candle[] baseMinutes, Candle[] live, int windowBars, bool fromFuture, bool volumeWeighted)
    {
        int tailLen = live.Length;
        if (tailLen == 0) return Array.Empty<Candle>();
        int w = Math.Max(1, windowBars);
        var result = new Candle[tailLen];
        var sums = new WindowSums();
        if (fromFuture)
        {
            for (int i = tailLen - 1; i >= 0; i--)
            {
                sums.Add(live[i]);
                if (sums.Count > w) sums.Remove(live[i + w]);
                result[i] = Flat(live[i].MinuteUnixSeconds, sums, volumeWeighted);
            }
        }
        else
        {
            int n = baseMinutes.Length;
            for (int j = Math.Max(0, n - (w - 1)); j < n; j++) sums.Add(baseMinutes[j]);
            for (int i = 0; i < tailLen; i++)
            {
                sums.Add(live[i]);
                if (sums.Count > w)
                {
                    int drop = n + i - w;
                    sums.Remove(drop < n ? baseMinutes[drop] : live[drop - n]);
                }
                result[i] = Flat(live[i].MinuteUnixSeconds, sums, volumeWeighted);
            }
        }
        return result;
    }

    private struct WindowSums
    {
        public long PriceSum;
        public int Count;
        public long VolumeSum;
        public long PriceVolumeSum;

        public void Add(Candle c)
        {
            PriceSum += c.Avg;
            Count++;
            if (!c.HasVolume) return;
            VolumeSum += c.Volume;
            PriceVolumeSum += (long)c.Avg * c.Volume;
        }

        public void Remove(Candle c)
        {
            PriceSum -= c.Avg;
            Count--;
            if (!c.HasVolume) return;
            VolumeSum -= c.Volume;
            PriceVolumeSum -= (long)c.Avg * c.Volume;
        }
    }

    private static bool Same(Candle a, Candle b, bool volumeWeighted) =>
        a.MinuteUnixSeconds == b.MinuteUnixSeconds && a.Avg == b.Avg
        && (!volumeWeighted || (a.HasVolume == b.HasVolume && a.Volume == b.Volume));

    private static Candle Flat(long minuteUnix, in WindowSums sums, bool volumeWeighted)
    {
        int v = volumeWeighted && sums.VolumeSum > 0
            ? (int)Math.Round(sums.PriceVolumeSum / (double)sums.VolumeSum, MidpointRounding.AwayFromZero)
            : (int)Math.Round(sums.PriceSum / (double)sums.Count, MidpointRounding.AwayFromZero);
        return new Candle(minuteUnix, v, v, v, true);
    }
}
