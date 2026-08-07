using System.Collections.ObjectModel;
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

    private readonly List<CheckBox> _targetBoxes = new();
    private readonly List<CheckBox> _zigZagBoxes = new();
    private readonly IReadOnlyList<ZigZagFindTarget> _zigZagTargets;
    private readonly long _fragStartUnix;
    private readonly long _fragEndUnix;
    private readonly ObservableCollection<PatternRow> _rows = new();
    private readonly int _seedSmoothMinutes;
    private readonly int _seedStepMinutes;
    private double _defaultPipTol = DefaultPipTolerance;
    private double _defaultMinTol = 30;
    private string _patternSource = "";
    private bool _ready;
    private bool _prefilling;
    private bool _rowsEdited;

    private const double DefaultPipTolerance = 5;

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

    public SearchParamsWindow(string indicatorName, string fragmentText, IReadOnlyList<string> pairs,
        IReadOnlyList<ZigZagFindTarget> zigZagTargets, long fragStartUnix, long fragEndUnix,
        IndicatorSymbol ind)
    {
        InitializeComponent();
        _zigZagTargets = zigZagTargets;
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
        SearchTypeBox.Items.Add(new ComboBoxItem { Content = FindSearchTypes.Similarity });
        SearchTypeBox.Items.Add(new ComboBoxItem
        {
            Content = FindSearchTypes.ZigZag,
            IsEnabled = zigZagTargets.Count > 0,
        });
        bool zig = FindSearchTypes.IsZigZag(ind.FindSearchType) && zigZagTargets.Count > 0;
        SearchTypeBox.SelectedIndex = zig ? 1 : 0;
        _ready = true;
        PrefillPoints();
        UpdateMode();
    }

    private bool IsZigZagMode => SearchTypeBox.SelectedIndex == 1;

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
        TargetsBorder.Visibility = zig ? Visibility.Collapsed : Visibility.Visible;
        ZigZagTargetsBorder.Visibility = zig ? Visibility.Visible : Visibility.Collapsed;
        SmoothLabel.Visibility = zig ? Visibility.Collapsed : Visibility.Visible;
        SmoothPanel.Visibility = zig ? Visibility.Collapsed : Visibility.Visible;
        StepLabel.Visibility = zig ? Visibility.Collapsed : Visibility.Visible;
        StepPanel.Visibility = zig ? Visibility.Collapsed : Visibility.Visible;
        PointsLabel.Visibility = zig ? Visibility.Visible : Visibility.Collapsed;
        PointsGrid.Visibility = zig ? Visibility.Visible : Visibility.Collapsed;
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
        if (!TryReadInt(StepBox, out int step) || step < 1 || step > 10080)
        {
            Warn("Step must be a whole number of minutes between 1 and 10080.");
            return;
        }
        SearchType = FindSearchTypes.Similarity;
        Targets = targets;
        Params = new SearchParams(smooth, zoom, step);
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
}
