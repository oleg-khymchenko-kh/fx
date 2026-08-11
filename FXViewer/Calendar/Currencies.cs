namespace FXViewer.Calendar;

public static class Currencies
{
    private static readonly (string Code, int Argb)[] Tracked =
    {
        ("EUR", unchecked((int)0xFF3366DD)),
        ("USD", unchecked((int)0xFF000000)),
        ("GBP", unchecked((int)0xFFD32F2F)),
        ("JPY", unchecked((int)0xFFB8860B)),
        ("CHF", unchecked((int)0xFF7B1FA2)),
        ("AUD", unchecked((int)0xFF1B5E20)),
        ("NZD", unchecked((int)0xFF66BB6A)),
        ("CAD", unchecked((int)0xFF795548)),
    };

    private const int OtherArgb = unchecked((int)0xFF808080);

    public static byte IdOf(string code)
    {
        for (int i = 0; i < Tracked.Length; i++)
            if (string.Equals(Tracked[i].Code, code, StringComparison.OrdinalIgnoreCase))
                return (byte)(i + 1);
        return 0;
    }

    public static bool IsTracked(string code) => IdOf(code) != 0;

    public static string CodeOf(byte id) =>
        id >= 1 && id <= Tracked.Length ? Tracked[id - 1].Code : "";

    public static int ColorArgb(byte id) =>
        id >= 1 && id <= Tracked.Length ? Tracked[id - 1].Argb : OtherArgb;

    private const string HighestCurrency = "USD";

    private const string RateDecisionEvent = "Federal Funds Rate";

    private static readonly string[] HighestEventPrefixes =
    {
        RateDecisionEvent,
        "Average Hourly Earnings",
        "Non-Farm Employment Change",
        "Unemployment Rate",
    };

    public static bool IsRateDecision(string currency, string eventName) =>
        string.Equals(currency, HighestCurrency, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(eventName)
        && eventName.StartsWith(RateDecisionEvent, StringComparison.OrdinalIgnoreCase);

    public static bool IsHighest(string currency, string eventName)
    {
        if (!string.Equals(currency, HighestCurrency, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(eventName)) return false;
        foreach (var prefix in HighestEventPrefixes)
            if (eventName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static CalendarImpact ParseImpact(string text, string currency, string eventName) =>
        IsHighest(currency, eventName) ? CalendarImpact.Highest : ParseImpact(text);

    public static CalendarImpact ParseImpact(string text)
    {
        if (string.IsNullOrEmpty(text)) return CalendarImpact.None;
        if (text.Contains("High", StringComparison.OrdinalIgnoreCase)) return CalendarImpact.High;
        if (text.Contains("Medium", StringComparison.OrdinalIgnoreCase)) return CalendarImpact.Medium;
        if (text.Contains("Low", StringComparison.OrdinalIgnoreCase)) return CalendarImpact.Low;
        if (text.Contains("Holiday", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Non-Economic", StringComparison.OrdinalIgnoreCase))
            return CalendarImpact.Holiday;
        return CalendarImpact.None;
    }
}
