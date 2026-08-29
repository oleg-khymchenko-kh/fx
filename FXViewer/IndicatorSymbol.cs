using FXViewer.Compute;
using FXViewer.Storage;

namespace FXViewer;

public sealed class IndicatorSymbol
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Type { get; set; } = IndicatorTypes.ZigZag;
    public int Limit1Pips { get; set; } = DefaultLimit1Pips;
    public int Limit2Pips { get; set; } = DefaultLimit2Pips;
    public int Limit2DelayMinutes { get; set; } = DefaultLimit2DelayMinutes;
    public int Period { get; set; } = 20;
    public string Unit { get; set; } = IndicatorUnits.Minutes;
    public bool FromFuture { get; set; }
    public bool AverageWeighted { get; set; }
    public long SourceTimeUnix { get; set; }
    public long ChartTimeUnix { get; set; }
    public bool Flip { get; set; }
    public long StartTimeUnix { get; set; }
    public long EndTimeUnix { get; set; }
    public string IndexMethod { get; set; } = IndexMethods.Median;
    public string IndexAlgorithm { get; set; } = IndexAlgorithms.Percent;
    public List<string> IndexPairs { get; set; } = new();
    public string IndexPair { get; set; } = "";
    public int StopLossPips { get; set; } = DefaultStopLossPips;
    public int TakeProfitPips { get; set; } = DefaultTakeProfitPips;
    public string DealsFile { get; set; } = "";
    public string TargetSymbol { get; set; } = "";
    public int FindSmoothMinutes { get; set; } = DefaultFindSmoothMinutes;
    public int FindZoomPercent { get; set; } = DefaultFindZoomPercent;
    public int FindStepMinutes { get; set; } = DefaultFindStepMinutes;
    public List<string> FindTargets { get; set; } = new();
    public string FindSearchType { get; set; } = FindSearchTypes.Similarity;
    public List<string> FindZigZagTargets { get; set; } = new();
    public List<int> DensityPeriods { get; set; } = new();
    public List<string> DensityUnits { get; set; } = new();
    public List<int> DensityScalePercents { get; set; } = new();
    public double DensityScalePerPixel { get; set; }
    public int DensitySelected { get; set; }
    public int VolumeGroupMinutes { get; set; } = 1;
    public double VolumeBarScale { get; set; } = 1;
    public double VolumeBarUnit { get; set; }
    public bool VolumeGroupLocked { get; set; }
    public bool VolumeSplitSides { get; set; } = true;
    public int ColorArgb { get; set; } = unchecked((int)0xFFFF8C00);
    public int SellColorArgb { get; set; } = unchecked((int)0xFF00ACC1);

    public const int DefaultLimit1Pips = 50;
    public const int DefaultLimit2Pips = 20;
    public const int DefaultLimit2DelayMinutes = 90;
    public const int DefaultStopLossPips = 15;
    public const int DefaultTakeProfitPips = 45;
    public const int DefaultFindSmoothMinutes = 240;
    public const int DefaultFindZoomPercent = 50;
    public const int DefaultFindStepMinutes = 60;
    public const int DensityOptionCount = 9;
    public const int DensityAllOption = DensityOptionCount;

    public static readonly int[] VolumeGroupSteps =
        { 1, 2, 3, 5, 10, 15, 30, 60, 120, 240, 480, 720, 1440 };

    public int EffectiveVolumeGroupMinutes() => Math.Max(1, VolumeGroupMinutes);

    public const double VolumeHeightGainPerDoubledGroup = 1.5;

    private static readonly double VolumeUnitGroupPower =
        1 - Math.Log2(VolumeHeightGainPerDoubledGroup);

    public static double ScaleVolumeBarUnit(double unit, int fromGroup, int toGroup)
    {
        if (unit <= 0 || fromGroup <= 0 || toGroup <= 0 || fromGroup == toGroup) return unit;
        return unit * Math.Pow((double)toGroup / fromGroup, VolumeUnitGroupPower);
    }

    public static int StepVolumeGroup(int minutes, int delta)
    {
        int current = Math.Clamp(minutes, VolumeGroupSteps[0], VolumeGroupSteps[^1]);
        var steps = VolumeGroupSteps;
        if (delta > 0)
        {
            for (int i = 0; i < steps.Length; i++)
                if (steps[i] > current) return steps[i];
            return steps[^1];
        }
        for (int i = steps.Length - 1; i >= 0; i--)
            if (steps[i] < current) return steps[i];
        return steps[0];
    }

    public static readonly int[] DefaultDensityPeriods = { 1, 2, 5, 10, 20, 40, 60, 120, 240 };

    public static readonly string[] DefaultDensityUnits =
    {
        IndicatorUnits.Days, IndicatorUnits.Days, IndicatorUnits.Days,
        IndicatorUnits.Days, IndicatorUnits.Days, IndicatorUnits.Days,
        IndicatorUnits.Days, IndicatorUnits.Days, IndicatorUnits.Days,
    };

    public int DensityPeriodAt(int option) =>
        option < DensityPeriods.Count && DensityPeriods[option] > 0
            ? DensityPeriods[option]
            : DefaultDensityPeriods[option];

    public string DensityUnitAt(int option) =>
        option < DensityUnits.Count && !string.IsNullOrEmpty(DensityUnits[option])
            ? DensityUnits[option]
            : DefaultDensityUnits[option];

    public const int DefaultDensityScalePercent = 100;

    public int DensityScalePercentAt(int option) =>
        option >= 0 && option < DensityScalePercents.Count && DensityScalePercents[option] > 0
            ? DensityScalePercents[option]
            : DefaultDensityScalePercent;

    public int[] DensityScalePercentValues()
    {
        var percents = new int[DensityAllOption + 1];
        for (int i = 0; i < percents.Length; i++) percents[i] = DensityScalePercentAt(i);
        return percents;
    }

    public int[] DensityWindowBars()
    {
        var bars = new int[DensityOptionCount];
        for (int i = 0; i < bars.Length; i++)
            bars[i] = DensityPeriodAt(i) * IndicatorUnits.BarsPerUnit(DensityUnitAt(i));
        return bars;
    }

    public string ShiftTarget() => TargetSymbol.Length > 0 ? TargetSymbol : Source;

    public List<string> EffectiveFindTargets() =>
        FindTargets.Count > 0 ? new List<string>(FindTargets) : new List<string> { ShiftTarget() };

    public IndicatorSymbol Clone() => new()
    {
        Name = Name,
        Source = Source,
        Type = Type,
        Limit1Pips = Limit1Pips,
        Limit2Pips = Limit2Pips,
        Limit2DelayMinutes = Limit2DelayMinutes,
        Period = Period,
        Unit = Unit,
        FromFuture = FromFuture,
        AverageWeighted = AverageWeighted,
        SourceTimeUnix = SourceTimeUnix,
        ChartTimeUnix = ChartTimeUnix,
        Flip = Flip,
        StartTimeUnix = StartTimeUnix,
        EndTimeUnix = EndTimeUnix,
        IndexMethod = IndexMethod,
        IndexAlgorithm = IndexAlgorithm,
        IndexPairs = new List<string>(IndexPairs),
        IndexPair = IndexPair,
        StopLossPips = StopLossPips,
        TakeProfitPips = TakeProfitPips,
        DealsFile = DealsFile,
        TargetSymbol = TargetSymbol,
        FindSmoothMinutes = FindSmoothMinutes,
        FindZoomPercent = FindZoomPercent,
        FindStepMinutes = FindStepMinutes,
        FindTargets = new List<string>(FindTargets),
        FindSearchType = FindSearchType,
        FindZigZagTargets = new List<string>(FindZigZagTargets),
        DensityPeriods = new List<int>(DensityPeriods),
        DensityUnits = new List<string>(DensityUnits),
        DensityScalePercents = new List<int>(DensityScalePercents),
        DensityScalePerPixel = DensityScalePerPixel,
        DensitySelected = DensitySelected,
        VolumeGroupMinutes = VolumeGroupMinutes,
        VolumeBarScale = VolumeBarScale,
        VolumeBarUnit = VolumeBarUnit,
        VolumeGroupLocked = VolumeGroupLocked,
        VolumeSplitSides = VolumeSplitSides,
        ColorArgb = ColorArgb,
        SellColorArgb = SellColorArgb,
    };

    public bool SameData(IndicatorSymbol other)
    {
        if (!string.Equals(Type, other.Type, StringComparison.OrdinalIgnoreCase)) return false;
        if (IndicatorTypes.IsIndex(Type))
            return StartTimeUnix == other.StartTimeUnix
                && EndTimeUnix == other.EndTimeUnix
                && string.Equals(IndexMethod, other.IndexMethod, StringComparison.OrdinalIgnoreCase)
                && string.Equals(IndexAlgorithm, other.IndexAlgorithm, StringComparison.OrdinalIgnoreCase)
                && SamePairs(IndexPairs, other.IndexPairs);
        if (NameKey(Source) != NameKey(other.Source)) return false;
        if (IndicatorTypes.IsCurrency(Type))
            return NameKey(IndexPair) == NameKey(other.IndexPair);
        if (IndicatorTypes.IsAverage(Type))
            return Period == other.Period
                && string.Equals(Unit, other.Unit, StringComparison.OrdinalIgnoreCase)
                && FromFuture == other.FromFuture
                && AverageWeighted == other.AverageWeighted;
        if (IndicatorTypes.IsEntryPoints(Type))
            return StopLossPips == other.StopLossPips && TakeProfitPips == other.TakeProfitPips;
        if (IndicatorTypes.IsPriceAge(Type)) return true;
        if (IndicatorTypes.IsDrawing(Type) || IndicatorTypes.IsShift(Type)
            || IndicatorTypes.IsDeals(Type) || IndicatorTypes.IsDensity(Type)
            || IndicatorTypes.IsSpread(Type) || IndicatorTypes.IsVolume(Type)
            || IndicatorTypes.IsOrderBook(Type)) return true;
        return Limit1Pips == other.Limit1Pips
            && Limit2Pips == other.Limit2Pips
            && Limit2DelayMinutes == other.Limit2DelayMinutes;
    }

    public bool RebaseShiftAnchors(long anchorUnix)
    {
        if (!IndicatorTypes.IsShift(Type)) return false;
        if (!ShiftedSymbol.AnchorsBroken(SourceTimeUnix, ChartTimeUnix)) return false;
        (SourceTimeUnix, ChartTimeUnix) =
            ShiftedSymbol.Rebase(SourceTimeUnix, ChartTimeUnix, anchorUnix);
        return true;
    }

    public static string NameKey(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static bool SamePairs(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        var left = a.Select(NameKey).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var right = b.Select(NameKey).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < left.Length; i++)
            if (left[i] != right[i]) return false;
        return true;
    }
}

public static class IndicatorTypes
{
    public const string ZigZag = "ZigZag";
    public const string Average = "Average";
    public const string Shift = "Shift";
    public const string Drawing = "Drawing";
    public const string Index = "Index";
    public const string Currency = "Currency";
    public const string EntryPoints = "EntryPoints";
    public const string PriceAge = "PriceAge";
    public const string Deals = "Deals";
    public const string Density = "Density";
    public const string Spread = "Spread";
    public const string Volume = "Volume";
    public const string LegacyVolumeProfile = "VolumeProfile";
    public const string PendingOrders = "PendingOrders";
    public const string OpenPositions = "OpenPositions";
    public const string MarketDepth = "MarketDepth";

    public static readonly string[] All =
    {
        ZigZag, Average, Shift, Drawing, Index, Currency, EntryPoints, PriceAge, Deals, Density,
        Spread, Volume, PendingOrders, OpenPositions, MarketDepth,
    };

    public static bool IsDrawing(string type) =>
        string.Equals(type, Drawing, StringComparison.OrdinalIgnoreCase);

    public static bool IsDeals(string type) =>
        string.Equals(type, Deals, StringComparison.OrdinalIgnoreCase);

    public static bool IsAverage(string type) =>
        string.Equals(type, Average, StringComparison.OrdinalIgnoreCase);

    public static bool IsShift(string type) =>
        string.Equals(type, Shift, StringComparison.OrdinalIgnoreCase);

    public static bool IsDensity(string type) =>
        string.Equals(type, Density, StringComparison.OrdinalIgnoreCase);

    public static bool IsSpread(string type) =>
        string.Equals(type, Spread, StringComparison.OrdinalIgnoreCase);

    public static bool IsVolume(string type) =>
        string.Equals(type, Volume, StringComparison.OrdinalIgnoreCase);

    public static bool IsLegacyVolumeProfile(string type) =>
        string.Equals(type, LegacyVolumeProfile, StringComparison.OrdinalIgnoreCase);

    public static bool IsPendingOrders(string type) =>
        string.Equals(type, PendingOrders, StringComparison.OrdinalIgnoreCase);

    public static bool IsOpenPositions(string type) =>
        string.Equals(type, OpenPositions, StringComparison.OrdinalIgnoreCase);

    public static bool IsMarketDepth(string type) =>
        string.Equals(type, MarketDepth, StringComparison.OrdinalIgnoreCase);

    public static bool IsOrderBook(string type) =>
        IsPendingOrders(type) || IsOpenPositions(type) || IsMarketDepth(type);

    public static bool HasStorage(string type) =>
        !IsDrawing(type) && !IsShift(type) && !IsDeals(type) && !IsDensity(type) && !IsSpread(type)
        && !IsVolume(type) && !IsOrderBook(type) && !IsAverage(type);

    public static bool IsZigZag(string type) =>
        string.Equals(type, ZigZag, StringComparison.OrdinalIgnoreCase);

    public static bool IsIndex(string type) =>
        string.Equals(type, Index, StringComparison.OrdinalIgnoreCase);

    public static bool IsCurrency(string type) =>
        string.Equals(type, Currency, StringComparison.OrdinalIgnoreCase);

    public static bool IsEntryPoints(string type) =>
        string.Equals(type, EntryPoints, StringComparison.OrdinalIgnoreCase);

    public static bool IsPriceAge(string type) =>
        string.Equals(type, PriceAge, StringComparison.OrdinalIgnoreCase);

    public static bool NeedsSource(string type) => !IsIndex(type);

    public static bool SourceIsIndex(string type) => IsCurrency(type);

    public static string Label(string type) =>
        IsIndex(type) ? "USD Index"
        : IsCurrency(type) ? "Currency Index"
        : IsEntryPoints(type) ? "Entry points"
        : IsPriceAge(type) ? "Price age"
        : IsPendingOrders(type) ? "Pending orders"
        : IsOpenPositions(type) ? "Open positions"
        : IsMarketDepth(type) ? "Market depth"
        : type;

    public static string FromLabel(string label) =>
        All.FirstOrDefault(t => string.Equals(Label(t), label, StringComparison.OrdinalIgnoreCase)) ?? label;
}

public static class IndexMethods
{
    public const string Median = "Median";
    public const string Average = "Average";

    public static readonly string[] All = { Median, Average };

    public static bool IsAverage(string method) =>
        string.Equals(method, Average, StringComparison.OrdinalIgnoreCase);
}

public static class IndexAlgorithms
{
    public const string Percent = "Percent";
    public const string Pips = "Pips";

    public static readonly string[] All = { Percent, Pips };

    public static bool IsPips(string algorithm) =>
        string.Equals(algorithm, Pips, StringComparison.OrdinalIgnoreCase);
}

public static class IndicatorUnits
{
    public const string Minutes = "Minutes";
    public const string Hours = "Hours";
    public const string Days = "Days";

    public static readonly string[] All = { Minutes, Hours, Days };

    public static int BarsPerUnit(string unit) =>
        string.Equals(unit, Hours, StringComparison.OrdinalIgnoreCase) ? 60 :
        string.Equals(unit, Days, StringComparison.OrdinalIgnoreCase) ? 1440 :
        1;
}

public static class IndicatorPalette
{
    public static readonly int[] Colors =
    {
        unchecked((int)0xFFE53935), unchecked((int)0xFFD81B60),
        unchecked((int)0xFF8E24AA), unchecked((int)0xFF5E35B1),
        unchecked((int)0xFF3949AB), unchecked((int)0xFF1E88E5),
        unchecked((int)0xFF039BE5), unchecked((int)0xFF00ACC1),
        unchecked((int)0xFF00897B), unchecked((int)0xFF43A047),
        unchecked((int)0xFF7CB342), unchecked((int)0xFFC0CA33),
        unchecked((int)0xFFFDD835), unchecked((int)0xFFFFB300),
        unchecked((int)0xFFFB8C00), unchecked((int)0xFFF4511E),
        unchecked((int)0xFF6D4C41), unchecked((int)0xFF757575),
        unchecked((int)0xFF546E7A), unchecked((int)0xFFAD1457),
        unchecked((int)0xFF4A148C), unchecked((int)0xFF283593),
        unchecked((int)0xFF1565C0), unchecked((int)0xFF0277BD),
        unchecked((int)0xFF00838F), unchecked((int)0xFF2E7D32),
        unchecked((int)0xFF9E9D24), unchecked((int)0xFFF9A825),
        unchecked((int)0xFFEF6C00), unchecked((int)0xFFD84315),
        unchecked((int)0xFF4E342E), unchecked((int)0xFF37474F),
    };
}
