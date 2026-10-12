namespace FXViewer.Chart;

public readonly record struct Level2(int Index, int Value, bool Top, int ApproachPoints, int ReversalPoints);

public sealed class LevelSizeTable
{
    private readonly double[] _points;
    private readonly double[] _pixels;

    public LevelSizeTable(IEnumerable<LevelStep> steps)
    {
        var sorted = steps
            .Where(x => x.Pips > 0 && x.Pixels >= 0)
            .GroupBy(x => x.Pips)
            .Select(g => g.Last())
            .OrderBy(x => x.Pips)
            .ToArray();
        _points = sorted.Select(x => x.Pips * DensityProfile.PipPoints).ToArray();
        _pixels = sorted.Select(x => x.Pixels).ToArray();
        MaxPixels = _pixels.Length == 0 ? 0 : _pixels.Max();
    }

    public bool IsEmpty => _points.Length == 0;

    public double MinPoints => _points.Length == 0 ? double.PositiveInfinity : _points[0];

    public double MaxPoints => _points.Length == 0 ? double.PositiveInfinity : _points[^1];

    public double MaxPixels { get; }

    public double PixelsAt(double points)
    {
        if (_points.Length == 0 || points < _points[0]) return 0;
        if (points >= _points[^1]) return _pixels[^1];
        int hi = 1;
        while (_points[hi] <= points) hi++;
        double t = (points - _points[hi - 1]) / (_points[hi] - _points[hi - 1]);
        return _pixels[hi - 1] + t * (_pixels[hi] - _pixels[hi - 1]);
    }
}

public sealed class Levels2Finder
{
    public const int ApproachLookbackBars = 50_000;

    private int[] _high = Array.Empty<int>();
    private int[] _low = Array.Empty<int>();
    private int[] _stackIndex = Array.Empty<int>();
    private int[] _stackGap = Array.Empty<int>();
    private int[] _openIndex = Array.Empty<int>();
    private int[] _openLowest = Array.Empty<int>();
    private int[] _approach = Array.Empty<int>();
    private int[] _reversal = Array.Empty<int>();

    public static int IndexAfter(MinuteSequence minutes, long unixSeconds)
    {
        int lo = 0;
        int hi = minutes.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (minutes[mid].MinuteUnixSeconds <= unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public void Find(MinuteSequence minutes, long fromUnix, long anchorUnix,
        double minApproachPoints, double minReversalPoints, double approachSaturationPoints,
        List<Level2> output)
    {
        output.Clear();
        int last = IndexAfter(minutes, anchorUnix) - 1;
        int first = IndexAfter(minutes, fromUnix - 1);
        if (last < 0 || first > last) return;
        Find(minutes, first, last, Math.Max(0, first - ApproachLookbackBars),
            minApproachPoints, minReversalPoints, approachSaturationPoints, output);
    }

    public void Find(MinuteSequence minutes, int first, int last, int lookbackFrom,
        double minApproachPoints, double minReversalPoints, double approachSaturationPoints,
        List<Level2> output)
    {
        output.Clear();
        if (last < first) return;
        int count = last - first + 1;
        if (_high.Length < count)
        {
            _high = new int[count];
            _low = new int[count];
            _stackIndex = new int[count];
            _stackGap = new int[count];
            _openIndex = new int[count];
            _openLowest = new int[count];
            _approach = new int[count * 2];
            _reversal = new int[count * 2];
        }
        for (int i = 0; i < count; i++)
        {
            var candle = minutes[first + i];
            _high[i] = candle.Max;
            _low[i] = candle.Min;
        }
        int back = Math.Clamp(lookbackFrom, 0, first);
        ApproachPass(minutes, first, back, count, true, 0, approachSaturationPoints);
        ApproachPass(minutes, first, back, count, false, count, approachSaturationPoints);
        ReversalPass(count, true, 0);
        ReversalPass(count, false, count);
        for (int i = 0; i < count; i++)
        {
            if (_approach[i] >= minApproachPoints && _reversal[i] >= minReversalPoints)
                output.Add(new Level2(first + i, _high[i], true, _approach[i], _reversal[i]));
            int j = count + i;
            if (_approach[j] >= minApproachPoints && _reversal[j] >= minReversalPoints)
                output.Add(new Level2(first + i, _low[i], false, _approach[j], _reversal[j]));
        }
    }

    private int Peak(int i, bool top) => top ? _high[i] : -_low[i];

    private int Trough(int i, bool top) => top ? _low[i] : -_high[i];

    private void ApproachPass(MinuteSequence minutes, int first, int back, int count, bool top,
        int slot, double saturation)
    {
        int sp = 0;
        int baseMin = int.MaxValue;
        int open = 0;
        for (int i = 0; i < count; i++)
        {
            int peak = Peak(i, top);
            int acc = int.MaxValue;
            while (sp > 0 && Peak(_stackIndex[sp - 1], top) < peak)
            {
                sp--;
                acc = Math.Min(acc, Math.Min(Trough(_stackIndex[sp], top), _stackGap[sp]));
            }
            if (sp > 0)
            {
                int gap = Math.Min(_stackGap[sp - 1], acc);
                _stackGap[sp - 1] = gap;
                _approach[slot + i] = gap == int.MaxValue ? 0 : peak - gap;
            }
            else
            {
                baseMin = Math.Min(baseMin, acc);
                _openIndex[open] = i;
                _openLowest[open] = baseMin;
                open++;
            }
            _stackIndex[sp] = i;
            _stackGap[sp] = int.MaxValue;
            sp++;
        }
        ResolveOpen(minutes, first, back, top, slot, open, saturation);
    }

    private void ResolveOpen(MinuteSequence minutes, int first, int back, bool top, int slot,
        int open, double saturation)
    {
        int next = 0;
        int lowest = int.MaxValue;
        for (int k = first - 1; k >= back && next < open; k--)
        {
            var candle = minutes[k];
            int peak = top ? candle.Max : -candle.Min;
            while (next < open && Peak(_openIndex[next], top) <= peak)
            {
                SetOpenApproach(next, top, slot, lowest);
                next++;
            }
            lowest = Math.Min(lowest, top ? candle.Min : -candle.Max);
            if (next < open
                && Peak(_openIndex[next], top) - Math.Min(_openLowest[next], lowest) >= saturation)
                break;
        }
        for (; next < open; next++) SetOpenApproach(next, top, slot, lowest);
    }

    private void SetOpenApproach(int open, bool top, int slot, int lowestBefore)
    {
        int lowest = Math.Min(_openLowest[open], lowestBefore);
        int i = _openIndex[open];
        _approach[slot + i] = lowest == int.MaxValue ? 0 : Peak(i, top) - lowest;
    }

    private void ReversalPass(int count, bool top, int slot)
    {
        int sp = 0;
        int baseMin = int.MaxValue;
        for (int i = count - 1; i >= 0; i--)
        {
            int peak = Peak(i, top);
            int acc = int.MaxValue;
            while (sp > 0 && Peak(_stackIndex[sp - 1], top) <= peak)
            {
                sp--;
                acc = Math.Min(acc, Math.Min(Trough(_stackIndex[sp], top), _stackGap[sp]));
            }
            int lowest;
            if (sp > 0)
            {
                _stackGap[sp - 1] = Math.Min(_stackGap[sp - 1], acc);
                lowest = _stackGap[sp - 1];
            }
            else
            {
                baseMin = Math.Min(baseMin, acc);
                lowest = baseMin;
            }
            _reversal[slot + i] = lowest == int.MaxValue ? 0 : peak - lowest;
            _stackIndex[sp] = i;
            _stackGap[sp] = int.MaxValue;
            sp++;
        }
    }
}
