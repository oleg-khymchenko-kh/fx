using FXViewer.Chart;

namespace FXViewer.Storage;

public static class WideSpreadRule
{
    public const int MinTenths = 41;

    public static readonly long MeasuredFromUnix =
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static readonly HashSet<string> MeasuredPairs = new(StringComparer.OrdinalIgnoreCase);

    public static bool Hide;

    public static void SetMeasuredPairs(IEnumerable<string> symbols)
    {
        MeasuredPairs.Clear();
        foreach (var symbol in symbols) MeasuredPairs.Add(symbol.Trim());
    }

    public static bool Measured(string symbol) => MeasuredPairs.Contains(symbol.Trim());

    public static bool InQuietWindow(long minuteUnixSeconds)
    {
        var utc = DateTimeOffset.FromUnixTimeSeconds(minuteUnixSeconds).UtcDateTime;
        return utc.Hour >= SessionClock.AmericaCloseHourUtc(utc);
    }

    public static bool IsWide(string symbol, long minuteUnixSeconds, bool hasSpread, int spreadCode) =>
        InQuietWindow(minuteUnixSeconds)
        && (hasSpread
            ? SpreadCodes.ToTenths(spreadCode) >= MinTenths
            : minuteUnixSeconds >= MeasuredFromUnix && Measured(symbol));

    public static bool Hidden(string symbol, long minuteUnixSeconds, bool hasSpread, int spreadCode) =>
        Hide && IsWide(symbol, minuteUnixSeconds, hasSpread, spreadCode);
}
