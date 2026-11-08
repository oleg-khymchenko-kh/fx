using FXViewer.Storage;

namespace FXViewer.Chart;

public sealed record FlattenBySpec(string Base, double Level);

public static class FlattenBySeries
{
    public static CandleHistory Build(CandleHistory source, CandleHistory baseHistory, double level)
    {
        var line = new BaseLine(baseHistory);
        var history = CandleHistory.Build(Shift(source.Minutes, line, level));
        history.SetHiddenMinutes(Shift(source.HiddenMinutes, line, level));
        ApplyLive(history, source, line, level);
        return history;
    }

    public static void UpdateLive(CandleHistory target, CandleHistory source, CandleHistory baseHistory,
        double level) =>
        ApplyLive(target, source, new BaseLine(baseHistory), level);

    public static double? ValueAt(CandleHistory history, long unixSeconds)
    {
        var line = new BaseLine(history);
        return line.Count == 0 ? null : line.ValueAt(unixSeconds);
    }

    public static Candle[] Shift(Candle[] candles, BaseLine line, double level)
    {
        if (candles.Length == 0 || line.Count == 0) return Array.Empty<Candle>();
        var result = new Candle[candles.Length];
        int j = 0;
        for (int i = 0; i < candles.Length; i++)
        {
            var c = candles[i];
            long t = c.MinuteUnixSeconds;
            while (j < line.Count && line.TimeAt(j) < t) j++;
            int d = Delta(level, line.ValueNear(j, t));
            result[i] = c with { Min = c.Min + d, Max = c.Max + d, Avg = c.Avg + d };
        }
        return result;
    }

    private static void ApplyLive(CandleHistory target, CandleHistory source, BaseLine line, double level)
    {
        var live = source.Live;
        target.SetLive(Shift(live, line, level), Shift(source.LiveHidden, line, level));
        if (!source.HasLastTick || line.Count == 0)
        {
            target.ClearLastTick();
            return;
        }
        long at = live.Length > 0 ? live[^1].MinuteUnixSeconds
            : source.Minutes.Length > 0 ? source.Minutes[^1].MinuteUnixSeconds
            : line.TimeAt(line.Count - 1);
        target.SetLastTick(source.LastTick + Delta(level, line.ValueAt(at)));
    }

    private static int Delta(double level, double baseValue) =>
        (int)Math.Round(level - baseValue, MidpointRounding.AwayFromZero);

    public readonly struct BaseLine
    {
        private readonly Candle[] _minutes;
        private readonly Candle[] _live;
        private readonly int _liveFrom;

        public BaseLine(CandleHistory history)
        {
            _minutes = history.Minutes;
            _live = history.Live;
            long after = _minutes.Length > 0 ? _minutes[^1].MinuteUnixSeconds : long.MinValue;
            int from = 0;
            while (from < _live.Length && _live[from].MinuteUnixSeconds <= after) from++;
            _liveFrom = from;
            Count = _minutes.Length + _live.Length - from;
        }

        public int Count { get; }

        public long TimeAt(int index) => At(index).MinuteUnixSeconds;

        public double ValueAt(long unixSeconds)
        {
            int lo = 0;
            int hi = Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (TimeAt(mid) < unixSeconds) lo = mid + 1;
                else hi = mid;
            }
            return ValueNear(lo, unixSeconds);
        }

        public double ValueNear(int index, long unixSeconds)
        {
            if (index < Count && TimeAt(index) == unixSeconds) return At(index).Avg;
            if (index <= 0) return At(0).Avg;
            if (index >= Count) return At(Count - 1).Avg;
            var a = At(index - 1);
            var b = At(index);
            double f = (double)(unixSeconds - a.MinuteUnixSeconds)
                / (b.MinuteUnixSeconds - a.MinuteUnixSeconds);
            return a.Avg + (b.Avg - a.Avg) * f;
        }

        private Candle At(int index) =>
            index < _minutes.Length ? _minutes[index] : _live[_liveFrom + index - _minutes.Length];
    }
}
