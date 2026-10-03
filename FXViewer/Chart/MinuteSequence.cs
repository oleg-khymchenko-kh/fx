using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly struct MinuteSequence
{
    private readonly Candle[] _loaded;
    private readonly Candle[] _live;
    private readonly int _liveFrom;

    private MinuteSequence(Candle[] loaded, Candle[] live, int liveFrom)
    {
        _loaded = loaded;
        _live = live;
        _liveFrom = liveFrom;
    }

    public static MinuteSequence Of(Candle[] loaded) => new(loaded, Array.Empty<Candle>(), 0);

    public static MinuteSequence Of(CandleHistory history)
    {
        var loaded = history.Minutes;
        var live = history.Live;
        int liveFrom = loaded.Length == 0 ? 0 : UpperBound(live, loaded[^1].MinuteUnixSeconds);
        return new MinuteSequence(loaded, live, liveFrom);
    }

    public int Length => _loaded == null ? 0 : _loaded.Length + _live.Length - _liveFrom;

    public Candle this[int index] => index < _loaded.Length
        ? _loaded[index]
        : _live[index - _loaded.Length + _liveFrom];

    private static int UpperBound(Candle[] candles, long unixSeconds)
    {
        int lo = 0;
        int hi = candles.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (candles[mid].MinuteUnixSeconds <= unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
