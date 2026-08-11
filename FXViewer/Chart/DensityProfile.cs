using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly record struct DensityHistogram(int LoPip, int[] Counts, int MaxCount, int AnchorIndex);

public static class DensityProfile
{
    public const int PipPoints = 10;

    public static DensityHistogram Build(Candle[] minutes, long anchorUnix, int windowBars)
    {
        int anchor = UpperBound(minutes, anchorUnix) - 1;
        if (anchor < 0 || windowBars <= 0)
            return new DensityHistogram(0, Array.Empty<int>(), 0, -1);
        int start = Math.Max(0, anchor - windowBars + 1);
        int lo = int.MaxValue;
        int hi = int.MinValue;
        for (int i = start; i <= anchor; i++)
        {
            int a = PipLevel(minutes[i].Min);
            int b = PipLevel(minutes[i].Max);
            if (a < lo) lo = a;
            if (b > hi) hi = b;
        }
        var diff = new int[hi - lo + 2];
        for (int i = start; i <= anchor; i++)
        {
            diff[PipLevel(minutes[i].Min) - lo]++;
            diff[PipLevel(minutes[i].Max) - lo + 1]--;
        }
        var counts = new int[hi - lo + 1];
        int running = 0;
        int max = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            running += diff[i];
            counts[i] = running;
            if (running > max) max = running;
        }
        return new DensityHistogram(lo, counts, max, anchor);
    }

    public static int MaxCountIn(DensityHistogram histogram, int pipLo, int pipHi)
    {
        int from = Math.Max(pipLo - histogram.LoPip, 0);
        int to = Math.Min(pipHi - histogram.LoPip, histogram.Counts.Length - 1);
        int max = 0;
        for (int i = from; i <= to; i++)
            if (histogram.Counts[i] > max) max = histogram.Counts[i];
        return max;
    }

    public static int PipLevel(int points) => (points + PipPoints / 2) / PipPoints;

    private static int UpperBound(Candle[] minutes, long unixSeconds)
    {
        int lo = 0;
        int hi = minutes.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (minutes[mid].MinuteUnixSeconds <= unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
