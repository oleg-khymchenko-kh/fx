using FXViewer.Storage;

namespace FXViewer.Chart;

public sealed class AverageFlattenMap : FlattenMap
{
    private const int MaxCuts = 20000;

    private readonly CandleHistory _history;
    private readonly Candle[] _live;
    private readonly CandleHistory? _extension;
    private readonly Candle[] _tail;
    private readonly long _cutUnix;
    private readonly long _anchorUnix;
    private readonly WeekendCompressor? _map;
    private readonly int _minuteCount;
    private readonly int _liveFrom;
    private readonly int _liveCount;

    private AverageFlattenMap(CandleHistory history, Candle[] live, CandleHistory? extension, long cutUnix,
        long anchorUnix, WeekendCompressor? map, int minuteCount, int liveFrom, int liveCount)
    {
        _history = history;
        _live = live;
        _extension = extension;
        _tail = extension?.Minutes ?? Array.Empty<Candle>();
        _cutUnix = cutUnix;
        _anchorUnix = anchorUnix;
        _map = map;
        _minuteCount = minuteCount;
        _liveFrom = liveFrom;
        _liveCount = liveCount;
        Level = Math.Round(ValueAt(anchorUnix, Virtual(anchorUnix)));
        MaxAbsShift = ComputeMaxAbsShift();
    }

    public double Level { get; }

    public override double MaxAbsShift { get; }

    private int Count => _minuteCount + _liveCount + _tail.Length;

    public static AverageFlattenMap? Build(CandleHistory history, CandleHistory? extension, long cutUnix,
        long anchorUnix, WeekendCompressor? map)
    {
        long limit = cutUnix;
        if (extension is { Minutes.Length: > 0 })
            limit = Math.Min(limit, extension.Minutes[0].MinuteUnixSeconds);
        var minutes = history.Minutes;
        var live = history.Live;
        int minuteCount = LowerBound(minutes, limit);
        int liveFrom = minuteCount > 0
            ? LowerBound(live, minutes[minuteCount - 1].MinuteUnixSeconds + 1)
            : 0;
        int liveTo = Math.Max(liveFrom, LowerBound(live, limit));
        int tail = extension?.Minutes.Length ?? 0;
        if (minuteCount + liveTo - liveFrom + tail == 0) return null;
        return new AverageFlattenMap(history, live, extension, cutUnix, anchorUnix, map,
            minuteCount, liveFrom, liveTo - liveFrom);
    }

    public bool SameInputs(CandleHistory history, CandleHistory? extension, long cutUnix, long anchorUnix,
        WeekendCompressor? map) =>
        ReferenceEquals(_history, history) && ReferenceEquals(_live, history.Live)
        && ReferenceEquals(_extension, extension) && _cutUnix == cutUnix && _anchorUnix == anchorUnix
        && ReferenceEquals(_map, map);

    public override double ShiftAt(long virtualSeconds) =>
        Level - ValueAt(_map?.ToReal(virtualSeconds) ?? virtualSeconds, virtualSeconds);

    public override double[] ColumnShifts(long firstBucket, int count, long bucketSeconds)
    {
        var view = ChartColumns.BuildLineView(
            _history, bucketSeconds, firstBucket, count, _map, _cutUnix, _extension);
        var shifts = new double[count];
        for (int i = 0; i < count; i++)
        {
            var column = view.Columns[i];
            shifts[i] = column.HasData
                ? Level - column.Avg
                : ShiftAt((firstBucket + i) * bucketSeconds);
        }
        return shifts;
    }

    public override long[] CutsBetween(long fromVirtual, long toVirtual, long columnSeconds,
        long clipFrom = long.MinValue, long clipTo = long.MaxValue)
    {
        long lo = Math.Min(fromVirtual, toVirtual);
        long hi = Math.Max(fromVirtual, toVirtual);
        long from = Math.Max(lo, Math.Max(clipFrom, Virtual(At(0).MinuteUnixSeconds)));
        long to = Math.Min(hi, Math.Min(clipTo, Virtual(At(Count - 1).MinuteUnixSeconds)));
        if (from > to) return Array.Empty<long>();
        long step = Math.Max(columnSeconds, ChartColumns.MinuteSeconds);
        long need = (to - from) / MaxCuts + 1;
        if (need > step) step = (need + step - 1) / step * step;
        var cuts = new List<long>();
        if (from > lo) cuts.Add(from);
        for (long t = (from / step + 1) * step; t < to; t += step) cuts.Add(t);
        if (to < hi && (cuts.Count == 0 || cuts[^1] < to)) cuts.Add(to);
        if (fromVirtual > toVirtual) cuts.Reverse();
        return cuts.ToArray();
    }

    private double ValueAt(long unixSeconds, long virtualSeconds)
    {
        int n = Count;
        int i = LowerBoundAll(unixSeconds);
        if (i < n && At(i).MinuteUnixSeconds == unixSeconds) return At(i).Avg;
        if (i == 0) return At(0).Avg;
        if (i == n) return At(n - 1).Avg;
        var a = At(i - 1);
        var b = At(i);
        long va = Virtual(a.MinuteUnixSeconds);
        long vb = Virtual(b.MinuteUnixSeconds);
        double f = vb > va ? Math.Clamp((double)(virtualSeconds - va) / (vb - va), 0, 1) : 0;
        return a.Avg + (b.Avg - a.Avg) * f;
    }

    private double ComputeMaxAbsShift()
    {
        int lo = int.MaxValue;
        int hi = int.MinValue;
        if (_minuteCount > 0)
        {
            long end = _history.Minutes[_minuteCount - 1].MinuteUnixSeconds;
            foreach (var b in _history.Levels[^1])
            {
                if (b.StartUnixSeconds > end) break;
                if (b.Min < lo) lo = b.Min;
                if (b.Max > hi) hi = b.Max;
            }
        }
        for (int i = _minuteCount; i < Count; i++)
        {
            int v = At(i).Avg;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        return lo > hi ? 0 : Math.Max(Math.Abs(Level - lo), Math.Abs(Level - hi));
    }

    private long Virtual(long unixSeconds) => _map?.ToVirtual(unixSeconds) ?? unixSeconds;

    private Candle At(int index)
    {
        if (index < _minuteCount) return _history.Minutes[index];
        index -= _minuteCount;
        if (index < _liveCount) return _live[_liveFrom + index];
        return _tail[index - _liveCount];
    }

    private int LowerBoundAll(long unixSeconds)
    {
        int lo = 0;
        int hi = Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (At(mid).MinuteUnixSeconds < unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int LowerBound(Candle[] candles, long unixSeconds)
    {
        int lo = 0;
        int hi = candles.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (candles[mid].MinuteUnixSeconds < unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
