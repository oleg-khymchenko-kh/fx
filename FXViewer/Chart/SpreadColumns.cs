using FXViewer.Storage;

namespace FXViewer.Chart;

public static class SpreadColumns
{
    public static int[] Build(CandleHistory history, long columnSeconds, long firstBucket, int count,
        WeekendCompressor? map)
    {
        var columns = new int[count];
        Array.Fill(columns, -1);
        if (count <= 0) return columns;
        int run = ChartColumns.MinuteRun(columnSeconds);
        if (run > 1)
        {
            long minuteFirst = ChartColumns.MinuteBucket(firstBucket, run);
            var minutes = Build(history, ChartColumns.MinuteSeconds, minuteFirst,
                ChartColumns.MinuteCount(count, run), map);
            return ChartColumns.Expand(minutes, minuteFirst, run, firstBucket, count, -1);
        }
        var edges = ChartColumns.ColumnEdges(map, columnSeconds, firstBucket, count);
        int level = ChartColumns.LevelFor(columnSeconds, map);
        if (level >= 0) Fill(history.Levels[level], edges, columns);
        else Fill(history.Minutes, edges, columns);
        Fill(history.Live, edges, columns);
        return columns;
    }

    private static void Fill(AggBlock[] blocks, long[] edges, int[] columns)
    {
        int i = LowerBound(blocks, edges[0]);
        for (int col = 0; col < columns.Length && i < blocks.Length; col++)
        {
            long hi = edges[col + 1];
            for (; i < blocks.Length && blocks[i].StartUnixSeconds < hi; i++)
                if (blocks[i].SpreadMaxTenths > columns[col]) columns[col] = blocks[i].SpreadMaxTenths;
        }
    }

    private static void Fill(Candle[] minutes, long[] edges, int[] columns)
    {
        int i = LowerBound(minutes, edges[0]);
        for (int col = 0; col < columns.Length && i < minutes.Length; col++)
        {
            long hi = edges[col + 1];
            for (; i < minutes.Length && minutes[i].MinuteUnixSeconds < hi; i++)
            {
                if (!minutes[i].HasSpread) continue;
                int tenths = minutes[i].SpreadTenths;
                if (tenths > columns[col]) columns[col] = tenths;
            }
        }
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
}
