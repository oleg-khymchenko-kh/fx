namespace FXViewer.Calendar;

public enum CalendarImpact : byte
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Holiday = 4,
    Highest = 5,
}

public readonly record struct CalendarEntry(
    long UnixSeconds, byte Impact, byte Currency, long DetailOffset, int DetailLength,
    bool RateDecision);

public sealed record CalendarDetail(
    long UnixSeconds, string Currency, string Impact, string Event,
    string Actual, string Forecast, string Previous, string Detail)
{
    public CalendarImpact ImpactLevel => Currencies.ParseImpact(Impact, Currency, Event);
}

public sealed record CalendarSummary(
    long UnixSeconds, string Currency, CalendarImpact Impact, string Event,
    string Actual, string Forecast, string Previous);

public sealed class CalendarSettings
{
    public bool ShowHighest { get; set; } = true;
    public bool ShowHigh { get; set; } = true;
    public bool ShowMedium { get; set; } = true;
    public bool ShowLow { get; set; } = true;
    public bool ShowHoliday { get; set; }
    public bool ShowAtAnyZoom { get; set; } = true;

    public bool IsShown(CalendarImpact impact) => impact switch
    {
        CalendarImpact.Highest => ShowHighest,
        CalendarImpact.High => ShowHigh,
        CalendarImpact.Medium => ShowMedium,
        CalendarImpact.Low => ShowLow,
        CalendarImpact.Holiday => ShowHoliday,
        _ => false,
    };

    public bool[] ShownMask()
    {
        var mask = new bool[6];
        for (int i = 0; i < mask.Length; i++) mask[i] = IsShown((CalendarImpact)i);
        return mask;
    }

    public CalendarImpact? ShownLevel()
    {
        if (!ShowHighest || !ShowHigh) return null;
        if (!ShowMedium) return ShowLow ? null : CalendarImpact.High;
        return ShowLow ? CalendarImpact.Low : CalendarImpact.Medium;
    }

    public CalendarSettings WithLevel(CalendarImpact level)
    {
        var copy = Clone();
        copy.ShowHighest = true;
        copy.ShowHigh = true;
        copy.ShowMedium = level is CalendarImpact.Medium or CalendarImpact.Low;
        copy.ShowLow = level == CalendarImpact.Low;
        return copy;
    }

    public CalendarSettings Clone() => (CalendarSettings)MemberwiseClone();
}
