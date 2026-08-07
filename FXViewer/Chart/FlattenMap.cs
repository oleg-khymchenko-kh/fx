using FXViewer.Compute;

namespace FXViewer.Chart;

public sealed class FlattenMap
{
    private readonly long[] _times;
    private readonly double[] _shifts;

    private FlattenMap(long[] times, double[] shifts)
    {
        _times = times;
        _shifts = shifts;
        double max = 0;
        foreach (var s in shifts) max = Math.Max(max, Math.Abs(s));
        MaxAbsShift = max;
    }

    public double MaxAbsShift { get; }

    public static FlattenMap? Build(PivotPoint[] points, SeriesTransform transform,
        WeekendCompressor? map)
    {
        if (points.Length < 2) return null;
        var ordered = new (long Time, double Display)[points.Length];
        for (int i = 0; i < points.Length; i++)
            ordered[i] = (map?.ToVirtual(points[i].UnixSeconds) ?? points[i].UnixSeconds,
                transform.ToDisplay(points[i].Value));
        Array.Sort(ordered, (a, b) => a.Time.CompareTo(b.Time));
        var times = new List<long>(ordered.Length);
        var shifts = new List<double>(ordered.Length);
        foreach (var (time, display) in ordered)
        {
            if (times.Count > 0 && times[^1] == time) continue;
            times.Add(time);
            shifts.Add(ordered[0].Display - display);
        }
        if (times.Count < 2) return null;
        times.Add(times[^1] + 1);
        shifts.Add(0);
        return new FlattenMap(times.ToArray(), shifts.ToArray());
    }

    public double ShiftAt(long virtualSeconds)
    {
        if (virtualSeconds < _times[0] || virtualSeconds > _times[^1]) return 0;
        int i = FirstAtOrAfter(virtualSeconds);
        if (_times[i] == virtualSeconds) return _shifts[i];
        double f = (double)(virtualSeconds - _times[i - 1]) / (_times[i] - _times[i - 1]);
        return _shifts[i - 1] + (_shifts[i] - _shifts[i - 1]) * f;
    }

    public double[] ColumnShifts(long firstBucket, int count, long bucketSeconds)
    {
        var shifts = new double[count];
        for (int i = 0; i < count; i++) shifts[i] = ShiftAt((firstBucket + i) * bucketSeconds);
        return shifts;
    }

    public long[] CutsBetween(long fromVirtual, long toVirtual)
    {
        long lo = Math.Min(fromVirtual, toVirtual);
        long hi = Math.Max(fromVirtual, toVirtual);
        int from = FirstAtOrAfter(lo);
        while (from < _times.Length && _times[from] <= lo) from++;
        int to = FirstAtOrAfter(hi);
        if (to <= from) return Array.Empty<long>();
        var cuts = new long[to - from];
        Array.Copy(_times, from, cuts, 0, cuts.Length);
        if (fromVirtual > toVirtual) Array.Reverse(cuts);
        return cuts;
    }

    private int FirstAtOrAfter(long virtualSeconds)
    {
        int lo = 0;
        int hi = _times.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_times[mid] < virtualSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
