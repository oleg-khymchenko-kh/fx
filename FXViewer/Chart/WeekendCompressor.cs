namespace FXViewer.Chart;

public sealed class WeekendCompressor
{
    public static readonly WeekendCompressor Instance = new();

    private const int FirstYear = 2007;
    private const int LastYear = 2100;
    private const long HourSeconds = 3600;

    private readonly long[] _gapStart;
    private readonly long[] _gapEnd;
    private readonly long[] _gapVirtual;
    private readonly long[] _removed;

    private WeekendCompressor()
    {
        var starts = new List<long>();
        var ends = new List<long>();
        var sunday = new DateTime(FirstYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        while (sunday.DayOfWeek != DayOfWeek.Sunday) sunday = sunday.AddDays(1);
        var last = new DateTime(LastYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (; sunday < last; sunday = sunday.AddDays(7))
        {
            var friday = sunday.AddDays(-2);
            long open = Unix(sunday) + (IsEuSummer(sunday.AddHours(21)) ? 21 : 22) * HourSeconds;
            long close = Unix(friday) + (IsUsSummer(friday.AddHours(21)) ? 21 : 22) * HourSeconds;
            if (close >= open) continue;
            starts.Add(close);
            ends.Add(open);
        }
        _gapStart = starts.ToArray();
        _gapEnd = ends.ToArray();
        _removed = new long[_gapStart.Length + 1];
        _gapVirtual = new long[_gapStart.Length];
        for (int i = 0; i < _gapStart.Length; i++)
        {
            _gapVirtual[i] = _gapStart[i] - _removed[i];
            _removed[i + 1] = _removed[i] + _gapEnd[i] - _gapStart[i];
        }
    }

    public long ToVirtual(long unixSeconds)
    {
        int i = CountAtOrBefore(_gapStart, unixSeconds);
        if (i > 0 && unixSeconds < _gapEnd[i - 1]) return _gapVirtual[i - 1];
        return unixSeconds - _removed[i];
    }

    public long ToReal(long virtualSeconds)
    {
        int i = CountAtOrBefore(_gapVirtual, virtualSeconds);
        return virtualSeconds + _removed[i];
    }

    public long ToRealEnd(long virtualSeconds)
    {
        int i = CountAtOrBefore(_gapVirtual, virtualSeconds);
        if (i > 0 && virtualSeconds == _gapVirtual[i - 1]) return _gapStart[i - 1];
        return virtualSeconds + _removed[i];
    }

    public bool InGap(long unixSeconds)
    {
        int i = CountAtOrBefore(_gapStart, unixSeconds);
        return i > 0 && unixSeconds < _gapEnd[i - 1];
    }

    private static int CountAtOrBefore(long[] values, long key)
    {
        int lo = 0;
        int hi = values.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (values[mid] <= key) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static long Unix(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    private static bool IsEuSummer(DateTime utc) =>
        utc >= LastSunday(utc.Year, 3).AddHours(1) && utc < LastSunday(utc.Year, 10).AddHours(1);

    private static bool IsUsSummer(DateTime utc) =>
        utc >= NthSunday(utc.Year, 3, 2).AddHours(7) && utc < NthSunday(utc.Year, 11, 1).AddHours(6);

    private static DateTime LastSunday(int year, int month)
    {
        var d = new DateTime(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Utc);
        return d.AddDays(-(int)d.DayOfWeek);
    }

    private static DateTime NthSunday(int year, int month, int n)
    {
        var d = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        return d.AddDays((7 - (int)d.DayOfWeek) % 7 + 7 * (n - 1));
    }
}
