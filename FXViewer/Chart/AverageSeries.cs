using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly record struct AverageDiff(int NewFrom, int NewToExcl, int OldFrom, int OldToExcl);

public enum BandPick
{
    Max,
    Min,
    Avg,
}

public readonly record struct AverageSpec(
    int WindowBars, bool FromFuture = false, bool VolumeWeighted = false, int BandCount = 1,
    BandPick Pick = BandPick.Max, bool TimeWindow = false)
{
    public int StepBars => Math.Max(1, WindowBars);

    public int Steps => Math.Max(1, BandCount);

    public bool Band => Steps > 1;

    public int BarsAt(int step) => (int)Math.Clamp((long)StepBars * step, 1, int.MaxValue);

    public int LongestBars => BarsAt(Steps);
}

public sealed record AverageJob(
    string Symbol, AverageSpec Spec, CandleHistory History, Candle[]? Parent);

public sealed record AverageResult(AverageJob Job, CandleHistory? History);

public static class AverageSeries
{
    private const int BlockBars = 16384;

    private static readonly Candle[] NoCandles = Array.Empty<Candle>();

    public static Candle[] Compute(Candle[] parent, in AverageSpec spec) =>
        ComputeRange(parent, spec, 0, parent.Length);

    public static Candle[] ComputeRange(Candle[] parent, in AverageSpec spec, int from, int toExcl)
    {
        int n = parent.Length;
        from = Math.Clamp(from, 0, n);
        toExcl = Math.Clamp(toExcl, from, n);
        if (toExcl == from) return Array.Empty<Candle>();
        var values = Run(new Bars(parent, NoCandles), spec, from, toExcl);
        var result = new Candle[values.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = Flat(parent[from + i].MinuteUnixSeconds, Finish(values[i], spec));
        return result;
    }

    public static AverageDiff? Diff(Candle[] oldParent, Candle[] newParent, in AverageSpec spec)
    {
        int n0 = oldParent.Length;
        int n1 = newParent.Length;
        int limit = Math.Min(n0, n1);
        int prefix = 0;
        while (prefix < limit && Same(oldParent[prefix], newParent[prefix], spec.VolumeWeighted)) prefix++;
        int suffix = 0;
        while (suffix < limit - prefix
               && Same(oldParent[n0 - 1 - suffix], newParent[n1 - 1 - suffix], spec.VolumeWeighted))
            suffix++;
        if (n0 == n1 && prefix == n1) return null;
        var bars = new Bars(newParent, NoCandles);
        int w = spec.LongestBars;
        int changedEnd = n1 - suffix;
        int newFrom = spec.FromFuture && prefix < n1 ? WindowStart(bars, spec, w, prefix) : prefix;
        int newToExcl = spec.FromFuture || changedEnd <= 0
            ? changedEnd
            : WindowEnd(bars, spec, w, changedEnd);
        return new AverageDiff(newFrom, newToExcl, newFrom, newToExcl - (n1 - n0));
    }

    public static AverageResult[] Rebuild(IReadOnlyList<AverageJob> jobs, Candle[] parentMinutes)
    {
        if (jobs.Count == 0) return Array.Empty<AverageResult>();
        var results = new AverageResult[jobs.Count];
        for (int i = 0; i < jobs.Count; i++) results[i] = Rebuild(jobs[i], parentMinutes);
        return results;
    }

    public static AverageResult Rebuild(AverageJob job, Candle[] parentMinutes) =>
        new(job, Recompute(job.History, job.Parent, parentMinutes, job.Spec));

    public static CandleHistory? Recompute(
        CandleHistory current, Candle[]? parent, Candle[] parentMinutes, in AverageSpec spec)
    {
        if (parent is { Length: > 0 } && current.Minutes.Length == parent.Length)
        {
            var diff = Diff(parent, parentMinutes, spec);
            if (diff == null) return null;
            var replacement = ComputeRange(parentMinutes, spec, diff.Value.NewFrom, diff.Value.NewToExcl);
            return current.WithReplacedRange(
                diff.Value.OldFrom, diff.Value.OldToExcl - diff.Value.OldFrom, replacement);
        }
        return CandleHistory.Build(Compute(parentMinutes, spec));
    }

    public static Candle[] LiveTail(Candle[] baseMinutes, Candle[] live, in AverageSpec spec)
    {
        int tailLen = live.Length;
        if (tailLen == 0) return Array.Empty<Candle>();
        int from = baseMinutes.Length;
        var values = Run(new Bars(baseMinutes, live), spec, from, from + tailLen);
        var result = new Candle[tailLen];
        for (int i = 0; i < tailLen; i++)
            result[i] = Flat(live[i].MinuteUnixSeconds, Finish(values[i], spec));
        return result;
    }

    private static long[] Run(in Bars bars, in AverageSpec spec, int from, int toExcl)
    {
        var window = new Window(bars, spec, from, toExcl);
        var values = new long[toExcl - from];
        int steps = spec.Steps;
        var cursors = new int[steps];
        int start = spec.FromFuture ? window.EndExcl - window.Origin : 0;
        for (int step = 0; step < steps; step++) cursors[step] = start;
        if (spec.FromFuture)
        {
            for (int hi = toExcl; hi > from; hi -= BlockBars)
            {
                int lo = Math.Max(from, hi - BlockBars);
                for (int step = 0; step < steps; step++)
                    Accumulate(window, spec, spec.BarsAt(step + 1), from, lo, hi, values,
                        step == 0, ref cursors[step]);
            }
            return values;
        }
        for (int lo = from; lo < toExcl; lo += BlockBars)
        {
            int hi = Math.Min(toExcl, lo + BlockBars);
            for (int step = 0; step < steps; step++)
                Accumulate(window, spec, spec.BarsAt(step + 1), from, lo, hi, values,
                    step == 0, ref cursors[step]);
        }
        return values;
    }

    private static void Accumulate(Window win, in AverageSpec spec, int w, int first0,
        int blockFrom, int blockToExcl, long[] values, bool first, ref int cursor)
    {
        bool timed = spec.TimeWindow;
        var pick = spec.Pick;
        int span = w - 1;
        int origin = win.Origin;
        var count = win.CountPrefix;
        var virt = win.VirtualMinutes;
        if (spec.FromFuture)
        {
            int right = cursor;
            for (int i = blockToExcl - 1; i >= blockFrom; i--)
            {
                int at = i - origin;
                int have = count[at];
                if (timed)
                {
                    int edge = virt[at] + span;
                    while (right - 1 > at && count[right] - have > 1 && virt[right - 1] > edge) right--;
                }
                else
                {
                    while (right - 1 > at && count[right] - have > w) right--;
                }
                Combine(values, i - first0, win.Value(at, right, at), first, pick);
            }
            cursor = right;
            return;
        }
        int left = cursor;
        for (int i = blockFrom; i < blockToExcl; i++)
        {
            int at = i - origin;
            int end = at + 1;
            int have = count[end];
            if (timed)
            {
                int edge = virt[at] - span;
                while (left < at && have - count[left] > 1 && virt[left] < edge) left++;
            }
            else
            {
                while (left < at && have - count[left] > w) left++;
            }
            Combine(values, i - first0, win.Value(left, end, at), first, pick);
        }
        cursor = left;
    }

    private sealed class Window
    {
        private readonly Bars _bars;
        private readonly long[] _sum;
        private readonly long[] _volume;
        private readonly long[] _priceVolume;
        private readonly bool _weighted;

        public readonly int Origin;
        public readonly int EndExcl;
        public readonly int[] CountPrefix;
        public readonly int[] VirtualMinutes;

        public Window(in Bars bars, in AverageSpec spec, int from, int toExcl)
        {
            _bars = bars;
            int w = spec.LongestBars;
            if (spec.FromFuture)
            {
                Origin = from;
                EndExcl = WindowEnd(bars, spec, w, toExcl);
            }
            else
            {
                Origin = WindowStart(bars, spec, w, from);
                EndExcl = toExcl;
            }
            int len = EndExcl - Origin;
            _weighted = spec.VolumeWeighted;
            _sum = new long[len + 1];
            CountPrefix = new int[len + 1];
            _volume = _weighted ? new long[len + 1] : Array.Empty<long>();
            _priceVolume = _weighted ? new long[len + 1] : Array.Empty<long>();
            VirtualMinutes = spec.TimeWindow ? new int[len] : Array.Empty<int>();
            long sum = 0;
            int count = 0;
            long volume = 0;
            long priceVolume = 0;
            int cursor = 0;
            long firstMinute = 0;
            for (int k = 0; k < len; k++)
            {
                var c = bars[Origin + k];
                if (spec.TimeWindow)
                {
                    long minute =
                        WeekendCompressor.Instance.ToVirtual(c.MinuteUnixSeconds, ref cursor) / 60;
                    if (k == 0) firstMinute = minute;
                    VirtualMinutes[k] = (int)(minute - firstMinute);
                }
                if (!c.WideSpread)
                {
                    sum += c.Avg;
                    count++;
                    if (_weighted && c.HasVolume)
                    {
                        volume += c.Volume;
                        priceVolume += (long)c.Avg * c.Volume;
                    }
                }
                _sum[k + 1] = sum;
                CountPrefix[k + 1] = count;
                if (!_weighted) continue;
                _volume[k + 1] = volume;
                _priceVolume[k + 1] = priceVolume;
            }
        }

        public int Value(int from, int toExcl, int anchor)
        {
            int count = CountPrefix[toExcl] - CountPrefix[from];
            if (count == 0) return _bars[Origin + anchor].Avg;
            if (_weighted)
            {
                long volume = _volume[toExcl] - _volume[from];
                if (volume > 0)
                    return Rounded(_priceVolume[toExcl] - _priceVolume[from], volume);
            }
            return Rounded(_sum[toExcl] - _sum[from], count);
        }

        private static int Rounded(long sum, long count) =>
            (int)(sum >= 0
                ? (2 * sum + count) / (2 * count)
                : -((-2 * sum + count) / (2 * count)));
    }

    private static int WindowStart(in Bars bars, in AverageSpec spec, int w, int anchor)
    {
        int left = anchor;
        bool usable = !bars[anchor].WideSpread;
        if (spec.TimeWindow)
        {
            int cursor = 0;
            long limit = VirtualMinute(bars, anchor, ref cursor) - (w - 1);
            while (left > 0 && VirtualMinute(bars, left - 1, ref cursor) >= limit)
            {
                left--;
                usable |= !bars[left].WideSpread;
            }
        }
        else
        {
            int need = usable ? w - 1 : w;
            while (left > 0 && need > 0)
            {
                left--;
                if (bars[left].WideSpread) continue;
                usable = true;
                need--;
            }
        }
        while (!usable && left > 0)
        {
            left--;
            usable = !bars[left].WideSpread;
        }
        return left;
    }

    private static int WindowEnd(in Bars bars, in AverageSpec spec, int w, int anchorExcl)
    {
        int n = bars.Length;
        int right = anchorExcl;
        bool usable = !bars[anchorExcl - 1].WideSpread;
        if (spec.TimeWindow)
        {
            int cursor = 0;
            long limit = VirtualMinute(bars, anchorExcl - 1, ref cursor) + (w - 1);
            while (right < n && VirtualMinute(bars, right, ref cursor) <= limit)
            {
                usable |= !bars[right].WideSpread;
                right++;
            }
        }
        else
        {
            int need = usable ? w - 1 : w;
            while (right < n && need > 0)
            {
                if (!bars[right].WideSpread) { usable = true; need--; }
                right++;
            }
        }
        while (!usable && right < n)
        {
            usable = !bars[right].WideSpread;
            right++;
        }
        return right;
    }

    private static long VirtualMinute(in Bars bars, int index, ref int cursor) =>
        WeekendCompressor.Instance.ToVirtual(bars[index].MinuteUnixSeconds, ref cursor) / 60;

    private static void Combine(long[] values, int index, int value, bool first, BandPick pick)
    {
        if (pick == BandPick.Avg)
        {
            values[index] = first ? value : values[index] + value;
            return;
        }
        if (first || (pick == BandPick.Min ? value < values[index] : value > values[index]))
            values[index] = value;
    }

    private static int Finish(long accumulated, in AverageSpec spec) =>
        spec.Pick == BandPick.Avg
            ? (int)Math.Round(accumulated / (double)spec.Steps, MidpointRounding.AwayFromZero)
            : (int)accumulated;

    private readonly struct Bars
    {
        private readonly Candle[] _head;
        private readonly Candle[] _tail;

        public Bars(Candle[] head, Candle[] tail)
        {
            _head = head;
            _tail = tail;
        }

        public int Length => _head.Length + _tail.Length;

        public Candle this[int index] =>
            index < _head.Length ? _head[index] : _tail[index - _head.Length];
    }

    private static bool Same(Candle a, Candle b, bool volumeWeighted) =>
        a.MinuteUnixSeconds == b.MinuteUnixSeconds && a.Avg == b.Avg
        && a.WideSpread == b.WideSpread
        && (!volumeWeighted || (a.HasVolume == b.HasVolume && a.Volume == b.Volume));

    private static Candle Flat(long minuteUnix, int value) =>
        new(minuteUnix, value, value, value, true);
}
