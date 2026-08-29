using FXViewer.Chart;

namespace FXViewer.Storage;

public static class WideSpreadRule
{
    public const int MinTenths = 41;

    public static bool Hide;

    public static bool InQuietWindow(long minuteUnixSeconds)
    {
        var utc = DateTimeOffset.FromUnixTimeSeconds(minuteUnixSeconds).UtcDateTime;
        return utc.Hour >= SessionClock.AmericaCloseHourUtc(utc);
    }

    public static bool IsWide(long minuteUnixSeconds, bool hasSpread, int spreadCode) =>
        hasSpread && SpreadCodes.ToTenths(spreadCode) >= MinTenths && InQuietWindow(minuteUnixSeconds);

    public static bool Hidden(long minuteUnixSeconds, bool hasSpread, int spreadCode) =>
        Hide && IsWide(minuteUnixSeconds, hasSpread, spreadCode);
}
