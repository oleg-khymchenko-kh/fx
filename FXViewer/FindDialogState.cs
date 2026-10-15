using FXViewer.Storage;

namespace FXViewer;

public sealed record FindPatternRow(
    double Pips, double Minutes, double PipsUp, double PipsDown, double MinLeft, double MinRight, bool Zoom,
    long SourceUnix);

public sealed record FindLevelRow(
    string Level, string Use, double TolUp, double TolDown, string Seq, double MinLeft, double MinRight,
    long TimeUnix);

public sealed class FindDialogState
{
    public long FragStartUnix { get; set; }
    public long FragEndUnix { get; set; }
    public string SearchType { get; set; } = FindSearchTypes.Similarity;
    public List<string> Targets { get; set; } = new();
    public List<string> ZigZagTargets { get; set; } = new();
    public List<string> Levels2Targets { get; set; } = new();
    public string Levels2Template { get; set; } = "";
    public string Smooth { get; set; } = "";
    public string SmoothUnit { get; set; } = "";
    public string Zoom { get; set; } = "";
    public string Step { get; set; } = "";
    public string Tolerance { get; set; } = "";
    public string Stretch { get; set; } = "";
    public string MinSpan { get; set; } = "";
    public string MinSpanUnit { get; set; } = "";
    public string LevelsMinLeft { get; set; } = "";
    public string LevelsMinRight { get; set; } = "";
    public bool PickOnChart { get; set; }
    public List<LevelStep> LeftSteps { get; set; } = new();
    public List<LevelStep> RightSteps { get; set; } = new();
    public List<FindPatternRow> Points { get; set; } = new();
    public string PointsSource { get; set; } = "";
    public bool PointsEdited { get; set; }
    public List<FindLevelRow> Levels { get; set; } = new();
    public bool LevelsEdited { get; set; }
    public bool SeqEdited { get; set; }

    public bool HasRange() => FragEndUnix > FragStartUnix;

    public void RenameZigZag(string from, string to)
    {
        for (int i = 0; i < ZigZagTargets.Count; i++)
            if (IndicatorSymbol.NameKey(ZigZagTargets[i]) == IndicatorSymbol.NameKey(from))
                ZigZagTargets[i] = to;
        if (IndicatorSymbol.NameKey(PointsSource) == IndicatorSymbol.NameKey(from)) PointsSource = to;
    }

    public FindDialogState Clone() => new()
    {
        FragStartUnix = FragStartUnix,
        FragEndUnix = FragEndUnix,
        SearchType = SearchType,
        Targets = new List<string>(Targets),
        ZigZagTargets = new List<string>(ZigZagTargets),
        Levels2Targets = new List<string>(Levels2Targets),
        Levels2Template = Levels2Template,
        Smooth = Smooth,
        SmoothUnit = SmoothUnit,
        Zoom = Zoom,
        Step = Step,
        Tolerance = Tolerance,
        Stretch = Stretch,
        MinSpan = MinSpan,
        MinSpanUnit = MinSpanUnit,
        LevelsMinLeft = LevelsMinLeft,
        LevelsMinRight = LevelsMinRight,
        PickOnChart = PickOnChart,
        LeftSteps = LevelStep.CopyOf(LeftSteps),
        RightSteps = LevelStep.CopyOf(RightSteps),
        Points = new List<FindPatternRow>(Points),
        PointsSource = PointsSource,
        PointsEdited = PointsEdited,
        Levels = new List<FindLevelRow>(Levels),
        LevelsEdited = LevelsEdited,
        SeqEdited = SeqEdited,
    };
}
