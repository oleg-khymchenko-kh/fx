using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly record struct DensityHistogram(
    int LoPip, double[] Counts, double MaxCount, int AnchorIndex)
{
    public double[]? BidCounts { get; init; }

    public double[]? AskCounts { get; init; }

    public bool HasSides => BidCounts != null && AskCounts != null;
}

public readonly record struct DensityRowValue(double Total, double Bid, double Ask);

public static class DensityProfile
{
    public const int PipPoints = 10;

    private const double WeightEpsilon = 1e-9;

    public static DensityHistogram Build(MinuteSequence minutes, long anchorUnix, int windowBars)
    {
        int anchor = UpperBound(minutes, anchorUnix) - 1;
        if (anchor < 0 || windowBars <= 0)
            return new DensityHistogram(0, Array.Empty<double>(), 0, -1);
        int start = Math.Max(0, anchor - windowBars + 1);
        int lo = int.MaxValue;
        int hi = int.MinValue;
        for (int i = start; i <= anchor; i++)
        {
            var candle = minutes[i];
            int a = PipLevel(candle.Min);
            int b = PipLevel(candle.Max);
            if (a < lo) lo = a;
            if (b > hi) hi = b;
        }
        var diff = new double[hi - lo + 2];
        for (int i = start; i <= anchor; i++)
        {
            var candle = minutes[i];
            diff[PipLevel(candle.Min) - lo]++;
            diff[PipLevel(candle.Max) - lo + 1]--;
        }
        return FromDiff(diff, lo, anchor);
    }

    public static DensityHistogram BuildWeighted(MinuteSequence minutes, long anchorUnix, int windowBars)
    {
        int anchor = UpperBound(minutes, anchorUnix) - 1;
        if (anchor < 0 || windowBars <= 0)
            return new DensityHistogram(0, Array.Empty<double>(), 0, -1);
        return Weighted(minutes, Math.Max(0, anchor - windowBars + 1), anchor);
    }

    public static DensityHistogram BuildWeightedRange(MinuteSequence minutes, long fromUnix, long toUnix)
    {
        int last = UpperBound(minutes, toUnix) - 1;
        int start = UpperBound(minutes, fromUnix - 1);
        if (last < 0 || start > last)
            return new DensityHistogram(0, Array.Empty<double>(), 0, -1);
        return Weighted(minutes, start, last);
    }

    public static DensityHistogram BuildProfiled(MinuteSequence minutes, ProfileSet profiles,
        long anchorUnix, int windowBars, SeriesTransform? transform = null)
    {
        int anchor = UpperBound(minutes, anchorUnix) - 1;
        if (anchor < 0 || windowBars <= 0)
            return new DensityHistogram(0, Array.Empty<double>(), 0, -1);
        return Profiled(minutes, profiles, Math.Max(0, anchor - windowBars + 1), anchor, transform);
    }

    public static DensityHistogram BuildProfiledRange(MinuteSequence minutes, ProfileSet profiles,
        long fromUnix, long toUnix, SeriesTransform? transform = null)
    {
        int last = UpperBound(minutes, toUnix) - 1;
        int start = UpperBound(minutes, fromUnix - 1);
        if (last < 0 || start > last)
            return new DensityHistogram(0, Array.Empty<double>(), 0, -1);
        return Profiled(minutes, profiles, start, last, transform);
    }

    public static int StoredPipToDisplay(SeriesTransform? transform, int storedPip) =>
        transform == null ? storedPip : PipLevel(transform.ToDisplay(storedPip * PipPoints));

    private static DensityHistogram Profiled(MinuteSequence minutes, ProfileSet profiles, int start, int end,
        SeriesTransform? transform)
    {
        int lo = int.MaxValue;
        int hi = int.MinValue;
        int scan = profiles.LowerBound(minutes[start].MinuteUnixSeconds);
        int cursor = scan;
        for (int i = start; i <= end; i++)
        {
            var candle = minutes[i];
            long minute = candle.MinuteUnixSeconds;
            while (cursor < profiles.Count && profiles.MinuteUnix[cursor] < minute) cursor++;
            if (cursor < profiles.Count && profiles.MinuteUnix[cursor] == minute)
            {
                int stored = profiles.BasePip[cursor];
                int a = StoredPipToDisplay(transform, stored);
                int b = StoredPipToDisplay(transform,
                    stored + profiles.Start[cursor + 1] - profiles.Start[cursor] - 1);
                if (a > b) (a, b) = (b, a);
                if (a < lo) lo = a;
                if (b > hi) hi = b;
            }
            else if (candle.HasVolume && candle.Volume > 0)
            {
                int a = PipLevel(candle.Min);
                int b = PipLevel(candle.Max);
                if (a < lo) lo = a;
                if (b > hi) hi = b;
            }
        }
        if (lo > hi) return new DensityHistogram(0, Array.Empty<double>(), 0, -1);
        var diff = new double[hi - lo + 2];
        var bidDiff = new double[hi - lo + 2];
        var askDiff = new double[hi - lo + 2];
        bool anySides = false;
        cursor = scan;
        for (int i = start; i <= end; i++)
        {
            var candle = minutes[i];
            long minute = candle.MinuteUnixSeconds;
            while (cursor < profiles.Count && profiles.MinuteUnix[cursor] < minute) cursor++;
            if (cursor < profiles.Count && profiles.MinuteUnix[cursor] == minute)
            {
                int basePip = profiles.BasePip[cursor];
                int from = profiles.Start[cursor];
                int to = profiles.Start[cursor + 1];
                for (int c = from; c < to; c++)
                {
                    int cell = StoredPipToDisplay(transform, basePip + c - from) - lo;
                    int bid = profiles.Bid[c];
                    int ask = profiles.Ask[c];
                    diff[cell] += bid + ask;
                    diff[cell + 1] -= bid + ask;
                    bidDiff[cell] += bid;
                    bidDiff[cell + 1] -= bid;
                    askDiff[cell] += ask;
                    askDiff[cell + 1] -= ask;
                }
                anySides = true;
            }
            else if (candle.HasVolume && candle.Volume > 0)
            {
                int a = PipLevel(candle.Min);
                int b = PipLevel(candle.Max);
                double share = (double)candle.Volume / (b - a + 1);
                diff[a - lo] += share;
                diff[b - lo + 1] -= share;
            }
        }
        var histogram = FromDiff(diff, lo, end);
        if (!anySides || histogram.Counts.Length == 0) return histogram;
        return histogram with
        {
            BidCounts = Running(bidDiff, histogram.Counts.Length),
            AskCounts = Running(askDiff, histogram.Counts.Length),
        };
    }

    private static double[] Running(double[] diff, int length)
    {
        var counts = new double[length];
        double running = 0;
        for (int i = 0; i < length; i++)
        {
            running += diff[i];
            counts[i] = running < 0 ? 0 : running;
        }
        return counts;
    }

    private static DensityHistogram Weighted(MinuteSequence minutes, int start, int end)
    {
        int lo = int.MaxValue;
        int hi = int.MinValue;
        for (int i = start; i <= end; i++)
        {
            var candle = minutes[i];
            int a = PipLevel(candle.Min);
            int b = PipLevel(candle.Max);
            if (a < lo) lo = a;
            if (b > hi) hi = b;
        }
        var diff = new double[hi - lo + 2];
        for (int i = start; i <= end; i++)
        {
            var candle = minutes[i];
            if (!candle.HasVolume || candle.Volume <= 0) continue;
            int a = PipLevel(candle.Min);
            int b = PipLevel(candle.Max);
            double share = (double)candle.Volume / (b - a + 1);
            diff[a - lo] += share;
            diff[b - lo + 1] -= share;
        }
        return FromDiff(diff, lo, end);
    }

    private static DensityHistogram FromDiff(double[] diff, int lo, int anchor)
    {
        var counts = new double[diff.Length - 1];
        double running = 0;
        double max = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            running += diff[i];
            counts[i] = running;
            if (running > max) max = running;
        }
        if (max <= 0) return new DensityHistogram(0, Array.Empty<double>(), 0, -1);
        double floor = max * WeightEpsilon;
        for (int i = 0; i < counts.Length; i++)
            if (counts[i] < floor) counts[i] = 0;
        return new DensityHistogram(lo, counts, max, anchor);
    }

    public static double MaxCountIn(DensityHistogram histogram, int pipLo, int pipHi)
    {
        int from = Math.Max(pipLo - histogram.LoPip, 0);
        int to = Math.Min(pipHi - histogram.LoPip, histogram.Counts.Length - 1);
        double max = 0;
        for (int i = from; i <= to; i++)
            if (histogram.Counts[i] > max) max = histogram.Counts[i];
        return max;
    }

    public static DensityRowValue RowValueIn(DensityHistogram histogram, int pipLo, int pipHi)
    {
        int from = Math.Max(pipLo - histogram.LoPip, 0);
        int to = Math.Min(pipHi - histogram.LoPip, histogram.Counts.Length - 1);
        double max = 0;
        int at = -1;
        for (int i = from; i <= to; i++)
            if (histogram.Counts[i] > max)
            {
                max = histogram.Counts[i];
                at = i;
            }
        if (at < 0 || !histogram.HasSides) return new DensityRowValue(max, 0, 0);
        double bid = histogram.BidCounts![at];
        double ask = histogram.AskCounts![at];
        double sides = bid + ask;
        if (sides > max && sides > 0)
        {
            bid = bid * max / sides;
            ask = ask * max / sides;
        }
        return new DensityRowValue(max, bid, ask);
    }

    public static int PipLevel(int points) => (points + PipPoints / 2) / PipPoints;

    public static int CountInRange(MinuteSequence minutes, long fromUnix, long toUnix) =>
        UpperBound(minutes, toUnix) - UpperBound(minutes, fromUnix - 1);

    private static int UpperBound(MinuteSequence minutes, long unixSeconds)
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
