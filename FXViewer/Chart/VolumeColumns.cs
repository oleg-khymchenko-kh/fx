using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly record struct VolumeColumnSet(long[] Total, long[] Bid, long[] Ask);

public static class VolumeColumns
{
    public static VolumeColumnSet Build(CandleHistory history, ProfileSet? profiles,
        long columnSeconds, long firstBucket, int count, WeekendCompressor? map, int groupMinutes = 1)
    {
        var columns = new long[count];
        var bid = new long[count];
        var ask = new long[count];
        Array.Fill(columns, -1L);
        if (count <= 0) return new VolumeColumnSet(columns, bid, ask);
        int run = ChartColumns.MinuteRun(columnSeconds);
        if (run > 1)
        {
            long minuteFirst = ChartColumns.MinuteBucket(firstBucket, run);
            int minuteCount = ChartColumns.MinuteCount(count, run);
            var inner = Build(history, profiles, ChartColumns.MinuteSeconds, minuteFirst,
                minuteCount, map, groupMinutes);
            return new VolumeColumnSet(
                ChartColumns.Expand(inner.Total, minuteFirst, run, firstBucket, count, -1L),
                ChartColumns.Expand(inner.Bid, minuteFirst, run, firstBucket, count, 0L),
                ChartColumns.Expand(inner.Ask, minuteFirst, run, firstBucket, count, 0L));
        }
        long groupSec = Math.Max(1, groupMinutes) * ChartColumns.MinuteSeconds;
        var edges = ChartColumns.ColumnEdges(map, columnSeconds, firstBucket, count);
        int columnLevel = ChartColumns.LevelFor(columnSeconds, map);
        if (groupSec <= ChartColumns.MinuteSeconds && columnLevel >= 0)
        {
            MaxBlocks(history.Levels[columnLevel], edges, columns);
            MaxMinutes(history.Live, edges, columns);
        }
        else
        {
            MaxGroups(history, map, groupSec, edges, columns);
        }
        FillSides(profiles, edges, bid, ask);
        return new VolumeColumnSet(columns, bid, ask);
    }

    private static void MaxBlocks(AggBlock[] blocks, long[] edges, long[] columns)
    {
        int i = LowerBound(blocks, edges[0]);
        for (int c = 0; c < columns.Length && i < blocks.Length; c++)
        {
            long hi = edges[c + 1];
            for (; i < blocks.Length && blocks[i].StartUnixSeconds < hi; i++)
                if (blocks[i].VolumeMax > columns[c]) columns[c] = blocks[i].VolumeMax;
        }
    }

    private static void MaxMinutes(Candle[] minutes, long[] edges, long[] columns)
    {
        int i = LowerBound(minutes, edges[0]);
        for (int c = 0; c < columns.Length && i < minutes.Length; c++)
        {
            long hi = edges[c + 1];
            for (; i < minutes.Length && minutes[i].MinuteUnixSeconds < hi; i++)
                if (minutes[i].HasVolume && minutes[i].Volume > columns[c])
                    columns[c] = minutes[i].Volume;
        }
    }

    private static void MaxGroups(CandleHistory history, WeekendCompressor? map, long groupSec,
        long[] edges, long[] columns)
    {
        int count = columns.Length;
        long from = edges[0] - edges[0] % groupSec;
        long toExcl = edges[count] - 1;
        toExcl = toExcl - toExcl % groupSec + groupSec;
        if (toExcl <= from) return;
        int level = ChartColumns.LevelFor(groupSec, map);
        var blocks = level >= 0 ? history.Levels[level] : Array.Empty<AggBlock>();
        var minutes = level >= 0 ? Array.Empty<Candle>() : history.Minutes;
        var live = history.Live;
        int bi = LowerBound(blocks, from);
        int mi = LowerBound(minutes, from);
        int li = LowerBound(live, from);
        long groupStart = long.MinValue;
        long groupSum = -1;
        int cursor = 0;
        while (true)
        {
            long tb = bi < blocks.Length ? blocks[bi].StartUnixSeconds : long.MaxValue;
            long tm = mi < minutes.Length ? minutes[mi].MinuteUnixSeconds : long.MaxValue;
            long tl = li < live.Length ? live[li].MinuteUnixSeconds : long.MaxValue;
            long t;
            long v;
            if (tb <= tm && tb <= tl)
            {
                t = tb;
                v = blocks[bi].VolumeSum;
                bi++;
            }
            else if (tm <= tl)
            {
                t = tm;
                v = minutes[mi].HasVolume ? minutes[mi].Volume : -1;
                mi++;
            }
            else
            {
                t = tl;
                v = live[li].HasVolume ? live[li].Volume : -1;
                li++;
            }
            if (t >= toExcl) break;
            long start = t - t % groupSec;
            if (start != groupStart)
            {
                Emit(edges, columns, ref cursor, groupStart, groupSec, groupSum);
                groupStart = start;
                groupSum = -1;
            }
            if (v >= 0) groupSum = Math.Max(groupSum, 0) + v;
        }
        Emit(edges, columns, ref cursor, groupStart, groupSec, groupSum);
    }

    private static void Emit(long[] edges, long[] columns, ref int cursor,
        long groupStart, long groupSec, long groupSum)
    {
        if (groupStart == long.MinValue || groupSum < 0) return;
        long groupEnd = groupStart + groupSec;
        while (cursor < columns.Length && edges[cursor + 1] <= groupStart) cursor++;
        for (int c = cursor; c < columns.Length && edges[c] < groupEnd; c++)
            if (groupSum > columns[c]) columns[c] = groupSum;
    }

    private static void FillSides(ProfileSet? profiles, long[] edges, long[] bid, long[] ask)
    {
        if (profiles == null || profiles.Count == 0) return;
        int i = profiles.LowerBound(edges[0]);
        for (int b = 0; b < bid.Length && i < profiles.Count; b++)
        {
            long hi = edges[b + 1];
            for (; i < profiles.Count && profiles.MinuteUnix[i] < hi; i++)
                for (int c = profiles.Start[i]; c < profiles.Start[i + 1]; c++)
                {
                    bid[b] += profiles.Bid[c];
                    ask[b] += profiles.Ask[c];
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
