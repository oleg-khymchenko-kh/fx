using System.Globalization;
using FXViewer.Chart;
using FXViewer.Storage;

namespace FXViewer.Game;

public sealed record GameDayPick(DateOnly Day, int Plays, int Pool, int Index);

public static class GameDayPicker
{
    public static readonly DateOnly FirstDay = new(2026, 1, 1);
    public const int MinDayCandles = 60;
    public const int AfternoonWinterHourUtc = 15;
    public const int DayEndMinutesBeforeClose = 5;

    public static DateOnly LastDay(DateOnly today) =>
        today.AddDays(-(((int)today.DayOfWeek + 6) % 7) - 3);

    public static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? Parse(string key) =>
        DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var day)
            ? day
            : null;

    public static long StartUnix(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

    public static long EndUnix(DateOnly day) =>
        StartUnix(day) + SessionClock.AmericaCloseHourUtc(Noon(day)) * SessionClock.HourSeconds
        - DayEndMinutesBeforeClose * ChartColumns.MinuteSeconds;

    public static int AfternoonHourUtc(DateOnly day) =>
        AfternoonWinterHourUtc - (SessionClock.UsSummer(Noon(day)) ? 1 : 0);

    public static long StartUnix(DateOnly day, string mode) => GameModes.IsAfternoon(mode)
        ? StartUnix(day) + AfternoonHourUtc(day) * SessionClock.HourSeconds
        : StartUnix(day);

    public static List<DateOnly> Candidates(DateOnly first, DateOnly last, IReadOnlyList<Candle[]> series,
        string mode)
    {
        var days = new List<DateOnly>();
        for (var day = first; day <= last; day = day.AddDays(1))
            if (IsTradingDay(series, day, mode))
                days.Add(day);
        return days;
    }

    public static bool HasNextDay(DateOnly day, DateOnly today) => day < LastDay(today);

    public static DateOnly? NextDay(DateOnly day, DateOnly last, IReadOnlyList<Candle[]> series, string mode)
    {
        for (var next = day.AddDays(1); next <= last; next = next.AddDays(1))
            if (IsTradingDay(series, next, mode))
                return next;
        return null;
    }

    private static DateTime Noon(DateOnly day) =>
        DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Utc);

    private static bool IsTradingDay(IReadOnlyList<Candle[]> series, DateOnly day, string mode) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && HasCandles(series, day, mode);

    private static bool HasCandles(IReadOnlyList<Candle[]> series, DateOnly day, string mode)
    {
        long from = StartUnix(day, mode);
        long to = EndUnix(day);
        foreach (var minutes in series)
            if (GameSim.IndexAt(minutes, to + 1) - GameSim.IndexAt(minutes, from) >= MinDayCandles)
                return true;
        return false;
    }

    public static GameDayPick? Pick(IReadOnlyList<DateOnly> days, IReadOnlyDictionary<string, int> plays,
        Func<int, int> nextIndex)
    {
        int least = int.MaxValue;
        var pool = new List<DateOnly>();
        foreach (var day in days)
        {
            int count = plays.GetValueOrDefault(Key(day));
            if (count > least) continue;
            if (count < least)
            {
                least = count;
                pool.Clear();
            }
            pool.Add(day);
        }
        if (pool.Count == 0) return null;
        int index = nextIndex(pool.Count);
        return new GameDayPick(pool[index], least, pool.Count, index);
    }
}
