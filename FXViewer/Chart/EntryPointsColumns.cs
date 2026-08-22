using FXViewer.Compute;
using FXViewer.Storage;

namespace FXViewer.Chart;

public static class EntryPointsColumns
{
    public const byte Buy = 1;
    public const byte Sell = 2;
    public const byte BothLost = 4;

    public static byte[] Build(CandleHistory history, long columnSeconds, long firstBucket, int count,
        WeekendCompressor? map)
    {
        var states = new byte[count];
        if (count <= 0) return states;
        int run = ChartColumns.MinuteRun(columnSeconds);
        if (run > 1)
        {
            long minuteFirst = ChartColumns.MinuteBucket(firstBucket, run);
            var minutes = Build(history, ChartColumns.MinuteSeconds, minuteFirst,
                ChartColumns.MinuteCount(count, run), map);
            return ChartColumns.Expand(minutes, minuteFirst, run, firstBucket, count, (byte)0);
        }
        var edges = ChartColumns.ColumnEdges(map, columnSeconds, firstBucket, count);
        int level = ChartColumns.LevelFor(columnSeconds, map);
        if (level >= 0) Fill(history.Levels[level], edges, states);
        else Fill(history.Minutes, edges, states);
        return states;
    }

    private static void Fill(AggBlock[] blocks, long[] edges, byte[] states)
    {
        int i = LowerBound(blocks, edges[0]);
        for (int col = 0; col < states.Length && i < blocks.Length; col++)
        {
            long hi = edges[col + 1];
            byte s = 0;
            for (; i < blocks.Length && blocks[i].StartUnixSeconds < hi; i++)
            {
                var b = blocks[i];
                if (EntryPointsSymbol.StoredBuyWin(b.Max)) s |= Buy;
                if (EntryPointsSymbol.StoredSellWin(b.Min)) s |= Sell;
                if (EntryPointsSymbol.StoredBothLost(b.AvgSum)) s |= BothLost;
            }
            states[col] = s;
        }
    }

    private static void Fill(Candle[] minutes, long[] edges, byte[] states)
    {
        int i = LowerBound(minutes, edges[0]);
        for (int col = 0; col < states.Length && i < minutes.Length; col++)
        {
            long hi = edges[col + 1];
            byte s = 0;
            for (; i < minutes.Length && minutes[i].MinuteUnixSeconds < hi; i++)
            {
                var c = minutes[i];
                if (EntryPointsSymbol.StoredBuyWin(c.Max)) s |= Buy;
                if (EntryPointsSymbol.StoredSellWin(c.Min)) s |= Sell;
                if (EntryPointsSymbol.StoredBothLost(c.Avg)) s |= BothLost;
            }
            states[col] = s;
        }
    }

    private static int LowerBound(AggBlock[] blocks, long unixSeconds)
    {
        int lo = 0;
        int hi = blocks.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (blocks[mid].StartUnixSeconds < unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int LowerBound(Candle[] minutes, long unixSeconds)
    {
        int lo = 0;
        int hi = minutes.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (minutes[mid].MinuteUnixSeconds < unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
