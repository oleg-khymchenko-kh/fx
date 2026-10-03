using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly record struct VolumeColumnSet(long[] Total, long[] Bid, long[] Ask);

public static class VolumeColumns
{
    public static VolumeColumnSet Build(CandleHistory history, ProfileSet? profiles,
        long columnSeconds, long firstBucket, int count, WeekendCompressor? map, int groupMinutes = 1,
        long maxUnix = long.MaxValue)
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
                minuteCount, map, groupMinutes, maxUnix);
            return new VolumeColumnSet(
                ChartColumns.Expand(inner.Total, minuteFirst, run, firstBucket, count, -1L),
                ChartColumns.Expand(inner.Bid, minuteFirst, run, firstBucket, count, 0L),
                ChartColumns.Expand(inner.Ask, minuteFirst, run, firstBucket, count, 0L));
        }
        long groupSec = Math.Max(1, groupMinutes) * ChartColumns.MinuteSeconds;
        var edges = ChartColumns.ColumnEdges(map, columnSeconds, firstBucket, count, maxUnix);
        int columnLevel = ChartColumns.LevelFor(columnSeconds, map);
        var set = new VolumeColumnSet(columns, bid, ask);
        if (groupSec <= ChartColumns.MinuteSeconds && columnLevel >= 0)
        {
            MaxBlocks(history.Levels[columnLevel], edges, columns);
            MaxMinutes(history.Live, edges, columns);
            PeakMinuteSides(history, profiles, edges, set);
        }
        else
        {
            MaxGroups(history, profiles, map, groupSec, edges, set);
        }
        for (int c = 0; c < count && maxUnix != long.MaxValue; c++)
        {
            if (edges[c] < maxUnix) continue;
            columns[c] = -1L;
            bid[c] = 0L;
            ask[c] = 0L;
        }
        return set;
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

    private static void MaxGroups(CandleHistory history, ProfileSet? profiles, WeekendCompressor? map,
        long groupSec, long[] edges, VolumeColumnSet set)
    {
        int count = set.Total.Length;
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
        int pi = profiles?.LowerBound(from) ?? 0;
        long groupStart = long.MinValue;
        long groupSum = -1;
        int cursor = 0;
        while (true)
        {
            long tb = bi < blocks.Length ? blocks[bi].StartUnixSeconds : long.MaxValue;
            long tm = mi < minutes.Length ? minutes[mi].MinuteUnixSeconds : long.MaxValue;
            long tl = li < live.Length ? live[li].MinuteUnixSeconds : long.MaxValue;
            long t = Math.Min(tb, Math.Min(tm, tl));
            if (t >= toExcl) break;
            long v;
            if (t == tb)
            {
                v = blocks[bi].VolumeSum;
                bi++;
            }
            else if (t == tm)
            {
                v = minutes[mi].HasVolume ? minutes[mi].Volume : -1;
                mi++;
            }
            else
            {
                v = live[li].HasVolume ? live[li].Volume : -1;
                li++;
            }
            long start = t - t % groupSec;
            if (start != groupStart)
            {
                Emit(edges, set, ref cursor, profiles, ref pi, groupStart, groupSec, groupSum);
                groupStart = start;
                groupSum = -1;
            }
            if (v >= 0) groupSum = Math.Max(groupSum, 0) + v;
        }
        Emit(edges, set, ref cursor, profiles, ref pi, groupStart, groupSec, groupSum);
    }

    private static void Emit(long[] edges, VolumeColumnSet set, ref int cursor,
        ProfileSet? profiles, ref int profileCursor, long groupStart, long groupSec, long groupSum)
    {
        if (groupStart == long.MinValue || groupSum < 0) return;
        long groupEnd = groupStart + groupSec;
        var (bid, ask) = SumSides(profiles, ref profileCursor, groupStart, groupEnd);
        var columns = set.Total;
        while (cursor < columns.Length && edges[cursor + 1] <= groupStart) cursor++;
        for (int c = cursor; c < columns.Length && edges[c] < groupEnd; c++)
        {
            if (groupSum <= columns[c]) continue;
            columns[c] = groupSum;
            set.Bid[c] = bid;
            set.Ask[c] = ask;
        }
    }

    private static (long Bid, long Ask) SumSides(ProfileSet? profiles, ref int cursor,
        long fromUnix, long toExclUnix)
    {
        if (profiles == null) return (0, 0);
        while (cursor < profiles.Count && profiles.MinuteUnix[cursor] < fromUnix) cursor++;
        long bid = 0;
        long ask = 0;
        for (; cursor < profiles.Count && profiles.MinuteUnix[cursor] < toExclUnix; cursor++)
            for (int c = profiles.Start[cursor]; c < profiles.Start[cursor + 1]; c++)
            {
                bid += profiles.Bid[c];
                ask += profiles.Ask[c];
            }
        return (bid, ask);
    }

    private static void PeakMinuteSides(CandleHistory history, ProfileSet? profiles, long[] edges,
        VolumeColumnSet set)
    {
        if (profiles == null || profiles.Count == 0) return;
        var minutes = history.Minutes;
        var live = history.Live;
        int i = profiles.LowerBound(edges[0]);
        if (i >= profiles.Count) return;
        int mi = LowerBound(minutes, profiles.MinuteUnix[i]);
        int li = LowerBound(live, profiles.MinuteUnix[i]);
        var columns = set.Total;
        for (int c = 0; c < columns.Length && i < profiles.Count; c++)
        {
            long hi = edges[c + 1];
            bool found = columns[c] < 0;
            for (; i < profiles.Count && profiles.MinuteUnix[i] < hi; i++)
            {
                if (found) continue;
                long minute = profiles.MinuteUnix[i];
                while (mi < minutes.Length && minutes[mi].MinuteUnixSeconds < minute) mi++;
                while (li < live.Length && live[li].MinuteUnixSeconds < minute) li++;
                if (!HasVolume(minutes, mi, minute, columns[c]) && !HasVolume(live, li, minute, columns[c]))
                    continue;
                int cursor = i;
                (set.Bid[c], set.Ask[c]) = SumSides(profiles, ref cursor, minute, minute + 1);
                found = true;
            }
        }
    }

    private static bool HasVolume(Candle[] candles, int index, long minuteUnix, long volume) =>
        index < candles.Length && candles[index].MinuteUnixSeconds == minuteUnix
        && candles[index].HasVolume && candles[index].Volume == volume;

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
