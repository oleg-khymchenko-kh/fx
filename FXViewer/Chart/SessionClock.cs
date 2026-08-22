namespace FXViewer.Chart;

public enum ChartSession
{
    None,
    Europe,
    Overlap,
    America,
}

public static class SessionClock
{
    public const long HourSeconds = 3600;
    public const int EuropeOpenWinterHourUtc = 8;
    public const int EuropeCloseWinterHourUtc = 17;
    public const int AmericaOpenWinterHourUtc = 13;
    public const int AmericaCloseWinterHourUtc = 22;

    public static ChartSession At(long unixSeconds)
    {
        var utc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        if (utc.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return ChartSession.None;
        long secondOfDay = utc.TimeOfDay.Ticks / TimeSpan.TicksPerSecond;
        bool eu = EuSummer(utc);
        bool us = UsSummer(utc);
        long euOpen = (EuropeOpenWinterHourUtc - (eu ? 1 : 0)) * HourSeconds;
        long euClose = (EuropeCloseWinterHourUtc - (eu ? 1 : 0)) * HourSeconds;
        long usOpen = (AmericaOpenWinterHourUtc - (us ? 1 : 0)) * HourSeconds;
        long usClose = (AmericaCloseWinterHourUtc - (us ? 1 : 0)) * HourSeconds;
        bool inEurope = secondOfDay >= euOpen && secondOfDay < euClose;
        bool inAmerica = secondOfDay >= usOpen && secondOfDay < usClose;
        if (inEurope && inAmerica) return ChartSession.Overlap;
        if (inAmerica) return ChartSession.America;
        if (inEurope) return ChartSession.Europe;
        return ChartSession.None;
    }

    public static bool EuSummer(DateTime utc) =>
        utc >= LastSunday(utc.Year, 3).AddHours(1) && utc < LastSunday(utc.Year, 10).AddHours(1);

    public static bool UsSummer(DateTime utc) =>
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
