using FXViewer.Chart;

namespace FXViewer;

public sealed class ChartTab
{
    public string Name { get; set; } = "";
    public ChartViewState? State { get; set; }
    public List<ShiftPlacement> Shifts { get; set; } = new();

    public ChartTab Clone() => new()
    {
        Name = Name,
        State = State?.Clone(),
        Shifts = Shifts.Select(x => x.Clone()).ToList(),
    };
}

public sealed class ShiftPlacement
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string TargetSymbol { get; set; } = "";
    public long SourceTimeUnix { get; set; }
    public long ChartTimeUnix { get; set; }
    public bool Flip { get; set; }

    public static ShiftPlacement From(IndicatorSymbol ind) => new()
    {
        Name = ind.Name,
        Source = ind.Source,
        TargetSymbol = ind.TargetSymbol,
        SourceTimeUnix = ind.SourceTimeUnix,
        ChartTimeUnix = ind.ChartTimeUnix,
        Flip = ind.Flip,
    };

    public ShiftPlacement Clone() => new()
    {
        Name = Name,
        Source = Source,
        TargetSymbol = TargetSymbol,
        SourceTimeUnix = SourceTimeUnix,
        ChartTimeUnix = ChartTimeUnix,
        Flip = Flip,
    };

    public string Target() => TargetSymbol.Length > 0 ? TargetSymbol : Source;

    public bool Matches(IndicatorSymbol ind) =>
        IndicatorSymbol.NameKey(Source) == IndicatorSymbol.NameKey(ind.Source)
        && IndicatorSymbol.NameKey(TargetSymbol) == IndicatorSymbol.NameKey(ind.TargetSymbol)
        && SourceTimeUnix == ind.SourceTimeUnix
        && ChartTimeUnix == ind.ChartTimeUnix
        && Flip == ind.Flip;

    public void ApplyTo(IndicatorSymbol ind)
    {
        ind.Source = Source;
        ind.TargetSymbol = TargetSymbol;
        ind.SourceTimeUnix = SourceTimeUnix;
        ind.ChartTimeUnix = ChartTimeUnix;
        ind.Flip = Flip;
    }
}
