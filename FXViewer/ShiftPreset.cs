using System.Globalization;
using FXViewer.Compute;

namespace FXViewer;

public sealed class ShiftPreset
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string TargetSymbol { get; set; } = "";
    public long SourceTimeUnix { get; set; }
    public long ChartTimeUnix { get; set; }
    public bool Flip { get; set; }
    public double ZoomPercent { get; set; } = ShiftedSymbol.DefaultZoomPercent;
    public bool ReverseTime { get; set; }
    public double OffsetOverSource { get; set; }
    public long MirrorBase { get; set; }

    public static ShiftPreset From(IndicatorSymbol ind, double offsetOverSource, long mirrorBase,
        string name = "") => new()
    {
        Name = name,
        Source = ind.Source,
        TargetSymbol = ind.TargetSymbol,
        SourceTimeUnix = ind.SourceTimeUnix,
        ChartTimeUnix = ind.ChartTimeUnix,
        Flip = ind.Flip,
        ZoomPercent = ind.ZoomPercent,
        ReverseTime = ind.ReverseTime,
        OffsetOverSource = offsetOverSource,
        MirrorBase = mirrorBase,
    };

    public string Target() => TargetSymbol.Length > 0 ? TargetSymbol : Source;

    public string DefaultName()
    {
        var day = DateTimeOffset.FromUnixTimeSeconds(SourceTimeUnix).UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string zoom = ZoomPercent == ShiftedSymbol.DefaultZoomPercent
            ? ""
            : " " + ZoomPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        return $"{Target()} {day}{(Flip ? " flip" : "")}{(ReverseTime ? " reversed" : "")}{zoom}";
    }

    public void CopyParamsFrom(ShiftPreset other)
    {
        Source = other.Source;
        TargetSymbol = other.TargetSymbol;
        SourceTimeUnix = other.SourceTimeUnix;
        ChartTimeUnix = other.ChartTimeUnix;
        Flip = other.Flip;
        ZoomPercent = other.ZoomPercent;
        ReverseTime = other.ReverseTime;
        OffsetOverSource = other.OffsetOverSource;
        MirrorBase = other.MirrorBase;
    }

    public void ApplyTo(ShiftPlacement placement)
    {
        placement.Source = Source;
        placement.TargetSymbol = TargetSymbol;
        placement.SourceTimeUnix = SourceTimeUnix;
        placement.ChartTimeUnix = ChartTimeUnix;
        placement.Flip = Flip;
        placement.ZoomPercent = ZoomPercent;
        placement.ReverseTime = ReverseTime;
    }

    public bool Matches(IndicatorSymbol ind) =>
        IndicatorSymbol.NameKey(Source) == IndicatorSymbol.NameKey(ind.Source)
        && IndicatorSymbol.NameKey(TargetSymbol) == IndicatorSymbol.NameKey(ind.TargetSymbol)
        && SourceTimeUnix == ind.SourceTimeUnix
        && ChartTimeUnix == ind.ChartTimeUnix
        && Flip == ind.Flip
        && ZoomPercent == ind.ZoomPercent
        && ReverseTime == ind.ReverseTime;

    public bool SameParams(ShiftPreset other) =>
        IndicatorSymbol.NameKey(Source) == IndicatorSymbol.NameKey(other.Source)
        && IndicatorSymbol.NameKey(TargetSymbol) == IndicatorSymbol.NameKey(other.TargetSymbol)
        && SourceTimeUnix == other.SourceTimeUnix
        && ChartTimeUnix == other.ChartTimeUnix
        && Flip == other.Flip
        && ZoomPercent == other.ZoomPercent
        && ReverseTime == other.ReverseTime;

    public bool Rebase(long anchorUnix)
    {
        if (!ShiftedSymbol.AnchorsBroken(SourceTimeUnix, ChartTimeUnix)) return false;
        (SourceTimeUnix, ChartTimeUnix) =
            ShiftedSymbol.Rebase(SourceTimeUnix, ChartTimeUnix, anchorUnix, ReverseTime);
        return true;
    }
}
