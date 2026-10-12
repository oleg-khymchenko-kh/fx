using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FXViewer.Chart;
using FXViewer.Compute;
using FXViewer.Storage;

namespace FXViewer;

public sealed class PatternCellConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Convert.ToDouble(value, CultureInfo.InvariantCulture)
            .ToString(parameter as string ?? "F1", CultureInfo.InvariantCulture);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        double.TryParse((value as string ?? "").Trim(),
            NumberStyles.Float & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture,
            out double d) && double.IsFinite(d)
            ? d
            : DependencyProperty.UnsetValue;
}

public partial class SearchParamsWindow : Window
{
    private const string MinutesLabel = "Minutes";
    private const string HoursLabel = "Hours";

    public sealed class PatternRow
    {
        public double Pips { get; set; }
        public double Minutes { get; set; }
        public double PipsUp { get; set; }
        public double PipsDown { get; set; }
        public double MinLeft { get; set; }
        public double MinRight { get; set; }
        public bool Zoom { get; set; }
        public long SourceUnix { get; set; }
    }

    public sealed class RequiredRow : INotifyPropertyChanged
    {
        private string _use = UseRequired;
        private string _seq = "";

        public string Level { get; set; } = "";

        public string Use
        {
            get => _use;
            set
            {
                if (_use == value) return;
                _use = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Use)));
            }
        }

        public double TolUp { get; set; }
        public double TolDown { get; set; }

        public string Seq
        {
            get => _seq;
            set
            {
                if (_seq == value) return;
                _seq = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Seq)));
            }
        }

        public double MinLeft { get; set; }
        public double MinRight { get; set; }
        public long TimeUnix { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public const string UseRequired = "Required";
    public const string UsePreferred = "Preferred";
    public const string UseOff = "Off";
    private static readonly string[] UseOptions = { UseRequired, UsePreferred, UseOff };

    private readonly List<CheckBox> _targetBoxes = new();
    private readonly List<CheckBox> _zigZagBoxes = new();
    private readonly List<CheckBox> _levels2Boxes = new();
    private readonly IReadOnlyList<ZigZagFindTarget> _zigZagTargets;
    private readonly IReadOnlyList<Levels2FindTarget> _levels2Targets;
    private readonly long _fragStartUnix;
    private readonly long _fragEndUnix;
    private readonly ObservableCollection<PatternRow> _rows = new();
    private readonly ObservableCollection<RequiredRow> _requiredRows = new();
    private readonly List<Level2> _templateLevels = new();
    private readonly List<Level2> _extraLevels = new();
    private readonly List<Level2> _shownLevels = new();
    private readonly List<Level2> _extremes = new();
    private string _requiredSource = "";
    private bool _requiredFilling;
    private bool _requiredEdited;
    private bool _seqEdited;
    private readonly int _seedSmoothMinutes;
    private readonly int _seedStepMinutes;
    private double _defaultPipTol = DefaultPipTolerance;
    private double _defaultMinTol = 30;
    private string _patternSource = "";
    private bool _ready;
    private bool _prefilling;
    private bool _rowsEdited;

    private const double DefaultPipTolerance = 5;
    private const string TableModeLabel = "Table";
    private const string ChartModeLabel = "Chart";

    public SearchParams Params { get; private set; } = new(
        IndicatorSymbol.DefaultFindSmoothMinutes,
        IndicatorSymbol.DefaultFindZoomPercent,
        IndicatorSymbol.DefaultFindStepMinutes);

    public List<string> Targets { get; private set; } = new();

    public string SearchType { get; private set; } = FindSearchTypes.Similarity;

    public List<string> ZigZagTargetNames { get; private set; } = new();

    public string PatternZigZagName { get; private set; } = "";

    public List<ZigZagPatternPoint> Pattern { get; private set; } = new();

    public long AnchorUnix { get; private set; }

    public List<string> Levels2TargetNames { get; private set; } = new();

    public string TemplateLevels2Name { get; private set; } = "";

    public int TolerancePips { get; private set; } = IndicatorSymbol.DefaultFindLevelTolerancePips;

    public List<Levels2LevelRule> LevelRules { get; private set; } = new();

    public int RequiredMinLeftPips { get; private set; } = IndicatorSymbol.DefaultFindLevels2ArmPips;

    public int RequiredMinRightPips { get; private set; } = IndicatorSymbol.DefaultFindLevels2ArmPips;

    public bool RequiredPickOnChart { get; private set; }

    public SearchParamsWindow(string indicatorName, string fragmentText, IReadOnlyList<string> pairs,
        IReadOnlyList<ZigZagFindTarget> zigZagTargets, IReadOnlyList<Levels2FindTarget> levels2Targets,
        long fragStartUnix, long fragEndUnix, IndicatorSymbol ind)
    {
        InitializeComponent();
        _zigZagTargets = zigZagTargets;
        _levels2Targets = levels2Targets;
        _fragStartUnix = fragStartUnix;
        _fragEndUnix = fragEndUnix;
        _seedSmoothMinutes = Math.Max(0, ind.FindSmoothMinutes);
        _seedStepMinutes = ind.FindStepMinutes;
        Title = "Search parameters - " + indicatorName;
        FragmentText.Text = fragmentText;
        SmoothUnitBox.Items.Add(MinutesLabel);
        SmoothUnitBox.Items.Add(HoursLabel);
        int smooth = _seedSmoothMinutes;
        if (smooth >= 60 && smooth % 60 == 0)
        {
            SmoothBox.Text = (smooth / 60).ToString(CultureInfo.InvariantCulture);
            SmoothUnitBox.SelectedItem = HoursLabel;
        }
        else
        {
            SmoothBox.Text = smooth.ToString(CultureInfo.InvariantCulture);
            SmoothUnitBox.SelectedItem = MinutesLabel;
        }
        ZoomBox.Text = ind.FindZoomPercent.ToString(CultureInfo.InvariantCulture);
        StepBox.Text = ind.FindStepMinutes.ToString(CultureInfo.InvariantCulture);
        ToleranceBox.Text = Math.Max(1, ind.FindLevelTolerancePips).ToString(CultureInfo.InvariantCulture);
        RequiredLeftBox.Text = Math.Max(0, ind.FindLevels2MinLeftPips).ToString(CultureInfo.InvariantCulture);
        RequiredRightBox.Text = Math.Max(0, ind.FindLevels2MinRightPips).ToString(CultureInfo.InvariantCulture);
        ZoomBox.TextChanged += (_, _) => UpdateZoomHint();
        UpdateZoomHint();
        var selected = new HashSet<string>(
            ind.EffectiveFindTargets().Select(IndicatorSymbol.NameKey));
        foreach (var pair in pairs)
        {
            var box = new CheckBox
            {
                Content = pair,
                Margin = new Thickness(0, 2, 12, 2),
                IsChecked = selected.Contains(IndicatorSymbol.NameKey(pair)),
                Tag = pair,
            };
            _targetBoxes.Add(box);
            TargetsPanel.Children.Add(box);
        }
        PointsGrid.ItemsSource = _rows;
        PointsGrid.RowEditEnding += (_, e) =>
        {
            if (e.EditAction == DataGridEditAction.Commit) MarkRowsEdited();
        };
        _rows.CollectionChanged += (_, _) => MarkRowsEdited();
        RequiredGrid.ItemsSource = _requiredRows;
        RequiredModeBox.Items.Add(TableModeLabel);
        RequiredModeBox.Items.Add(ChartModeLabel);
        RequiredModeBox.SelectedItem = ind.FindLevels2PickOnChart ? ChartModeLabel : TableModeLabel;
        RequiredPicker.LevelClicked += OnPickerLevelClicked;
        RequiredPicker.LevelAdded += OnPickerLevelAdded;
        UseColumn.ItemsSource = UseOptions;
        RequiredGrid.RowEditEnding += (_, e) =>
        {
            if (e.EditAction == DataGridEditAction.Commit && !_requiredFilling) _requiredEdited = true;
        };
        RequiredGrid.CellEditEnding += (_, e) =>
        {
            if (e.EditAction == DataGridEditAction.Commit && e.Column == SeqColumn) _seqEdited = true;
        };
        _requiredRows.CollectionChanged += (_, _) =>
        {
            if (!_requiredFilling) _requiredEdited = true;
            RequiredPicker.Refresh();
        };
        var storedZigZags = new HashSet<string>(
            ind.FindZigZagTargets.Select(IndicatorSymbol.NameKey));
        bool anyStored = zigZagTargets.Any(
            t => storedZigZags.Contains(IndicatorSymbol.NameKey(t.Name)));
        for (int i = 0; i < zigZagTargets.Count; i++)
        {
            var box = new CheckBox
            {
                Content = zigZagTargets[i].Name,
                Margin = new Thickness(0, 2, 12, 2),
                IsChecked = anyStored
                    ? storedZigZags.Contains(IndicatorSymbol.NameKey(zigZagTargets[i].Name))
                    : i == 0,
                Tag = i,
            };
            box.Checked += (_, _) => OnZigZagTargetsChanged();
            box.Unchecked += (_, _) => OnZigZagTargetsChanged();
            _zigZagBoxes.Add(box);
            ZigZagTargetsPanel.Children.Add(box);
        }
        var storedLevels2 = new HashSet<string>(
            ind.FindLevels2Targets.Select(IndicatorSymbol.NameKey));
        bool anyStoredLevels2 = levels2Targets.Any(
            t => storedLevels2.Contains(IndicatorSymbol.NameKey(t.Name)));
        int templateIndex = -1;
        for (int i = 0; i < levels2Targets.Count; i++)
        {
            string name = levels2Targets[i].Name;
            bool stored = storedLevels2.Contains(IndicatorSymbol.NameKey(name));
            var box = new CheckBox
            {
                Content = name,
                Margin = new Thickness(0, 2, 12, 2),
                IsChecked = anyStoredLevels2 ? stored : i == 0,
                Tag = i,
            };
            _levels2Boxes.Add(box);
            Levels2TargetsPanel.Children.Add(box);
            Levels2TemplateBox.Items.Add(name);
            if (templateIndex < 0 && IndicatorSymbol.NameKey(name)
                    == IndicatorSymbol.NameKey(ind.FindLevels2Template))
                templateIndex = i;
        }
        if (templateIndex < 0)
            templateIndex = _levels2Boxes.FindIndex(b => b.IsChecked == true);
        if (levels2Targets.Count > 0)
            Levels2TemplateBox.SelectedIndex = Math.Max(0, templateIndex);
        SearchTypeBox.Items.Add(new ComboBoxItem { Content = FindSearchTypes.Similarity });
        SearchTypeBox.Items.Add(new ComboBoxItem
        {
            Content = FindSearchTypes.ZigZag,
            IsEnabled = zigZagTargets.Count > 0,
        });
        SearchTypeBox.Items.Add(new ComboBoxItem
        {
            Content = FindSearchTypes.Levels2,
            IsEnabled = levels2Targets.Count > 0,
        });
        bool zig = FindSearchTypes.IsZigZag(ind.FindSearchType) && zigZagTargets.Count > 0;
        bool lv2 = FindSearchTypes.IsLevels2(ind.FindSearchType) && levels2Targets.Count > 0;
        SearchTypeBox.SelectedIndex = zig ? 1 : lv2 ? 2 : 0;
        _ready = true;
        PrefillPoints();
        LoadTemplateLevels();
        FillRequiredRows();
        UpdateMode();
    }

    private bool IsChartPickMode => (RequiredModeBox.SelectedItem as string) == ChartModeLabel;

    private void RequiredModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        RequiredGrid.CommitEdit(DataGridEditingUnit.Row, true);
        UpdateMode();
    }

    private void RequiredDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        RequiredGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = RequiredGrid.SelectedItems.OfType<RequiredRow>().ToList();
        if (selected.Count == 0 && RequiredGrid.CurrentItem is RequiredRow current) selected.Add(current);
        foreach (var row in selected) _requiredRows.Remove(row);
    }

    private void LoadTemplateLevels()
    {
        _templateLevels.Clear();
        _extraLevels.Clear();
        _shownLevels.Clear();
        _extremes.Clear();
        var target = SelectedTemplate();
        if (target?.TemplateMinutes == null)
        {
            RequiredPicker.Clear();
            return;
        }
        Levels2Search.TemplateLevels(target, target.TemplateMinutes, _fragStartUnix, _fragEndUnix,
            _templateLevels);
        Levels2Search.TemplateExtremes(target, target.TemplateMinutes, _fragStartUnix, _fragEndUnix, _extremes);
        _shownLevels.AddRange(_templateLevels);
        RequiredPicker.Show(target.TemplateMinutes, _shownLevels, _extremes, _fragStartUnix, _fragEndUnix,
            level => UseOf(RowOfLevel(target, level)),
            level => LevelsPickerView.LevelLabel(LevelText(target, level.Value), level),
            level => RowOfLevel(target, level)?.Seq?.Trim() ?? "");
    }

    private static LevelUse UseOf(RequiredRow? row) => row?.Use switch
    {
        UseRequired => LevelUse.Required,
        UsePreferred => LevelUse.Preferred,
        _ => LevelUse.Off,
    };

    private static long LevelTime(Levels2FindTarget target, Level2 level) =>
        target.TemplateMinutes is { } minutes && level.Index >= 0 && level.Index < minutes.Count
            ? minutes[level.Index].MinuteUnixSeconds
            : 0;

    private RequiredRow? RowOfLevel(Levels2FindTarget target, Level2 level)
    {
        long time = LevelTime(target, level);
        RequiredRow? byPrice = null;
        foreach (var row in _requiredRows)
        {
            if (!TryLevelPoints(target, row.Level, out double points)
                || Math.Abs(points - level.Value) >= DensityProfile.PipPoints / 2.0)
                continue;
            if (row.TimeUnix == time) return row;
            if (row.TimeUnix == 0) byPrice ??= row;
        }
        return byPrice;
    }

    private void OnPickerLevelClicked(Level2 level)
    {
        var target = SelectedTemplate();
        if (target == null) return;
        var row = RowOfLevel(target, level);
        if (row == null)
        {
            AddLevelRow(target, level);
            return;
        }
        row.Use = row.Use switch
        {
            UseRequired => UsePreferred,
            UsePreferred => UseOff,
            _ => UseRequired,
        };
        RequiredPicker.Refresh();
    }

    private void OnPickerLevelAdded(Level2 level)
    {
        var target = SelectedTemplate();
        if (target == null) return;
        if (!_shownLevels.Any(l => l.Index == level.Index && l.Top == level.Top))
        {
            _extraLevels.Add(level);
            _shownLevels.Add(level);
        }
        var row = RowOfLevel(target, level);
        if (row == null) AddLevelRow(target, level);
        else row.Use = UseRequired;
        RequiredPicker.Refresh();
    }

    private void AddLevelRow(Levels2FindTarget target, Level2 level)
    {
        var row = NewLevelRow(target, level, ReadArmPips(RequiredLeftBox), ReadArmPips(RequiredRightBox), true);
        int at = 0;
        while (at < _requiredRows.Count && _requiredRows[at].TimeUnix != 0
            && _requiredRows[at].TimeUnix <= row.TimeUnix)
            at++;
        _requiredRows.Insert(at, row);
        if (!_seqEdited) Renumber();
    }

    private RequiredRow NewLevelRow(Levels2FindTarget target, Level2 level, int left, int right, bool fitArms)
    {
        double tol = ReadTolerance();
        double pip = DensityProfile.PipPoints;
        return new RequiredRow
        {
            Level = LevelText(target, level.Value),
            Use = UseRequired,
            TolUp = tol,
            TolDown = tol,
            MinLeft = fitArms ? Math.Min(left, Math.Floor(level.ApproachPoints / pip)) : left,
            MinRight = fitArms ? Math.Min(right, Math.Floor(level.ReversalPoints / pip)) : right,
            TimeUnix = LevelTime(target, level),
        };
    }

    private void Renumber()
    {
        int seq = 0;
        foreach (var row in _requiredRows.Where(r => r.TimeUnix != 0).OrderBy(r => r.TimeUnix))
            row.Seq = (++seq).ToString(CultureInfo.InvariantCulture);
    }

    private static int ReadArmPips(TextBox box) =>
        TryReadInt(box, out int value) && value >= 0 ? value : IndicatorSymbol.DefaultFindLevels2ArmPips;

    private double ReadTolerance() =>
        TryReadInt(ToleranceBox, out int value) && value >= 0
            ? value
            : IndicatorSymbol.DefaultFindLevelTolerancePips;

    private Levels2FindTarget? SelectedTemplate()
    {
        int i = Levels2TemplateBox.SelectedIndex;
        return i >= 0 && i < _levels2Targets.Count ? _levels2Targets[i] : null;
    }

    private void Levels2TemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        RequiredGrid.CommitEdit(DataGridEditingUnit.Row, true);
        string name = SelectedTemplate()?.Name ?? "";
        if (name == _requiredSource) return;
        LoadTemplateLevels();
        if (_requiredEdited && _requiredRows.Count > 0)
        {
            _requiredSource = name;
            return;
        }
        FillRequiredRows();
    }

    private void RequiredFillBtn_Click(object sender, RoutedEventArgs e)
    {
        RequiredGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!TryReadRequiredThresholds(out _, out _)) return;
        FillRequiredRows();
    }

    private bool TryReadRequiredThresholds(out int left, out int right)
    {
        right = 0;
        if (!TryReadInt(RequiredLeftBox, out left) || left < 0 || left > 10000)
        {
            Warn("Min left must be a whole number of pips between 0 and 10000.");
            return false;
        }
        if (!TryReadInt(RequiredRightBox, out right) || right < 0 || right > 10000)
        {
            Warn("Min right must be a whole number of pips between 0 and 10000.");
            return false;
        }
        return true;
    }

    private void FillRequiredRows()
    {
        _requiredFilling = true;
        try
        {
            _requiredRows.Clear();
            var target = SelectedTemplate();
            _requiredSource = target?.Name ?? "";
            if (target?.TemplateMinutes == null) return;
            int left = ReadArmPips(RequiredLeftBox);
            int right = ReadArmPips(RequiredRightBox);
            double leftPoints = left * (double)DensityProfile.PipPoints;
            double rightPoints = right * (double)DensityProfile.PipPoints;
            foreach (var level in _templateLevels.OrderBy(l => l.Index).ThenByDescending(l => l.Value))
            {
                if (level.ApproachPoints < leftPoints || level.ReversalPoints < rightPoints) continue;
                _requiredRows.Add(NewLevelRow(target, level, left, right, false));
            }
            Renumber();
        }
        finally
        {
            _requiredFilling = false;
            _requiredEdited = false;
            _seqEdited = false;
            RequiredPicker.Refresh();
        }
    }

    private static string LevelText(Levels2FindTarget target, double displayPoints)
    {
        double raw = target.Transform?.ToRaw(displayPoints) ?? displayPoints;
        return (raw * target.PriceMul / 100000.0).ToString("F" + target.PriceDigits, CultureInfo.InvariantCulture);
    }

    private static bool TryLevelPoints(Levels2FindTarget target, string text, out double displayPoints)
    {
        displayPoints = 0;
        if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float & ~NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out double price) || !double.IsFinite(price))
            return false;
        double raw = price * 100000.0 / target.PriceMul;
        displayPoints = target.Transform?.ToDisplay(raw) ?? raw;
        return true;
    }

    private void RequiredGrid_InitializingNewItem(object sender, InitializingNewItemEventArgs e)
    {
        if (e.NewItem is not RequiredRow row) return;
        row.MinLeft = ReadArmPips(RequiredLeftBox);
        row.MinRight = ReadArmPips(RequiredRightBox);
        row.TolUp = ReadTolerance();
        row.TolDown = row.TolUp;
    }

    private bool IsZigZagMode => SearchTypeBox.SelectedIndex == 1;

    private bool IsLevels2Mode => SearchTypeBox.SelectedIndex == 2;

    private ZigZagFindTarget? FirstCheckedZigZag()
    {
        foreach (var box in _zigZagBoxes)
            if (box.IsChecked == true)
                return _zigZagTargets[(int)box.Tag!];
        return null;
    }

    private void SearchTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateMode();
    }

    private void MarkRowsEdited()
    {
        if (!_prefilling) _rowsEdited = true;
    }

    private void OnZigZagTargetsChanged()
    {
        if (!_ready) return;
        PointsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        string name = FirstCheckedZigZag()?.Name ?? "";
        if (name == _patternSource) return;
        if (_rowsEdited && _rows.Count > 0) return;
        PrefillPoints();
    }

    private void UpdateMode()
    {
        bool zig = IsZigZagMode;
        bool lv2 = IsLevels2Mode;
        bool plain = !zig && !lv2;
        TargetsLabel.Text = lv2 ? "Search in" : "Target symbols";
        Levels2TemplateLabel.Visibility = lv2 ? Visibility.Visible : Visibility.Collapsed;
        Levels2TemplatePanel.Visibility = lv2 ? Visibility.Visible : Visibility.Collapsed;
        TargetsBorder.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
        ZigZagTargetsBorder.Visibility = zig ? Visibility.Visible : Visibility.Collapsed;
        Levels2TargetsBorder.Visibility = lv2 ? Visibility.Visible : Visibility.Collapsed;
        SmoothLabel.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
        SmoothPanel.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
        StepLabel.Visibility = zig ? Visibility.Collapsed : Visibility.Visible;
        StepPanel.Visibility = zig ? Visibility.Collapsed : Visibility.Visible;
        ToleranceLabel.Visibility = lv2 ? Visibility.Visible : Visibility.Collapsed;
        TolerancePanel.Visibility = lv2 ? Visibility.Visible : Visibility.Collapsed;
        PointsLabel.Visibility = zig ? Visibility.Visible : Visibility.Collapsed;
        PointsGrid.Visibility = zig ? Visibility.Visible : Visibility.Collapsed;
        RequiredLabel.Visibility = lv2 ? Visibility.Visible : Visibility.Collapsed;
        RequiredPanel.Visibility = lv2 ? Visibility.Visible : Visibility.Collapsed;
        bool chartPick = IsChartPickMode;
        RequiredGrid.Visibility = lv2 && !chartPick ? Visibility.Visible : Visibility.Collapsed;
        RequiredPicker.Visibility = lv2 && chartPick ? Visibility.Visible : Visibility.Collapsed;
        RequiredDeleteBtn.Visibility = chartPick ? Visibility.Collapsed : Visibility.Visible;
        if (chartPick) RequiredPicker.Refresh();
    }

    private void PrefillPoints()
    {
        _prefilling = true;
        try
        {
            FillRowsFromFirstChecked();
        }
        finally
        {
            _prefilling = false;
            _rowsEdited = false;
        }
    }

    private void FillRowsFromFirstChecked()
    {
        _rows.Clear();
        var target = FirstCheckedZigZag();
        _patternSource = target?.Name ?? "";
        if (target == null) return;
        var inSelection = target.Points
            .Where(p => p.UnixSeconds >= _fragStartUnix && p.UnixSeconds <= _fragEndUnix)
            .ToList();
        if (inSelection.Count == 0) return;
        var w = WeekendCompressor.Instance;
        var first = inSelection[0];
        long v0 = w.ToVirtual(first.UnixSeconds) / 60;
        var pips = new double[inSelection.Count];
        var minutes = new double[inSelection.Count];
        int minIdx = 0, maxIdx = 0;
        for (int i = 0; i < inSelection.Count; i++)
        {
            pips[i] = Math.Round(
                (inSelection[i].Value - first.Value) / (double)target.PipPoints, 1);
            minutes[i] = w.ToVirtual(inSelection[i].UnixSeconds) / 60 - v0;
            if (pips[i] < pips[minIdx]) minIdx = i;
            if (pips[i] > pips[maxIdx]) maxIdx = i;
        }
        _defaultPipTol = DefaultPipTolerance;
        _defaultMinTol = Math.Max(30, Math.Round(0.1 * minutes[^1]));
        for (int i = 0; i < inSelection.Count; i++)
            _rows.Add(new PatternRow
            {
                Pips = pips[i],
                Minutes = minutes[i],
                PipsUp = _defaultPipTol,
                PipsDown = _defaultPipTol,
                MinLeft = _defaultMinTol,
                MinRight = _defaultMinTol,
                Zoom = i == minIdx || i == maxIdx,
                SourceUnix = inSelection[i].UnixSeconds,
            });
    }

    private void PointsGrid_InitializingNewItem(object sender, InitializingNewItemEventArgs e)
    {
        if (e.NewItem is not PatternRow row) return;
        row.PipsUp = _defaultPipTol;
        row.PipsDown = _defaultPipTol;
        row.MinLeft = _defaultMinTol;
        row.MinRight = _defaultMinTol;
    }

    private void UpdateZoomHint()
    {
        if (ZoomHint == null) return;
        if (!TryReadInt(ZoomBox, out int percent) || percent < 0)
        {
            ZoomHint.Text = "";
            return;
        }
        double max = 1 + percent / 100.0;
        ZoomHint.Text = string.Format(CultureInfo.InvariantCulture, "{0:F2}..{1:F2}", 1 / max, max);
    }

    private static bool TryReadInt(TextBox box, out int value) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool RowIsFinite(PatternRow r) =>
        double.IsFinite(r.Pips) && double.IsFinite(r.Minutes)
        && double.IsFinite(r.PipsUp) && double.IsFinite(r.PipsDown)
        && double.IsFinite(r.MinLeft) && double.IsFinite(r.MinRight);

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadInt(ZoomBox, out int zoom) || zoom < 0 || zoom > 500)
        {
            Warn("Max zoom must be a whole number of percent between 0 and 500.");
            return;
        }
        if (IsZigZagMode)
        {
            if (!PointsGrid.CommitEdit(DataGridEditingUnit.Row, true))
            {
                Warn("Fix the invalid cell value first.");
                return;
            }
            var zigZagNames = _zigZagBoxes.Where(b => b.IsChecked == true)
                .Select(b => _zigZagTargets[(int)b.Tag!].Name)
                .ToList();
            if (zigZagNames.Count == 0)
            {
                Warn("Pick at least one ZigZag indicator.");
                return;
            }
            var rows = _rows.ToList();
            if (rows.Count < 2)
            {
                Warn("The pattern needs at least two points.");
                return;
            }
            var zoomRows = rows.Where(r => r.Zoom).ToList();
            if (zoomRows.Count != 2)
            {
                Warn("Mark exactly two Zoom points.");
                return;
            }
            if (zoomRows[0].Pips == zoomRows[1].Pips)
            {
                Warn("The two Zoom points must sit at different pip levels.");
                return;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                if (!RowIsFinite(rows[i]))
                {
                    Warn($"Point {i + 1}: values must be plain numbers.");
                    return;
                }
                if (rows[i].PipsUp < 0 || rows[i].PipsDown < 0
                    || rows[i].MinLeft < 0 || rows[i].MinRight < 0)
                {
                    Warn($"Point {i + 1}: tolerances must be 0 or more.");
                    return;
                }
                if (i > 0 && rows[i].Minutes < rows[i - 1].Minutes)
                {
                    Warn($"Point {i + 1}: minutes must not decrease.");
                    return;
                }
            }
            SearchType = FindSearchTypes.ZigZag;
            ZigZagTargetNames = zigZagNames;
            PatternZigZagName = _patternSource.Length > 0 ? _patternSource : zigZagNames[0];
            double basePips = rows[0].Pips;
            double baseMinutes = rows[0].Minutes;
            Pattern = rows
                .Select(r => new ZigZagPatternPoint(
                    r.Pips - basePips, r.Minutes - baseMinutes,
                    r.PipsUp, r.PipsDown, r.MinLeft, r.MinRight, r.Zoom))
                .ToList();
            AnchorUnix = rows[0].SourceUnix != 0 ? rows[0].SourceUnix : _fragStartUnix;
            Params = new SearchParams(_seedSmoothMinutes, zoom, _seedStepMinutes);
            DialogResult = true;
            return;
        }
        if (!TryReadInt(StepBox, out int stepMinutes) || stepMinutes < 1 || stepMinutes > 10080)
        {
            Warn("Step must be a whole number of minutes between 1 and 10080.");
            return;
        }
        if (IsLevels2Mode)
        {
            var levels2Names = _levels2Boxes.Where(b => b.IsChecked == true)
                .Select(b => _levels2Targets[(int)b.Tag!].Name)
                .ToList();
            if (levels2Names.Count == 0)
            {
                Warn("Pick at least one Levels2 indicator to search in.");
                return;
            }
            if (Levels2TemplateBox.SelectedItem is not string templateName)
            {
                Warn("Pick the template Levels2 indicator.");
                return;
            }
            if (!TryReadInt(ToleranceBox, out int tolerance) || tolerance < 1 || tolerance > 100)
            {
                Warn("Tolerance must be a whole number of pips between 1 and 100.");
                return;
            }
            if (!RequiredGrid.CommitEdit(DataGridEditingUnit.Row, true))
            {
                Warn("Fix the invalid cell value first.");
                return;
            }
            if (!TryReadRequiredThresholds(out int minLeft, out int minRight)) return;
            var template = SelectedTemplate();
            if (template == null)
            {
                Warn("Pick the template Levels2 indicator.");
                return;
            }
            var rules = new List<Levels2LevelRule>();
            for (int i = 0; i < _requiredRows.Count; i++)
            {
                var row = _requiredRows[i];
                if (row.Use == UseOff) continue;
                if (!TryLevelPoints(template, row.Level, out double points))
                {
                    Warn($"Level {i + 1}: the level must be a price like 1.17250.");
                    return;
                }
                if (!double.IsFinite(row.MinLeft) || !double.IsFinite(row.MinRight)
                    || row.MinLeft < 0 || row.MinRight < 0)
                {
                    Warn($"Level {i + 1}: minimum pips must be 0 or more.");
                    return;
                }
                if (!double.IsFinite(row.TolUp) || !double.IsFinite(row.TolDown)
                    || row.TolUp < 0 || row.TolDown < 0 || row.TolUp > 1000 || row.TolDown > 1000)
                {
                    Warn($"Level {i + 1}: tolerance up and down must be 0..1000 pips.");
                    return;
                }
                int seq = 0;
                string seqText = (row.Seq ?? "").Trim();
                if (seqText.Length > 0
                    && (!int.TryParse(seqText, NumberStyles.Integer, CultureInfo.InvariantCulture, out seq)
                        || seq < 1))
                {
                    Warn($"Level {i + 1}: sequence must be empty or a whole number 1 or more.");
                    return;
                }
                rules.Add(new Levels2LevelRule(points, row.Use != UsePreferred, row.TolUp, row.TolDown, seq,
                    row.MinLeft, row.MinRight));
            }
            SearchType = FindSearchTypes.Levels2;
            Levels2TargetNames = levels2Names;
            TemplateLevels2Name = templateName;
            TolerancePips = tolerance;
            LevelRules = rules;
            RequiredMinLeftPips = minLeft;
            RequiredMinRightPips = minRight;
            RequiredPickOnChart = IsChartPickMode;
            Params = new SearchParams(_seedSmoothMinutes, zoom, stepMinutes);
            DialogResult = true;
            return;
        }
        var targets = _targetBoxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag!).ToList();
        if (targets.Count == 0)
        {
            Warn("Pick at least one target symbol.");
            return;
        }
        if (!TryReadInt(SmoothBox, out int smooth) || smooth < 0)
        {
            Warn("Smoothing must be a whole number, 0 or more.");
            return;
        }
        if ((SmoothUnitBox.SelectedItem as string) == HoursLabel) smooth *= 60;
        SearchType = FindSearchTypes.Similarity;
        Targets = targets;
        Params = new SearchParams(smooth, zoom, stepMinutes);
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
}
