using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly record struct AgeColumn(int Up, int Down);

public static class PriceAgeColumns
{
    public static AgeColumn[] Build(CandleHistory history, int k, long firstBucket, int count,
        WeekendCompressor? map, bool mirror)
    {
        var columns = new AgeColumn[count];
        if (count <= 0) return columns;
        var edges = ChartColumns.ColumnEdges(map, k, firstBucket, count);
        int level = ChartColumns.LevelFor(k, map);
        if (level >= 0) Fill(history.Levels[level], edges, columns);
        else Fill(history.Minutes, edges, columns);
        if (mirror)
            for (int i = 0; i < columns.Length; i++)
                columns[i] = new AgeColumn(-columns[i].Down, -columns[i].Up);
        return columns;
    }

    private static void Fill(AggBlock[] blocks, long[] edges, AgeColumn[] columns)
    {
        int i = LowerBound(blocks, edges[0]);
        for (int col = 0; col < columns.Length && i < blocks.Length; col++)
        {
            long hi = edges[col + 1];
            int up = 0;
            int down = 0;
            for (; i < blocks.Length && blocks[i].StartUnixSeconds < hi; i++)
            {
                if (blocks[i].Max > up) up = blocks[i].Max;
                if (blocks[i].Min < down) down = blocks[i].Min;
            }
            columns[col] = new AgeColumn(up, down);
        }
    }

    private static void Fill(Candle[] minutes, long[] edges, AgeColumn[] columns)
    {
        int i = LowerBound(minutes, edges[0]);
        for (int col = 0; col < columns.Length && i < minutes.Length; col++)
        {
            long hi = edges[col + 1];
            int up = 0;
            int down = 0;
            for (; i < minutes.Length && minutes[i].MinuteUnixSeconds < hi; i++)
            {
                int v = minutes[i].Max;
                if (v > up) up = v;
                if (v < down) down = v;
            }
            columns[col] = new AgeColumn(up, down);
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
