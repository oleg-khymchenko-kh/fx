using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FXViewer.Compute;

namespace FXViewer;

public partial class SymbolEditorWindow : Window
{
    private const double SwatchSize = 24;
    private const string PastLabel = "Past";
    private const string FutureLabel = "Future";
    private const string SameAsSourceLabel = "same as source";
    private const string TimeFormat = "hh\\:mm";
    private const int MaxLevelWindowMinutes = 10_000_000;
    private const int MaxLevelPixels = 500;
    private const int MaxBandCount = 200;
    private const int MaxBandWindowMinutes = 10_000_000;

    private static readonly string[] TimeInputFormats = { "hh\\:mm", "h\\:mm" };

    private readonly IndicatorSymbol? _editing;
    private readonly HashSet<string> _takenNames;
    private readonly Func<IndicatorSymbol, IndicatorSymbol?, IProgress<double>, CancellationToken, Task> _apply;
    private readonly Func<IProgress<double>, CancellationToken, Task>? _refresh;
    private readonly List<Border> _swatches = new();
    private readonly List<Border> _sellSwatches = new();
    private readonly List<CheckBox> _pairBoxes = new();
    private readonly TextBox[] _densityPeriodBoxes;
    private readonly ComboBox[] _densityUnitBoxes;
    private readonly TextBox[] _densityPercentBoxes;
    private int _selectedColor;
    private int _selectedSellColor;
    private CancellationTokenSource? _cts;
    private bool _busy;

    private readonly IReadOnlyList<string> _allSources;
    private readonly IReadOnlyList<string> _indexSources;
    private readonly IReadOnlyList<string> _zigzagSources;
    private readonly string _wantedSource;

    public SymbolEditorWindow(
        IReadOnlyList<string> sources,
        IReadOnlyCollection<string> existingNames,
        IndicatorSymbol? editing,
        string? sourcePrefill,
        Func<IndicatorSymbol, IndicatorSymbol?, IProgress<double>, CancellationToken, Task> apply,
        Func<IProgress<double>, CancellationToken, Task>? refresh = null,
        IReadOnlyList<string>? indexPairs = null,
        IReadOnlyList<string>? indexSources = null,
        IReadOnlyList<string>? zigzagSources = null)
    {
        InitializeComponent();
        _editing = editing;
        _apply = apply;
        _refresh = refresh;
        RefreshBtn.Visibility = refresh == null ? Visibility.Collapsed : Visibility.Visible;
        _takenNames = new HashSet<string>();
        foreach (var n in existingNames)
        {
            if (editing != null && IndicatorSymbol.NameKey(n) == IndicatorSymbol.NameKey(editing.Name)) continue;
            _takenNames.Add(IndicatorSymbol.NameKey(n));
        }

        Title = editing == null ? "Add symbol" : "Edit symbol";
        _allSources = sources;
        _indexSources = indexSources ?? Array.Empty<string>();
        _zigzagSources = zigzagSources ?? Array.Empty<string>();
        _wantedSource = editing?.Source ?? sourcePrefill ?? "";
        foreach (var s in sources) SourceBox.Items.Add(s);
        foreach (var p in indexPairs ?? Array.Empty<string>()) CurrencyPairBox.Items.Add(p);
        TargetBox.Items.Add(SameAsSourceLabel);
        foreach (var p in indexPairs ?? Array.Empty<string>()) TargetBox.Items.Add(p);
        foreach (var t in IndicatorTypes.All) TypeBox.Items.Add(IndicatorTypes.Label(t));
        foreach (var u in IndicatorUnits.All) UnitBox.Items.Add(u);
        foreach (var u in IndicatorUnits.All) LevelUnitBox.Items.Add(u);
        _densityPeriodBoxes = new[]
        {
            Density1Box, Density2Box, Density3Box, Density4Box, Density5Box,
            Density6Box, Density7Box, Density8Box, Density9Box,
        };
        _densityUnitBoxes = new[]
        {
            Density1Unit, Density2Unit, Density3Unit, Density4Unit, Density5Unit,
            Density6Unit, Density7Unit, Density8Unit, Density9Unit,
        };
        _densityPercentBoxes = new[]
        {
            Density1Pct, Density2Pct, Density3Pct, Density4Pct, Density5Pct,
            Density6Pct, Density7Pct, Density8Pct, Density9Pct, Density0Pct,
        };
        foreach (var box in _densityUnitBoxes)
            foreach (var u in IndicatorUnits.All)
                box.Items.Add(u);
        DirectionBox.Items.Add(PastLabel);
        DirectionBox.Items.Add(FutureLabel);
        foreach (var m in BandModes.All) BandModeBox.Items.Add(m);
        foreach (var u in IndicatorUnits.All) BandUnitBox.Items.Add(u);
        BandCountBox.TextChanged += (_, _) => UpdateBandHint();
        BandPeriodBox.TextChanged += (_, _) => UpdateBandHint();
        BandUnitBox.SelectionChanged += (_, _) => UpdateBandHint();
        foreach (var m in IndexMethods.All) MethodBox.Items.Add(m);
        foreach (var a in IndexAlgorithms.All) AlgorithmBox.Items.Add(a);
        BuildPairBoxes(indexPairs ?? Array.Empty<string>(), editing);
        BuildSwatches();
        VolumeSplitBox.IsChecked = editing?.VolumeSplitSides ?? true;

        if (editing != null)
        {
            NameBox.Text = editing.Name;
            SourceBox.SelectedItem = sources.FirstOrDefault(
                s => IndicatorSymbol.NameKey(s) == IndicatorSymbol.NameKey(editing.Source));
            TypeBox.SelectedItem = IndicatorTypes.All
                .Where(t => string.Equals(t, editing.Type, StringComparison.OrdinalIgnoreCase))
                .Select(IndicatorTypes.Label)
                .FirstOrDefault() ?? IndicatorTypes.Label(IndicatorTypes.ZigZag);
            Limit1Box.Text = editing.Limit1Pips.ToString(CultureInfo.InvariantCulture);
            Limit2Box.Text = editing.Limit2Pips.ToString(CultureInfo.InvariantCulture);
            Limit2DelayBox.Text = editing.Limit2DelayMinutes.ToString(CultureInfo.InvariantCulture);
            PeriodBox.Text = editing.Period.ToString(CultureInfo.InvariantCulture);
            StopLossBox.Text = editing.StopLossPips.ToString(CultureInfo.InvariantCulture);
            TakeProfitBox.Text = editing.TakeProfitPips.ToString(CultureInfo.InvariantCulture);
            UnitBox.SelectedItem = IndicatorUnits.All.FirstOrDefault(
                u => string.Equals(u, editing.Unit, StringComparison.OrdinalIgnoreCase)) ?? IndicatorUnits.Minutes;
            DirectionBox.SelectedItem = editing.FromFuture ? FutureLabel : PastLabel;
            WeightedBox.IsChecked = editing.AverageWeighted;
            bool band = IndicatorTypes.IsAverageBand(editing.Type);
            TimeWindowBox.IsChecked =
                band ? IndicatorSymbol.DefaultAverageTimeWindow : editing.AverageTimeWindow;
            BandTimeWindowBox.IsChecked =
                band ? editing.AverageTimeWindow : IndicatorSymbol.DefaultAverageTimeWindow;
            BandCountBox.Text = (band && editing.BandCount > 0
                ? editing.BandCount
                : IndicatorSymbol.DefaultBandCount).ToString(CultureInfo.InvariantCulture);
            BandPeriodBox.Text = (band ? editing.Period : IndicatorSymbol.DefaultBandPeriod)
                .ToString(CultureInfo.InvariantCulture);
            BandUnitBox.SelectedItem = IndicatorUnits.All.FirstOrDefault(
                u => string.Equals(u, band ? editing.Unit : IndicatorSymbol.DefaultBandUnit,
                    StringComparison.OrdinalIgnoreCase)) ?? IndicatorSymbol.DefaultBandUnit;
            BandModeBox.SelectedItem = band ? BandModes.Effective(editing.BandMode) : BandModes.Max;
            bool levels = IndicatorTypes.IsLevels(editing.Type);
            LevelPeriodBox.Text = (levels ? editing.Period : IndicatorSymbol.DefaultLevelPeriod)
                .ToString(CultureInfo.InvariantCulture);
            LevelUnitBox.SelectedItem = IndicatorUnits.All.FirstOrDefault(
                u => string.Equals(u, levels ? editing.Unit : IndicatorSymbol.DefaultLevelUnit,
                    StringComparison.OrdinalIgnoreCase)) ?? IndicatorSymbol.DefaultLevelUnit;
            LevelLengthBox.Text = (editing.LevelLengthPx > 0
                ? editing.LevelLengthPx
                : IndicatorSymbol.DefaultLevelLengthPx).ToString(CultureInfo.InvariantCulture);
            LevelStepBox.Text = (editing.LevelStepPx > 0
                ? editing.LevelStepPx
                : IndicatorSymbol.DefaultLevelStepPx).ToString(CultureInfo.InvariantCulture);
            long shiftSource = editing.SourceTimeUnix;
            long shiftChart = editing.ChartTimeUnix;
            if (IndicatorTypes.IsShift(editing.Type)
                && ShiftedSymbol.AnchorsBroken(shiftSource, shiftChart))
                (shiftSource, shiftChart) = ShiftedSymbol.Rebase(
                    shiftSource, shiftChart, ShiftedSymbol.DefaultAnchorUnix());
            SetAnchor(SourceDate, SourceTimeBox, shiftSource, DefaultSourceTime);
            SetAnchor(ChartDate, ChartTimeBox, shiftChart, DefaultChartTime);
            FlipBox.IsChecked = editing.Flip;
            TargetBox.SelectedItem = TargetBox.Items.Cast<string>().FirstOrDefault(
                p => p != SameAsSourceLabel
                     && IndicatorSymbol.NameKey(p) == IndicatorSymbol.NameKey(editing.TargetSymbol))
                ?? SameAsSourceLabel;
            IndexMirrorBox.IsChecked = editing.Flip;
            SetAnchor(IndexStartDate, IndexStartTimeBox, editing.StartTimeUnix, DefaultSourceTime);
            SetOptionalAnchor(IndexEndDate, IndexEndTimeBox, editing.EndTimeUnix);
            MethodBox.SelectedItem = IndexMethods.All.FirstOrDefault(
                m => string.Equals(m, editing.IndexMethod, StringComparison.OrdinalIgnoreCase))
                ?? IndexMethods.Median;
            AlgorithmBox.SelectedItem = IndexAlgorithms.All.FirstOrDefault(
                a => string.Equals(a, editing.IndexAlgorithm, StringComparison.OrdinalIgnoreCase))
                ?? IndexAlgorithms.Percent;
            CurrencyPairBox.SelectedItem = CurrencyPairBox.Items.Cast<string>().FirstOrDefault(
                p => IndicatorSymbol.NameKey(p) == IndicatorSymbol.NameKey(editing.IndexPair));
            DealsFileBox.Text = editing.DealsFile;
            VolumeGroupBox.Text =
                editing.EffectiveVolumeGroupMinutes().ToString(CultureInfo.InvariantCulture);
            VolumeLockBox.IsChecked = editing.VolumeGroupLocked;
            for (int i = 0; i < IndicatorSymbol.DensityOptionCount; i++)
            {
                _densityPeriodBoxes[i].Text =
                    editing.DensityPeriodAt(i).ToString(CultureInfo.InvariantCulture);
                _densityUnitBoxes[i].SelectedItem = IndicatorUnits.All.FirstOrDefault(
                    u => string.Equals(u, editing.DensityUnitAt(i), StringComparison.OrdinalIgnoreCase))
                    ?? IndicatorUnits.Minutes;
            }
            for (int i = 0; i <= IndicatorSymbol.DensityAllOption; i++)
                _densityPercentBoxes[i].Text =
                    editing.DensityScalePercentAt(i).ToString(CultureInfo.InvariantCulture);
            DensityScaleBox.Text = editing.DensityScalePerPixel > 0
                ? editing.DensityScalePerPixel.ToString("0.####", CultureInfo.InvariantCulture)
                : "";
            SelectColor(editing.ColorArgb);
            SelectSellColor(editing.SellColorArgb);
        }
        else
        {
            SourceBox.SelectedItem = sourcePrefill != null
                ? sources.FirstOrDefault(s => IndicatorSymbol.NameKey(s) == IndicatorSymbol.NameKey(sourcePrefill))
                : null;
            TypeBox.SelectedItem = null;
            Limit1Box.Text = IndicatorSymbol.DefaultLimit1Pips.ToString(CultureInfo.InvariantCulture);
            Limit2Box.Text = IndicatorSymbol.DefaultLimit2Pips.ToString(CultureInfo.InvariantCulture);
            Limit2DelayBox.Text =
                IndicatorSymbol.DefaultLimit2DelayMinutes.ToString(CultureInfo.InvariantCulture);
            PeriodBox.Text = "20";
            StopLossBox.Text = IndicatorSymbol.DefaultStopLossPips.ToString(CultureInfo.InvariantCulture);
            TakeProfitBox.Text = IndicatorSymbol.DefaultTakeProfitPips.ToString(CultureInfo.InvariantCulture);
            UnitBox.SelectedItem = IndicatorUnits.Minutes;
            DirectionBox.SelectedItem = PastLabel;
            WeightedBox.IsChecked = false;
            TimeWindowBox.IsChecked = IndicatorSymbol.DefaultAverageTimeWindow;
            BandTimeWindowBox.IsChecked = IndicatorSymbol.DefaultAverageTimeWindow;
            BandCountBox.Text =
                IndicatorSymbol.DefaultBandCount.ToString(CultureInfo.InvariantCulture);
            BandPeriodBox.Text =
                IndicatorSymbol.DefaultBandPeriod.ToString(CultureInfo.InvariantCulture);
            BandUnitBox.SelectedItem = IndicatorSymbol.DefaultBandUnit;
            BandModeBox.SelectedItem = BandModes.Max;
            LevelPeriodBox.Text =
                IndicatorSymbol.DefaultLevelPeriod.ToString(CultureInfo.InvariantCulture);
            LevelUnitBox.SelectedItem = IndicatorSymbol.DefaultLevelUnit;
            LevelLengthBox.Text =
                IndicatorSymbol.DefaultLevelLengthPx.ToString(CultureInfo.InvariantCulture);
            LevelStepBox.Text =
                IndicatorSymbol.DefaultLevelStepPx.ToString(CultureInfo.InvariantCulture);
            SetAnchor(SourceDate, SourceTimeBox, 0, DefaultSourceTime);
            SetAnchor(ChartDate, ChartTimeBox, 0, DefaultChartTime);
            TargetBox.SelectedItem = SameAsSourceLabel;
            SetAnchor(IndexStartDate, IndexStartTimeBox, 0, DefaultSourceTime);
            SetOptionalAnchor(IndexEndDate, IndexEndTimeBox, 0);
            MethodBox.SelectedItem = IndexMethods.Median;
            AlgorithmBox.SelectedItem = IndexAlgorithms.Percent;
            VolumeGroupBox.Text = "1";
            for (int i = 0; i < IndicatorSymbol.DensityOptionCount; i++)
            {
                _densityPeriodBoxes[i].Text =
                    IndicatorSymbol.DefaultDensityPeriods[i].ToString(CultureInfo.InvariantCulture);
                _densityUnitBoxes[i].SelectedItem = IndicatorSymbol.DefaultDensityUnits[i];
            }
            foreach (var box in _densityPercentBoxes)
                box.Text = IndicatorSymbol.DefaultDensityScalePercent
                    .ToString(CultureInfo.InvariantCulture);
            DensityScaleBox.Text = "";
            SelectColor(IndicatorPalette.Colors[0]);
            SelectSellColor(IndicatorPalette.Colors[7]);
        }

        UpdateParamsVisibility();
        Loaded += (_, _) => NameBox.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy) Close();
        };
    }

    private void BuildPairBoxes(IReadOnlyList<string> pairs, IndicatorSymbol? editing)
    {
        var selected = new HashSet<string>(
            (editing != null && IndicatorTypes.IsIndex(editing.Type)
                ? DollarIndexSymbol.Effective(editing.IndexPairs)
                : DollarIndexSymbol.DefaultPairs)
            .Select(IndicatorSymbol.NameKey));
        foreach (var pair in pairs)
        {
            var box = new CheckBox
            {
                Content = pair,
                Margin = new Thickness(0, 2, 12, 2),
                IsChecked = selected.Contains(IndicatorSymbol.NameKey(pair)),
                Tag = pair,
            };
            _pairBoxes.Add(box);
            PairsPanel.Children.Add(box);
        }
    }

    private void BuildSwatches()
    {
        FillSwatches(ColorPanel, _swatches, SelectColor);
        FillSwatches(SellColorPanel, _sellSwatches, SelectSellColor);
    }

    private static void FillSwatches(UniformGrid panel, List<Border> swatches, Action<int> select)
    {
        foreach (var argb in IndicatorPalette.Colors)
        {
            var color = Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            var fill = new SolidColorBrush(color);
            fill.Freeze();
            var swatch = new Border
            {
                Width = SwatchSize,
                Height = SwatchSize,
                Margin = new Thickness(2),
                Background = fill,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = argb,
            };
            swatch.MouseLeftButtonDown += (_, _) => select(argb);
            swatches.Add(swatch);
            panel.Children.Add(swatch);
        }
    }

    private void SelectColor(int argb)
    {
        _selectedColor = argb;
        Highlight(_swatches, argb);
    }

    private void SelectSellColor(int argb)
    {
        _selectedSellColor = argb;
        Highlight(_sellSwatches, argb);
    }

    private static void Highlight(List<Border> swatches, int argb)
    {
        foreach (var swatch in swatches)
        {
            bool selected = (int)swatch.Tag! == argb;
            swatch.BorderBrush = selected ? Brushes.Black : Brushes.Transparent;
        }
    }

    private static DateTime DefaultChartTime =>
        new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime DefaultSourceTime => DefaultChartTime.AddYears(-1);

    private static void SetAnchor(DatePicker date, TextBox time, long unixSeconds, DateTime fallback)
    {
        var utc = unixSeconds == 0
            ? fallback
            : DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        date.SelectedDate = utc.Date;
        time.Text = utc.TimeOfDay.ToString(TimeFormat, CultureInfo.InvariantCulture);
    }

    private static bool TryReadAnchor(DatePicker date, TextBox time, out long unixSeconds)
    {
        unixSeconds = 0;
        if (date.SelectedDate is not { } day) return false;
        if (!TimeSpan.TryParseExact(time.Text.Trim(), TimeInputFormats, CultureInfo.InvariantCulture, out var tod))
            return false;
        if (tod < TimeSpan.Zero || tod >= TimeSpan.FromDays(1)) return false;
        var utc = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Utc).Add(tod);
        unixSeconds = new DateTimeOffset(utc).ToUnixTimeSeconds();
        return true;
    }

    private static void SetOptionalAnchor(DatePicker date, TextBox time, long unixSeconds)
    {
        if (unixSeconds == 0)
        {
            date.SelectedDate = null;
            time.Text = "";
            return;
        }
        SetAnchor(date, time, unixSeconds, DefaultChartTime);
    }

    private static bool TryReadOptionalAnchor(DatePicker date, TextBox time, out long unixSeconds)
    {
        unixSeconds = 0;
        if (date.SelectedDate == null && time.Text.Trim().Length == 0) return true;
        return TryReadAnchor(date, time, out unixSeconds);
    }

    private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateParamsVisibility();

    private void CurrencyPairBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateCurrencyHint();

    private void UpdateCurrencyHint()
    {
        if (CurrencyHint == null) return;
        CurrencyHint.Text = CurrencyPairBox.SelectedItem is string pair
            ? "= " + CurrencyIndexSymbol.CurrencyOf(pair) + " index"
            : "";
    }

    private string? SelectedType() =>
        TypeBox.SelectedItem is string label ? IndicatorTypes.FromLabel(label) : null;

    private void FillSources(string? type)
    {
        var wanted = type == null ? _allSources
            : IndicatorTypes.SourceIsIndex(type) ? _indexSources
            : IndicatorTypes.SourceIsZigZag(type) ? _zigzagSources
            : _allSources;
        if (SourceBox.Items.Count == wanted.Count
            && SourceBox.Items.Cast<string>().SequenceEqual(wanted))
            return;
        var keep = (SourceBox.SelectedItem as string) ?? _wantedSource;
        SourceBox.Items.Clear();
        foreach (var s in wanted) SourceBox.Items.Add(s);
        SourceBox.SelectedItem = wanted.FirstOrDefault(
            s => IndicatorSymbol.NameKey(s) == IndicatorSymbol.NameKey(keep));
    }

    private void UpdateParamsVisibility()
    {
        var type = SelectedType();
        if (SourceLabel != null && SourceBox != null)
        {
            var vis = type != null && !IndicatorTypes.NeedsSource(type)
                ? Visibility.Collapsed
                : Visibility.Visible;
            SourceLabel.Visibility = vis;
            SourceBox.Visibility = vis;
            FillSources(type);
        }
        if (ZigZagParams != null)
            ZigZagParams.Visibility =
                type == IndicatorTypes.ZigZag ? Visibility.Visible : Visibility.Collapsed;
        if (AverageParams != null)
            AverageParams.Visibility =
                type == IndicatorTypes.Average ? Visibility.Visible : Visibility.Collapsed;
        if (BandParams != null)
            BandParams.Visibility =
                type == IndicatorTypes.AverageBand ? Visibility.Visible : Visibility.Collapsed;
        if (ShiftParams != null)
            ShiftParams.Visibility =
                type == IndicatorTypes.Shift ? Visibility.Visible : Visibility.Collapsed;
        if (IndexParams != null)
            IndexParams.Visibility =
                type == IndicatorTypes.Index ? Visibility.Visible : Visibility.Collapsed;
        if (CurrencyParams != null)
            CurrencyParams.Visibility =
                type == IndicatorTypes.Currency ? Visibility.Visible : Visibility.Collapsed;
        if (EntryParams != null)
            EntryParams.Visibility =
                type == IndicatorTypes.EntryPoints ? Visibility.Visible : Visibility.Collapsed;
        if (LevelsParams != null)
            LevelsParams.Visibility =
                type == IndicatorTypes.Levels ? Visibility.Visible : Visibility.Collapsed;
        if (DealsParams != null)
            DealsParams.Visibility =
                type == IndicatorTypes.Deals ? Visibility.Visible : Visibility.Collapsed;
        if (VolumeParams != null)
            VolumeParams.Visibility =
                type == IndicatorTypes.Volume ? Visibility.Visible : Visibility.Collapsed;
        if (DensityParams != null)
            DensityParams.Visibility =
                type == IndicatorTypes.Density || type == IndicatorTypes.Volume
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        if (DensityScaleHint != null)
            DensityScaleHint.Text = type == IndicatorTypes.Volume
                ? "contracts per pixel"
                : "minutes per pixel";
        if (SellColorBlock != null && ColorLabel != null)
        {
            bool orderBook = type != null && IndicatorTypes.IsOrderBook(type);
            bool volume = type == IndicatorTypes.Volume;
            bool levels = type == IndicatorTypes.Levels;
            SellColorBlock.Visibility = orderBook || volume || levels
                ? Visibility.Visible
                : Visibility.Collapsed;
            ColorLabel.Text = orderBook ? "Buy color" : volume ? "Ask color"
                : levels ? "From below" : "Color";
            if (SellColorLabel != null)
                SellColorLabel.Text = volume ? "Bid color" : levels ? "From above" : "Sell color";
        }
        UpdateCurrencyHint();
        UpdateBandHint();
    }

    private void UpdateBandHint()
    {
        if (BandHint == null) return;
        string unit = ((BandUnitBox.SelectedItem as string) ?? IndicatorSymbol.DefaultBandUnit)
            .ToLowerInvariant();
        BandHint.Text = TryReadPositive(BandCountBox, out int count)
                        && TryReadPositive(BandPeriodBox, out int period)
            ? string.Create(CultureInfo.InvariantCulture,
                $"= {period} .. {(long)period * count} {unit}")
            : "";
    }

    private static bool TryReadPositive(TextBox box, out int value) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
        && value > 0;

    private IndicatorSymbol? BuildDefinition()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            Warn("Enter a name.");
            return null;
        }
        if (IndicatorSymbol.NameKey(name).Length == 0)
        {
            Warn("Name must contain letters or digits.");
            return null;
        }
        if (_takenNames.Contains(IndicatorSymbol.NameKey(name)))
        {
            Warn($"Name '{name}' is already used.");
            return null;
        }
        if (SelectedType() is not { } type)
        {
            Warn("Pick a type.");
            return null;
        }
        string source = "";
        if (IndicatorTypes.NeedsSource(type))
        {
            if (SourceBox.SelectedItem is not string picked)
            {
                Warn(IndicatorTypes.SourceIsIndex(type) && SourceBox.Items.Count == 0
                    ? "A currency index needs a USD Index symbol. Create one first."
                    : IndicatorTypes.SourceIsZigZag(type) && SourceBox.Items.Count == 0
                        ? "ZigZag levels need a ZigZag indicator. Create one first."
                        : "Pick a source symbol.");
                return null;
            }
            if (IndicatorSymbol.NameKey(picked) == IndicatorSymbol.NameKey(name))
            {
                Warn("Name must differ from the source symbol.");
                return null;
            }
            source = picked;
        }
        int limit1 = _editing?.Limit1Pips ?? IndicatorSymbol.DefaultLimit1Pips;
        int limit2 = _editing?.Limit2Pips ?? IndicatorSymbol.DefaultLimit2Pips;
        int limit2Delay = _editing?.Limit2DelayMinutes ?? IndicatorSymbol.DefaultLimit2DelayMinutes;
        int period = _editing?.Period ?? 20;
        string unit = _editing?.Unit ?? IndicatorUnits.Minutes;
        bool fromFuture = _editing?.FromFuture ?? false;
        bool averageWeighted = _editing?.AverageWeighted ?? false;
        bool averageTimeWindow =
            _editing?.AverageTimeWindow ?? IndicatorSymbol.DefaultAverageTimeWindow;
        int bandCount = _editing?.BandCount ?? IndicatorSymbol.DefaultBandCount;
        string bandMode = BandModes.Effective(_editing?.BandMode ?? BandModes.Max);
        long sourceTime = _editing?.SourceTimeUnix ?? 0;
        long chartTime = _editing?.ChartTimeUnix ?? 0;
        bool flip = _editing?.Flip ?? false;
        long startTime = _editing?.StartTimeUnix ?? 0;
        long endTime = _editing?.EndTimeUnix ?? 0;
        string indexMethod = _editing?.IndexMethod ?? IndexMethods.Median;
        string indexAlgorithm = _editing?.IndexAlgorithm ?? IndexAlgorithms.Percent;
        var indexPairs = new List<string>(_editing?.IndexPairs ?? new List<string>());
        string indexPair = _editing?.IndexPair ?? "";
        int stopLoss = _editing?.StopLossPips ?? IndicatorSymbol.DefaultStopLossPips;
        int takeProfit = _editing?.TakeProfitPips ?? IndicatorSymbol.DefaultTakeProfitPips;
        string dealsFile = _editing?.DealsFile ?? "";
        string targetSymbol = _editing?.TargetSymbol ?? "";
        var densityPeriods = new List<int>(_editing?.DensityPeriods ?? new List<int>());
        var densityUnits = new List<string>(_editing?.DensityUnits ?? new List<string>());
        var densityPercents = new List<int>(_editing?.DensityScalePercents ?? new List<int>());
        double densityPerPixel = _editing?.DensityScalePerPixel ?? 0;
        int volumeGroup = _editing?.EffectiveVolumeGroupMinutes() ?? 1;
        int volumeGroupWas = volumeGroup;
        double volumeBarScale = _editing?.VolumeBarScale ?? 1;
        double volumeBarUnit = _editing?.VolumeBarUnit ?? 0;
        bool volumeLocked = _editing?.VolumeGroupLocked ?? false;
        bool volumeSplit = _editing?.VolumeSplitSides ?? true;
        int levelLength = _editing?.LevelLengthPx ?? IndicatorSymbol.DefaultLevelLengthPx;
        int levelStep = _editing?.LevelStepPx ?? IndicatorSymbol.DefaultLevelStepPx;
        if (type == IndicatorTypes.ZigZag)
        {
            if (!int.TryParse(Limit1Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out limit1) || limit1 <= 0)
            {
                Warn("Limit 1 must be a positive whole number of pips.");
                return null;
            }
            if (!int.TryParse(Limit2Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out limit2) || limit2 <= 0)
            {
                Warn("Limit 2 must be a positive whole number of pips.");
                return null;
            }
            if (limit2 > limit1)
            {
                Warn("Limit 2 must not be bigger than Limit 1: a pullback of Limit 1 pips is already " +
                    "a reversal.");
                return null;
            }
            if (!int.TryParse(Limit2DelayBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out limit2Delay) || limit2Delay < 0)
            {
                Warn("Delay must be a whole number of minutes, zero or more.");
                return null;
            }
        }
        else if (type == IndicatorTypes.Average)
        {
            if (!int.TryParse(PeriodBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out period)
                || period <= 0)
            {
                Warn("Period must be a positive whole number.");
                return null;
            }
            unit = (UnitBox.SelectedItem as string) ?? IndicatorUnits.Minutes;
            fromFuture = (DirectionBox.SelectedItem as string) == FutureLabel;
            averageWeighted = WeightedBox.IsChecked == true;
            averageTimeWindow = TimeWindowBox.IsChecked == true;
        }
        else if (type == IndicatorTypes.AverageBand)
        {
            if (!int.TryParse(BandCountBox.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out bandCount)
                || bandCount <= 0 || bandCount > MaxBandCount)
            {
                Warn($"Count must be a whole number of averages, 1 to {MaxBandCount}.");
                return null;
            }
            if (!int.TryParse(BandPeriodBox.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out period) || period <= 0)
            {
                Warn("Step must be a positive whole number.");
                return null;
            }
            unit = (BandUnitBox.SelectedItem as string) ?? IndicatorSymbol.DefaultBandUnit;
            if ((long)period * IndicatorUnits.BarsPerUnit(unit) * bandCount > MaxBandWindowMinutes)
            {
                Warn("The longest average is too long, keep it under 10M minutes.");
                return null;
            }
            bandMode = BandModes.Effective((BandModeBox.SelectedItem as string) ?? BandModes.Max);
            averageTimeWindow = BandTimeWindowBox.IsChecked == true;
        }
        else if (type == IndicatorTypes.Shift)
        {
            if (!TryReadAnchor(SourceDate, SourceTimeBox, out sourceTime))
            {
                Warn("Source time needs a date and a time as HH:mm.");
                return null;
            }
            if (!TryReadAnchor(ChartDate, ChartTimeBox, out chartTime))
            {
                Warn("Chart time needs a date and a time as HH:mm.");
                return null;
            }
            flip = FlipBox.IsChecked == true;
            targetSymbol = TargetBox.SelectedItem is string picked && picked != SameAsSourceLabel
                ? picked
                : "";
        }
        else if (type == IndicatorTypes.Index)
        {
            if (!TryReadAnchor(IndexStartDate, IndexStartTimeBox, out startTime))
            {
                Warn("Start needs a date and a time as HH:mm.");
                return null;
            }
            if (!TryReadOptionalAnchor(IndexEndDate, IndexEndTimeBox, out endTime))
            {
                Warn("End needs a date and a time as HH:mm, or leave both empty.");
                return null;
            }
            if (endTime != 0 && endTime <= startTime)
            {
                Warn("End must be after the start.");
                return null;
            }
            indexMethod = (MethodBox.SelectedItem as string) ?? IndexMethods.Median;
            indexAlgorithm = (AlgorithmBox.SelectedItem as string) ?? IndexAlgorithms.Percent;
            indexPairs = _pairBoxes.Where(b => b.IsChecked == true)
                .Select(b => (string)b.Tag!).ToList();
            if (indexPairs.Count == 0)
            {
                Warn("Pick at least one pair for the index.");
                return null;
            }
            flip = IndexMirrorBox.IsChecked == true;
        }
        else if (type == IndicatorTypes.Currency)
        {
            if (CurrencyPairBox.SelectedItem is not string picked)
            {
                Warn("Pick a pair to take the currency from.");
                return null;
            }
            indexPair = picked;
        }
        else if (type == IndicatorTypes.Deals)
        {
            dealsFile = DealsFileBox.Text.Trim();
            if (dealsFile.Length == 0)
            {
                Warn("Pick a deals file.");
                return null;
            }
            if (!System.IO.File.Exists(dealsFile))
            {
                Warn("Deals file not found: " + dealsFile);
                return null;
            }
        }
        else if (type == IndicatorTypes.Volume)
        {
            if (!int.TryParse(VolumeGroupBox.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out volumeGroup) || volumeGroup <= 0)
            {
                Warn("Group must be a positive whole number of minutes.");
                return null;
            }
            volumeLocked = VolumeLockBox.IsChecked == true;
            volumeSplit = VolumeSplitBox.IsChecked == true;
            if (ReadDensityOptions() is not { } volumeOpts) return null;
            densityPeriods = volumeOpts.Periods;
            densityUnits = volumeOpts.Units;
            densityPercents = volumeOpts.Percents;
            densityPerPixel = volumeOpts.PerPixel;
        }
        else if (type == IndicatorTypes.EntryPoints)
        {
            if (!int.TryParse(StopLossBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out stopLoss) || stopLoss <= 0)
            {
                Warn("Stop loss must be a positive whole number of pips.");
                return null;
            }
            if (!int.TryParse(TakeProfitBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out takeProfit) || takeProfit <= 0)
            {
                Warn("Take profit must be a positive whole number of pips.");
                return null;
            }
        }
        else if (type == IndicatorTypes.Levels)
        {
            if (!int.TryParse(LevelPeriodBox.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out period) || period <= 0)
            {
                Warn("Lookback must be a positive whole number.");
                return null;
            }
            unit = (LevelUnitBox.SelectedItem as string) ?? IndicatorSymbol.DefaultLevelUnit;
            if ((long)period * IndicatorUnits.BarsPerUnit(unit) > MaxLevelWindowMinutes)
            {
                Warn("Lookback is too long, keep it under 10M minutes.");
                return null;
            }
            if (!int.TryParse(LevelLengthBox.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out levelLength)
                || levelLength <= 0 || levelLength > MaxLevelPixels)
            {
                Warn($"Length must be a whole number of pixels, 1 to {MaxLevelPixels}.");
                return null;
            }
            if (!int.TryParse(LevelStepBox.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out levelStep)
                || levelStep <= 0 || levelStep > MaxLevelPixels)
            {
                Warn($"Step must be a whole number of pixels, 1 to {MaxLevelPixels}.");
                return null;
            }
        }
        else if (type == IndicatorTypes.Density)
        {
            if (ReadDensityOptions() is not { } opts) return null;
            densityPeriods = opts.Periods;
            densityUnits = opts.Units;
            densityPercents = opts.Percents;
            densityPerPixel = opts.PerPixel;
        }
        return new IndicatorSymbol
        {
            Name = name,
            Source = source,
            Type = type,
            Limit1Pips = limit1,
            Limit2Pips = limit2,
            Limit2DelayMinutes = limit2Delay,
            Period = period,
            Unit = unit,
            FromFuture = fromFuture,
            AverageWeighted = averageWeighted,
            AverageTimeWindow = averageTimeWindow,
            BandCount = bandCount,
            BandMode = bandMode,
            SourceTimeUnix = sourceTime,
            ChartTimeUnix = chartTime,
            Flip = flip,
            StartTimeUnix = startTime,
            EndTimeUnix = endTime,
            IndexMethod = indexMethod,
            IndexAlgorithm = indexAlgorithm,
            IndexPairs = indexPairs,
            IndexPair = indexPair,
            StopLossPips = stopLoss,
            TakeProfitPips = takeProfit,
            DealsFile = dealsFile,
            TargetSymbol = targetSymbol,
            FindSmoothMinutes = _editing?.FindSmoothMinutes ?? IndicatorSymbol.DefaultFindSmoothMinutes,
            FindZoomPercent = _editing?.FindZoomPercent ?? IndicatorSymbol.DefaultFindZoomPercent,
            FindStepMinutes = _editing?.FindStepMinutes ?? IndicatorSymbol.DefaultFindStepMinutes,
            FindTargets = new List<string>(_editing?.FindTargets ?? new List<string>()),
            DensityPeriods = densityPeriods,
            DensityUnits = densityUnits,
            DensityScalePercents = densityPercents,
            DensityScalePerPixel = densityPerPixel,
            DensitySelected = _editing?.DensitySelected ?? 0,
            LevelLengthPx = levelLength,
            LevelStepPx = levelStep,
            VolumeGroupMinutes = volumeGroup,
            VolumeBarScale = volumeBarScale,
            VolumeBarUnit = IndicatorSymbol.ScaleVolumeBarUnit(volumeBarUnit, volumeGroupWas, volumeGroup),
            VolumeGroupLocked = volumeLocked,
            VolumeSplitSides = volumeSplit,
            ColorArgb = _selectedColor,
            SellColorArgb = _selectedSellColor,
        };
    }

    private async void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var def = BuildDefinition();
        if (def == null) return;

        bool recompute = _editing == null || !_editing.SameData(def);
        SetBusy(true);
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = recompute ? "Computing..." : "Saving...";
        ProgressBar.IsIndeterminate = recompute;
        _cts = new CancellationTokenSource();
        var progress = new Progress<double>(p =>
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = Math.Clamp(p * 100, 0, 100);
        });
        try
        {
            await _apply(def, _editing, progress, _cts.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
            SetBusy(false);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed: " + ex.Message;
            SetBusy(false);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _refresh == null) return;
        SetBusy(true);
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;
        StatusText.Text = "Refreshing...";
        _cts = new CancellationTokenSource();
        var progress = new Progress<double>(p =>
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = Math.Clamp(p * 100, 0, 100);
        });
        try
        {
            await _refresh(progress, _cts.Token);
            StatusText.Text = "Refreshed.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed: " + ex.Message;
        }
        finally
        {
            ProgressBar.Visibility = Visibility.Collapsed;
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _cts?.Cancel();
            return;
        }
        Close();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OkBtn.IsEnabled = !busy;
        RefreshBtn.IsEnabled = !busy && _refresh != null;
        NameBox.IsEnabled = !busy;
        SourceBox.IsEnabled = !busy;
        TypeBox.IsEnabled = !busy;
        Limit1Box.IsEnabled = !busy;
        Limit2Box.IsEnabled = !busy;
        Limit2DelayBox.IsEnabled = !busy;
        PeriodBox.IsEnabled = !busy;
        UnitBox.IsEnabled = !busy;
        DirectionBox.IsEnabled = !busy;
        WeightedBox.IsEnabled = !busy;
        TimeWindowBox.IsEnabled = !busy;
        BandTimeWindowBox.IsEnabled = !busy;
        SourceDate.IsEnabled = !busy;
        SourceTimeBox.IsEnabled = !busy;
        ChartDate.IsEnabled = !busy;
        ChartTimeBox.IsEnabled = !busy;
        FlipBox.IsEnabled = !busy;
        TargetBox.IsEnabled = !busy;
        IndexStartDate.IsEnabled = !busy;
        IndexStartTimeBox.IsEnabled = !busy;
        IndexEndDate.IsEnabled = !busy;
        IndexEndTimeBox.IsEnabled = !busy;
        MethodBox.IsEnabled = !busy;
        AlgorithmBox.IsEnabled = !busy;
        PairsPanel.IsEnabled = !busy;
        IndexMirrorBox.IsEnabled = !busy;
        CurrencyPairBox.IsEnabled = !busy;
        StopLossBox.IsEnabled = !busy;
        TakeProfitBox.IsEnabled = !busy;
        DealsFileBox.IsEnabled = !busy;
        DealsBrowseBtn.IsEnabled = !busy;
        VolumeGroupBox.IsEnabled = !busy;
        VolumeLockBox.IsEnabled = !busy;
        VolumeSplitBox.IsEnabled = !busy;
        foreach (var box in _densityPeriodBoxes) box.IsEnabled = !busy;
        foreach (var box in _densityUnitBoxes) box.IsEnabled = !busy;
        foreach (var box in _densityPercentBoxes) box.IsEnabled = !busy;
        DensityScaleBox.IsEnabled = !busy;
        ColorPanel.IsEnabled = !busy;
        SellColorPanel.IsEnabled = !busy;
    }

    private void DealsBrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Deals files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        var current = DealsFileBox.Text.Trim();
        if (current.Length > 0)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    dialog.InitialDirectory = dir;
            }
            catch
            {
            }
        }
        else
        {
            var dealsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "deals");
            if (System.IO.Directory.Exists(dealsDir)) dialog.InitialDirectory = dealsDir;
        }
        if (dialog.ShowDialog(this) == true) DealsFileBox.Text = dialog.FileName;
    }

    private (List<int> Periods, List<string> Units, List<int> Percents, double PerPixel)?
        ReadDensityOptions()
    {
        var periods = new List<int>();
        var units = new List<string>();
        for (int i = 0; i < IndicatorSymbol.DensityOptionCount; i++)
        {
            if (!int.TryParse(_densityPeriodBoxes[i].Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int period) || period <= 0)
            {
                Warn($"Option {i + 1} period must be a positive whole number.");
                return null;
            }
            string unit = (_densityUnitBoxes[i].SelectedItem as string) ?? IndicatorUnits.Minutes;
            if ((long)period * IndicatorUnits.BarsPerUnit(unit) > 10_000_000)
            {
                Warn($"Option {i + 1} lookback is too long.");
                return null;
            }
            periods.Add(period);
            units.Add(unit);
        }
        var percents = new List<int>();
        for (int i = 0; i <= IndicatorSymbol.DensityAllOption; i++)
        {
            var text = _densityPercentBoxes[i].Text.Trim();
            if (text.Length == 0)
            {
                percents.Add(IndicatorSymbol.DefaultDensityScalePercent);
                continue;
            }
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int percent) || percent <= 0 || percent > 100_000)
            {
                Warn($"Key {(i == IndicatorSymbol.DensityAllOption ? 0 : i + 1)} percent must be " +
                    "a whole number between 1 and 100000.");
                return null;
            }
            percents.Add(percent);
        }
        double perPixel = 0;
        var scaleText = DensityScaleBox.Text.Trim();
        if (scaleText.Length > 0
            && (!double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out perPixel) || perPixel <= 0))
        {
            Warn("Scale must be a positive number, or empty to fit the window.");
            return null;
        }
        return (periods, units, percents, perPixel);
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
}
