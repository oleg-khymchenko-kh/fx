using FXViewer.Chart;
using FXViewer.Storage;

namespace FXViewer.Compute;

public readonly record struct PivotPoint(long UnixSeconds, double Value, bool Level = false);

public readonly record struct ZigZagLimits(int Limit1Points, int Limit2Points, int Limit2DelayMinutes)
{
    public long Limit2DelaySeconds => (long)Limit2DelayMinutes * 60;
}

public static partial class ZigZagSymbol
{
    private readonly record struct Swing(long Unix, long Virtual, int Value);

    private sealed class ScanState
    {
        public Swing High;
        public Swing Low;
        public Swing DeepAnchor;
        public Swing DeepPull;
        public bool HasDeep;
        public Swing LatePull;
        public bool HasLate;
        public bool TrendMoved;
        public bool Started;
        public int Direction;
    }

    public static List<PivotPoint> BuildPoints(IReadOnlyList<Candle> minutes, ZigZagLimits limits,
        CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var points = new List<PivotPoint>();
        if (minutes.Count == 0) return points;
        var first = minutes[0];
        var last = minutes[^1];
        AddPoint(points, new PivotPoint(first.MinuteUnixSeconds, first.Avg));
        foreach (var s in CollectSwings(minutes, limits, ct, progress))
            AddPoint(points, new PivotPoint(s.Unix, s.Value));
        AddPoint(points, new PivotPoint(last.MinuteUnixSeconds, last.Avg));
        return points;
    }

    private static List<Swing> CollectSwings(IReadOnlyList<Candle> minutes, ZigZagLimits limits,
        CancellationToken ct, IProgress<double>? progress)
    {
        var compressor = WeekendCompressor.Instance;
        var swings = new List<Swing>();
        var st = new ScanState();
        for (int i = 0; i < minutes.Count; i++)
        {
            if ((i & 0xFFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((double)i / minutes.Count);
            }
            var c = minutes[i];
            long unix = c.MinuteUnixSeconds;
            long virt = compressor.ToVirtual(unix);
            bool maxFirst = MaxFirst(minutes, i);
            int firstTick = maxFirst ? c.Max : c.Min;
            int secondTick = maxFirst ? c.Min : c.Max;
            Step(swings, st, unix, virt, firstTick, limits);
            if (secondTick != firstTick)
                Step(swings, st, unix, virt, secondTick, limits);
        }
        if (st.Direction > 0) swings.Add(st.High);
        else if (st.Direction < 0) swings.Add(st.Low);
        return swings;
    }

    private static void Step(List<Swing> swings, ScanState st, long unix, long virt, int value,
        ZigZagLimits limits)
    {
        if (!st.Started)
        {
            st.High = new Swing(unix, virt, value);
            st.Low = st.High;
            st.Started = true;
            return;
        }
        if (st.Direction == 0)
        {
            if (value > st.High.Value)
            {
                st.High = new Swing(unix, virt, value);
                if (st.High.Value - st.Low.Value >= limits.Limit1Points)
                {
                    swings.Add(st.Low);
                    st.Direction = 1;
                }
            }
            else if (value < st.Low.Value)
            {
                st.Low = new Swing(unix, virt, value);
                if (st.High.Value - st.Low.Value >= limits.Limit1Points)
                {
                    swings.Add(st.High);
                    st.Direction = -1;
                }
            }
            return;
        }
        if (st.Direction > 0)
        {
            while (st.HasDeep && value - st.DeepPull.Value >= limits.Limit1Points)
            {
                bool ok = PullbackQualifies(st.DeepAnchor, st.DeepPull, limits);
                if (ok)
                {
                    swings.Add(st.DeepAnchor);
                    swings.Add(st.DeepPull);
                }
                st.HasDeep = false;
                if (!ok && st.HasLate)
                {
                    st.DeepAnchor = st.High;
                    st.DeepPull = st.LatePull;
                    st.HasDeep = true;
                    st.TrendMoved = false;
                    st.HasLate = false;
                    continue;
                }
                st.HasLate = false;
                break;
            }
            if (value > st.High.Value)
            {
                st.High = new Swing(unix, virt, value);
                st.HasLate = false;
                st.TrendMoved = st.HasDeep;
                return;
            }
            if (st.High.Value - value >= limits.Limit1Points)
            {
                swings.Add(st.High);
                st.Low = new Swing(unix, virt, value);
                st.Direction = -1;
                st.HasDeep = false;
                st.HasLate = false;
                return;
            }
            if (st.HasDeep
                ? value < st.DeepPull.Value || BiggerPullback(value, st.High, st.DeepAnchor, st.DeepPull)
                : value < st.High.Value)
            {
                st.DeepPull = new Swing(unix, virt, value);
                st.DeepAnchor = st.High;
                st.HasDeep = true;
                st.TrendMoved = false;
                st.HasLate = false;
                return;
            }
            if (st.TrendMoved && value < st.High.Value
                && (!st.HasLate || value < st.LatePull.Value))
            {
                st.LatePull = new Swing(unix, virt, value);
                st.HasLate = true;
            }
            return;
        }
        while (st.HasDeep && st.DeepPull.Value - value >= limits.Limit1Points)
        {
            bool ok = PullbackQualifies(st.DeepAnchor, st.DeepPull, limits);
            if (ok)
            {
                swings.Add(st.DeepAnchor);
                swings.Add(st.DeepPull);
            }
            st.HasDeep = false;
            if (!ok && st.HasLate)
            {
                st.DeepAnchor = st.Low;
                st.DeepPull = st.LatePull;
                st.HasDeep = true;
                st.TrendMoved = false;
                st.HasLate = false;
                continue;
            }
            st.HasLate = false;
            break;
        }
        if (value < st.Low.Value)
        {
            st.Low = new Swing(unix, virt, value);
            st.HasLate = false;
            st.TrendMoved = st.HasDeep;
            return;
        }
        if (value - st.Low.Value >= limits.Limit1Points)
        {
            swings.Add(st.Low);
            st.High = new Swing(unix, virt, value);
            st.Direction = 1;
            st.HasDeep = false;
            st.HasLate = false;
            return;
        }
        if (st.HasDeep
            ? value > st.DeepPull.Value || BiggerPullback(value, st.Low, st.DeepAnchor, st.DeepPull)
            : value > st.Low.Value)
        {
            st.DeepPull = new Swing(unix, virt, value);
            st.DeepAnchor = st.Low;
            st.HasDeep = true;
            st.TrendMoved = false;
            st.HasLate = false;
            return;
        }
        if (st.TrendMoved && value > st.Low.Value
            && (!st.HasLate || value > st.LatePull.Value))
        {
            st.LatePull = new Swing(unix, virt, value);
            st.HasLate = true;
        }
    }

    private static bool BiggerPullback(int value, Swing edge, Swing anchor, Swing pull) =>
        Math.Abs(value - edge.Value) > Math.Abs(pull.Value - anchor.Value);

    private static bool PullbackQualifies(Swing from, Swing pull, ZigZagLimits limits) =>
        Math.Abs(pull.Value - from.Value) >= limits.Limit2Points
        && pull.Virtual - from.Virtual >= limits.Limit2DelaySeconds;

    private static bool MaxFirst(IReadOnlyList<Candle> minutes, int index)
    {
        var c = minutes[index];
        if (index > 0)
        {
            long d = 2L * minutes[index - 1].Avg - c.Min - c.Max;
            if (d != 0) return d > 0;
        }
        return 2L * c.Avg < (long)c.Min + c.Max;
    }

    private static void AddPoint(List<PivotPoint> points, PivotPoint p)
    {
        if (points.Count > 0 && points[^1] == p) return;
        points.Add(p);
    }

    public static int FirstAtOrAfter(IReadOnlyList<PivotPoint> points, long unixSeconds)
    {
        int lo = 0;
        int hi = points.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (points[mid].UnixSeconds < unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public static List<PivotPoint> NormalizeEditedPoints(IReadOnlyList<PivotPoint> points)
    {
        var pts = new List<PivotPoint>(points.Count);
        foreach (var p in points)
            pts.Add(new PivotPoint(p.UnixSeconds - p.UnixSeconds % 60, p.Value));
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = pts.Count - 1; i >= 1; i--)
            {
                if (pts[i] == pts[i - 1])
                {
                    pts.RemoveAt(i);
                    changed = true;
                    continue;
                }
                if (i >= pts.Count - 1) continue;
                bool sharesMinute = pts[i].UnixSeconds == pts[i - 1].UnixSeconds
                    || pts[i].UnixSeconds == pts[i + 1].UnixSeconds;
                if (!sharesMinute) continue;
                double before = pts[i].Value - pts[i - 1].Value;
                double after = pts[i + 1].Value - pts[i].Value;
                if (before == 0 || after == 0 || Math.Sign(before) == Math.Sign(after))
                {
                    pts.RemoveAt(i);
                    changed = true;
                }
            }
        }
        return pts;
    }
}
