using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FXViewer.Calendar;
using FXViewer.Compute;
using FXViewer.Storage;

namespace FXViewer.Chart;

public sealed record SeriesTransform(bool Mirror, long MirrorBase, int PipPoints)
{
    public int ToDisplay(int raw)
    {
        long scaled = PipPoints == 10 ? raw : ((long)raw * 10 + PipPoints / 2) / PipPoints;
        return (int)(Mirror ? MirrorBase - scaled : scaled);
    }

    public int ToRaw(int display)
    {
        long scaled = Mirror ? MirrorBase - display : display;
        return PipPoints == 10 ? (int)scaled : (int)Math.Round(scaled * PipPoints / 10.0);
    }
}

public sealed record DealMark(
    long EntryUnix, int EntryValue, long ExitUnix, int ExitValue, bool Buy, bool Win, bool Closed);

public sealed record SymbolSeries(string Symbol, CandleHistory History, int ColorArgb, int PipPoints = 10,
    bool Editable = false, PivotPoint[]? Pivots = null, SeriesTransform? Transform = null,
    string? SourceSymbol = null, PivotPoint[][]? DrawingLines = null, bool EntryPanel = false,
    DealMark[]? DealMarks = null, bool AgePanel = false)
{
    public int PriceMul { get; init; } = 1;
    public bool TimeShift { get; init; }
    public bool AgeMirror { get; init; }
    public bool DensityPanel { get; init; }
    public int[]? DensityWindows { get; init; }
    public int DensitySelected { get; init; }
    public bool BottomPanel => EntryPanel || AgePanel || DensityPanel;
}

public sealed record PivotEditRequest(string Symbol, IReadOnlyList<PivotPoint> Points);

public sealed class ChartView : Grid
{
    private static readonly ChartPalette Palette = new(
        unchecked((int)0xFFFFFFFF),
        unchecked((int)0xFFF7F7F7),
        unchecked((int)0xFFE0E0E0),
        unchecked((int)0xFFB4B4B4),
        unchecked((int)0xFF7A7A7A),
        unchecked((int)0xFFC8C8C8),
        unchecked((int)0xFFC8C8C8),
        unchecked((int)0xFFE0E0E0),
        unchecked((int)0xFFC8C8C8),
        unchecked((int)0xFF8C8C8C));

    private const double TiltedAngleStepDegrees = 1;
    private const double TiltedAngleFineDegrees = 0.1;
    private const double TiltedMinAngleDegrees = 0.1;
    private const double TiltedMaxAngleDegrees = 89.9;
    private const int TiltedPipsDecimals = 3;

    private const int CrosshairArgb = unchecked((int)0xFF808080);
    private const int CursorCenterArgb = unchecked((int)0xFF303030);
    private const int CursorHoleRadius = 3;
    private const int CursorArmLength = 6;
    private const int NoiseThreshold = 4;
    private const double ZoomStep = 1.25;
    private const double VerticalZoomShare = 1.0 / 1.75;
    private const int MinVisibleColumns = 10;
    private const int NavigateMinutesPerColumn = 5;
    private const int PipPoints = 10;
    private const int OffsetStepPips = 10;
    private const int OffsetStepPipsFine = 1;
    private const int PivotCircleRadiusPx = 4;
    private const double CollinearMaxDeviationPx = 1.5;
    private const int CollinearMinNeighborDx = 8;
    private const int MaxCollinearCircles = 300;
    private const int MaxSourceDepth = 16;
    private const int DensityMaxWidthPx = 120;
    private const int DensityFillAlpha = 96;

    private readonly Image _image;
    private readonly Image _crosshairV;
    private readonly Image _crosshairH;
    private readonly Image _cursorMark;
    private readonly RectangleGeometry _vClipTop = new();
    private readonly RectangleGeometry _vClipBottom = new();
    private readonly RectangleGeometry _hClipLeft = new();
    private readonly RectangleGeometry _hClipRight = new();
    private WriteableBitmap? _crosshairVBmp;
    private WriteableBitmap? _crosshairHBmp;
    private readonly DispatcherTimer _rebuildTimer;
    private WriteableBitmap? _bitmap;
    private IReadOnlyList<SymbolSeries>? _series;
    private long _firstUnix;
    private long _lastUnix;
    private int[] _staging = Array.Empty<int>();
    private int _version;
    private bool _computing;
    private bool _pending;
    private int _minutesPerColumn;
    private long _viewStartBucket;
    private double _topPrice;
    private double _pointsPerRow;
    private double _priceOffsetPoints;
    private readonly Dictionary<string, double> _seriesOffsetPoints = new();
    private readonly HashSet<string> _hiddenSymbols = new();
    private readonly HashSet<string> _collapsedSources = new();
    private readonly HashSet<string> _effectiveHidden = new();
    private readonly HashSet<string> _alignExcluded = new();
    private int _globalMinPrice;
    private int _globalMaxPrice;
    private CalendarEntry[] _calendarEntries = Array.Empty<CalendarEntry>();
    private bool _calendarVisible;
    private CalendarSettings _calendarSettings = new();
    private bool[] _calendarShown = new CalendarSettings().ShownMask();
    private bool _weekendsHidden;
    private readonly Ellipse _calHoverCircle = new()
    {
        Visibility = Visibility.Collapsed,
        Stroke = Brushes.White,
    };
    private int _calHoverX = -1;
    private double _calCircleCenterX;
    private double _calCircleCenterY;
    private double _calCircleRadiusPx;
    private Popup? _calPopup;
    private const int CalCircleRadiusPx = 5;
    private const int CalCircleBottomMarginPx = 18;
    public Func<CalendarEntry, CalendarDetail?>? CalendarDetailLookup { get; set; }
    private bool _dragging;
    private int _dragStartX;
    private int _dragStartY;
    private long _dragStartViewBucket;
    private double _dragStartTopPrice;

    private readonly Canvas _markerCanvas = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly Canvas _editCanvas = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly Ellipse _hoverCircle = new()
    {
        Visibility = Visibility.Collapsed,
        StrokeThickness = 1.5,
        Fill = Brushes.Transparent,
    };
    private readonly Line _dragLinePrev = new() { Visibility = Visibility.Collapsed, StrokeThickness = 1 };
    private readonly Line _dragLineNext = new() { Visibility = Visibility.Collapsed, StrokeThickness = 1 };
    private sealed record SeriesPivots(PivotPoint[] Raw, int[] Display);

    private static readonly Dictionary<int, SolidColorBrush> BrushCache = new();
    private readonly Dictionary<string, SeriesPivots> _pivots = new();
    private bool _pivotDragging;
    private bool _editMoved;
    private int _editPressX;
    private int _editPressY;
    private int _editSeries = -1;
    private int _editPoint = -1;
    private long _editOrigUnix;
    private int _editOrigRaw;
    private int _editOrigDisplay;
    private long _editCurUnix;
    private int _editCurDisplay;
    private double _renderedTopPrice;
    private double _renderedPointsPerRow;
    private string? _hidePivotSymbol;
    private int _hidePivotIndex = -1;
    private (object? Series, int K, long Start, double Top, double Ppr, double OffsetsHash,
        int Hidden, int Pw, int Ph, double DpiX, bool Weekends, object? Flatten) _markerStamp;

    private string? _drawSymbol;
    private readonly List<PivotPoint> _drawPoints = new();
    private readonly Polyline _drawPreview = new()
    {
        Visibility = Visibility.Collapsed,
        StrokeThickness = 1.5,
    };
    private readonly Line _drawCursor = new()
    {
        Visibility = Visibility.Collapsed,
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 3, 3 },
    };

    private const int RangeFillArgb = unchecked((int)0x203060C0);
    private const int RangeEdgeArgb = unchecked((int)0xFF3060C0);
    private const int StatsCursorRadiusPx = 2;
    private const double StatsPopupGapDip = 20;

    private Popup? _statsPopup;

    private bool _rangeSelecting;
    private bool _hasRange;
    private long _rangeStartUnix;
    private long _rangeEndUnix;
    private readonly Rectangle _rangeBand = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _rangeLabelText = new() { FontSize = 11, FontWeight = FontWeights.Bold };
    private readonly Border _rangeLabel = new()
    {
        Visibility = Visibility.Collapsed,
        Background = Brushes.White,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(6, 1, 6, 1),
    };

    private const double AlignEdgeFraction = 0.2;

    private const int LineHitRadiusPx = 2;
    private string? _selSymbol;
    private int _selLine = -1;
    private bool _selDragging;
    private bool _selMoved;
    private int _selDragVertex = -1;
    private int _selPressX;
    private int _selPressY;
    private PivotPoint[] _selOrigPoints = Array.Empty<PivotPoint>();
    private PivotPoint[] _selCurPoints = Array.Empty<PivotPoint>();
    private readonly List<Ellipse> _selCircles = new();
    private readonly Polyline _selPreview = new()
    {
        Visibility = Visibility.Collapsed,
        StrokeThickness = 1.5,
    };

    private string? _flattenSymbol;
    private int _flattenLine = -1;
    private FlattenMap? _flatten;

    private readonly TiltedGridState[] _tiltedGrids =
        Enumerable.Range(0, ChartViewState.TiltedGridCount)
            .Select(_ => TiltedGridState.CreateDefault()).ToArray();
    private int _tiltedUpIndex;
    private int _tiltedDownIndex;
    private bool? _tiltedNearestUp;
    private long _tiltedNearestLine;
    private int _tiltedLockReported;
    private bool _tiltedDragging;
    private int _tiltedDragStartX;
    private int _tiltedDragStartY;
    private double _tiltedDragUpSeconds;
    private double _tiltedDragUpPoints;
    private double _tiltedDragDownSeconds;
    private double _tiltedDragDownPoints;
    private int _cursorPx;
    private int _cursorPy;
    private bool _cursorOnChart;
    private readonly Image _densityImage;
    private readonly StackPanel _densityLabelPanel = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 28, 8, 0),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };
    private WriteableBitmap? _densityBmp;
    private int[] _densityStaging = Array.Empty<int>();
    private int _densitySelected;
    private long _densityAnchorColumn = long.MinValue;

    private const int ShiftHitRadiusPx = 2;
    private const int ShiftHotWidthPx = 2;
    private readonly List<(string Symbol, RenderLine Line)> _shiftLines = new();
    private string? _shiftHotSymbol;
    private string? _shiftDragSymbol;
    private bool _shiftDragging;
    private int _shiftDragStartX;
    private int _shiftDragStartY;
    private int _shiftDragColumns;
    private double _shiftDragStartOffset;

    public int EditHitRadiusPx { get; set; } = 3;
    public bool EditLocked { get; set; }
    public event Action<PivotEditRequest>? PivotEditRequested;
    public event Action<string, PivotPoint[]>? DrawingCommitted;
    public event Action<string, PivotPoint[][]>? DrawingLinesChanged;

    public event Action<string>? Info;
    public event Action<int, long, int, WeekendCompressor?>? ViewChanged;
    public event Action<long?, double>? CursorTimeChanged;
    public event Action<double[]?>? CursorPricesChanged;
    public event Action<ChartViewState>? StateChanged;
    public event Action? SeriesOffsetsChanged;
    public event Action<string, long>? SeriesTimeShiftRequested;
    public event Action<int>? DensitySelectedChanged;

    private int _renderedK;
    private long _renderedStartBucket;
    private bool _firstRenderLogged;

    public ChartView()
    {
        Background = Brushes.White;
        _image = new Image
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        Children.Add(_image);
        _crosshairV = CreateCrosshairImage();
        _crosshairH = CreateCrosshairImage();
        _cursorMark = CreateCrosshairImage();
        _cursorMark.Source = BuildCursorMarkBitmap();
        var vClip = new GeometryGroup();
        vClip.Children.Add(_vClipTop);
        vClip.Children.Add(_vClipBottom);
        _crosshairV.Clip = vClip;
        var hClip = new GeometryGroup();
        hClip.Children.Add(_hClipLeft);
        hClip.Children.Add(_hClipRight);
        _crosshairH.Clip = hClip;
        var overlay = new Canvas { IsHitTestVisible = false };
        _densityImage = CreateCrosshairImage();
        overlay.Children.Add(_densityImage);
        overlay.Children.Add(_crosshairV);
        overlay.Children.Add(_crosshairH);
        overlay.Children.Add(_cursorMark);
        Children.Add(_markerCanvas);
        Children.Add(overlay);
        _rangeBand.Fill = BrushFor(RangeFillArgb);
        _rangeBand.Stroke = BrushFor(RangeEdgeArgb);
        _rangeLabel.BorderBrush = BrushFor(RangeEdgeArgb);
        _rangeLabelText.Foreground = BrushFor(RangeEdgeArgb);
        _rangeLabel.Child = _rangeLabelText;
        _editCanvas.Children.Add(_rangeBand);
        _editCanvas.Children.Add(_dragLinePrev);
        _editCanvas.Children.Add(_dragLineNext);
        _editCanvas.Children.Add(_hoverCircle);
        _editCanvas.Children.Add(_drawPreview);
        _editCanvas.Children.Add(_drawCursor);
        _editCanvas.Children.Add(_selPreview);
        _editCanvas.Children.Add(_calHoverCircle);
        _editCanvas.Children.Add(_rangeLabel);
        Children.Add(_editCanvas);
        Children.Add(_densityLabelPanel);
        Cursor = Cursors.None;
        Focusable = true;
        MouseEnter += (_, _) =>
        {
            if (!IsKeyboardFocusWithin) Focus();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space)
            {
                ToggleStatsPopup();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _statsPopup != null)
            {
                CloseStatsPopup();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _drawSymbol != null)
            {
                CancelDrawing();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _selSymbol != null)
            {
                DeselectLine();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && _selSymbol != null && !_selDragging)
            {
                DeleteSelectedLine();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _hasRange)
            {
                ClearRange();
                e.Handled = true;
            }
            else if (DensityOptionKey(e.Key) is { } option && SelectDensityOption(option))
            {
                e.Handled = true;
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (IsAltKey(e)) UpdateAltTarget(true);
        };
        PreviewKeyUp += (_, e) =>
        {
            if (IsAltKey(e)) UpdateAltTarget(false);
        };
        MouseMove += OnCrosshairMove;
        MouseMove += OnDragMove;
        MouseMove += OnEditMove;
        MouseLeave += (_, _) =>
        {
            SetCrosshairVisible(false);
            HideHover();
            HideCalendarHover();
            _drawCursor.Visibility = Visibility.Collapsed;
            CursorTimeChanged?.Invoke(null, 0);
            CursorPricesChanged?.Invoke(null);
            _cursorOnChart = false;
            UpdateAltTarget(false);
            UpdateDensityOverlay();
        };
        MouseWheel += OnZoom;
        MouseLeftButtonDown += OnDragStart;
        MouseRightButtonDown += OnEditMenu;
        MouseLeftButtonUp += (_, _) =>
        {
            if (_shiftDragging) EndShiftDrag();
            else if (_tiltedDragging) EndTiltedDrag();
            else if (_rangeSelecting) EndRangeSelect();
            else if (_selDragging) CommitSelDrag();
            else if (_pivotDragging) CommitPivotDrag();
            else EndDrag();
        };
        LostMouseCapture += (_, _) =>
        {
            _tiltedDragging = false;
            CancelShiftDrag();
            EndRangeSelect();
            CancelPivotDrag();
            CancelSelDrag();
        };
        _rebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _rebuildTimer.Tick += (_, _) =>
        {
            _rebuildTimer.Stop();
            Rebuild();
        };
        SizeChanged += (_, _) => RequestRebuild();
        Loaded += (_, _) => RequestRebuild();
        Unloaded += (_, _) => _rebuildTimer.Stop();
    }

    private static Image CreateCrosshairImage()
    {
        var img = new Image
        {
            Stretch = Stretch.Fill,
            Visibility = Visibility.Collapsed,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        return img;
    }

    private static WriteableBitmap BuildCursorMarkBitmap()
    {
        int half = CursorHoleRadius + CursorArmLength;
        int size = half * 2 + 1;
        var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Pbgra32, null);
        var pixels = new int[size * size];
        for (int i = CursorHoleRadius + 1; i <= half; i++)
        {
            pixels[(half - i) * size + half] = CrosshairArgb;
            pixels[(half + i) * size + half] = CrosshairArgb;
            pixels[half * size + (half - i)] = CrosshairArgb;
            pixels[half * size + (half + i)] = CrosshairArgb;
        }
        pixels[half * size + half] = CursorCenterArgb;
        bmp.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        return bmp;
    }

    private static bool IsAltKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key is Key.LeftAlt or Key.RightAlt;
    }

    private void OnCrosshairMove(object sender, MouseEventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int ph = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (pw < 1 || ph < 1) return;
        EnsureCrosshairBitmaps(pw, ph, dpi);
        var pos = e.GetPosition(this);
        int cx = (int)Math.Floor(pos.X * dpi.DpiScaleX);
        int cy = (int)Math.Floor(pos.Y * dpi.DpiScaleY);
        _cursorPx = cx;
        _cursorPy = cy;
        _cursorOnChart = true;
        UpdateAltTarget(AltDown);
        Canvas.SetLeft(_crosshairV, cx / dpi.DpiScaleX);
        Canvas.SetTop(_crosshairV, 0);
        Canvas.SetLeft(_crosshairH, 0);
        Canvas.SetTop(_crosshairH, cy / dpi.DpiScaleY);
        int half = CursorHoleRadius + CursorArmLength;
        int size = half * 2 + 1;
        Canvas.SetLeft(_cursorMark, (cx - half) / dpi.DpiScaleX);
        Canvas.SetTop(_cursorMark, (cy - half) / dpi.DpiScaleY);
        _cursorMark.Width = size / dpi.DpiScaleX;
        _cursorMark.Height = size / dpi.DpiScaleY;
        double viewW = pw / dpi.DpiScaleX;
        double viewH = ph / dpi.DpiScaleY;
        double holeTop = (cy - CursorHoleRadius) / dpi.DpiScaleY;
        double holeBottom = (cy + CursorHoleRadius + 1) / dpi.DpiScaleY;
        _vClipTop.Rect = new Rect(0, 0, 1, Math.Max(0, holeTop));
        _vClipBottom.Rect = new Rect(0, holeBottom, 1, Math.Max(0, viewH - holeBottom));
        double holeLeft = (cx - CursorHoleRadius) / dpi.DpiScaleX;
        double holeRight = (cx + CursorHoleRadius + 1) / dpi.DpiScaleX;
        _hClipLeft.Rect = new Rect(0, 0, Math.Max(0, holeLeft), 1);
        _hClipRight.Rect = new Rect(holeRight, 0, Math.Max(0, viewW - holeRight), 1);
        SetCrosshairVisible(true);
        if (_renderedK > 0 && _renderedStartBucket + cx != _densityAnchorColumn)
            UpdateDensityOverlay();
        if (_renderedK > 0)
        {
            long unix = ToReal((_renderedStartBucket + cx) * (_renderedK * 60L));
            CursorTimeChanged?.Invoke(unix, cx / dpi.DpiScaleX);
        }
        if (_pointsPerRow > 0 && _series != null)
        {
            double screenPrice = _topPrice - cy * _pointsPerRow;
            if (_renderedK > 0)
                screenPrice -= FlattenShiftVirtual((_renderedStartBucket + cx) * (_renderedK * 60L));
            var prices = new double[_series.Count];
            for (int i = 0; i < _series.Count; i++)
                prices[i] = screenPrice - SeriesOffset(_series[i].Symbol);
            CursorPricesChanged?.Invoke(prices);
        }
    }

    public void RestoreState(ChartViewState state)
    {
        _weekendsHidden = state.WeekendsHidden;
        if (state.MinutesPerColumn > 0)
        {
            int k = Math.Max(1, ChartColumns.SnapK(state.MinutesPerColumn));
            _minutesPerColumn = k;
            _viewStartBucket = state.ViewStartBucket * state.MinutesPerColumn / k;
        }
        else
        {
            _minutesPerColumn = 0;
            _viewStartBucket = 0;
        }
        if (double.IsFinite(state.TopPrice) && double.IsFinite(state.PointsPerRow) && state.PointsPerRow > 0)
        {
            _topPrice = state.TopPrice;
            _pointsPerRow = state.PointsPerRow;
        }
        if (double.IsFinite(state.PriceOffsetPoints)) _priceOffsetPoints = state.PriceOffsetPoints;
        if (state.SymbolOffsetPoints != null)
        {
            _seriesOffsetPoints.Clear();
            foreach (var (symbol, offset) in state.SymbolOffsetPoints)
                if (double.IsFinite(offset)) _seriesOffsetPoints[symbol] = offset;
            if (!state.RelativeIndicatorOffsets && _series != null)
                foreach (var s in _series)
                    if (s.SourceSymbol != null
                        && state.SymbolOffsetPoints.TryGetValue(s.SourceSymbol, out var sourceAbs)
                        && double.IsFinite(sourceAbs))
                        _seriesOffsetPoints[s.Symbol] =
                            _seriesOffsetPoints.GetValueOrDefault(s.Symbol) - sourceAbs;
        }
        if (state.HiddenSymbols != null)
        {
            _hiddenSymbols.Clear();
            foreach (var symbol in state.HiddenSymbols) _hiddenSymbols.Add(symbol);
        }
        _collapsedSources.Clear();
        if (state.CollapsedSymbols != null)
            foreach (var symbol in state.CollapsedSymbols) _collapsedSources.Add(symbol);
        RefreshHidden();
        SeriesOffsetsChanged?.Invoke();
        _calendarVisible = state.CalendarVisible;
        _flattenSymbol = state.FlattenSymbol;
        _flattenLine = state.FlattenSymbol == null ? -1 : state.FlattenLine;
        state.EnsureTiltedGrids();
        for (int i = 0; i < _tiltedGrids.Length; i++) _tiltedGrids[i] = state.TiltedGrids![i].Clone();
        _tiltedUpIndex = state.TiltedUpGridIndex;
        _tiltedDownIndex = state.TiltedDownGridIndex;
        _tiltedNearestUp = null;
        RebuildFlatten();
        RequestRebuild();
    }

    public IReadOnlyCollection<string> HiddenSymbols => _hiddenSymbols;

    public IReadOnlyCollection<string> CollapsedSources => _collapsedSources;

    public bool ToggleCollapsed(string symbol)
    {
        if (!_collapsedSources.Remove(symbol)) _collapsedSources.Add(symbol);
        RefreshHidden();
        if (_selSymbol != null && IsHidden(_selSymbol)) DeselectLine();
        Rebuild();
        return _collapsedSources.Contains(symbol);
    }

    private bool IsHidden(string symbol) => _effectiveHidden.Contains(symbol);

    private void RefreshHidden()
    {
        _effectiveHidden.Clear();
        foreach (var symbol in _hiddenSymbols) _effectiveHidden.Add(symbol);
        var list = _series;
        if (list == null || _collapsedSources.Count == 0) return;
        foreach (var s in list)
            if (HiddenBySource(s)) _effectiveHidden.Add(s.Symbol);
    }

    private bool HiddenBySource(SymbolSeries series)
    {
        var source = series.SourceSymbol;
        for (int depth = 0; source != null && depth < MaxSourceDepth; depth++)
        {
            if (_collapsedSources.Contains(source) && _hiddenSymbols.Contains(source)) return true;
            source = GetSeries(source)?.SourceSymbol;
        }
        return false;
    }

    public bool IsFitView => _minutesPerColumn <= 0;

    public bool ToggleSeries(string symbol)
    {
        if (!_hiddenSymbols.Remove(symbol)) _hiddenSymbols.Add(symbol);
        RefreshHidden();
        if (_selSymbol != null && IsHidden(_selSymbol)) DeselectLine();
        Rebuild();
        return !_hiddenSymbols.Contains(symbol);
    }

    public void SetAlignExcluded(IEnumerable<string> symbols)
    {
        _alignExcluded.Clear();
        foreach (var symbol in symbols) _alignExcluded.Add(symbol);
    }

    public bool CalendarVisible => _calendarVisible;

    public bool WeekendsHidden => _weekendsHidden;

    public WeekendCompressor? Compressor => _weekendsHidden ? WeekendCompressor.Instance : null;

    private long ToVirtual(long unixSeconds) =>
        _weekendsHidden ? WeekendCompressor.Instance.ToVirtual(unixSeconds) : unixSeconds;

    private long ToReal(long virtualSeconds) =>
        _weekendsHidden ? WeekendCompressor.Instance.ToReal(virtualSeconds) : virtualSeconds;

    private long ToRealEnd(long virtualSeconds) =>
        _weekendsHidden ? WeekendCompressor.Instance.ToRealEnd(virtualSeconds) : virtualSeconds;

    private long ViewFirstUnix => ToVirtual(_firstUnix);

    private long ViewLastUnix => ToVirtual(_lastUnix);

    public bool ToggleWeekends()
    {
        long anchor = _minutesPerColumn > 0 ? ToReal(_viewStartBucket * (_minutesPerColumn * 60L)) : 0;
        _weekendsHidden = !_weekendsHidden;
        if (_minutesPerColumn > 0)
            _viewStartBucket = ToVirtual(anchor) / (_minutesPerColumn * 60L);
        CancelPivotDrag();
        CancelSelDrag();
        EndDrag();
        HideHover();
        HideCalendarHover();
        CloseCalendarPopup();
        RebuildFlatten();
        Rebuild();
        return _weekendsHidden;
    }

    public bool HasCalendar => _calendarEntries.Length > 0;

    public void SetCalendar(CalendarEntry[] entries)
    {
        _calendarEntries = entries ?? Array.Empty<CalendarEntry>();
        Rebuild();
    }

    public CalendarSettings CalendarSettings => _calendarSettings;

    public void SetCalendarSettings(CalendarSettings settings)
    {
        _calendarSettings = settings ?? new CalendarSettings();
        _calendarShown = _calendarSettings.ShownMask();
        HideCalendarHover();
        CloseCalendarPopup();
        Rebuild();
    }

    private bool CalendarLinesDrawn(int minutesPerColumn) =>
        _calendarSettings.ShowAtAnyZoom || ChartRasterizer.HourGridVisible(minutesPerColumn);

    private bool CalendarImpactShown(byte impact) =>
        impact < _calendarShown.Length && _calendarShown[impact];

    public bool ToggleCalendar()
    {
        _calendarVisible = !_calendarVisible;
        if (!_calendarVisible)
        {
            HideCalendarHover();
            CloseCalendarPopup();
        }
        Rebuild();
        return _calendarVisible;
    }

    public bool TiltedGridVisible => _tiltedUpIndex > 0 || _tiltedDownIndex > 0;

    public int TiltedUpGridIndex => _tiltedUpIndex;

    public int TiltedDownGridIndex => _tiltedDownIndex;

    public int TiltedSettingsGridIndex =>
        _tiltedUpIndex > 0 ? _tiltedUpIndex : Math.Max(_tiltedDownIndex, 1);

    public IReadOnlyList<TiltedGridState> TiltedGrids => _tiltedGrids;

    private int TiltedIndex(bool up) => up ? _tiltedUpIndex : _tiltedDownIndex;

    private TiltedGridState? TiltedFamily(bool up)
    {
        int index = TiltedIndex(up);
        return index > 0 ? _tiltedGrids[index - 1] : null;
    }

    public void ToggleTiltedGrid(bool up, int slot)
    {
        slot = Math.Clamp(slot, 0, _tiltedGrids.Length);
        int next = slot == TiltedIndex(up) ? 0 : slot;
        if (next == TiltedIndex(up)) return;
        if (up) _tiltedUpIndex = next;
        else _tiltedDownIndex = next;
        _tiltedLockReported = 0;
        RefreshTiltedNearest(AltDown && _shiftHotSymbol == null);
        Rebuild();
    }

    public void ResetTiltedGridPlacement()
    {
        TiltedFamily(true)?.SetAnchor(true, 0, 0);
        TiltedFamily(false)?.SetAnchor(false, 0, 0);
        Rebuild();
    }

    private static bool AltDown => (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

    private bool RefreshTiltedNearest(bool altDown)
    {
        bool? nearest = null;
        long line = 0;
        if (altDown && TiltedGridVisible && _cursorOnChart && _renderedK > 0 && _pointsPerRow > 0)
        {
            bool up = NearestTiltedLineIsUp(_cursorPx, _cursorPy);
            nearest = up;
            line = NearestTiltedLine(up, _cursorPx, _cursorPy, _renderedK * 60.0).Line;
        }
        if (nearest == _tiltedNearestUp && line == _tiltedNearestLine) return false;
        _tiltedNearestUp = nearest;
        _tiltedNearestLine = line;
        return true;
    }

    private void UpdateAltTarget(bool altDown)
    {
        bool changed = RefreshShiftHot(altDown);
        changed |= RefreshTiltedNearest(altDown && _shiftHotSymbol == null);
        if (changed) Rebuild();
    }

    private bool RefreshShiftHot(bool altDown)
    {
        if (_shiftDragging) return false;
        string? hot = altDown && _cursorOnChart && _renderedK > 0
            ? FindShiftSeriesAt(_cursorPx, _cursorPy)
            : null;
        if (hot == _shiftHotSymbol) return false;
        _shiftHotSymbol = hot;
        return true;
    }

    private string? FindShiftSeriesAt(int cx, int cy)
    {
        if (_renderedPointsPerRow <= 0) return null;
        string? best = null;
        double bestDistance = double.MaxValue;
        foreach (var (symbol, line) in _shiftLines)
        {
            double distance = ShiftLineDistance(line, cx, cy);
            if (distance > ShiftHitRadiusPx || distance >= bestDistance) continue;
            bestDistance = distance;
            best = symbol;
        }
        return best;
    }

    private double ShiftLineDistance(RenderLine line, int cx, int cy)
    {
        int startColumn = (int)(_renderedStartBucket - line.Series.FirstBucket);
        double best = double.MaxValue;
        for (int dx = -ShiftHitRadiusPx; dx <= ShiftHitRadiusPx; dx++)
        {
            double? row = ShiftLineRow(line, startColumn + cx + dx);
            if (row == null) continue;
            double lo = row.Value;
            double hi = row.Value;
            double? prev = ShiftLineRow(line, startColumn + cx + dx - 1);
            if (prev != null)
            {
                lo = Math.Min(lo, prev.Value);
                hi = Math.Max(hi, prev.Value);
            }
            double dy = cy < lo ? lo - cy : cy > hi ? cy - hi : 0;
            double distance = Math.Sqrt((double)dx * dx + dy * dy);
            if (distance < best) best = distance;
        }
        return best;
    }

    private double? ShiftLineRow(RenderLine line, int index)
    {
        var columns = line.Series.Columns;
        if (index < 0 || index >= columns.Length || !columns[index].HasData) return null;
        var shift = line.ColumnShift;
        double value = shift == null || index >= shift.Length
            ? line.Chosen[index]
            : line.Chosen[index] + shift[index];
        return (_renderedTopPrice - line.OffsetPoints - value) / _renderedPointsPerRow;
    }

    private void BeginShiftDrag(int cx, int cy)
    {
        var symbol = _shiftHotSymbol;
        if (symbol == null || _pointsPerRow <= 0) return;
        _shiftDragSymbol = symbol;
        _shiftDragStartX = cx;
        _shiftDragStartY = cy;
        _shiftDragStartOffset = _seriesOffsetPoints.GetValueOrDefault(symbol);
        _shiftDragColumns = 0;
        _shiftDragging = true;
        CaptureMouse();
        Rebuild();
    }

    private void MoveShiftDrag(int cx, int cy)
    {
        var symbol = _shiftDragSymbol;
        if (symbol == null || _renderedK <= 0 || _pointsPerRow <= 0) return;
        double offset = _shiftDragStartOffset - (cy - _shiftDragStartY) * _pointsPerRow;
        bool changed = false;
        if (_seriesOffsetPoints.GetValueOrDefault(symbol) != offset)
        {
            _seriesOffsetPoints[symbol] = offset;
            SeriesOffsetsChanged?.Invoke();
            changed = true;
        }
        int columns = cx - _shiftDragStartX;
        if (columns != _shiftDragColumns)
        {
            long step = (long)(columns - _shiftDragColumns) * _renderedK * 60L;
            _shiftDragColumns = columns;
            SeriesTimeShiftRequested?.Invoke(symbol, step);
        }
        if (changed) Rebuild();
    }

    private void EndShiftDrag()
    {
        if (!_shiftDragging) return;
        _shiftDragging = false;
        _shiftDragSymbol = null;
        ReleaseMouseCapture();
        RefreshShiftHot(AltDown);
        RefreshTiltedNearest(AltDown && _shiftHotSymbol == null);
        Rebuild();
    }

    private void CancelShiftDrag()
    {
        if (!_shiftDragging) return;
        _shiftDragging = false;
        _shiftDragSymbol = null;
        Rebuild();
    }

    private void BeginTiltedDrag(int cx, int cy)
    {
        _tiltedDragStartX = cx;
        _tiltedDragStartY = cy;
        var up = TiltedFamily(true);
        var down = TiltedFamily(false);
        _tiltedDragUpSeconds = up?.UpAnchorSeconds ?? 0;
        _tiltedDragUpPoints = up?.UpAnchorPoints ?? 0;
        _tiltedDragDownSeconds = down?.DownAnchorSeconds ?? 0;
        _tiltedDragDownPoints = down?.DownAnchorPoints ?? 0;
        _tiltedDragging = true;
        CaptureMouse();
    }

    private void MoveTiltedDrag(int cx, int cy)
    {
        if (_renderedK <= 0 || _pointsPerRow <= 0) return;
        double deltaSeconds = (cx - _tiltedDragStartX) * (_renderedK * 60.0);
        double deltaPoints = -(cy - _tiltedDragStartY) * _pointsPerRow;
        bool moved = DragTiltedFamily(true, _tiltedDragUpSeconds + deltaSeconds,
            _tiltedDragUpPoints + deltaPoints);
        moved |= DragTiltedFamily(false, _tiltedDragDownSeconds + deltaSeconds,
            _tiltedDragDownPoints + deltaPoints);
        if (moved) Rebuild();
    }

    private bool DragTiltedFamily(bool up, double seconds, double points)
    {
        var grid = TiltedFamily(up);
        if (grid == null) return false;
        if (seconds == grid.AnchorSecondsOf(up) && points == grid.AnchorPointsOf(up)) return false;
        grid.SetAnchor(up, seconds, points);
        return true;
    }

    private void EndTiltedDrag()
    {
        if (!_tiltedDragging) return;
        _tiltedDragging = false;
        ReleaseMouseCapture();
    }

    private double TiltedAngleOf(double slope, double bucketSec) =>
        Math.Atan(Math.Abs(slope) * bucketSec / _pointsPerRow);

    public bool NearestTiltedLineIsUp(double cursorX, double cursorY)
    {
        if (TiltedIndex(false) == 0) return true;
        if (TiltedIndex(true) == 0) return false;
        double bucketSec = _renderedK * 60.0;
        return NearestTiltedLine(true, cursorX, cursorY, bucketSec).Gap
            <= NearestTiltedLine(false, cursorX, cursorY, bucketSec).Gap;
    }

    private (double Gap, long Line) NearestTiltedLine(
        bool up, double cursorX, double cursorY, double bucketSec)
    {
        var grid = TiltedFamily(up);
        if (grid == null || !(bucketSec > 0) || !(_pointsPerRow > 0)) return (double.MaxValue, 0);
        double slope = grid.Slope(up);
        if (!double.IsFinite(slope) || slope == 0) return (double.MaxValue, 0);
        double cursorSeconds = (_renderedStartBucket + cursorX) * bucketSec;
        double cursorPoints = _topPrice - cursorY * _pointsPerRow;
        double step = ChartRasterizer.TiltedStepPoints(slope, bucketSec, _pointsPerRow);
        double atCursor = grid.AnchorPointsOf(up)
            + slope * (cursorSeconds - grid.AnchorSecondsOf(up));
        double residual = cursorPoints - atCursor;
        if (!double.IsFinite(residual)) return (double.MaxValue, 0);
        double line = Math.Round(residual / step);
        if (Math.Abs(line) > 1e15) return (double.MaxValue, 0);
        double gapPixels = Math.Abs(residual - line * step) / _pointsPerRow;
        return (gapPixels * Math.Cos(TiltedAngleOf(slope, bucketSec)), (long)line);
    }

    public void RotateTiltedGrid(int wheelDelta, bool fine, double cursorX, double cursorY)
    {
        if (!TiltedGridVisible || _renderedK <= 0 || _pointsPerRow <= 0) return;
        int notches = wheelDelta / 120;
        if (notches == 0) notches = Math.Sign(wheelDelta);
        if (notches == 0) return;
        bool up = NearestTiltedLineIsUp(cursorX, cursorY);
        var grid = TiltedFamily(up);
        if (grid == null) return;
        if (grid.LockedOf(up))
        {
            ReportTiltedLocked(up);
            return;
        }
        _tiltedLockReported = 0;
        double slopeOld = grid.Slope(up);
        if (!double.IsFinite(slopeOld) || slopeOld == 0) return;
        double bucketSec = _renderedK * 60.0;
        double angle = TiltedAngleOf(slopeOld, bucketSec);
        double step = notches * (fine ? TiltedAngleFineDegrees : TiltedAngleStepDegrees)
            * Math.PI / 180.0;
        double target = Math.Clamp(angle + step,
            TiltedMinAngleDegrees * Math.PI / 180.0, TiltedMaxAngleDegrees * Math.PI / 180.0);
        if (target == angle) return;
        double slopeNew = Math.Tan(target) * _pointsPerRow / bucketSec;
        double pipsNew = TiltedGridState.PipsOfSlope(slopeNew);
        if (!double.IsFinite(pipsNew)) return;
        pipsNew = TiltedGridState.SignedPips(Math.Round(pipsNew, TiltedPipsDecimals), up);
        if (pipsNew == grid.PipsPerDay(up)) return;
        SnapTiltedAnchor(up, cursorX, cursorY, bucketSec);
        grid.SetPipsPerDay(up, pipsNew);
        _tiltedNearestUp = up;
        _tiltedNearestLine = NearestTiltedLine(up, cursorX, cursorY, bucketSec).Line;
        Rebuild();
    }

    private void ReportTiltedLocked(bool up)
    {
        int key = TiltedIndex(up) * 2 + (up ? 1 : 0);
        if (_tiltedLockReported == key) return;
        _tiltedLockReported = key;
        Info?.Invoke($"Tilted grid {TiltedIndex(up)}: the "
            + (up ? "rising" : "falling") + " line is locked - uncheck its Lock in Settings");
    }

    private void SnapTiltedAnchor(bool up, double cursorX, double cursorY, double bucketSec)
    {
        var grid = TiltedFamily(up);
        if (grid == null) return;
        double cursorSeconds = (_renderedStartBucket + cursorX) * bucketSec;
        double cursorPoints = _topPrice - cursorY * _pointsPerRow;
        var other = TiltedFamily(!up);
        if (other != null
            && SnapTiltedToIntersection(grid, up, other, cursorSeconds, cursorPoints, bucketSec))
            return;
        double step = ChartRasterizer.GridPriceStepPoints;
        double atCursor = grid.AnchorPointsOf(up)
            + grid.Slope(up) * (cursorSeconds - grid.AnchorSecondsOf(up));
        double residual = cursorPoints - atCursor;
        if (!double.IsFinite(residual)) return;
        grid.SetAnchor(up, cursorSeconds, atCursor + Math.Round(residual / step) * step);
    }

    private bool SnapTiltedToIntersection(TiltedGridState grid, bool up, TiltedGridState other,
        double cursorSeconds, double cursorPoints, double bucketSec)
    {
        double slope = grid.Slope(up);
        double otherSlope = other.Slope(!up);
        double delta = slope - otherSlope;
        if (!double.IsFinite(delta) || delta == 0) return false;
        double step = ChartRasterizer.GridPriceStepPoints;
        double anchorSeconds = grid.AnchorSecondsOf(up);
        double anchorPoints = grid.AnchorPointsOf(up);
        double baseSeconds = (other.AnchorPointsOf(!up) - anchorPoints
            + slope * anchorSeconds - otherSlope * other.AnchorSecondsOf(!up)) / delta;
        double spacing = step / delta;
        if (!double.IsFinite(baseSeconds) || !double.IsFinite(spacing) || spacing == 0) return false;
        double index = (cursorSeconds - baseSeconds) / spacing;
        if (!double.IsFinite(index) || Math.Abs(index) > 1e15) return false;
        long first = (long)Math.Floor(index);
        double bestDistance = double.MaxValue;
        double bestSeconds = 0;
        double bestPoints = 0;
        for (long d = first; d <= first + 1; d++)
        {
            double seconds = baseSeconds + d * spacing;
            double linePoints = anchorPoints + slope * (seconds - anchorSeconds);
            double points = linePoints + Math.Round((cursorPoints - linePoints) / step) * step;
            double dx = (seconds - cursorSeconds) / bucketSec;
            double dy = (cursorPoints - points) / _pointsPerRow;
            double distance = dx * dx + dy * dy;
            if (!(distance < bestDistance)) continue;
            bestDistance = distance;
            bestSeconds = seconds;
            bestPoints = points;
        }
        if (!double.IsFinite(bestSeconds) || !double.IsFinite(bestPoints)) return false;
        grid.SetAnchor(up, bestSeconds, bestPoints);
        return true;
    }

    public void SetTiltedGrids(IReadOnlyList<TiltedGridState> grids)
    {
        for (int i = 0; i < _tiltedGrids.Length && i < grids.Count; i++)
        {
            var grid = grids[i].Clone();
            grid.Normalize();
            _tiltedGrids[i] = grid;
        }
        _tiltedLockReported = 0;
        Rebuild();
    }

    private TiltedFamilySettings TiltedRenderSettings(bool up)
    {
        var grid = TiltedFamily(up);
        return grid == null
            ? default
            : new TiltedFamilySettings(true, grid.AnchorSecondsOf(up), grid.AnchorPointsOf(up),
                grid.Slope(up), _tiltedNearestUp == up, _tiltedNearestLine);
    }

    public void AlignSeriesOffsetToGrid(string symbol)
    {
        double total = SeriesOffset(symbol);
        double aligned = Math.Round(total / ChartRasterizer.GridPriceStepPoints)
            * ChartRasterizer.GridPriceStepPoints;
        if (aligned == total) return;
        _seriesOffsetPoints[symbol] =
            _seriesOffsetPoints.GetValueOrDefault(symbol) + aligned - total;
        SeriesOffsetsChanged?.Invoke();
        Rebuild();
    }

    public void AutoAlignSeries(string symbol)
    {
        var s = GetSeries(symbol);
        if (s == null || s.BottomPanel) return;
        if (_renderedK <= 0 || _pointsPerRow <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int ph = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (pw < 1 || ph < 2) return;
        if (VisibleDisplayRange(s, pw) is not { } range)
        {
            Info?.Invoke("Auto align: no data in view");
            return;
        }
        double center = _topPrice - _pointsPerRow * (ph - 1) / 2;
        double middle = (range.Lo + range.Hi) / 2;
        const double step = ChartRasterizer.GridPriceStepPoints;
        double total = Math.Round((center - middle) / step) * step;
        double sourceOffset = s.SourceSymbol == null ? 0 : EffectiveSeriesOffset(s.SourceSymbol);
        double own = total - _priceOffsetPoints - sourceOffset;
        if (_seriesOffsetPoints.GetValueOrDefault(symbol) == own) return;
        _seriesOffsetPoints[symbol] = own;
        SeriesOffsetsChanged?.Invoke();
        Rebuild();
    }

    private (double Lo, double Hi)? VisibleDisplayRange(SymbolSeries s, int pw) =>
        DisplayRangeAt(s, _renderedK, _renderedStartBucket, pw);

    private (double Lo, double Hi)? DisplayRangeAt(SymbolSeries s, int k, long viewStart, int pw)
    {
        double lo = double.MaxValue;
        double hi = double.MinValue;
        long bucketSec = k * 60L;
        if (s.History.Minutes.Length > 0)
        {
            var view = ChartColumns.BuildView(s.History, k, viewStart, pw, Compressor);
            var shifts = _flatten?.ColumnShifts(viewStart, pw, bucketSec);
            for (int i = 0; i < view.Columns.Length; i++)
            {
                if (!view.Columns[i].HasData) continue;
                double shift = shifts == null ? 0 : shifts[i];
                if (view.Columns[i].Min + shift < lo) lo = view.Columns[i].Min + shift;
                if (view.Columns[i].Max + shift > hi) hi = view.Columns[i].Max + shift;
            }
        }
        if (s.Transform != null)
        {
            foreach (var poly in VectorLines(s))
                foreach (var p in poly)
                {
                    long v = ToVirtual(p.UnixSeconds);
                    long bucket = v / bucketSec;
                    if (bucket < viewStart || bucket >= viewStart + pw) continue;
                    double d = s.Transform.ToDisplay(p.Value) + FlattenShiftVirtual(v);
                    if (d < lo) lo = d;
                    if (d > hi) hi = d;
                }
        }
        return lo <= hi ? (lo, hi) : null;
    }

    public bool ShowTimeCentered(long unixSeconds, string? centerSymbol)
    {
        if (_firstUnix > _lastUnix) return false;
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int ph = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (pw < 1 || ph < 2) return false;
        bool wasFitView = _minutesPerColumn <= 0;
        int k = wasFitView ? NavigateMinutesPerColumn : _minutesPerColumn;
        _minutesPerColumn = k;
        long bucketSec = k * 60L;
        long target = ToVirtual(unixSeconds);
        long start = ClampViewStart(
            target / bucketSec - pw / 2, ViewFirstUnix / bucketSec, ViewLastUnix / bucketSec, pw);
        _viewStartBucket = start;
        if (wasFitView) _pointsPerRow = 0;
        else CenterSeriesVertically(centerSymbol, k, start, pw, ph);
        CancelPivotDrag();
        CancelSelDrag();
        EndDrag();
        HideHover();
        HideCalendarHover();
        CloseCalendarPopup();
        Rebuild();
        return true;
    }

    private void CenterSeriesVertically(string? symbol, int k, long viewStart, int pw, int ph)
    {
        if (symbol == null || _pointsPerRow <= 0) return;
        var s = GetSeries(symbol);
        if (s == null || s.BottomPanel) return;
        if (DisplayRangeAt(s, k, viewStart, pw) is not { } range) return;
        double middle = (range.Lo + range.Hi) / 2 + SeriesOffset(symbol);
        _topPrice = middle + _pointsPerRow * (ph - 1) / 2;
    }

    public void RenameSeriesKeys(string oldName, string newName)
    {
        if (oldName == newName) return;
        if (_seriesOffsetPoints.Remove(oldName, out var offset)) _seriesOffsetPoints[newName] = offset;
        if (_hiddenSymbols.Remove(oldName)) _hiddenSymbols.Add(newName);
        if (_collapsedSources.Remove(oldName)) _collapsedSources.Add(newName);
        RefreshHidden();
        if (_flattenSymbol == oldName) _flattenSymbol = newName;
        if (_shiftHotSymbol == oldName) _shiftHotSymbol = newName;
        if (_shiftDragSymbol == oldName) _shiftDragSymbol = newName;
    }

    public void SetSeriesOffset(string symbol, double points)
    {
        if (_seriesOffsetPoints.GetValueOrDefault(symbol) == points) return;
        _seriesOffsetPoints[symbol] = points;
        SeriesOffsetsChanged?.Invoke();
        Rebuild();
    }

    public void ShiftSeriesOffset(string symbol, int wheelDelta)
    {
        var shifted = ShiftedOffset(_seriesOffsetPoints.GetValueOrDefault(symbol), wheelDelta);
        if (shifted == null) return;
        _seriesOffsetPoints[symbol] = shifted.Value;
        SeriesOffsetsChanged?.Invoke();
        Rebuild();
    }

    private double? ShiftedOffset(double current, int wheelDelta)
    {
        if (wheelDelta == 0 || _pointsPerRow <= 0) return null;
        var dpi = VisualTreeHelper.GetDpi(this);
        int ph = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (ph < 2) return null;
        double screenRange = _pointsPerRow * (ph - 1);
        double maxOffset = Math.Max(0, _globalMaxPrice - (double)_globalMinPrice) + screenRange;
        int stepPips = (Keyboard.Modifiers & ModifierKeys.Control) != 0
            ? OffsetStepPipsFine
            : OffsetStepPips;
        double shifted = Math.Clamp(
            current + wheelDelta / 120.0 * stepPips * PipPoints,
            Math.Min(current, -maxOffset), Math.Max(current, maxOffset));
        return shifted == current ? null : shifted;
    }

    private void EnsureCrosshairBitmaps(int pw, int ph, DpiScale dpi)
    {
        if (_crosshairVBmp == null || _crosshairVBmp.PixelHeight != ph)
        {
            _crosshairVBmp = new WriteableBitmap(1, ph, 96, 96, PixelFormats.Pbgra32, null);
            var pixels = new int[ph];
            for (int i = 0; i < ph; i += 2) pixels[i] = CrosshairArgb;
            _crosshairVBmp.WritePixels(new Int32Rect(0, 0, 1, ph), pixels, 4, 0);
            _crosshairV.Source = _crosshairVBmp;
        }
        _crosshairV.Width = 1 / dpi.DpiScaleX;
        _crosshairV.Height = ph / dpi.DpiScaleY;
        if (_crosshairHBmp == null || _crosshairHBmp.PixelWidth != pw)
        {
            _crosshairHBmp = new WriteableBitmap(pw, 1, 96, 96, PixelFormats.Pbgra32, null);
            var pixels = new int[pw];
            for (int i = 0; i < pw; i += 2) pixels[i] = CrosshairArgb;
            _crosshairHBmp.WritePixels(new Int32Rect(0, 0, pw, 1), pixels, pw * 4, 0);
            _crosshairH.Source = _crosshairHBmp;
        }
        _crosshairH.Width = pw / dpi.DpiScaleX;
        _crosshairH.Height = 1 / dpi.DpiScaleY;
    }

    private void SetCrosshairVisible(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        _crosshairV.Visibility = v;
        _crosshairH.Visibility = v;
        _cursorMark.Visibility = v;
    }

    public void SetSeries(IReadOnlyList<SymbolSeries> series)
    {
        EndDrag();
        CancelPivotDrag();
        CancelDrawing();
        DeselectLine();
        HideHover();
        ClearRange();
        ForgetHiddenPivotSegments();
        series = series.ToList();
        _series = series.Count > 0 ? series : null;
        _shiftLines.Clear();
        _shiftHotSymbol = null;
        _shiftDragSymbol = null;
        _shiftDragging = false;
        _minutesPerColumn = 0;
        _pointsPerRow = 0;
        _priceOffsetPoints = 0;
        _seriesOffsetPoints.Clear();
        _hiddenSymbols.Clear();
        _collapsedSources.Clear();
        _effectiveHidden.Clear();
        _flattenSymbol = null;
        _flattenLine = -1;
        _flatten = null;
        foreach (var s in series)
            if (s.DensityPanel && s.DensityWindows is { Length: > 0 })
            {
                _densitySelected = Math.Clamp(s.DensitySelected, 0, s.DensityWindows.Length);
                break;
            }
        _densityAnchorColumn = long.MinValue;
        RecomputeGlobalRange();
        RebuildPivots();
        RequestRebuild();
    }

    public void ReplaceSeries(string symbol, CandleHistory history, SeriesTransform? transform = null)
    {
        var series = _series;
        if (series == null || history.Minutes.Length == 0) return;
        int idx = -1;
        for (int i = 0; i < series.Count; i++)
            if (series[i].Symbol == symbol) { idx = i; break; }
        if (idx < 0) return;
        CancelPivotDrag();
        HideHover();
        ForgetHiddenPivotSegments();
        var list = new List<SymbolSeries>(series);
        list[idx] = list[idx] with
        {
            History = history,
            Transform = transform ?? list[idx].Transform,
        };
        _series = list;
        if (transform != null) SeriesOffsetsChanged?.Invoke();
        RecomputeGlobalRange();
        RebuildPivots();
        RebuildFlatten();
        Rebuild();
    }

    public void ReplacePivots(string symbol, PivotPoint[] points)
    {
        var series = _series;
        if (series == null) return;
        int idx = -1;
        for (int i = 0; i < series.Count; i++)
            if (series[i].Symbol == symbol) { idx = i; break; }
        if (idx < 0) return;
        CancelPivotDrag();
        HideHover();
        ForgetHiddenPivotSegments();
        var list = new List<SymbolSeries>(series);
        list[idx] = list[idx] with { Pivots = points };
        _series = list;
        RecomputeGlobalRange();
        RebuildPivots();
        Rebuild();
    }

    public string? FirstVisibleSeries(string preferred)
    {
        var list = _series;
        if (list == null) return null;
        foreach (var s in list)
            if (!s.BottomPanel && s.History.Minutes.Length > 0
                && !IsHidden(s.Symbol)
                && string.Equals(s.Symbol, preferred, StringComparison.OrdinalIgnoreCase))
                return s.Symbol;
        foreach (var s in list)
            if (!s.BottomPanel && s.History.Minutes.Length > 0 && !IsHidden(s.Symbol))
                return s.Symbol;
        return null;
    }

    public SymbolSeries? GetSeries(string symbol)
    {
        var series = _series;
        if (series == null) return null;
        foreach (var s in series)
            if (s.Symbol == symbol) return s;
        return null;
    }

    public void ClearHiddenPivotSegments()
    {
        if (_hidePivotSymbol == null) return;
        ForgetHiddenPivotSegments();
        Rebuild();
    }

    private void ForgetHiddenPivotSegments()
    {
        _hidePivotSymbol = null;
        _hidePivotIndex = -1;
    }

    private static int CalendarLowerBound(CalendarEntry[] entries, long unix)
    {
        int lo = 0;
        int hi = entries.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (entries[mid].UnixSeconds < unix) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static readonly byte[] CalendarDrawOrder =
        { (byte)CalendarImpact.Holiday, (byte)CalendarImpact.Low, (byte)CalendarImpact.Medium,
          (byte)CalendarImpact.High, (byte)CalendarImpact.Highest };

    private const int RateMarkTopPx = 0;
    private const int RateMarkHalfWidthPx = 3;
    private const int RateMarkHeightPx = 4;
    private const int RateMarkArgb = unchecked((int)0xFF000000);

    private static void DrawCalendar(int[] buffer, int width, int height,
        CalendarEntry[] entries, int minutesPerColumn, long startBucket, WeekendCompressor? map,
        bool[] shown)
    {
        long bucketSec = minutesPerColumn * 60L;
        long tLo = map == null ? startBucket * bucketSec : map.ToReal(startBucket * bucketSec);
        long tHi = map == null ? (startBucket + width) * bucketSec
            : map.ToReal((startBucket + width) * bucketSec);
        int start = CalendarLowerBound(entries, tLo);
        foreach (byte level in CalendarDrawOrder)
        {
            if (level >= shown.Length || !shown[level]) continue;
            int on;
            int period;
            switch ((CalendarImpact)level)
            {
                case CalendarImpact.Highest:
                case CalendarImpact.High: on = 1; period = 1; break;
                case CalendarImpact.Medium: on = 3; period = 6; break;
                default: on = 1; period = 3; break;
            }
            for (int i = start; i < entries.Length && entries[i].UnixSeconds < tHi; i++)
            {
                if (entries[i].Impact != level) continue;
                long t = map == null ? entries[i].UnixSeconds : map.ToVirtual(entries[i].UnixSeconds);
                int x = (int)(t / bucketSec - startBucket);
                ChartRasterizer.DrawVerticalDashed(
                    buffer, width, height, x, Currencies.ColorArgb(entries[i].Currency), on, period);
                if (entries[i].RateDecision)
                    ChartRasterizer.DrawDownTriangle(buffer, width, height, x,
                        RateMarkTopPx, RateMarkHalfWidthPx, RateMarkHeightPx, RateMarkArgb);
            }
        }
    }

    private void RecomputeGlobalRange()
    {
        long firstUnix = long.MaxValue;
        long lastUnix = long.MinValue;
        int mn = int.MaxValue;
        int mx = int.MinValue;
        foreach (var s in _series ?? Enumerable.Empty<SymbolSeries>())
        {
            var minutes = s.History.Minutes;
            if (minutes.Length > 0)
            {
                if (minutes[0].MinuteUnixSeconds < firstUnix) firstUnix = minutes[0].MinuteUnixSeconds;
                if (minutes[^1].MinuteUnixSeconds > lastUnix) lastUnix = minutes[^1].MinuteUnixSeconds;
                if (!s.BottomPanel)
                    foreach (var b in s.History.Levels[^1])
                    {
                        if (b.Min < mn) mn = b.Min;
                        if (b.Max > mx) mx = b.Max;
                    }
            }
            if (s.Transform == null) continue;
            foreach (var line in VectorLines(s))
                foreach (var p in line)
                {
                    if (p.UnixSeconds < firstUnix) firstUnix = p.UnixSeconds;
                    if (p.UnixSeconds > lastUnix) lastUnix = p.UnixSeconds;
                    int d = s.Transform.ToDisplay(p.Value);
                    if (d < mn) mn = d;
                    if (d > mx) mx = d;
                }
        }
        _firstUnix = firstUnix;
        _lastUnix = lastUnix;
        _globalMinPrice = mn;
        _globalMaxPrice = mx;
    }

    private static PivotPoint[][] VectorLines(SymbolSeries s)
    {
        if (s.DrawingLines is { Length: > 0 }) return s.DrawingLines;
        if (s.Pivots is { Length: > 0 } points) return new[] { points };
        return Array.Empty<PivotPoint[]>();
    }

    private void RebuildPivots()
    {
        _pivots.Clear();
        var series = _series;
        if (series == null) return;
        foreach (var s in series)
        {
            if (!s.Editable || s.Pivots == null || s.Transform == null) continue;
            var display = new int[s.Pivots.Length];
            for (int i = 0; i < display.Length; i++)
                display[i] = s.Transform.ToDisplay(s.Pivots[i].Value);
            _pivots[s.Symbol] = new SeriesPivots(s.Pivots, display);
        }
    }

    public void SetLastTick(string symbol, int displayValue)
    {
        var series = _series;
        if (series == null) return;
        foreach (var s in series)
            if (s.Symbol == symbol) { s.History.SetLastTick(displayValue); return; }
    }

    public void ClearLastTick(string symbol)
    {
        var series = _series;
        if (series == null) return;
        foreach (var s in series)
            if (s.Symbol == symbol) { s.History.ClearLastTick(); return; }
    }

    public void SetLiveTail(string symbol, Candle[] tail)
    {
        var series = _series;
        if (series == null) return;
        SymbolSeries? match = null;
        foreach (var s in series)
            if (s.Symbol == symbol) { match = s; break; }
        if (match == null) return;
        match.History.SetLive(tail);
        if (tail.Length > 0)
        {
            long last = tail[^1].MinuteUnixSeconds;
            if (last > _lastUnix) _lastUnix = last;
            foreach (var c in tail)
            {
                if (c.Min < _globalMinPrice) _globalMinPrice = c.Min;
                if (c.Max > _globalMaxPrice) _globalMaxPrice = c.Max;
            }
        }
        RequestRebuild();
    }

    private static double PairedVerticalFactor(double horizontalFactor) =>
        horizontalFactor <= 0 ? 1.0 : Math.Pow(horizontalFactor, VerticalZoomShare);

    private bool ApplyVerticalZoom(double factor, double posY)
    {
        if (_pointsPerRow <= 0) return false;
        if (_series == null) return false;
        if (factor <= 0 || factor == 1.0) return false;
        var dpi = VisualTreeHelper.GetDpi(this);
        int ph = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (ph < 2) return false;
        int cy = Math.Clamp((int)Math.Floor(posY * dpi.DpiScaleY), 0, ph - 1);
        double anchorPrice = _topPrice - cy * _pointsPerRow;
        double newPpr = _pointsPerRow * factor;
        double maxPpr = Math.Max(1.0, (_globalMaxPrice - _globalMinPrice) * 10.0 / (ph - 1));
        newPpr = Math.Clamp(newPpr, 0.05, maxPpr);
        if (newPpr == _pointsPerRow) return false;
        double screenRange = newPpr * (ph - 1);
        double newTop = Math.Clamp(anchorPrice + cy * newPpr, PanMinPrice, PanMaxPrice + screenRange);
        _pointsPerRow = newPpr;
        _topPrice = newTop;
        if (_dragging)
        {
            _dragStartY = cy;
            _dragStartTopPrice = newTop;
        }
        return true;
    }

    private void OnVerticalZoom(MouseWheelEventArgs e)
    {
        e.Handled = true;
        var pos = e.GetPosition(this);
        if (ApplyVerticalZoom(e.Delta > 0 ? 1.0 / ZoomStep : ZoomStep, pos.Y))
            Rebuild();
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (_series == null) return;
        if (_drawSymbol != null)
        {
            HandleDrawClick(e);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var pos = e.GetPosition(this);
        int cx = (int)Math.Floor(pos.X * dpi.DpiScaleX);
        int cy = (int)Math.Floor(pos.Y * dpi.DpiScaleY);
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && _renderedK > 0)
        {
            if (_shiftHotSymbol != null)
            {
                BeginShiftDrag(cx, cy);
                return;
            }
            if (TiltedGridVisible)
            {
                BeginTiltedDrag(cx, cy);
                return;
            }
        }
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _renderedK > 0)
        {
            if (_selSymbol != null)
            {
                int shiftVertex = FindSelectedVertexAt(cx, cy);
                if (shiftVertex >= 0)
                {
                    BeginSelDrag(shiftVertex, cx, cy);
                    return;
                }
            }
            BeginRangeSelect(cx);
            return;
        }
        if (_calHoverCircle.Visibility == Visibility.Visible && _calHoverX >= 0)
        {
            double ddx = cx - _calCircleCenterX;
            double ddy = cy - _calCircleCenterY;
            double rr = _calCircleRadiusPx + 3;
            if (ddx * ddx + ddy * ddy <= rr * rr)
            {
                ShowCalendarPopup(_calHoverX);
                return;
            }
        }
        var hit = FindPivotAt(cx, cy);
        if (hit != null)
        {
            if (e.ClickCount == 1) BeginPivotDrag(hit.Value.Series, hit.Value.Point, cx, cy);
            return;
        }
        if (_selSymbol != null)
        {
            int vertex = FindSelectedVertexAt(cx, cy);
            if (vertex >= 0)
            {
                BeginSelDrag(vertex, cx, cy);
                return;
            }
        }
        var lineHit = FindDrawingLineAt(cx, cy);
        if (lineHit != null)
        {
            if (lineHit.Value.Symbol == _selSymbol && lineHit.Value.Line == _selLine)
            {
                BeginSelDrag(-1, cx, cy);
                return;
            }
            SelectLine(lineHit.Value.Symbol, lineHit.Value.Line);
            return;
        }
        if (_selSymbol != null) DeselectLine();
        if (e.ClickCount == 2)
        {
            AlignSeriesToGrid(pos);
            return;
        }
        int pw = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        if (pw < 1) return;
        _dragStartX = cx;
        _dragStartY = cy;
        if (_minutesPerColumn <= 0)
        {
            int fitK = ChartColumns.FitK(ViewFirstUnix, ViewLastUnix, pw);
            _minutesPerColumn = fitK;
            _viewStartBucket = ViewFirstUnix / (fitK * 60L);
        }
        _dragStartViewBucket = _viewStartBucket;
        _dragStartTopPrice = _topPrice;
        _dragging = true;
        CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_shiftDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndShiftDrag();
                return;
            }
            var dpiShift = VisualTreeHelper.GetDpi(this);
            var posShift = e.GetPosition(this);
            MoveShiftDrag(
                (int)Math.Floor(posShift.X * dpiShift.DpiScaleX),
                (int)Math.Floor(posShift.Y * dpiShift.DpiScaleY));
            return;
        }
        if (_tiltedDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndTiltedDrag();
                return;
            }
            var dpiTilt = VisualTreeHelper.GetDpi(this);
            var posTilt = e.GetPosition(this);
            MoveTiltedDrag(
                (int)Math.Floor(posTilt.X * dpiTilt.DpiScaleX),
                (int)Math.Floor(posTilt.Y * dpiTilt.DpiScaleY));
            return;
        }
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        if (_series == null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int ph = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (pw < 1 || ph < 1) return;
        var pos = e.GetPosition(this);
        int dx = (int)Math.Floor(pos.X * dpi.DpiScaleX) - _dragStartX;
        int dy = (int)Math.Floor(pos.Y * dpi.DpiScaleY) - _dragStartY;
        bool changed = false;
        if (dx != 0 && _minutesPerColumn > 0)
        {
            int k = _minutesPerColumn;
            long fb = ViewFirstUnix / (k * 60L);
            long lb = ViewLastUnix / (k * 60L);
            long newStart = ClampViewStart(_dragStartViewBucket - dx, fb, lb, pw);
            if (newStart != _viewStartBucket)
            {
                _viewStartBucket = newStart;
                changed = true;
            }
        }
        if (_pointsPerRow > 0 && _dragStartTopPrice > 0 && dy != 0)
        {
            double screenRange = _pointsPerRow * (ph - 1);
            double newTop = Math.Clamp(_dragStartTopPrice + dy * _pointsPerRow,
                PanMinPrice, PanMaxPrice + screenRange);
            if (newTop != _topPrice)
            {
                _topPrice = newTop;
                changed = true;
            }
        }
        if (changed) Rebuild();
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
    }

    public (long StartUnix, long EndUnix)? SelectedRange => _hasRange
        ? (Math.Min(_rangeStartUnix, _rangeEndUnix), Math.Max(_rangeStartUnix, _rangeEndUnix))
        : null;

    private long ColumnTime(int cx)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int x = Math.Clamp(cx, 0, Math.Max(0, pw - 1));
        return ToReal((_renderedStartBucket + x) * (_renderedK * 60L));
    }

    private void BeginRangeSelect(int cx)
    {
        EndDrag();
        CloseStatsPopup();
        HideHover();
        HideCalendarHover();
        CloseCalendarPopup();
        DeselectLine();
        _rangeSelecting = true;
        _hasRange = true;
        _rangeStartUnix = ColumnTime(cx);
        _rangeEndUnix = _rangeStartUnix;
        UpdateRangeVisuals();
        Focusable = true;
        Focus();
        CaptureMouse();
    }

    private void UpdateRangeSelect(int cx)
    {
        _rangeEndUnix = ColumnTime(cx);
        UpdateRangeVisuals();
    }

    private void EndRangeSelect()
    {
        if (!_rangeSelecting) return;
        _rangeSelecting = false;
        ReleaseMouseCapture();
        UpdateRangeVisuals();
    }

    public void ClearRange()
    {
        CloseStatsPopup();
        _rangeSelecting = false;
        _hasRange = false;
        _rangeBand.Visibility = Visibility.Collapsed;
        _rangeLabel.Visibility = Visibility.Collapsed;
    }

    private void UpdateRangeVisuals()
    {
        double viewW = ActualWidth;
        double viewH = ActualHeight;
        if (!_hasRange || _renderedK <= 0 || viewW < 1 || viewH < 1)
        {
            _rangeBand.Visibility = Visibility.Collapsed;
            _rangeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        long bucketSec = _renderedK * 60L;
        long lo = Math.Min(_rangeStartUnix, _rangeEndUnix);
        long hi = Math.Max(_rangeStartUnix, _rangeEndUnix);
        double x1 = (ToVirtual(lo) / bucketSec - _renderedStartBucket) / dpi.DpiScaleX;
        double x2 = (ToVirtual(hi) / bucketSec - _renderedStartBucket + 1) / dpi.DpiScaleX;
        if (x2 <= 0 || x1 >= viewW)
        {
            _rangeBand.Visibility = Visibility.Collapsed;
            _rangeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        double left = Math.Clamp(x1, 0, viewW);
        double right = Math.Clamp(x2, 0, viewW);
        _rangeBand.Width = Math.Max(1, right - left);
        _rangeBand.Height = viewH;
        _rangeBand.StrokeThickness = 1 / dpi.DpiScaleX;
        Canvas.SetLeft(_rangeBand, left);
        Canvas.SetTop(_rangeBand, 0);
        _rangeBand.Visibility = Visibility.Visible;
        _rangeLabelText.Text = RangeText(lo, hi);
        _rangeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double labelW = _rangeLabel.DesiredSize.Width;
        Canvas.SetLeft(_rangeLabel,
            Math.Clamp((left + right - labelW) / 2, 0, Math.Max(0, viewW - labelW)));
        Canvas.SetTop(_rangeLabel, 2);
        _rangeLabel.Visibility = Visibility.Visible;
    }

    private sealed record RangeStats(string Symbol, int ColorArgb, int Digits,
        double Min, double Max, double SizePips, double Avg, double In, double Out, int Count);

    private static int LowerBound(Candle[] minutes, long unixSeconds)
    {
        int lo = 0;
        int hi = minutes.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (minutes[mid].MinuteUnixSeconds < unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static double ToTruePoints(SeriesTransform? t, double display)
    {
        if (t == null) return display;
        double scaled = t.Mirror ? t.MirrorBase - display : display;
        return t.PipPoints == 10 ? scaled : scaled * t.PipPoints / 10.0;
    }

    private static int PriceDigits(int pipPoints)
    {
        int digits = 5;
        for (int p = pipPoints; p >= 10; p /= 10) digits--;
        return digits;
    }

    private List<RangeStats> ComputeRangeStats(long loUnix, long hiUnix)
    {
        var result = new List<RangeStats>();
        var series = _series;
        if (series == null) return result;
        foreach (var s in series)
        {
            if (IsHidden(s.Symbol) || s.BottomPanel) continue;
            int mn = int.MaxValue;
            int mx = int.MinValue;
            int first = 0;
            int last = 0;
            long sum = 0;
            int count = 0;
            void Take(Candle c)
            {
                if (c.Min < mn) mn = c.Min;
                if (c.Max > mx) mx = c.Max;
                if (count == 0) first = c.Avg;
                last = c.Avg;
                sum += c.Avg;
                count++;
            }
            var minutes = s.History.Minutes;
            for (int i = LowerBound(minutes, loUnix);
                 i < minutes.Length && minutes[i].MinuteUnixSeconds <= hiUnix; i++)
                Take(minutes[i]);
            foreach (var c in s.History.Live)
                if (c.MinuteUnixSeconds >= loUnix && c.MinuteUnixSeconds <= hiUnix)
                    Take(c);
            if (count == 0)
            {
                result.Add(new RangeStats(s.Symbol, s.ColorArgb, PriceDigits(s.PipPoints * s.PriceMul),
                    0, 0, 0, 0, 0, 0, 0));
                continue;
            }
            double a = ToTruePoints(s.Transform, mn);
            double b = ToTruePoints(s.Transform, mx);
            int mul = s.PriceMul;
            result.Add(new RangeStats(s.Symbol, s.ColorArgb, PriceDigits(s.PipPoints * mul),
                Math.Min(a, b) * mul, Math.Max(a, b) * mul, Math.Abs(b - a) / s.PipPoints,
                ToTruePoints(s.Transform, sum / (double)count) * mul,
                ToTruePoints(s.Transform, first) * mul, ToTruePoints(s.Transform, last) * mul, count));
        }
        return result;
    }

    private void ToggleStatsPopup()
    {
        if (_statsPopup != null)
        {
            CloseStatsPopup();
            return;
        }
        var series = _series;
        if (series == null || _renderedK <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var mouse = Mouse.GetPosition(this);
        long lo;
        long hi;
        if (SelectedRange is { } sel) (lo, hi) = sel;
        else
        {
            int cx = (int)Math.Floor(mouse.X * dpi.DpiScaleX);
            lo = ColumnTime(cx - StatsCursorRadiusPx);
            hi = ColumnTime(cx + StatsCursorRadiusPx);
            if (hi < lo) (lo, hi) = (hi, lo);
        }
        var rows = ComputeRangeStats(lo, hi + _renderedK * 60L - 1);
        if (rows.Count == 0) return;
        CloseStatsPopup();
        var content = BuildStatsContent(lo, hi, rows);
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = content.DesiredSize.Width;
        double h = content.DesiredSize.Height;
        double left = mouse.X + StatsPopupGapDip + w <= ActualWidth
            ? mouse.X + StatsPopupGapDip
            : mouse.X - StatsPopupGapDip - w;
        double top = mouse.Y - h / 2;
        _statsPopup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Relative,
            HorizontalOffset = Math.Clamp(left, 0, Math.Max(0, ActualWidth - w)),
            VerticalOffset = Math.Clamp(top, 0, Math.Max(0, ActualHeight - h)),
            StaysOpen = true,
            AllowsTransparency = true,
            Child = content,
        };
        _statsPopup.IsOpen = true;
    }

    private void CloseStatsPopup()
    {
        if (_statsPopup == null) return;
        _statsPopup.IsOpen = false;
        _statsPopup = null;
    }

    private static readonly string[] StatsHeaders =
        { "", "min", "max", "pips", "avg", "in", "out" };

    private FrameworkElement BuildStatsContent(long loUnix, long hiUnix, List<RangeStats> rows)
    {
        var table = new Grid();
        for (int c = 0; c < StatsHeaders.Length; c++)
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r <= rows.Count; r++)
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var headBrush = BrushFor(unchecked((int)0xFF808080));
        for (int c = 0; c < StatsHeaders.Length; c++)
            table.Children.Add(StatsCell(StatsHeaders[c], 0, c, headBrush, c > 0));
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var brush = BrushFor(row.ColorArgb);
            table.Children.Add(StatsCell(row.Symbol, r + 1, 0, brush, false));
            if (row.Count == 0)
            {
                for (int c = 1; c < StatsHeaders.Length; c++)
                    table.Children.Add(StatsCell("-", r + 1, c, brush, true));
                continue;
            }
            string format = row.Digits > 0 ? "0." + new string('0', row.Digits) : "0";
            table.Children.Add(StatsCell(PriceText(row.Min, format), r + 1, 1, brush, true));
            table.Children.Add(StatsCell(PriceText(row.Max, format), r + 1, 2, brush, true));
            table.Children.Add(StatsCell(
                row.SizePips.ToString("0.0", CultureInfo.InvariantCulture), r + 1, 3, brush, true));
            table.Children.Add(StatsCell(PriceText(row.Avg, format), r + 1, 4, brush, true));
            table.Children.Add(StatsCell(PriceText(row.In, format), r + 1, 5, brush, true));
            table.Children.Add(StatsCell(PriceText(row.Out, format), r + 1, 6, brush, true));
        }
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = RangeText(loUnix, hiUnix),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(table);
        var closeButton = new Button
        {
            Content = "✕",
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ToolTip = "Close",
        };
        closeButton.Click += (_, _) => CloseStatsPopup();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(panel, 0);
        Grid.SetColumn(closeButton, 1);
        grid.Children.Add(panel);
        grid.Children.Add(closeButton);
        return new Border
        {
            Background = Brushes.White,
            BorderBrush = BrushFor(unchecked((int)0xFF888888)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 8),
            Cursor = Cursors.Arrow,
            Child = grid,
        };
    }

    private static TextBlock StatsCell(string text, int row, int column, Brush brush, bool numeric)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = 12,
            Margin = new Thickness(0, 1, 14, 1),
        };
        if (numeric)
        {
            block.FontFamily = new FontFamily("Consolas");
            block.HorizontalAlignment = HorizontalAlignment.Right;
        }
        Grid.SetRow(block, row);
        Grid.SetColumn(block, column);
        return block;
    }

    private static string PriceText(double points, string format) =>
        (points / 100000.0).ToString(format, CultureInfo.InvariantCulture);

    private static string RangeText(long loUnix, long hiUnix)
    {
        const string format = "dd-MMM-yy HH:mm";
        var from = DateTimeOffset.FromUnixTimeSeconds(loUnix).UtcDateTime;
        var to = DateTimeOffset.FromUnixTimeSeconds(hiUnix).UtcDateTime;
        return $"{from.ToString(format, CultureInfo.InvariantCulture)}  →  " +
            $"{to.ToString(format, CultureInfo.InvariantCulture)}     {DurationText(hiUnix - loUnix)}";
    }

    private static string DurationText(long seconds)
    {
        long minutes = seconds / 60;
        long days = minutes / 1440;
        long hours = minutes / 60 % 24;
        long mins = minutes % 60;
        var parts = new List<string>(3);
        if (days > 0) parts.Add($"{days}d");
        if (hours > 0) parts.Add($"{hours}h");
        if (mins > 0 || parts.Count == 0) parts.Add($"{mins}m");
        return string.Join(" ", parts);
    }

    private void AlignSeriesToGrid(Point pos)
    {
        var seriesList = _series;
        if (seriesList == null || _pointsPerRow <= 0 || _renderedK <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int ph = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (pw < 1 || ph < 1) return;
        int cx = Math.Clamp((int)Math.Floor(pos.X * dpi.DpiScaleX), 0, pw - 1);
        int cy = Math.Clamp((int)Math.Floor(pos.Y * dpi.DpiScaleY), 0, ph - 1);
        long bucket = _renderedStartBucket + cx;
        double cursorPrice = _topPrice - cy * _pointsPerRow;
        double gridPrice = Math.Round(cursorPrice / ChartRasterizer.GridPriceStepPoints)
            * ChartRasterizer.GridPriceStepPoints;
        double flattenShift = FlattenShiftVirtual(bucket * (_renderedK * 60L));
        var desired = new Dictionary<string, double>();
        foreach (var s in seriesList)
        {
            var column = ChartColumns.NearestColumn(s.History.Minutes, _renderedK, bucket, Compressor);
            if (!column.HasData) continue;
            desired[s.Symbol] = gridPrice - flattenShift - column.Avg - _priceOffsetPoints;
        }
        bool changed = false;
        foreach (var s in seriesList)
        {
            if (!desired.TryGetValue(s.Symbol, out var effective)) continue;
            double sourceEffective = s.SourceSymbol == null ? 0
                : desired.TryGetValue(s.SourceSymbol, out var se) ? se
                : EffectiveSeriesOffset(s.SourceSymbol);
            double offset = effective - sourceEffective;
            if (_seriesOffsetPoints.GetValueOrDefault(s.Symbol) != offset)
            {
                _seriesOffsetPoints[s.Symbol] = offset;
                changed = true;
            }
        }
        if (!changed) return;
        SeriesOffsetsChanged?.Invoke();
        Rebuild();
    }

    private bool RangeContainsColumn(int cx)
    {
        if (!_hasRange || _rangeSelecting || _renderedK <= 0) return false;
        long bucketSec = _renderedK * 60L;
        long lo = Math.Min(_rangeStartUnix, _rangeEndUnix);
        long hi = Math.Max(_rangeStartUnix, _rangeEndUnix);
        long x1 = ToVirtual(lo) / bucketSec - _renderedStartBucket;
        long x2 = ToVirtual(hi) / bucketSec - _renderedStartBucket;
        return cx >= x1 && cx <= x2;
    }

    private void ShowRangeMenu(Point pos)
    {
        var menu = new ContextMenu { PlacementTarget = this };
        var toMax = new MenuItem { Header = "Align to max" };
        toMax.Click += (_, _) => AlignRangeExtremeToGrid(pos, true);
        menu.Items.Add(toMax);
        var toMin = new MenuItem { Header = "Align to min" };
        toMin.Click += (_, _) => AlignRangeExtremeToGrid(pos, false);
        menu.Items.Add(toMin);
        menu.IsOpen = true;
    }

    private double? RangeExtreme(SymbolSeries s, long loUnix, long hiUnix, bool toMax)
    {
        double best = 0;
        bool has = false;
        void Take(Candle c)
        {
            double v = (toMax ? c.Max : c.Min) + FlattenShift(c.MinuteUnixSeconds);
            if (has && (toMax ? v <= best : v >= best)) return;
            best = v;
            has = true;
        }
        var minutes = s.History.Minutes;
        for (int i = LowerBound(minutes, loUnix);
             i < minutes.Length && minutes[i].MinuteUnixSeconds <= hiUnix; i++)
            Take(minutes[i]);
        foreach (var c in s.History.Live)
            if (c.MinuteUnixSeconds >= loUnix && c.MinuteUnixSeconds <= hiUnix)
                Take(c);
        return has ? best : null;
    }

    private (double Target, bool Pan) GridTarget(double cursorPrice, double screenRange, bool toMax)
    {
        const double step = ChartRasterizer.GridPriceStepPoints;
        double middle = _topPrice - screenRange / 2;
        if (toMax)
        {
            double above = Math.Ceiling(cursorPrice / step) * step;
            if (above <= _topPrice) return (above, false);
            double below = above - step;
            if (below > middle) return (below, false);
            return (above - cursorPrice <= cursorPrice - below ? above : below, true);
        }
        double under = Math.Floor(cursorPrice / step) * step;
        if (under >= _topPrice - screenRange) return (under, false);
        double over = under + step;
        if (over < middle) return (over, false);
        return (cursorPrice - under <= over - cursorPrice ? under : over, true);
    }

    private void AlignRangeExtremeToGrid(Point pos, bool toMax)
    {
        var seriesList = _series;
        if (seriesList == null || SelectedRange is not { } range) return;
        if (_pointsPerRow <= 0 || _renderedK <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int ph = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (ph < 2) return;
        int cy = Math.Clamp((int)Math.Floor(pos.Y * dpi.DpiScaleY), 0, ph - 1);
        long loUnix = range.StartUnix;
        long hiUnix = range.EndUnix + _renderedK * 60L - 1;
        var desired = new Dictionary<string, double>();
        double screenRange = _pointsPerRow * (ph - 1);
        var (target, pan) = GridTarget(_topPrice - cy * _pointsPerRow, screenRange, toMax);
        foreach (var s in seriesList)
        {
            if (s.Editable || IsHidden(s.Symbol)) continue;
            if (_alignExcluded.Contains(s.Symbol)) continue;
            if (RangeExtreme(s, loUnix, hiUnix, toMax) is not { } extreme) continue;
            desired[s.Symbol] = target - extreme - _priceOffsetPoints;
        }
        if (desired.Count == 0)
        {
            Info?.Invoke("Align: no candles inside the selection");
            return;
        }
        bool changed = false;
        foreach (var s in seriesList)
        {
            if (!desired.TryGetValue(s.Symbol, out var effective)) continue;
            double sourceEffective = s.SourceSymbol == null ? 0
                : desired.TryGetValue(s.SourceSymbol, out var se) ? se
                : EffectiveSeriesOffset(s.SourceSymbol);
            double offset = effective - sourceEffective;
            if (_seriesOffsetPoints.GetValueOrDefault(s.Symbol) != offset)
            {
                _seriesOffsetPoints[s.Symbol] = offset;
                changed = true;
            }
        }
        if (pan)
        {
            double y = (toMax ? AlignEdgeFraction : 1 - AlignEdgeFraction) * (ph - 1);
            double newTop = Math.Clamp(
                target + y * _pointsPerRow, PanMinPrice, PanMaxPrice + screenRange);
            if (newTop != _topPrice)
            {
                _topPrice = newTop;
                changed = true;
            }
        }
        if (!changed) return;
        SeriesOffsetsChanged?.Invoke();
        Rebuild();
    }

    private static long ClampViewStart(long start, long firstBucket, long lastBucket, int pw)
    {
        long count = lastBucket - firstBucket + 1;
        long m = Math.Min(MinVisibleColumns, count);
        long lo = firstBucket + m - pw;
        long hi = lastBucket - m + 1;
        if (hi < lo) hi = lo;
        return Math.Clamp(start, lo, hi);
    }

    private void OnZoom(object sender, MouseWheelEventArgs e)
    {
        if (_pivotDragging || _selDragging)
        {
            e.Handled = true;
            return;
        }
        HideHover();
        if (TiltedGridVisible && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            var dpiRot = VisualTreeHelper.GetDpi(this);
            var posRot = e.GetPosition(this);
            e.Handled = true;
            RotateTiltedGrid(e.Delta, (Keyboard.Modifiers & ModifierKeys.Control) != 0,
                posRot.X * dpiRot.DpiScaleX, posRot.Y * dpiRot.DpiScaleY);
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            OnVerticalZoom(e);
            return;
        }
        bool horizontalOnly = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (_series == null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        if (pw < 1) return;
        long firstUnix = ViewFirstUnix;
        long lastUnix = ViewLastUnix;
        int fitK = ChartColumns.FitK(firstUnix, lastUnix, pw);
        int k = _minutesPerColumn <= 0 || _minutesPerColumn > fitK ? fitK : _minutesPerColumn;
        long startBucket = _minutesPerColumn <= 0 ? firstUnix / (k * 60L) : _viewStartBucket;
        var pos = e.GetPosition(this);
        int xm = Math.Clamp((int)Math.Floor(pos.X * dpi.DpiScaleX), 0, pw - 1);
        long anchorTime = (startBucket + xm) * (k * 60L);
        int newK = e.Delta > 0
            ? (int)Math.Round(k / ZoomStep)
            : (int)Math.Round(k * ZoomStep);
        if (newK == k) newK = e.Delta > 0 ? k - 1 : k + 1;
        if (newK < 1) newK = 1;
        newK = ChartColumns.SnapK(newK);
        if (newK == k)
            newK = e.Delta > 0
                ? Math.Max(1, ChartColumns.SnapK(k - ChartColumns.Quantum(k)))
                : k + ChartColumns.Quantum(k);
        e.Handled = true;
        if (newK >= fitK)
        {
            if (_minutesPerColumn <= 0) return;
            _minutesPerColumn = 0;
            if (!horizontalOnly) ApplyVerticalZoom(PairedVerticalFactor((double)fitK / k), pos.Y);
            Rebuild();
            return;
        }
        if (newK == k && _minutesPerColumn > 0) return;
        long newFirstBucket = firstUnix / (newK * 60L);
        long newLastBucket = lastUnix / (newK * 60L);
        long newStart = ClampViewStart(anchorTime / (newK * 60L) - xm, newFirstBucket, newLastBucket, pw);
        _minutesPerColumn = newK;
        _viewStartBucket = newStart;
        if (_dragging)
        {
            _dragStartX = (int)Math.Floor(pos.X * dpi.DpiScaleX);
            _dragStartViewBucket = newStart;
        }
        if (!horizontalOnly) ApplyVerticalZoom(PairedVerticalFactor((double)newK / k), pos.Y);
        Rebuild();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RequestRebuild();
    }

    private void RequestRebuild()
    {
        _rebuildTimer.Stop();
        _rebuildTimer.Start();
    }

    private async void Rebuild()
    {
        if (!IsLoaded) return;
        var seriesList = _series;
        if (seriesList == null) return;
        var visibleSeries = seriesList.Where(s => !IsHidden(s.Symbol)).ToList();
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int ph = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (pw < 1 || ph < 1) return;
        if (_firstUnix > _lastUnix) return;
        int version = ++_version;
        if (_computing)
        {
            _pending = true;
            return;
        }
        _computing = true;
        int k;
        long startBucket;
        if (_minutesPerColumn <= 0)
        {
            k = ChartColumns.FitK(ViewFirstUnix, ViewLastUnix, pw);
            startBucket = ViewFirstUnix / (k * 60L);
        }
        else
        {
            k = _minutesPerColumn;
            long fb = ViewFirstUnix / (k * 60L);
            long lb = ViewLastUnix / (k * 60L);
            startBucket = ClampViewStart(_viewStartBucket, fb, lb, pw);
            _viewStartBucket = startBucket;
        }
        double topPrice = _topPrice;
        double pointsPerRow = _pointsPerRow;
        var lineOffsets = new double[visibleSeries.Count];
        for (int i = 0; i < visibleSeries.Count; i++)
            lineOffsets[i] = SeriesOffset(visibleSeries[i].Symbol);
        bool scaleInit = pointsPerRow <= 0;
        string? hideDrawSymbol = _selDragging && _selMoved ? _selSymbol : null;
        int hideDrawLine = _selLine;
        var hidePivotSymbol = _hidePivotSymbol;
        int hidePivotIndex = _hidePivotIndex;
        bool calVisible = _calendarVisible && CalendarLinesDrawn(k);
        var calEntries = _calendarEntries;
        var calShown = _calendarShown;
        var map = Compressor;
        var flatten = _flatten;
        var tiltedGrid = new TiltedGridSettings(
            TiltedRenderSettings(true), TiltedRenderSettings(false));
        var edges = ChartColumns.ColumnEdges(map, k, startBucket, pw);
        var swRender = _firstRenderLogged ? null : Stopwatch.StartNew();
        string? hotShift = _shiftHotSymbol;
        int hotShiftWidth = _shiftDragging ? 1 : ShiftHotWidthPx;
        var shiftLines = new List<(string Symbol, RenderLine Line)>();
        RenderLine? hotLine = null;
        try
        {
            await Task.Run(() =>
            {
                var lines = new List<RenderLine>(visibleSeries.Count);
                var columnShifts = flatten?.ColumnShifts(startBucket - pw, pw * 2, k * 60L);
                int hotIndex = -1;
                for (int si = 0; si < visibleSeries.Count; si++)
                {
                    var s = visibleSeries[si];
                    if (s.History.Minutes.Length == 0 || s.BottomPanel) continue;
                    var view = ChartColumns.BuildView(s.History, k, startBucket - pw, pw * 2, map);
                    var chosen = LineDecimator.ChooseValues(view.Columns, pw, NoiseThreshold);
                    var last = s.History.LastCandle;
                    int lastPrice = s.History.HasLastTick ? s.History.LastTick : last.Avg;
                    var line = new RenderLine(view, chosen, s.ColorArgb, lastPrice,
                        (map?.ToVirtual(last.MinuteUnixSeconds) ?? last.MinuteUnixSeconds) / (k * 60L),
                        lineOffsets[si], columnShifts);
                    if (s.TimeShift) shiftLines.Add((s.Symbol, line));
                    if (s.Symbol == hotShift) hotIndex = lines.Count;
                    lines.Add(line);
                }
                if (scaleInit)
                {
                    int mn = int.MaxValue;
                    int mx = int.MinValue;
                    foreach (var line in lines)
                    {
                        var cols = line.Series.Columns;
                        int visEnd = Math.Min(cols.Length, pw * 2);
                        for (int i = pw; i < visEnd; i++)
                        {
                            if (!cols[i].HasData) continue;
                            int shift = columnShifts == null ? 0 : (int)Math.Round(columnShifts[i]);
                            if (cols[i].Min + shift < mn) mn = cols[i].Min + shift;
                            if (cols[i].Max + shift > mx) mx = cols[i].Max + shift;
                        }
                    }
                    foreach (var s in visibleSeries)
                    {
                        if (s.Transform == null) continue;
                        foreach (var poly in VectorLines(s))
                            foreach (var p in poly)
                            {
                                long v = map?.ToVirtual(p.UnixSeconds) ?? p.UnixSeconds;
                                long b = v / (k * 60L);
                                if (b < startBucket || b >= startBucket + pw) continue;
                                int d = s.Transform.ToDisplay(p.Value)
                                    + (int)Math.Round(flatten?.ShiftAt(v) ?? 0);
                                if (d < mn) mn = d;
                                if (d > mx) mx = d;
                            }
                    }
                    if (mn <= mx)
                    {
                        int pad = Math.Max(1, (mx - mn) / 20);
                        topPrice = mx + pad;
                        pointsPerRow = (mx - mn + 2.0 * pad) / Math.Max(1, ph - 1);
                    }
                }
                if (hotIndex >= 0)
                {
                    hotLine = lines[hotIndex] with { Width = hotShiftWidth };
                    lines.RemoveAt(hotIndex);
                }
                if (_staging.Length < pw * ph) _staging = new int[pw * ph];
                ChartRasterizer.Render(_staging, pw, ph, lines, Palette, k, startBucket, edges,
                    topPrice, pointsPerRow, tiltedGrid);
                if (pointsPerRow > 0)
                    for (int si = 0; si < visibleSeries.Count; si++)
                    {
                        var s = visibleSeries[si];
                        if (s.Transform == null) continue;
                        var polys = VectorLines(s);
                        if (polys.Length == 0) continue;
                        bool ownPivots = s.DrawingLines is not { Length: > 0 };
                        int skipAround = ownPivots && hidePivotSymbol == s.Symbol ? hidePivotIndex : -1;
                        for (int li = 0; li < polys.Length; li++)
                        {
                            if (!ownPivots && hideDrawSymbol == s.Symbol && li == hideDrawLine) continue;
                            var poly = polys[li];
                            for (int i = 1; i < poly.Length; i++)
                            {
                                if (i - 1 == skipAround || i == skipAround) continue;
                                long ta = poly[i - 1].UnixSeconds;
                                long tb = poly[i].UnixSeconds;
                                if (Math.Max(ta, tb) < edges[0] || Math.Min(ta, tb) > edges[pw]) continue;
                                long va = map?.ToVirtual(ta) ?? ta;
                                long vb = map?.ToVirtual(tb) ?? tb;
                                DrawFlattenedSegment(_staging, pw, ph, flatten, k, startBucket,
                                    topPrice, pointsPerRow, lineOffsets[si],
                                    va, s.Transform.ToDisplay(poly[i - 1].Value),
                                    vb, s.Transform.ToDisplay(poly[i].Value), s.ColorArgb);
                            }
                        }
                    }
                if (pointsPerRow > 0)
                    for (int si = 0; si < visibleSeries.Count; si++)
                    {
                        var s = visibleSeries[si];
                        if (s.DealMarks is not { Length: > 0 } || s.Transform == null) continue;
                        DrawDealMarks(_staging, pw, ph, s, flatten, map, k, startBucket,
                            topPrice, pointsPerRow, lineOffsets[si]);
                    }
                if (calVisible && calEntries.Length > 0)
                    DrawCalendar(_staging, pw, ph, calEntries, k, startBucket, map, calShown);
                if (hotLine != null && pointsPerRow > 0)
                    ChartRasterizer.DrawSeries(_staging, pw, ph, hotLine.Value, startBucket,
                        topPrice, pointsPerRow);
                int panelBottom = ph - 1 - ChartRasterizer.EntryPanelBottomMarginPx;
                foreach (var s in visibleSeries)
                {
                    if (!s.BottomPanel || s.History.Minutes.Length == 0) continue;
                    if (s.AgePanel)
                    {
                        var ages = PriceAgeColumns.Build(
                            s.History, k, startBucket, pw, map, s.AgeMirror);
                        ChartRasterizer.DrawAgePanel(_staging, pw, ph, ages, panelBottom, s.ColorArgb);
                        panelBottom -= ChartRasterizer.AgePanelHeightPx + ChartRasterizer.EntryPanelGapPx;
                    }
                    else
                    {
                        var states = EntryPointsColumns.Build(s.History, k, startBucket, pw, map);
                        ChartRasterizer.DrawEntryPanel(
                            _staging, pw, ph, states, panelBottom, Palette.Background);
                        panelBottom -= ChartRasterizer.EntryPanelHeightPx + ChartRasterizer.EntryPanelGapPx;
                    }
                }
            });
            if (version == _version)
            {
                if (scaleInit && pointsPerRow > 0)
                {
                    _topPrice = topPrice;
                    _pointsPerRow = pointsPerRow;
                }
                _renderedK = k;
                _renderedStartBucket = startBucket;
                _renderedTopPrice = topPrice;
                _renderedPointsPerRow = pointsPerRow;
                _shiftLines.Clear();
                _shiftLines.AddRange(shiftLines);
                Present(pw, ph, dpi);
                if (swRender != null)
                {
                    _firstRenderLogged = true;
                    Info?.Invoke($"Chart first render: {swRender.ElapsedMilliseconds} ms " +
                        $"({visibleSeries.Count} series, {pw}x{ph}px, k={k})");
                }
                ViewChanged?.Invoke(k, startBucket, pw, map);
                StateChanged?.Invoke(new ChartViewState
                {
                    WeekendsHidden = _weekendsHidden,
                    MinutesPerColumn = _minutesPerColumn,
                    ViewStartBucket = _viewStartBucket,
                    TopPrice = _topPrice,
                    PointsPerRow = _pointsPerRow,
                    PriceOffsetPoints = _priceOffsetPoints,
                    SymbolOffsetPoints = new Dictionary<string, double>(_seriesOffsetPoints),
                    RelativeIndicatorOffsets = true,
                    HiddenSymbols = new List<string>(_hiddenSymbols),
                    CollapsedSymbols = new List<string>(_collapsedSources),
                    CalendarVisible = _calendarVisible,
                    FlattenSymbol = _flattenSymbol,
                    FlattenLine = _flattenLine,
                    TiltedUpGridIndex = _tiltedUpIndex,
                    TiltedDownGridIndex = _tiltedDownIndex,
                    TiltedGrids = _tiltedGrids.Select(g => g.Clone()).ToList(),
                });
            }
        }
        catch (Exception ex)
        {
            Info?.Invoke("Chart failed: " + ex.Message);
        }
        finally
        {
            _computing = false;
        }
        if (_pending)
        {
            _pending = false;
            Rebuild();
        }
    }

    private static void DrawDealMarks(int[] buffer, int pw, int ph, SymbolSeries s,
        FlattenMap? flatten, WeekendCompressor? map, int k, long startBucket,
        double topPrice, double pointsPerRow, double offset)
    {
        double bucketSec = k * 60.0;
        double X(long v) => v / bucketSec - startBucket;
        double Y(long v, double display) =>
            (topPrice - offset - display - (flatten?.ShiftAt(v) ?? 0)) / pointsPerRow;
        foreach (var mark in s.DealMarks!)
        {
            long va = map?.ToVirtual(mark.EntryUnix) ?? mark.EntryUnix;
            long vb = mark.Closed ? map?.ToVirtual(mark.ExitUnix) ?? mark.ExitUnix : va;
            double xa = X(va);
            double xb = X(vb);
            if (Math.Max(xa, xb) < -ChartRasterizer.DealMarkerHalfPx
                || Math.Min(xa, xb) >= pw + ChartRasterizer.DealMarkerHalfPx) continue;
            int color = mark.Closed
                ? mark.Win ? ChartRasterizer.DealWinArgb : ChartRasterizer.DealLossArgb
                : s.ColorArgb;
            int entryDisplay = s.Transform!.ToDisplay(mark.EntryValue);
            double ya = Y(va, entryDisplay);
            if (mark.Closed)
            {
                int exitDisplay = s.Transform.ToDisplay(mark.ExitValue);
                double yb = Y(vb, exitDisplay);
                if (Math.Abs(xb - xa) >= 2)
                {
                    double cx = xa;
                    double cy = ya;
                    if (flatten != null && va != vb)
                        foreach (long t in flatten.CutsBetween(va, vb))
                        {
                            double f = (double)(t - va) / (vb - va);
                            double x = X(t);
                            double y = Y(t, entryDisplay + (exitDisplay - entryDisplay) * f);
                            ChartRasterizer.BlendSegment(buffer, pw, ph, cx, cy, x, y,
                                color, ChartRasterizer.DealConnectorAlpha);
                            cx = x;
                            cy = y;
                        }
                    ChartRasterizer.BlendSegment(buffer, pw, ph, cx, cy, xb, yb,
                        color, ChartRasterizer.DealConnectorAlpha);
                }
                ChartRasterizer.FillDisc(buffer, pw, ph,
                    (int)Math.Round(xb), (int)Math.Round(yb),
                    ChartRasterizer.DealMarkerHalfPx, color);
            }
            ChartRasterizer.FillTriangle(buffer, pw, ph,
                (int)Math.Round(xa), (int)Math.Round(ya),
                ChartRasterizer.DealMarkerHalfPx, mark.Buy, color);
        }
    }

    private static void DrawFlattenedSegment(int[] buffer, int pw, int ph, FlattenMap? flatten,
        int k, long startBucket, double topPrice, double pointsPerRow, double offset,
        long fromVirtual, double fromDisplay, long toVirtual, double toDisplay, int color)
    {
        double X(long v) => v / (k * 60.0) - startBucket;
        double Y(long v, double display) =>
            (topPrice - offset - display - (flatten?.ShiftAt(v) ?? 0)) / pointsPerRow;
        double x0 = X(fromVirtual);
        double y0 = Y(fromVirtual, fromDisplay);
        if (flatten != null && fromVirtual != toVirtual)
            foreach (long t in flatten.CutsBetween(fromVirtual, toVirtual))
            {
                double f = (double)(t - fromVirtual) / (toVirtual - fromVirtual);
                double x = X(t);
                double y = Y(t, fromDisplay + (toDisplay - fromDisplay) * f);
                ChartRasterizer.DrawSegment(buffer, pw, ph, x0, y0, x, y, color);
                x0 = x;
                y0 = y;
            }
        ChartRasterizer.DrawSegment(buffer, pw, ph,
            x0, y0, X(toVirtual), Y(toVirtual, toDisplay), color);
    }

    private void Present(int pw, int ph, DpiScale dpi)
    {
        if (_bitmap == null || _bitmap.PixelWidth != pw || _bitmap.PixelHeight != ph)
        {
            _bitmap = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Bgr32, null);
            _image.Source = _bitmap;
        }
        _image.Width = pw / dpi.DpiScaleX;
        _image.Height = ph / dpi.DpiScaleY;
        _bitmap.WritePixels(new Int32Rect(0, 0, pw, ph), _staging, pw * 4, 0);
        UpdateRangeVisuals();
        UpdateEditMarkers(dpi, pw, ph);
        UpdateDensityOverlay();
        if (_drawSymbol != null)
        {
            UpdateDrawPreview(dpi);
            ReanchorDrawCursor(dpi);
        }
        if (_selSymbol != null) UpdateSelectionVisuals(dpi);
        if (!_calendarVisible || _renderedK <= 0 || !CalendarLinesDrawn(_renderedK))
        {
            HideCalendarHover();
            CloseCalendarPopup();
        }
    }

    private void ReanchorDrawCursor(DpiScale dpi)
    {
        if (_drawPoints.Count == 0 || _drawCursor.Visibility != Visibility.Visible) return;
        var s = _drawSymbol == null ? null : GetSeries(_drawSymbol);
        if (s?.Transform == null || _renderedK <= 0 || _renderedPointsPerRow <= 0) return;
        var from = ProjectDrawPoint(s.Transform, SeriesOffset(s.Symbol), _drawPoints[^1], dpi);
        var pos = Mouse.GetPosition(this);
        _drawCursor.X1 = from.X;
        _drawCursor.Y1 = from.Y;
        _drawCursor.X2 = pos.X;
        _drawCursor.Y2 = pos.Y;
    }

    private static int? DensityOptionKey(Key key) => key switch
    {
        Key.D1 or Key.NumPad1 => 0,
        Key.D2 or Key.NumPad2 => 1,
        Key.D3 or Key.NumPad3 => 2,
        Key.D4 or Key.NumPad4 => 3,
        Key.D5 or Key.NumPad5 => 4,
        Key.D6 or Key.NumPad6 => 5,
        Key.D7 or Key.NumPad7 => 6,
        Key.D8 or Key.NumPad8 => 7,
        Key.D9 or Key.NumPad9 => 8,
        Key.D0 or Key.NumPad0 => 9,
        _ => null,
    };

    private bool SelectDensityOption(int option)
    {
        var series = _series;
        if (series == null || !series.Any(s => s.DensityPanel)) return false;
        if (_densitySelected != option)
        {
            _densitySelected = option;
            DensitySelectedChanged?.Invoke(option);
        }
        _densityAnchorColumn = long.MinValue;
        UpdateDensityOverlay();
        return true;
    }

    private void UpdateDensityOverlay()
    {
        var series = _series;
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int ph = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        List<SymbolSeries>? panels = null;
        if (series != null && _renderedK > 0 && _renderedPointsPerRow > 0
            && pw >= DensityMaxWidthPx && ph >= 1)
            foreach (var s in series)
                if (s.DensityPanel && !IsHidden(s.Symbol)
                    && s.SourceSymbol != null && s.DensityWindows is { Length: > 0 })
                    (panels ??= new List<SymbolSeries>()).Add(s);
        if (panels == null)
        {
            _densityImage.Visibility = Visibility.Collapsed;
            _densityLabelPanel.Visibility = Visibility.Collapsed;
            _densityAnchorColumn = long.MinValue;
            return;
        }
        int cx = _cursorOnChart ? Math.Clamp(_cursorPx, 0, pw - 1) : pw - 1;
        _densityAnchorColumn = _renderedStartBucket + cx;
        long anchorUnix = ToRealEnd((_densityAnchorColumn + 1) * (_renderedK * 60L)) - 60;
        if (_densityStaging.Length < DensityMaxWidthPx * ph)
            _densityStaging = new int[DensityMaxWidthPx * ph];
        Array.Clear(_densityStaging, 0, DensityMaxWidthPx * ph);
        bool drew = false;
        foreach (var s in panels)
        {
            var minutes = GetSeries(s.SourceSymbol!)?.History.Minutes;
            if (minutes == null || minutes.Length == 0) continue;
            int option = Math.Clamp(_densitySelected, 0, s.DensityWindows!.Length);
            int window = option == s.DensityWindows.Length
                ? int.MaxValue
                : s.DensityWindows[option];
            var histogram = DensityProfile.Build(minutes, anchorUnix, window);
            if (histogram.MaxCount <= 0) continue;
            DrawDensity(_densityStaging, DensityMaxWidthPx, ph, histogram,
                SeriesOffset(s.SourceSymbol!), anchorUnix, s.ColorArgb);
            drew = true;
        }
        if (_densityBmp == null || _densityBmp.PixelWidth != DensityMaxWidthPx
            || _densityBmp.PixelHeight != ph)
        {
            _densityBmp = new WriteableBitmap(
                DensityMaxWidthPx, ph, 96, 96, PixelFormats.Pbgra32, null);
            _densityImage.Source = _densityBmp;
        }
        _densityBmp.WritePixels(
            new Int32Rect(0, 0, DensityMaxWidthPx, ph), _densityStaging, DensityMaxWidthPx * 4, 0);
        _densityImage.Width = DensityMaxWidthPx / dpi.DpiScaleX;
        _densityImage.Height = ph / dpi.DpiScaleY;
        Canvas.SetLeft(_densityImage, (pw - DensityMaxWidthPx) / dpi.DpiScaleX);
        Canvas.SetTop(_densityImage, 0);
        _densityImage.Visibility = drew ? Visibility.Visible : Visibility.Collapsed;
        UpdateDensityLabels(panels);
    }

    private void DrawDensity(int[] buffer, int width, int height, DensityHistogram histogram,
        double offset, long anchorUnix, int colorArgb)
    {
        int fill = PremultiplyArgb(colorArgb, DensityFillAlpha);
        int edge = colorArgb | unchecked((int)0xFF000000);
        for (int y = 0; y < height; y++)
        {
            double top = YToDisplay(y, offset, anchorUnix);
            double bottom = YToDisplay(y + 1, offset, anchorUnix);
            int pipLo = DensityProfile.PipLevel((int)Math.Round(bottom));
            int pipHi = DensityProfile.PipLevel((int)Math.Round(top));
            int count = DensityProfile.MaxCountIn(histogram, pipLo, pipHi);
            if (count <= 0) continue;
            int barWidth = Math.Max(1, (int)Math.Round(
                (double)count * width / histogram.MaxCount));
            int row = y * width;
            int x0 = width - barWidth;
            buffer[row + x0] = BlendOverPremultiplied(buffer[row + x0], edge);
            for (int x = x0 + 1; x < width; x++)
                buffer[row + x] = BlendOverPremultiplied(buffer[row + x], fill);
        }
    }

    private static int PremultiplyArgb(int argb, int alpha)
    {
        int r = (argb >> 16) & 0xFF;
        int g = (argb >> 8) & 0xFF;
        int b = argb & 0xFF;
        return (alpha << 24) | (r * alpha / 255 << 16) | (g * alpha / 255 << 8) | (b * alpha / 255);
    }

    private static int BlendOverPremultiplied(int dst, int src)
    {
        int srcA = src >>> 24;
        if (srcA == 255 || dst == 0) return src;
        int inv = 255 - srcA;
        int a = srcA + (dst >>> 24) * inv / 255;
        int r = ((src >> 16) & 0xFF) + ((dst >> 16) & 0xFF) * inv / 255;
        int g = ((src >> 8) & 0xFF) + ((dst >> 8) & 0xFF) * inv / 255;
        int b = (src & 0xFF) + (dst & 0xFF) * inv / 255;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private void UpdateDensityLabels(List<SymbolSeries> panels)
    {
        while (_densityLabelPanel.Children.Count > panels.Count)
            _densityLabelPanel.Children.RemoveAt(_densityLabelPanel.Children.Count - 1);
        while (_densityLabelPanel.Children.Count < panels.Count)
            _densityLabelPanel.Children.Add(new TextBlock
            {
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
        for (int i = 0; i < panels.Count; i++)
        {
            var s = panels[i];
            var label = (TextBlock)_densityLabelPanel.Children[i];
            int option = Math.Clamp(_densitySelected, 0, s.DensityWindows!.Length);
            label.Text = option == s.DensityWindows.Length
                ? $"{s.Symbol} 0: all"
                : $"{s.Symbol} {option + 1}: {FormatDensityWindow(s.DensityWindows[option])}";
            label.Foreground = BrushFor(s.ColorArgb);
        }
        _densityLabelPanel.Visibility = Visibility.Visible;
    }

    private static string FormatDensityWindow(int bars) =>
        bars % 1440 == 0 ? bars / 1440 + "d"
        : bars % 60 == 0 ? bars / 60 + "h"
        : bars + "m";

    private static SolidColorBrush BrushFor(int argb)
    {
        if (!BrushCache.TryGetValue(argb, out var brush))
        {
            brush = new SolidColorBrush(Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            brush.Freeze();
            BrushCache[argb] = brush;
        }
        return brush;
    }

    public bool IsFlattenLine(string symbol, int line) =>
        _flattenSymbol == symbol && _flattenLine == line;

    public void SetFlattenLine(string? symbol, int line)
    {
        _flattenSymbol = symbol;
        _flattenLine = symbol == null ? -1 : line;
        RebuildFlatten();
        Info?.Invoke(_flatten == null
            ? "Flatten off"
            : $"Flattened by {_flattenSymbol} line {_flattenLine + 1}");
        Rebuild();
    }

    private void RebuildFlatten()
    {
        _flatten = null;
        if (_flattenSymbol == null) return;
        var s = GetSeries(_flattenSymbol);
        if (s?.Transform == null || s.DrawingLines == null
            || _flattenLine < 0 || _flattenLine >= s.DrawingLines.Length)
        {
            _flattenSymbol = null;
            _flattenLine = -1;
            return;
        }
        _flatten = FlattenMap.Build(s.DrawingLines[_flattenLine], s.Transform, Compressor);
    }

    private double FlattenShiftVirtual(long virtualSeconds) =>
        _flatten?.ShiftAt(virtualSeconds) ?? 0;

    private double FlattenShift(long unixSeconds) =>
        _flatten == null ? 0 : _flatten.ShiftAt(ToVirtual(unixSeconds));

    private double DisplayToY(double display, double offset, long unixSeconds) =>
        (_renderedTopPrice - offset - display - FlattenShift(unixSeconds)) / _renderedPointsPerRow;

    private double YToDisplay(double y, double offset, long unixSeconds) =>
        _renderedTopPrice - offset - y * _renderedPointsPerRow - FlattenShift(unixSeconds);

    private double PanMinPrice => _globalMinPrice - (_flatten?.MaxAbsShift ?? 0);

    private double PanMaxPrice => _globalMaxPrice + (_flatten?.MaxAbsShift ?? 0);

    private double SeriesOffset(string symbol) =>
        _priceOffsetPoints + EffectiveSeriesOffset(symbol);

    private double EffectiveSeriesOffset(string symbol)
    {
        double total = 0;
        string? current = symbol;
        for (int depth = 0; current != null && depth < 8; depth++)
        {
            total += _seriesOffsetPoints.GetValueOrDefault(current);
            current = GetSeries(current)?.SourceSymbol;
        }
        return total;
    }

    private (int Series, int Point)? FindPivotAt(int cx, int cy)
    {
        var series = _series;
        if (series == null || EditLocked || _renderedK <= 0 || _renderedPointsPerRow <= 0) return null;
        long bucketSec = _renderedK * 60L;
        int r = Math.Max(1, EditHitRadiusPx);
        long best = long.MaxValue;
        (int, int)? bestHit = null;
        for (int si = 0; si < series.Count; si++)
        {
            var s = series[si];
            if (!s.Editable || IsHidden(s.Symbol)) continue;
            if (!_pivots.TryGetValue(s.Symbol, out var sp) || sp.Raw.Length == 0) continue;
            double offset = SeriesOffset(s.Symbol);
            long tLo = ToReal((_renderedStartBucket + cx - r) * bucketSec);
            long tHi = ToReal((_renderedStartBucket + cx + r + 1) * bucketSec);
            for (int i = ZigZagSymbol.FirstAtOrAfter(sp.Raw, tLo);
                 i < sp.Raw.Length && sp.Raw[i].UnixSeconds < tHi; i++)
            {
                long dx = ToVirtual(sp.Raw[i].UnixSeconds) / bucketSec - _renderedStartBucket - cx;
                long dy = (long)Math.Round(
                    DisplayToY(sp.Display[i], offset, sp.Raw[i].UnixSeconds)) - cy;
                long d2 = dx * dx + dy * dy;
                if (d2 <= (long)r * r && d2 < best)
                {
                    best = d2;
                    bestHit = (si, i);
                }
            }
        }
        return bestHit;
    }

    private void OnEditMove(object sender, MouseEventArgs e)
    {
        if (_series == null) return;
        if (_rangeSelecting)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndRangeSelect();
                return;
            }
            var p = e.GetPosition(this);
            UpdateRangeSelect((int)Math.Floor(p.X * VisualTreeHelper.GetDpi(this).DpiScaleX));
            return;
        }
        if (_drawSymbol != null)
        {
            UpdateDrawCursor(e);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var pos = e.GetPosition(this);
        int cx = (int)Math.Floor(pos.X * dpi.DpiScaleX);
        int cy = (int)Math.Floor(pos.Y * dpi.DpiScaleY);
        if (_selDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                CommitSelDrag();
                return;
            }
            UpdateSelDrag(cx, cy);
            return;
        }
        if (_pivotDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                CommitPivotDrag();
                return;
            }
            if (cx != _editPressX || cy != _editPressY) _editMoved = true;
            if (!_editMoved) return;
            UpdateDragTarget(cx, cy);
            UpdateDragPreview(dpi);
            return;
        }
        if (_dragging)
        {
            HideHover();
            HideCalendarHover();
            return;
        }
        if (UpdateHover(cx, cy, dpi)) HideCalendarHover();
        else UpdateCalendarHover(cx, cy, dpi);
    }

    private bool UpdateHover(int cx, int cy, DpiScale dpi)
    {
        var hit = FindPivotAt(cx, cy);
        if (hit == null)
        {
            HideHover();
            return false;
        }
        var (si, pi) = hit.Value;
        var s = _series![si];
        var sp = _pivots[s.Symbol];
        long bucketSec = _renderedK * 60L;
        double offset = SeriesOffset(s.Symbol);
        double px = ToVirtual(sp.Raw[pi].UnixSeconds) / bucketSec - _renderedStartBucket + 0.5;
        double py = DisplayToY(sp.Display[pi], offset, sp.Raw[pi].UnixSeconds) + 0.5;
        _hoverCircle.Width = 2.0 * PivotCircleRadiusPx / dpi.DpiScaleX;
        _hoverCircle.Height = 2.0 * PivotCircleRadiusPx / dpi.DpiScaleY;
        _hoverCircle.Stroke = BrushFor(s.ColorArgb);
        _hoverCircle.StrokeThickness = 1.5 / dpi.DpiScaleX;
        Canvas.SetLeft(_hoverCircle, (px - PivotCircleRadiusPx) / dpi.DpiScaleX);
        Canvas.SetTop(_hoverCircle, (py - PivotCircleRadiusPx) / dpi.DpiScaleY);
        _hoverCircle.Visibility = Visibility.Visible;
        return true;
    }

    private void HideHover()
    {
        _hoverCircle.Visibility = Visibility.Collapsed;
    }

    private void UpdateCalendarHover(int cx, int cy, DpiScale dpi)
    {
        if (!_calendarVisible || _calendarEntries.Length == 0
            || _renderedK <= 0 || !CalendarLinesDrawn(_renderedK))
        {
            HideCalendarHover();
            return;
        }
        int x = FindCalendarLineX(cx);
        if (x < 0)
        {
            HideCalendarHover();
            return;
        }
        var entries = CalendarColumnEntries(x);
        if (entries.Count == 0)
        {
            HideCalendarHover();
            return;
        }
        int argb = Currencies.ColorArgb(entries[0].Currency);
        int ph = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        double centerXpx = x + 0.5;
        double centerYpx = ph - CalCircleBottomMarginPx;
        _calHoverCircle.Width = 2.0 * CalCircleRadiusPx / dpi.DpiScaleX;
        _calHoverCircle.Height = 2.0 * CalCircleRadiusPx / dpi.DpiScaleY;
        _calHoverCircle.Fill = BrushFor(argb);
        _calHoverCircle.StrokeThickness = 1.5 / dpi.DpiScaleX;
        Canvas.SetLeft(_calHoverCircle, (centerXpx - CalCircleRadiusPx) / dpi.DpiScaleX);
        Canvas.SetTop(_calHoverCircle, (centerYpx - CalCircleRadiusPx) / dpi.DpiScaleY);
        _calHoverCircle.Visibility = Visibility.Visible;
        _calHoverX = x;
        _calCircleCenterX = centerXpx;
        _calCircleCenterY = centerYpx;
        _calCircleRadiusPx = CalCircleRadiusPx;
    }

    private void HideCalendarHover()
    {
        _calHoverCircle.Visibility = Visibility.Collapsed;
        _calHoverX = -1;
    }

    private int FindCalendarLineX(int cx)
    {
        long bucketSec = _renderedK * 60L;
        long tLo = ToReal((_renderedStartBucket + cx - LineHitRadiusPx) * bucketSec);
        long tHi = ToReal((_renderedStartBucket + cx + LineHitRadiusPx + 1) * bucketSec);
        int bestX = -1;
        int bestDist = int.MaxValue;
        int bestImp = -1;
        for (int i = CalendarLowerBound(_calendarEntries, tLo);
             i < _calendarEntries.Length && _calendarEntries[i].UnixSeconds < tHi; i++)
        {
            if (!CalendarImpactShown(_calendarEntries[i].Impact)) continue;
            int x = (int)(ToVirtual(_calendarEntries[i].UnixSeconds) / bucketSec - _renderedStartBucket);
            int d = Math.Abs(x - cx);
            if (d > LineHitRadiusPx) continue;
            int imp = _calendarEntries[i].Impact;
            if (d < bestDist || (d == bestDist && imp > bestImp))
            {
                bestDist = d;
                bestX = x;
                bestImp = imp;
            }
        }
        return bestX;
    }

    private List<CalendarEntry> CalendarColumnEntries(int x)
    {
        var result = new List<CalendarEntry>();
        if (_renderedK <= 0) return result;
        long bucketSec = _renderedK * 60L;
        long tLo = ToReal((_renderedStartBucket + x) * bucketSec);
        long tHi = ToReal((_renderedStartBucket + x + 1) * bucketSec);
        for (int i = CalendarLowerBound(_calendarEntries, tLo);
             i < _calendarEntries.Length && _calendarEntries[i].UnixSeconds < tHi; i++)
            if (CalendarImpactShown(_calendarEntries[i].Impact))
                result.Add(_calendarEntries[i]);
        result.Sort((a, b) => b.Impact - a.Impact);
        return result;
    }

    private void ShowCalendarPopup(int x)
    {
        var lookup = CalendarDetailLookup;
        if (lookup == null) return;
        var entries = CalendarColumnEntries(x);
        if (entries.Count == 0) return;
        var details = new List<CalendarDetail>();
        foreach (var en in entries)
        {
            var d = lookup(en);
            if (d != null) details.Add(d);
        }
        if (details.Count == 0) return;
        CloseCalendarPopup();
        var dpi = VisualTreeHelper.GetDpi(this);
        double cxDip = _calCircleCenterX / dpi.DpiScaleX;
        double cyDip = _calCircleCenterY / dpi.DpiScaleY;
        _calPopup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Top,
            PlacementRectangle = new Rect(
                cxDip - CalCircleRadiusPx, cyDip - CalCircleRadiusPx,
                2 * CalCircleRadiusPx, 2 * CalCircleRadiusPx),
            StaysOpen = true,
            AllowsTransparency = true,
            Child = BuildCalendarPopupContent(details),
        };
        _calPopup.IsOpen = true;
    }

    private void CloseCalendarPopup()
    {
        if (_calPopup == null) return;
        _calPopup.IsOpen = false;
        _calPopup = null;
    }

    private FrameworkElement BuildCalendarPopupContent(List<CalendarDetail> details)
    {
        var panel = new StackPanel();
        for (int i = 0; i < details.Count; i++)
        {
            if (i > 0)
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Background = BrushFor(unchecked((int)0xFFDDDDDD)),
                    Margin = new Thickness(0, 8, 0, 8),
                });
            var d = details[i];
            int argb = Currencies.ColorArgb(Currencies.IdOf(d.Currency));
            var when = DateTimeOffset.FromUnixTimeSeconds(d.UnixSeconds).UtcDateTime;
            panel.Children.Add(new TextBlock
            {
                Text = $"{when:ddd yyyy-MM-dd HH:mm} UTC  ·  {d.Currency}  ·  {ImpactText(d.ImpactLevel)}",
                Foreground = BrushFor(argb),
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = d.Event,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
            var vals = new List<string>();
            if (!string.IsNullOrWhiteSpace(d.Actual)) vals.Add($"Actual: {d.Actual}");
            if (!string.IsNullOrWhiteSpace(d.Forecast)) vals.Add($"Forecast: {d.Forecast}");
            if (!string.IsNullOrWhiteSpace(d.Previous)) vals.Add($"Previous: {d.Previous}");
            if (vals.Count > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = string.Join("     ", vals),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0),
                });
            if (!string.IsNullOrWhiteSpace(d.Detail))
                panel.Children.Add(new TextBlock
                {
                    Text = d.Detail.Replace(" | ", "\n"),
                    Foreground = BrushFor(unchecked((int)0xFF555555)),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Margin = new Thickness(0, 5, 0, 0),
                });
        }
        var closeButton = new Button
        {
            Content = "✕",
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ToolTip = "Close",
        };
        closeButton.Click += (_, _) => CloseCalendarPopup();
        var scroll = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 360,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(closeButton, 0);
        Grid.SetRow(scroll, 1);
        grid.Children.Add(closeButton);
        grid.Children.Add(scroll);
        return new Border
        {
            Background = Brushes.White,
            BorderBrush = BrushFor(unchecked((int)0xFF888888)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 10),
            MaxWidth = 400,
            Child = grid,
        };
    }

    private static string ImpactText(CalendarImpact impact) => impact switch
    {
        CalendarImpact.Highest => "Highest",
        CalendarImpact.High => "High",
        CalendarImpact.Medium => "Medium",
        CalendarImpact.Low => "Low",
        CalendarImpact.Holiday => "Holiday",
        _ => "",
    };

    private void BeginPivotDrag(int si, int pi, int cx, int cy)
    {
        var s = _series![si];
        var sp = _pivots[s.Symbol];
        _pivotDragging = true;
        _editMoved = false;
        _editPressX = cx;
        _editPressY = cy;
        _editSeries = si;
        _editPoint = pi;
        _editOrigUnix = sp.Raw[pi].UnixSeconds;
        _editOrigRaw = sp.Raw[pi].Value;
        _editOrigDisplay = sp.Display[pi];
        _editCurUnix = _editOrigUnix;
        _editCurDisplay = _editOrigDisplay;
        HideHover();
        var brush = BrushFor(s.ColorArgb);
        _dragLinePrev.Stroke = brush;
        _dragLineNext.Stroke = brush;
        _hidePivotSymbol = s.Symbol;
        _hidePivotIndex = pi;
        Rebuild();
        UpdateDragPreview(VisualTreeHelper.GetDpi(this));
        CaptureMouse();
    }

    private void UpdateDragTarget(int cx, int cy)
    {
        var s = _series![_editSeries];
        var sp = _pivots[s.Symbol];
        long bucketSec = _renderedK * 60L;
        long lo = long.MinValue / 2;
        long hi = long.MaxValue / 2;
        if (_editPoint > 0)
        {
            long prevT = sp.Raw[_editPoint - 1].UnixSeconds;
            lo = prevT == _editOrigUnix ? _editOrigUnix : prevT + 60;
        }
        if (_editPoint + 1 < sp.Raw.Length)
        {
            long nextT = sp.Raw[_editPoint + 1].UnixSeconds;
            hi = nextT == _editOrigUnix ? _editOrigUnix : nextT - 60;
        }
        long t = lo <= hi
            ? Math.Clamp(ToReal((_renderedStartBucket + cx) * bucketSec), lo, hi)
            : _editOrigUnix;
        _editCurUnix = t - t % 60;
        _editCurDisplay = (int)Math.Round(YToDisplay(cy, SeriesOffset(s.Symbol), _editCurUnix));
    }

    private void UpdateDragPreview(DpiScale dpi)
    {
        var s = _series![_editSeries];
        var sp = _pivots[s.Symbol];
        long bucketSec = _renderedK * 60L;
        double offset = SeriesOffset(s.Symbol);
        double x = ToVirtual(_editCurUnix) / bucketSec - _renderedStartBucket + 0.5;
        double y = DisplayToY(_editCurDisplay, offset, _editCurUnix) + 0.5;
        double thickness = 1.0 / dpi.DpiScaleX;
        _dragLinePrev.StrokeThickness = thickness;
        _dragLineNext.StrokeThickness = thickness;
        PlaceDragLine(_dragLinePrev, sp, _editPoint - 1, x, y, offset, bucketSec, dpi);
        PlaceDragLine(_dragLineNext, sp, _editPoint + 1, x, y, offset, bucketSec, dpi);
    }

    private void PlaceDragLine(Line line, SeriesPivots sp, int neighborIndex, double toX, double toY,
        double offset, long bucketSec, DpiScale dpi)
    {
        if (neighborIndex < 0 || neighborIndex >= sp.Raw.Length)
        {
            line.Visibility = Visibility.Collapsed;
            return;
        }
        double fx = ToVirtual(sp.Raw[neighborIndex].UnixSeconds) / bucketSec
            - _renderedStartBucket + 0.5;
        double fy = DisplayToY(sp.Display[neighborIndex], offset,
            sp.Raw[neighborIndex].UnixSeconds) + 0.5;
        line.X1 = fx / dpi.DpiScaleX;
        line.Y1 = fy / dpi.DpiScaleY;
        line.X2 = toX / dpi.DpiScaleX;
        line.Y2 = toY / dpi.DpiScaleY;
        line.Visibility = Visibility.Visible;
    }

    private void CommitPivotDrag()
    {
        if (!_pivotDragging) return;
        _pivotDragging = false;
        _dragLinePrev.Visibility = Visibility.Collapsed;
        _dragLineNext.Visibility = Visibility.Collapsed;
        int si = _editSeries;
        int pi = _editPoint;
        _editSeries = -1;
        _editPoint = -1;
        ReleaseMouseCapture();
        var series = _series;
        if (series == null || si < 0 || si >= series.Count || !_editMoved)
        {
            ClearHiddenPivotSegments();
            return;
        }
        var s = series[si];
        if (s.Transform == null || !_pivots.TryGetValue(s.Symbol, out var sp) || pi >= sp.Raw.Length)
        {
            ClearHiddenPivotSegments();
            return;
        }
        int newRaw = _editCurDisplay == _editOrigDisplay ? _editOrigRaw : s.Transform.ToRaw(_editCurDisplay);
        if (_editCurUnix == _editOrigUnix && newRaw == _editOrigRaw)
        {
            ClearHiddenPivotSegments();
            return;
        }
        var points = new List<PivotPoint>(sp.Raw);
        points[pi] = new PivotPoint(_editCurUnix, newRaw);
        PivotEditRequested?.Invoke(new PivotEditRequest(s.Symbol, points));
    }

    private void CancelPivotDrag()
    {
        if (!_pivotDragging) return;
        _pivotDragging = false;
        _dragLinePrev.Visibility = Visibility.Collapsed;
        _dragLineNext.Visibility = Visibility.Collapsed;
        _editSeries = -1;
        _editPoint = -1;
        ReleaseMouseCapture();
        ClearHiddenPivotSegments();
    }

    private enum MenuEditKind { Delete, AddLeft, AddRight }

    private void OnEditMenu(object sender, MouseButtonEventArgs e)
    {
        if (_drawSymbol != null)
        {
            e.Handled = true;
            FinishDrawing();
            return;
        }
        var series = _series;
        if (series == null || _pivotDragging || _selDragging) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var pos = e.GetPosition(this);
        int cx = (int)Math.Floor(pos.X * dpi.DpiScaleX);
        int cy = (int)Math.Floor(pos.Y * dpi.DpiScaleY);
        var hit = FindPivotAt(cx, cy);
        if (hit == null)
        {
            var vhit = FindDrawingVertexAt(cx, cy);
            if (vhit != null)
            {
                e.Handled = true;
                ShowDrawingVertexMenu(vhit.Value.Symbol, vhit.Value.Line, vhit.Value.Vertex);
                return;
            }
            var lhit = FindDrawingLineAt(cx, cy);
            if (lhit != null)
            {
                e.Handled = true;
                ShowDrawingLineMenu(lhit.Value.Symbol, lhit.Value.Line);
                return;
            }
            if (RangeContainsColumn(cx))
            {
                e.Handled = true;
                ShowRangeMenu(pos);
            }
            return;
        }
        e.Handled = true;
        var (si, pi) = hit.Value;
        var s = series[si];
        var sp = _pivots[s.Symbol];
        var ps = sp.Raw;
        bool hasPrev = pi > 0;
        bool hasNext = pi + 1 < ps.Length;
        var menu = new ContextMenu { PlacementTarget = this };
        var delete = new MenuItem { Header = "Delete point", IsEnabled = ps.Length > 2 };
        delete.Click += (_, _) => ApplyMenuEdit(s.Symbol, sp, pi, MenuEditKind.Delete);
        menu.Items.Add(delete);
        var addLeft = new MenuItem
        {
            Header = "Add point left",
            IsEnabled = hasPrev && ps[pi].UnixSeconds - ps[pi - 1].UnixSeconds >= 120,
        };
        addLeft.Click += (_, _) => ApplyMenuEdit(s.Symbol, sp, pi, MenuEditKind.AddLeft);
        menu.Items.Add(addLeft);
        var addRight = new MenuItem
        {
            Header = "Add point right",
            IsEnabled = hasNext && ps[pi + 1].UnixSeconds - ps[pi].UnixSeconds >= 120,
        };
        addRight.Click += (_, _) => ApplyMenuEdit(s.Symbol, sp, pi, MenuEditKind.AddRight);
        menu.Items.Add(addRight);
        menu.IsOpen = true;
    }

    public void BeginDrawLine(string symbol)
    {
        var s = GetSeries(symbol);
        if (s?.Transform == null || s.DrawingLines == null) return;
        CancelPivotDrag();
        EndDrag();
        DeselectLine();
        HideHover();
        _drawSymbol = symbol;
        _drawPoints.Clear();
        var brush = BrushFor(s.ColorArgb);
        _drawPreview.Stroke = brush;
        _drawCursor.Stroke = brush;
        _drawPreview.Points.Clear();
        _drawPreview.Visibility = Visibility.Collapsed;
        Focusable = true;
        Focus();
    }

    public void CancelDrawing()
    {
        _drawSymbol = null;
        _drawPoints.Clear();
        _drawPreview.Points.Clear();
        _drawPreview.Visibility = Visibility.Collapsed;
        _drawCursor.Visibility = Visibility.Collapsed;
    }

    private void FinishDrawing()
    {
        var symbol = _drawSymbol;
        var points = _drawPoints.ToArray();
        CancelDrawing();
        if (symbol != null && points.Length >= 2)
            DrawingCommitted?.Invoke(symbol, points);
    }

    private void HandleDrawClick(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            FinishDrawing();
            return;
        }
        if (_renderedK <= 0 || _renderedPointsPerRow <= 0) return;
        var s = GetSeries(_drawSymbol!);
        if (s?.Transform == null)
        {
            CancelDrawing();
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var pos = e.GetPosition(this);
        int cx = (int)Math.Floor(pos.X * dpi.DpiScaleX);
        int cy = (int)Math.Floor(pos.Y * dpi.DpiScaleY);
        long t = ToReal((_renderedStartBucket + cx) * (_renderedK * 60L));
        t -= t % 60;
        int display = (int)Math.Round(YToDisplay(cy, SeriesOffset(s.Symbol), t));
        _drawPoints.Add(new PivotPoint(t, s.Transform.ToRaw(display)));
        UpdateDrawPreview(dpi);
    }

    private Point ProjectDrawPoint(SeriesTransform transform, double offset, PivotPoint p, DpiScale dpi)
    {
        var (x, y) = ProjectDrawPointPx(transform, offset, p);
        return new Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY);
    }

    private void UpdateDrawPreview(DpiScale dpi)
    {
        var s = _drawSymbol == null ? null : GetSeries(_drawSymbol);
        if (s?.Transform == null || _renderedK <= 0 || _renderedPointsPerRow <= 0) return;
        double offset = SeriesOffset(s.Symbol);
        _drawPreview.Points.Clear();
        foreach (var p in _drawPoints)
            _drawPreview.Points.Add(ProjectDrawPoint(s.Transform, offset, p, dpi));
        _drawPreview.Visibility = _drawPoints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDrawCursor(MouseEventArgs e)
    {
        if (_drawPoints.Count == 0)
        {
            _drawCursor.Visibility = Visibility.Collapsed;
            return;
        }
        var s = GetSeries(_drawSymbol!);
        if (s?.Transform == null || _renderedK <= 0 || _renderedPointsPerRow <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var from = ProjectDrawPoint(s.Transform, SeriesOffset(s.Symbol), _drawPoints[^1], dpi);
        var pos = e.GetPosition(this);
        _drawCursor.X1 = from.X;
        _drawCursor.Y1 = from.Y;
        _drawCursor.X2 = pos.X;
        _drawCursor.Y2 = pos.Y;
        _drawCursor.Visibility = Visibility.Visible;
    }

    public void ReplaceDrawing(string symbol, PivotPoint[][] lines)
    {
        var series = _series;
        if (series == null) return;
        int idx = -1;
        for (int i = 0; i < series.Count; i++)
            if (series[i].Symbol == symbol) { idx = i; break; }
        if (idx < 0) return;
        var list = new List<SymbolSeries>(series);
        list[idx] = list[idx] with { DrawingLines = lines };
        _series = list;
        if (_selSymbol == symbol && _selLine >= lines.Length) DeselectLine();
        RecomputeGlobalRange();
        RebuildFlatten();
        Rebuild();
    }

    public void ReplaceDrawings(IReadOnlyDictionary<string, PivotPoint[][]> bySymbol)
    {
        var series = _series;
        if (series == null || bySymbol.Count == 0) return;
        var list = new List<SymbolSeries>(series);
        bool changed = false;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].DrawingLines == null) continue;
            if (!bySymbol.TryGetValue(list[i].Symbol, out var lines)) continue;
            if (ReferenceEquals(list[i].DrawingLines, lines)) continue;
            if (_selSymbol == list[i].Symbol) DeselectLine();
            list[i] = list[i] with { DrawingLines = lines };
            changed = true;
        }
        if (!changed) return;
        _series = list;
        RecomputeGlobalRange();
        RebuildFlatten();
        Rebuild();
    }

    public (long Lo, long Hi)? VisibleRealRange()
    {
        if (_renderedK <= 0) return null;
        int pw = (int)Math.Round(ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (pw < 1) return null;
        long bucketSec = _renderedK * 60L;
        return (ToReal(_renderedStartBucket * bucketSec),
            ToReal((_renderedStartBucket + pw) * bucketSec));
    }

    private (double X, double Y) ProjectDrawPointPx(SeriesTransform transform, double offset, PivotPoint p)
    {
        double bucketSec = _renderedK * 60.0;
        double x = ToVirtual(p.UnixSeconds) / bucketSec - _renderedStartBucket + 0.5;
        double y = DisplayToY(transform.ToDisplay(p.Value), offset, p.UnixSeconds) + 0.5;
        return (x, y);
    }

    private static double SegmentDistSq(double px, double py, double ax, double ay, double bx, double by)
    {
        double vx = bx - ax;
        double vy = by - ay;
        double len2 = vx * vx + vy * vy;
        double t = len2 <= 0 ? 0 : Math.Clamp(((px - ax) * vx + (py - ay) * vy) / len2, 0, 1);
        double dx = px - (ax + vx * t);
        double dy = py - (ay + vy * t);
        return dx * dx + dy * dy;
    }

    private (string Symbol, int Line)? FindDrawingLineAt(int cx, int cy)
    {
        var series = _series;
        if (series == null || _renderedK <= 0 || _renderedPointsPerRow <= 0) return null;
        double r = LineHitRadiusPx + 0.5;
        double best = double.MaxValue;
        (string, int)? hit = null;
        foreach (var s in series)
        {
            if (s.DrawingLines is not { Length: > 0 } || s.Transform == null) continue;
            if (IsHidden(s.Symbol)) continue;
            double offset = SeriesOffset(s.Symbol);
            for (int li = 0; li < s.DrawingLines.Length; li++)
            {
                var poly = s.DrawingLines[li];
                var prev = ProjectDrawPointPx(s.Transform, offset, poly[0]);
                if (poly.Length == 1)
                {
                    double d1 = SegmentDistSq(cx, cy, prev.X, prev.Y, prev.X, prev.Y);
                    if (d1 <= r * r && d1 < best) { best = d1; hit = (s.Symbol, li); }
                    continue;
                }
                for (int i = 1; i < poly.Length; i++)
                {
                    var cur = ProjectDrawPointPx(s.Transform, offset, poly[i]);
                    double d = SegmentDistSq(cx, cy, prev.X, prev.Y, cur.X, cur.Y);
                    if (d <= r * r && d < best) { best = d; hit = (s.Symbol, li); }
                    prev = cur;
                }
            }
        }
        return hit;
    }

    private PivotPoint[]? SelectedLinePoints()
    {
        if (_selSymbol == null) return null;
        var s = GetSeries(_selSymbol);
        if (s?.DrawingLines == null || _selLine < 0 || _selLine >= s.DrawingLines.Length) return null;
        return s.DrawingLines[_selLine];
    }

    private int FindSelectedVertexAt(int cx, int cy)
    {
        var points = SelectedLinePoints();
        var s = _selSymbol == null ? null : GetSeries(_selSymbol);
        if (points == null || s?.Transform == null
            || _renderedK <= 0 || _renderedPointsPerRow <= 0) return -1;
        double offset = SeriesOffset(s.Symbol);
        double r = PivotCircleRadiusPx + 1.5;
        double best = double.MaxValue;
        int bestIdx = -1;
        for (int i = 0; i < points.Length; i++)
        {
            var pt = ProjectDrawPointPx(s.Transform, offset, points[i]);
            double d = (cx - pt.X) * (cx - pt.X) + (cy - pt.Y) * (cy - pt.Y);
            if (d <= r * r && d < best) { best = d; bestIdx = i; }
        }
        return bestIdx;
    }

    private (string Symbol, int Line, int Vertex)? FindDrawingVertexAt(int cx, int cy)
    {
        var series = _series;
        if (series == null || _renderedK <= 0 || _renderedPointsPerRow <= 0) return null;
        double r = PivotCircleRadiusPx + 1.5;
        double best = double.MaxValue;
        (string, int, int)? hit = null;
        foreach (var s in series)
        {
            if (s.DrawingLines is not { Length: > 0 } || s.Transform == null) continue;
            if (IsHidden(s.Symbol)) continue;
            double offset = SeriesOffset(s.Symbol);
            for (int li = 0; li < s.DrawingLines.Length; li++)
            {
                var poly = s.DrawingLines[li];
                for (int i = 0; i < poly.Length; i++)
                {
                    var pt = ProjectDrawPointPx(s.Transform, offset, poly[i]);
                    double d = (cx - pt.X) * (cx - pt.X) + (cy - pt.Y) * (cy - pt.Y);
                    if (d <= r * r && d < best) { best = d; hit = (s.Symbol, li, i); }
                }
            }
        }
        return hit;
    }

    private void SelectLine(string symbol, int line)
    {
        _selSymbol = symbol;
        _selLine = line;
        Focusable = true;
        Focus();
        UpdateSelectionVisuals(VisualTreeHelper.GetDpi(this));
    }

    private void DeselectLine()
    {
        if (_selDragging) CancelSelDrag();
        _selSymbol = null;
        _selLine = -1;
        UpdateSelectionVisuals(VisualTreeHelper.GetDpi(this));
    }

    private void DeleteSelectedLine()
    {
        var symbol = _selSymbol;
        int line = _selLine;
        var points = SelectedLinePoints();
        if (symbol == null || points == null) return;
        var owner = Window.GetWindow(this);
        var result = MessageBox.Show(owner,
            $"Delete the selected line ({points.Length} points) of {symbol}?",
            "Delete line", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        if (_selSymbol != symbol || _selLine != line) return;
        var s = GetSeries(symbol);
        if (s?.DrawingLines == null || line < 0 || line >= s.DrawingLines.Length) return;
        var lines = new PivotPoint[s.DrawingLines.Length - 1][];
        for (int i = 0, j = 0; i < s.DrawingLines.Length; i++)
            if (i != line) lines[j++] = s.DrawingLines[i];
        DeselectLine();
        DrawingLinesChanged?.Invoke(symbol, lines);
    }

    private void ShowDrawingVertexMenu(string symbol, int line, int vertex)
    {
        SelectLine(symbol, line);
        var menu = new ContextMenu { PlacementTarget = this };
        var delete = new MenuItem { Header = "Delete point" };
        delete.Click += (_, _) => DeleteDrawingVertex(symbol, line, vertex);
        menu.Items.Add(delete);
        var addLeft = new MenuItem { Header = "Add point left" };
        addLeft.Click += (_, _) => AddDrawingVertex(symbol, line, vertex, true);
        menu.Items.Add(addLeft);
        var addRight = new MenuItem { Header = "Add point right" };
        addRight.Click += (_, _) => AddDrawingVertex(symbol, line, vertex, false);
        menu.Items.Add(addRight);
        menu.Items.Add(new Separator());
        menu.Items.Add(FlattenMenuItem(symbol, line));
        menu.IsOpen = true;
    }

    private void ShowDrawingLineMenu(string symbol, int line)
    {
        SelectLine(symbol, line);
        var menu = new ContextMenu { PlacementTarget = this };
        menu.Items.Add(FlattenMenuItem(symbol, line));
        menu.IsOpen = true;
    }

    private MenuItem FlattenMenuItem(string symbol, int line)
    {
        bool active = IsFlattenLine(symbol, line);
        var item = new MenuItem { Header = active ? "Unflatten" : "Flatten by line" };
        item.Click += (_, _) => SetFlattenLine(active ? null : symbol, line);
        return item;
    }

    private void DeleteDrawingVertex(string symbol, int line, int vertex)
    {
        var s = GetSeries(symbol);
        if (s?.DrawingLines == null || line < 0 || line >= s.DrawingLines.Length) return;
        var poly = s.DrawingLines[line];
        if (vertex < 0 || vertex >= poly.Length) return;
        if (poly.Length - 1 < 2)
        {
            var pruned = new PivotPoint[s.DrawingLines.Length - 1][];
            for (int i = 0, j = 0; i < s.DrawingLines.Length; i++)
                if (i != line) pruned[j++] = s.DrawingLines[i];
            DeselectLine();
            DrawingLinesChanged?.Invoke(symbol, pruned);
            return;
        }
        var np = new PivotPoint[poly.Length - 1];
        for (int i = 0, j = 0; i < poly.Length; i++)
            if (i != vertex) np[j++] = poly[i];
        var lines = (PivotPoint[][])s.DrawingLines.Clone();
        lines[line] = np;
        DrawingLinesChanged?.Invoke(symbol, lines);
    }

    private void AddDrawingVertex(string symbol, int line, int vertex, bool left)
    {
        var s = GetSeries(symbol);
        if (s?.DrawingLines == null || line < 0 || line >= s.DrawingLines.Length) return;
        var poly = s.DrawingLines[line];
        if (vertex < 0 || vertex >= poly.Length || poly.Length < 2) return;
        PivotPoint np;
        int insertAt;
        if (left)
        {
            if (vertex > 0) { np = DrawMidpoint(poly[vertex - 1], poly[vertex]); insertAt = vertex; }
            else { np = ExtrapolateDraw(poly[0], poly[1]); insertAt = 0; }
        }
        else
        {
            if (vertex < poly.Length - 1) { np = DrawMidpoint(poly[vertex], poly[vertex + 1]); insertAt = vertex + 1; }
            else { np = ExtrapolateDraw(poly[^1], poly[^2]); insertAt = poly.Length; }
        }
        var grown = new PivotPoint[poly.Length + 1];
        Array.Copy(poly, 0, grown, 0, insertAt);
        grown[insertAt] = np;
        Array.Copy(poly, insertAt, grown, insertAt + 1, poly.Length - insertAt);
        var lines = (PivotPoint[][])s.DrawingLines.Clone();
        lines[line] = grown;
        DrawingLinesChanged?.Invoke(symbol, lines);
    }

    private static PivotPoint DrawMidpoint(PivotPoint a, PivotPoint b)
    {
        long span = b.UnixSeconds - a.UnixSeconds;
        long t = a.UnixSeconds + span / 2;
        t -= ((t % 60) + 60) % 60;
        double f = span == 0 ? 0.5 : (double)(t - a.UnixSeconds) / span;
        int v = (int)Math.Round(a.Value + (b.Value - a.Value) * f, MidpointRounding.AwayFromZero);
        return new PivotPoint(t, v);
    }

    private static PivotPoint ExtrapolateDraw(PivotPoint anchor, PivotPoint neighbor)
    {
        long t = 2 * anchor.UnixSeconds - neighbor.UnixSeconds;
        t -= ((t % 60) + 60) % 60;
        long v = 2L * anchor.Value - neighbor.Value;
        return new PivotPoint(t, (int)Math.Clamp(v, int.MinValue, int.MaxValue));
    }

    private void BeginSelDrag(int vertex, int cx, int cy)
    {
        var points = SelectedLinePoints();
        if (points == null)
        {
            DeselectLine();
            return;
        }
        _selDragging = true;
        _selMoved = false;
        _selDragVertex = vertex;
        _selPressX = cx;
        _selPressY = cy;
        _selOrigPoints = points;
        _selCurPoints = points;
        HideHover();
        CaptureMouse();
    }

    private void UpdateSelDrag(int cx, int cy)
    {
        var s = _selSymbol == null ? null : GetSeries(_selSymbol);
        if (s?.Transform == null || _renderedK <= 0 || _renderedPointsPerRow <= 0)
        {
            CancelSelDrag();
            return;
        }
        if (!_selMoved && (cx != _selPressX || cy != _selPressY))
        {
            _selMoved = true;
            Rebuild();
        }
        long bucketSec = _renderedK * 60L;
        double offset = SeriesOffset(s.Symbol);
        var points = new PivotPoint[_selOrigPoints.Length];
        if (_selDragVertex >= 0 && _selDragVertex < points.Length)
        {
            Array.Copy(_selOrigPoints, points, points.Length);
            long t = ToReal((_renderedStartBucket + cx) * bucketSec);
            t -= t % 60;
            int raw;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && points.Length > 1)
            {
                raw = SlopePreservedValue(_selOrigPoints, _selDragVertex, t);
            }
            else
            {
                int display = (int)Math.Round(YToDisplay(cy, offset, t));
                raw = s.Transform.ToRaw(display);
            }
            points[_selDragVertex] = new PivotPoint(t, raw);
        }
        else
        {
            long dt = (long)(cx - _selPressX) * bucketSec;
            double dyDisplay = (cy - _selPressY) * _renderedPointsPerRow;
            for (int i = 0; i < points.Length; i++)
            {
                long t = ToReal(ToVirtual(_selOrigPoints[i].UnixSeconds) + dt);
                t -= t % 60;
                int display = (int)Math.Round(
                    s.Transform.ToDisplay(_selOrigPoints[i].Value) - dyDisplay);
                points[i] = new PivotPoint(t, s.Transform.ToRaw(display));
            }
        }
        _selCurPoints = points;
        UpdateSelectionVisuals(VisualTreeHelper.GetDpi(this));
    }

    private int SlopePreservedValue(PivotPoint[] orig, int vertex, long newUnix)
    {
        var p = orig[vertex];
        var anchor = vertex > 0 ? orig[vertex - 1] : orig[vertex + 1];
        double anchorVirtual = ToVirtual(anchor.UnixSeconds);
        double pointVirtual = ToVirtual(p.UnixSeconds);
        if (pointVirtual == anchorVirtual) return p.Value;
        double slope = (p.Value - anchor.Value) / (pointVirtual - anchorVirtual);
        double value = anchor.Value + slope * (ToVirtual(newUnix) - anchorVirtual);
        return (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero),
            int.MinValue, int.MaxValue);
    }

    private void CommitSelDrag()
    {
        if (!_selDragging) return;
        _selDragging = false;
        bool moved = _selMoved;
        _selMoved = false;
        var current = _selCurPoints;
        ReleaseMouseCapture();
        var symbol = _selSymbol;
        var s = symbol == null ? null : GetSeries(symbol);
        if (moved && s?.DrawingLines != null && _selLine >= 0 && _selLine < s.DrawingLines.Length)
        {
            var lines = (PivotPoint[][])s.DrawingLines.Clone();
            lines[_selLine] = current;
            DrawingLinesChanged?.Invoke(symbol!, lines);
        }
        Rebuild();
        UpdateSelectionVisuals(VisualTreeHelper.GetDpi(this));
    }

    private void CancelSelDrag()
    {
        if (!_selDragging) return;
        _selDragging = false;
        bool moved = _selMoved;
        _selMoved = false;
        ReleaseMouseCapture();
        if (moved) Rebuild();
        UpdateSelectionVisuals(VisualTreeHelper.GetDpi(this));
    }

    private void UpdateSelectionVisuals(DpiScale dpi)
    {
        var s = _selSymbol == null ? null : GetSeries(_selSymbol);
        var points = _selDragging && _selMoved ? _selCurPoints : SelectedLinePoints();
        bool valid = s?.Transform != null && points is { Length: > 0 }
            && !IsHidden(s!.Symbol)
            && _renderedK > 0 && _renderedPointsPerRow > 0;
        if (!valid)
        {
            foreach (var c in _selCircles) c.Visibility = Visibility.Collapsed;
            _selPreview.Visibility = Visibility.Collapsed;
            return;
        }
        double offset = SeriesOffset(s!.Symbol);
        var brush = BrushFor(s.ColorArgb);
        while (_selCircles.Count < points!.Length)
        {
            var circle = new Ellipse { Fill = Brushes.Transparent };
            _selCircles.Add(circle);
            _editCanvas.Children.Add(circle);
        }
        for (int i = 0; i < _selCircles.Count; i++)
        {
            var circle = _selCircles[i];
            if (i >= points.Length)
            {
                circle.Visibility = Visibility.Collapsed;
                continue;
            }
            var pt = ProjectDrawPointPx(s.Transform!, offset, points[i]);
            circle.Width = 2.0 * PivotCircleRadiusPx / dpi.DpiScaleX;
            circle.Height = 2.0 * PivotCircleRadiusPx / dpi.DpiScaleY;
            circle.Stroke = brush;
            circle.StrokeThickness = 1.5 / dpi.DpiScaleX;
            Canvas.SetLeft(circle, (pt.X - PivotCircleRadiusPx) / dpi.DpiScaleX);
            Canvas.SetTop(circle, (pt.Y - PivotCircleRadiusPx) / dpi.DpiScaleY);
            circle.Visibility = Visibility.Visible;
        }
        if (_selDragging && _selMoved)
        {
            _selPreview.Stroke = brush;
            _selPreview.Points.Clear();
            foreach (var p in points)
            {
                var pt = ProjectDrawPointPx(s.Transform!, offset, p);
                _selPreview.Points.Add(new Point(pt.X / dpi.DpiScaleX, pt.Y / dpi.DpiScaleY));
            }
            _selPreview.Visibility = Visibility.Visible;
        }
        else
        {
            _selPreview.Visibility = Visibility.Collapsed;
        }
    }

    private double? SourceAlignedOffset(string symbol)
    {
        var s = GetSeries(symbol);
        if (s?.SourceSymbol == null || s.BottomPanel) return null;
        var src = GetSeries(s.SourceSymbol);
        if (src == null) return null;
        if (s.Transform == null || src.Transform == null) return 0;
        return (double)src.Transform.ToDisplay(0) - s.Transform.ToDisplay(0);
    }

    public bool IsAlignedToSource(string symbol)
    {
        var aligned = SourceAlignedOffset(symbol);
        return aligned == null || _seriesOffsetPoints.GetValueOrDefault(symbol) == aligned.Value;
    }

    public void AlignSeriesToSource(string symbol) => AlignSeriesToSource(new[] { symbol });

    public void AlignSeriesToSource(IEnumerable<string> symbols)
    {
        bool changed = false;
        foreach (var symbol in symbols)
        {
            var aligned = SourceAlignedOffset(symbol);
            if (aligned == null || _seriesOffsetPoints.GetValueOrDefault(symbol) == aligned.Value) continue;
            _seriesOffsetPoints[symbol] = aligned.Value;
            changed = true;
        }
        if (!changed) return;
        SeriesOffsetsChanged?.Invoke();
        Rebuild();
    }

    private void ApplyMenuEdit(string symbol, SeriesPivots sp, int pi, MenuEditKind kind)
    {
        if (EditLocked) return;
        if (!_pivots.TryGetValue(symbol, out var current) || !ReferenceEquals(current, sp)) return;
        var points = new List<PivotPoint>(sp.Raw);
        if (pi < 0 || pi >= points.Count) return;
        switch (kind)
        {
            case MenuEditKind.Delete:
                points.RemoveAt(pi);
                break;
            case MenuEditKind.AddLeft:
                if (pi == 0) return;
                points.Insert(pi, Midpoint(points[pi - 1], points[pi]));
                break;
            default:
                if (pi + 1 >= points.Count) return;
                points.Insert(pi + 1, Midpoint(points[pi], points[pi + 1]));
                break;
        }
        if (points.Count < 2) return;
        PivotEditRequested?.Invoke(new PivotEditRequest(symbol, points));
    }

    private static PivotPoint Midpoint(PivotPoint a, PivotPoint b)
    {
        long t = a.UnixSeconds + (b.UnixSeconds - a.UnixSeconds) / 2;
        t -= t % 60;
        double f = (double)(t - a.UnixSeconds) / (b.UnixSeconds - a.UnixSeconds);
        int v = (int)Math.Round(a.Value + (b.Value - a.Value) * f, MidpointRounding.AwayFromZero);
        return new PivotPoint(t, v);
    }

    private void UpdateEditMarkers(DpiScale dpi, int pw, int ph)
    {
        double offsetsHash = _priceOffsetPoints;
        foreach (var kv in _seriesOffsetPoints) offsetsHash += kv.Value * 31.0 + kv.Key.Length;
        var stamp = (_series as object, _renderedK, _renderedStartBucket, _renderedTopPrice,
            _renderedPointsPerRow, offsetsHash, _effectiveHidden.Count, pw, ph, dpi.DpiScaleX,
            _weekendsHidden, _flatten as object);
        if (stamp == _markerStamp) return;
        _markerStamp = stamp;
        _markerCanvas.Children.Clear();
        var series = _series;
        if (series == null || _renderedK <= 0 || _renderedPointsPerRow <= 0) return;
        long bucketSec = _renderedK * 60L;
        int count = 0;
        for (int si = 0; si < series.Count; si++)
        {
            var s = series[si];
            if (!s.Editable || IsHidden(s.Symbol)) continue;
            if (!_pivots.TryGetValue(s.Symbol, out var sp)) continue;
            var ps = sp.Raw;
            double offset = SeriesOffset(s.Symbol);
            var brush = BrushFor(s.ColorArgb);
            long tLo = ToReal(_renderedStartBucket * bucketSec);
            long tHi = ToReal((_renderedStartBucket + pw) * bucketSec);
            for (int i = Math.Max(1, ZigZagSymbol.FirstAtOrAfter(ps, tLo));
                 i + 1 < ps.Length && ps[i].UnixSeconds < tHi; i++)
            {
                double px = ToVirtual(ps[i].UnixSeconds) / bucketSec - _renderedStartBucket;
                double ax = ToVirtual(ps[i - 1].UnixSeconds) / bucketSec - _renderedStartBucket;
                double bx = ToVirtual(ps[i + 1].UnixSeconds) / bucketSec - _renderedStartBucket;
                if (px - ax < CollinearMinNeighborDx || bx - px < CollinearMinNeighborDx) continue;
                double py = DisplayToY(sp.Display[i], offset, ps[i].UnixSeconds);
                if (py < -PivotCircleRadiusPx || py > ph + PivotCircleRadiusPx) continue;
                double ay = DisplayToY(sp.Display[i - 1], offset, ps[i - 1].UnixSeconds);
                double by = DisplayToY(sp.Display[i + 1], offset, ps[i + 1].UnixSeconds);
                double vx = bx - ax;
                double vy = by - ay;
                double len = Math.Sqrt(vx * vx + vy * vy);
                if (len <= 0) continue;
                double dev = Math.Abs(vx * (ay - py) - (ax - px) * vy) / len;
                if (dev > CollinearMaxDeviationPx) continue;
                var circle = new Ellipse
                {
                    Width = 2.0 * PivotCircleRadiusPx / dpi.DpiScaleX,
                    Height = 2.0 * PivotCircleRadiusPx / dpi.DpiScaleY,
                    Stroke = brush,
                    StrokeThickness = 1.2 / dpi.DpiScaleX,
                };
                Canvas.SetLeft(circle, (px + 0.5 - PivotCircleRadiusPx) / dpi.DpiScaleX);
                Canvas.SetTop(circle, (py + 0.5 - PivotCircleRadiusPx) / dpi.DpiScaleY);
                _markerCanvas.Children.Add(circle);
                if (++count >= MaxCollinearCircles) return;
            }
        }
    }
}
