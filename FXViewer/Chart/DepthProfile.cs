using FXViewer.OrderBook;

namespace FXViewer.Chart;

public static class DepthProfile
{
    private static readonly DensityHistogram Empty = new(0, Array.Empty<double>(), 0, -1);

    public static OrderBookSides BuildSides(DepthSnapshot? snapshot, int pipPoints,
        SeriesTransform transform, int? anchorPoints)
    {
        if (snapshot is not { } book) return new OrderBookSides(Empty, Empty);
        int levels = book.Bid.Length;
        if (levels == 0) return new OrderBookSides(Empty, Empty);

        int mid = anchorPoints ?? book.MidPoints;
        int lo = int.MaxValue;
        int hi = int.MinValue;
        var bidPips = new int[levels];
        var askPips = new int[levels];
        for (int i = 0; i < levels; i++)
        {
            bidPips[i] = DensityProfile.PipLevel(transform.ToDisplay(mid - (i + 1) * pipPoints));
            askPips[i] = DensityProfile.PipLevel(transform.ToDisplay(mid + i * pipPoints));
            lo = Math.Min(lo, Math.Min(bidPips[i], askPips[i]));
            hi = Math.Max(hi, Math.Max(bidPips[i], askPips[i]));
        }
        if (lo > hi) return new OrderBookSides(Empty, Empty);

        var buy = new double[hi - lo + 1];
        var sell = new double[hi - lo + 1];
        double max = 0;
        for (int i = 0; i < levels; i++)
        {
            double b = book.Bid[i];
            if (b > 0)
            {
                int at = bidPips[i] - lo;
                if (b > buy[at]) buy[at] = b;
                if (b > max) max = b;
            }
            double a = book.Ask[i];
            if (a > 0)
            {
                int at = askPips[i] - lo;
                if (a > sell[at]) sell[at] = a;
                if (a > max) max = a;
            }
        }
        if (max <= 0) return new OrderBookSides(Empty, Empty);
        return new OrderBookSides(new DensityHistogram(lo, buy, max, 0),
            new DensityHistogram(lo, sell, max, 0));
    }

    public static DepthSnapshot? At(DepthSnapshot[] snapshots, long anchorUnix)
    {
        if (snapshots.Length == 0) return null;
        int lo = 0;
        int hi = snapshots.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (snapshots[mid].MinuteUnix <= anchorUnix) lo = mid + 1;
            else hi = mid;
        }
        return lo == 0 ? null : snapshots[lo - 1];
    }
}
