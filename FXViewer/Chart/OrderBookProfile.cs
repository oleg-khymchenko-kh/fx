using FXViewer.OrderBook;
using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly record struct OrderBookSides(DensityHistogram Buy, DensityHistogram Sell);

public static class OrderBookProfile
{
    public const bool LeftSideIsBuy = true;

    private const long PriceMaxAgeSeconds = 3600;

    private static readonly DensityHistogram Empty = new(0, Array.Empty<double>(), 0, -1);

    public static OrderBookSides BuildSides(OrderBookSnapshot? snapshot, bool positions,
        SeriesTransform transform)
    {
        if (snapshot == null) return new OrderBookSides(Empty, Empty);

        var pips = new int[OrderBookSnapshot.Rows];
        int lo = int.MaxValue;
        int hi = int.MinValue;
        for (int row = 0; row < OrderBookSnapshot.Rows; row++)
        {
            int pip = DensityProfile.PipLevel(transform.ToDisplay(snapshot.RowPricePoints(row)));
            pips[row] = pip;
            if (pip < lo) lo = pip;
            if (pip > hi) hi = pip;
        }

        var left = new double[hi - lo + 1];
        var right = new double[hi - lo + 1];
        double max = 0;
        for (int row = 0; row < OrderBookSnapshot.Rows; row++)
        {
            double l = positions ? snapshot.PositionsLeft[row] : snapshot.PendingLeft[row];
            double r = positions ? snapshot.PositionsRight[row] : snapshot.PendingRight[row];
            if (l <= 0 && r <= 0) continue;
            int next = row + 1 < OrderBookSnapshot.Rows ? pips[row + 1] : pips[row];
            int from = Math.Min(pips[row], next) - lo;
            int to = Math.Max(pips[row], next) - lo;
            for (int i = from; i <= to; i++)
            {
                if (l > left[i]) left[i] = l;
                if (r > right[i]) right[i] = r;
            }
            if (l > max) max = l;
            if (r > max) max = r;
        }
        if (max <= 0) return new OrderBookSides(Empty, Empty);
        var leftSide = new DensityHistogram(lo, left, max, 0);
        var rightSide = new DensityHistogram(lo, right, max, 0);
        return LeftSideIsBuy
            ? new OrderBookSides(leftSide, rightSide)
            : new OrderBookSides(rightSide, leftSide);
    }

    public static int? PricePointsAt(Candle[] minutes, long timeUnix)
    {
        if (minutes.Length == 0) return null;
        int lo = 0;
        int hi = minutes.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (minutes[mid].MinuteUnixSeconds <= timeUnix) lo = mid + 1;
            else hi = mid;
        }
        if (lo == 0) return null;
        var candle = minutes[lo - 1];
        return timeUnix - candle.MinuteUnixSeconds > PriceMaxAgeSeconds ? null : candle.Avg;
    }

    public static OrderBookSnapshot? At(OrderBookSnapshot[] snapshots, long anchorUnix)
    {
        if (snapshots.Length == 0) return null;
        int lo = 0;
        int hi = snapshots.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (snapshots[mid].TimeUnix <= anchorUnix) lo = mid + 1;
            else hi = mid;
        }
        return lo == 0 ? null : snapshots[lo - 1];
    }
}
