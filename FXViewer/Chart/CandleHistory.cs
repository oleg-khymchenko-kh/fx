using FXViewer.Storage;

namespace FXViewer.Chart;

public readonly record struct AggBlock(long StartUnixSeconds, int Min, int Max, long AvgSum, int Count,
    int SpreadMaxTenths = -1, long VolumeSum = -1, long VolumeMax = -1);

public sealed class CandleHistory
{
    public static readonly int[] LevelMinutes = { 15, 60, 240, 1440 };

    public Candle[] Minutes { get; }
    public AggBlock[][] Levels { get; }

    private volatile Candle[] _live = Array.Empty<Candle>();

    public Candle[] Live => _live;

    public void SetLive(Candle[] live) => _live = live ?? Array.Empty<Candle>();

    private volatile int _lastTickValue;
    private volatile bool _hasLastTick;

    public bool HasLastTick => _hasLastTick;
    public int LastTick => _lastTickValue;

    public void SetLastTick(int displayValue)
    {
        _lastTickValue = displayValue;
        _hasLastTick = true;
    }

    public void ClearLastTick() => _hasLastTick = false;

    public Candle LastCandle
    {
        get
        {
            var live = _live;
            return live.Length > 0 ? live[^1] : Minutes[^1];
        }
    }

    private CandleHistory(Candle[] minutes, AggBlock[][] levels)
    {
        Minutes = minutes;
        Levels = levels;
    }

    public static CandleHistory Build(Candle[] minutes)
    {
        var levels = new AggBlock[LevelMinutes.Length][];
        levels[0] = FromMinutes(minutes, LevelMinutes[0] * 60L);
        for (int i = 1; i < LevelMinutes.Length; i++)
            levels[i] = FromBlocks(levels[i - 1], LevelMinutes[i] * 60L);
        return new CandleHistory(minutes, levels);
    }

    public CandleHistory WithReplacedRange(int index, int removeCount, IReadOnlyList<Candle> replacement)
    {
        var minutes = new Candle[Minutes.Length - removeCount + replacement.Count];
        Array.Copy(Minutes, 0, minutes, 0, index);
        for (int i = 0; i < replacement.Count; i++) minutes[index + i] = replacement[i];
        Array.Copy(Minutes, index + removeCount, minutes, index + replacement.Count,
            Minutes.Length - index - removeCount);
        long lo = long.MaxValue;
        long hi = long.MinValue;
        if (removeCount > 0)
        {
            lo = Minutes[index].MinuteUnixSeconds;
            hi = Minutes[index + removeCount - 1].MinuteUnixSeconds;
        }
        if (replacement.Count > 0)
        {
            lo = Math.Min(lo, replacement[0].MinuteUnixSeconds);
            hi = Math.Max(hi, replacement[^1].MinuteUnixSeconds);
        }
        if (lo > hi) return new CandleHistory(minutes, Levels);
        var levels = new AggBlock[LevelMinutes.Length][];
        levels[0] = PatchLevel(Levels[0], minutes, null, LevelMinutes[0] * 60L, lo, hi);
        for (int i = 1; i < LevelMinutes.Length; i++)
            levels[i] = PatchLevel(Levels[i], null, levels[i - 1], LevelMinutes[i] * 60L, lo, hi);
        return new CandleHistory(minutes, levels);
    }

    private static AggBlock[] PatchLevel(AggBlock[] old, Candle[]? minutes, AggBlock[]? lower,
        long blockSec, long lo, long hi)
    {
        long windowLo = lo - lo % blockSec;
        long windowHiExcl = hi - hi % blockSec + blockSec;
        int bi = LowerBound(old, windowLo);
        int bj = LowerBound(old, windowHiExcl);
        AggBlock[] rebuilt;
        if (minutes != null)
        {
            int mi = LowerBound(minutes, windowLo);
            int mj = LowerBound(minutes, windowHiExcl);
            rebuilt = FromMinutes(minutes[mi..mj], blockSec);
        }
        else
        {
            int li = LowerBound(lower!, windowLo);
            int lj = LowerBound(lower!, windowHiExcl);
            rebuilt = FromBlocks(lower![li..lj], blockSec);
        }
        var result = new AggBlock[old.Length - (bj - bi) + rebuilt.Length];
        Array.Copy(old, 0, result, 0, bi);
        Array.Copy(rebuilt, 0, result, bi, rebuilt.Length);
        Array.Copy(old, bj, result, bi + rebuilt.Length, old.Length - bj);
        return result;
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

    private static AggBlock[] FromMinutes(Candle[] minutes, long blockSec)
    {
        var result = new List<AggBlock>(minutes.Length / (int)(blockSec / 60) + 1);
        long start = long.MinValue;
        int mn = 0;
        int mx = 0;
        int n = 0;
        long sum = 0;
        int sp = -1;
        long vol = -1;
        long volMax = -1;
        foreach (var c in minutes)
        {
            long s = c.MinuteUnixSeconds - c.MinuteUnixSeconds % blockSec;
            if (s != start)
            {
                if (n > 0) result.Add(new AggBlock(start, mn, mx, sum, n, sp, vol, volMax));
                start = s;
                mn = c.Min;
                mx = c.Max;
                sum = 0;
                n = 0;
                sp = c.HasSpread ? c.SpreadTenths : -1;
                vol = c.HasVolume ? c.Volume : -1;
                volMax = vol;
            }
            else
            {
                if (c.Min < mn) mn = c.Min;
                if (c.Max > mx) mx = c.Max;
                if (c.HasSpread && c.SpreadTenths > sp) sp = c.SpreadTenths;
                if (c.HasVolume)
                {
                    vol = Math.Max(vol, 0) + c.Volume;
                    if (c.Volume > volMax) volMax = c.Volume;
                }
            }
            sum += c.Avg;
            n++;
        }
        if (n > 0) result.Add(new AggBlock(start, mn, mx, sum, n, sp, vol, volMax));
        return result.ToArray();
    }

    private static AggBlock[] FromBlocks(AggBlock[] blocks, long blockSec)
    {
        var result = new List<AggBlock>(blocks.Length / 2 + 1);
        long start = long.MinValue;
        int mn = 0;
        int mx = 0;
        int n = 0;
        long sum = 0;
        int sp = -1;
        long vol = -1;
        long volMax = -1;
        foreach (var b in blocks)
        {
            long s = b.StartUnixSeconds - b.StartUnixSeconds % blockSec;
            if (s != start)
            {
                if (n > 0) result.Add(new AggBlock(start, mn, mx, sum, n, sp, vol, volMax));
                start = s;
                mn = b.Min;
                mx = b.Max;
                sum = 0;
                n = 0;
                sp = b.SpreadMaxTenths;
                vol = b.VolumeSum;
                volMax = b.VolumeMax;
            }
            else
            {
                if (b.Min < mn) mn = b.Min;
                if (b.Max > mx) mx = b.Max;
                if (b.SpreadMaxTenths > sp) sp = b.SpreadMaxTenths;
                if (b.VolumeSum >= 0) vol = Math.Max(vol, 0) + b.VolumeSum;
                if (b.VolumeMax > volMax) volMax = b.VolumeMax;
            }
            sum += b.AvgSum;
            n += b.Count;
        }
        if (n > 0) result.Add(new AggBlock(start, mn, mx, sum, n, sp, vol, volMax));
        return result.ToArray();
    }
}
