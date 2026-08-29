using System.Text.Json.Serialization;

namespace FXViewer.Chart;

public sealed class ChartViewState
{
    public long ColumnSeconds { get; set; }
    public long ViewStartBucket { get; set; }
    public double TopPrice { get; set; }
    public double PointsPerRow { get; set; }
    public double PriceOffsetPoints { get; set; }
    public Dictionary<string, double>? SymbolOffsetPoints { get; set; }
    public bool RelativeIndicatorOffsets { get; set; }
    public List<string>? HiddenSymbols { get; set; }
    public List<string>? CollapsedSymbols { get; set; }
    public bool CalendarVisible { get; set; }
    public bool ForecastHidden { get; set; }
    public bool WeekendsHidden { get; set; }
    public bool SessionsVisible { get; set; }
    public string? FlattenSymbol { get; set; }
    public int FlattenLine { get; set; } = -1;
    public int ZoomLevelIndex { get; set; } = -1;
    public int TiltedUpGridIndex { get; set; }
    public int TiltedDownGridIndex { get; set; }
    public List<TiltedGridState>? TiltedGrids { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MinutesPerColumn { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TiltedGridVisible { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TiltedGridDays { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TiltedGridHours { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TiltedGridMinutes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TiltedGridDownDays { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TiltedGridDownHours { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TiltedGridDownMinutes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double TiltedGridAnchorSeconds { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double TiltedGridAnchorPoints { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TiltedGridIndex { get; set; }

    public const int TiltedGridCount = 7;

    public long RestoredColumnSeconds() =>
        ColumnSeconds > 0 ? ColumnSeconds : MinutesPerColumn * 60L;

    public ChartViewState Clone()
    {
        var copy = (ChartViewState)MemberwiseClone();
        if (SymbolOffsetPoints != null)
            copy.SymbolOffsetPoints = new Dictionary<string, double>(SymbolOffsetPoints);
        if (HiddenSymbols != null) copy.HiddenSymbols = new List<string>(HiddenSymbols);
        if (CollapsedSymbols != null) copy.CollapsedSymbols = new List<string>(CollapsedSymbols);
        if (TiltedGrids != null) copy.TiltedGrids = TiltedGrids.Select(g => g.Clone()).ToList();
        return copy;
    }

    public void EnsureTiltedGrids()
    {
        if (TiltedGrids is { Count: > 0 })
        {
            while (TiltedGrids.Count < TiltedGridCount) TiltedGrids.Add(TiltedGridState.CreateDefault());
            if (TiltedGrids.Count > TiltedGridCount)
                TiltedGrids.RemoveRange(TiltedGridCount, TiltedGrids.Count - TiltedGridCount);
        }
        else
        {
            TiltedGrids = new List<TiltedGridState> { LegacyTiltedGrid() };
            while (TiltedGrids.Count < TiltedGridCount) TiltedGrids.Add(TiltedGridState.CreateDefault());
            TiltedGridIndex = TiltedGridVisible ? 1 : 0;
        }
        foreach (var grid in TiltedGrids) grid.Migrate();
        if (TiltedGridIndex > 0 && TiltedUpGridIndex == 0 && TiltedDownGridIndex == 0)
        {
            TiltedUpGridIndex = TiltedGridIndex;
            TiltedDownGridIndex = TiltedGridIndex;
        }
        TiltedGridIndex = 0;
        TiltedGridVisible = false;
        TiltedUpGridIndex = Math.Clamp(TiltedUpGridIndex, 0, TiltedGridCount);
        TiltedDownGridIndex = Math.Clamp(TiltedDownGridIndex, 0, TiltedGridCount);
    }

    private TiltedGridState LegacyTiltedGrid() => new()
    {
        UpDays = Math.Max(0, TiltedGridDays),
        UpHours = Math.Clamp(TiltedGridHours, 0, 23),
        UpMinutes = Math.Clamp(TiltedGridMinutes, 0, 59),
        DownDays = Math.Max(0, TiltedGridDownDays),
        DownHours = Math.Clamp(TiltedGridDownHours, 0, 23),
        DownMinutes = Math.Clamp(TiltedGridDownMinutes, 0, 59),
        AnchorSeconds = TiltedGridAnchorSeconds,
        AnchorPoints = TiltedGridAnchorPoints,
    };
}

public sealed class TiltedGridState
{
    public const double DefaultPipsPerDay = 10;
    public const double MinPipsPerDay = 0.001;
    public const double MaxPipsPerDay = 1_000_000;

    private const double PointsPerPip = 10;
    private const double DaySeconds = 86400;
    private const double PipsPerGridStep = 100;

    public double UpPipsPerDay { get; set; }
    public double DownPipsPerDay { get; set; }
    public double UpAnchorSeconds { get; set; }
    public double UpAnchorPoints { get; set; }
    public double DownAnchorSeconds { get; set; }
    public double DownAnchorPoints { get; set; }
    public bool UpLocked { get; set; }
    public bool DownLocked { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Locked { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int UpDays { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int UpHours { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int UpMinutes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DownDays { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DownHours { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DownMinutes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double AnchorSeconds { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double AnchorPoints { get; set; }

    public static TiltedGridState CreateDefault()
    {
        var grid = new TiltedGridState();
        grid.Normalize();
        return grid;
    }

    [JsonIgnore]
    public double UpSlope => SlopeOfPips(UpPipsPerDay);

    [JsonIgnore]
    public double DownSlope => SlopeOfPips(DownPipsPerDay);

    public double PipsPerDay(bool up) => up ? UpPipsPerDay : DownPipsPerDay;

    public double Slope(bool up) => up ? UpSlope : DownSlope;

    public double AnchorSecondsOf(bool up) => up ? UpAnchorSeconds : DownAnchorSeconds;

    public double AnchorPointsOf(bool up) => up ? UpAnchorPoints : DownAnchorPoints;

    public bool LockedOf(bool up) => up ? UpLocked : DownLocked;

    public void SetLocked(bool up, bool locked)
    {
        if (up) UpLocked = locked;
        else DownLocked = locked;
    }

    public void SetPipsPerDay(bool up, double pips)
    {
        if (up) UpPipsPerDay = pips;
        else DownPipsPerDay = pips;
    }

    public void SetAnchor(bool up, double seconds, double points)
    {
        if (up)
        {
            UpAnchorSeconds = seconds;
            UpAnchorPoints = points;
        }
        else
        {
            DownAnchorSeconds = seconds;
            DownAnchorPoints = points;
        }
    }

    public static double SlopeOfPips(double pipsPerDay) => pipsPerDay * PointsPerPip / DaySeconds;

    public static double PipsOfSlope(double slope) => slope * DaySeconds / PointsPerPip;

    public static double SignedPips(double pipsPerDay, bool up)
    {
        double magnitude = double.IsFinite(pipsPerDay) ? Math.Abs(pipsPerDay) : 0;
        if (magnitude <= 0) magnitude = DefaultPipsPerDay;
        magnitude = Math.Clamp(magnitude, MinPipsPerDay, MaxPipsPerDay);
        return up ? magnitude : -magnitude;
    }

    public void Normalize()
    {
        UpPipsPerDay = SignedPips(UpPipsPerDay, true);
        DownPipsPerDay = SignedPips(DownPipsPerDay, false);
        if (!double.IsFinite(UpAnchorSeconds) || !double.IsFinite(UpAnchorPoints))
            SetAnchor(true, 0, 0);
        if (!double.IsFinite(DownAnchorSeconds) || !double.IsFinite(DownAnchorPoints))
            SetAnchor(false, 0, 0);
    }

    public void Migrate()
    {
        double legacyUp = PipsOfCycle(UpDays, UpHours, UpMinutes);
        double legacyDown = PipsOfCycle(DownDays, DownHours, DownMinutes);
        if (legacyDown <= 0) legacyDown = legacyUp;
        if (UpPipsPerDay == 0 && legacyUp > 0) UpPipsPerDay = legacyUp;
        if (DownPipsPerDay == 0 && legacyDown > 0) DownPipsPerDay = -legacyDown;
        if (UpAnchorSeconds == 0 && UpAnchorPoints == 0)
            SetAnchor(true, AnchorSeconds, AnchorPoints);
        if (DownAnchorSeconds == 0 && DownAnchorPoints == 0)
            SetAnchor(false, AnchorSeconds, AnchorPoints);
        if (Locked)
        {
            UpLocked = true;
            DownLocked = true;
            Locked = false;
        }
        UpDays = UpHours = UpMinutes = 0;
        DownDays = DownHours = DownMinutes = 0;
        AnchorSeconds = 0;
        AnchorPoints = 0;
        Normalize();
    }

    private static double PipsOfCycle(int days, int hours, int minutes)
    {
        double seconds = days * 86400.0 + hours * 3600.0 + minutes * 60.0;
        return seconds > 0 ? PipsPerGridStep * DaySeconds / seconds : 0;
    }

    public TiltedGridState Clone() => (TiltedGridState)MemberwiseClone();
}
