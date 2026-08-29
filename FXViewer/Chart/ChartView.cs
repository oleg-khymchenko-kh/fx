using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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

public sealed record ForecastMark(
    string Pair, string Day, long FromUnix, long ToUnix, int TopValue, int BottomValue, bool Band,
    ForecastRecord Record);

public sealed record ForecastMarker(int X, int Y, int HalfPx, ForecastMark Mark, int ColorArgb);

public sealed record DealMark(
    long EntryUnix, int EntryValue, long ExitUnix, int ExitValue, bool Buy, bool Win, bool Closed);

public sealed record SymbolSeries(string Symbol, CandleHistory History, int ColorArgb, int PipPoints = 10,
    bool Editable = false, PivotPoint[]? Pivots = null, SeriesTransform? Transform = null,
    string? SourceSymbol = null, PivotPoint[][]? DrawingLines = null, bool EntryPanel = false,
    DealMark[]? DealMarks = null, bool AgePanel = false)
{
    public int PriceMul { get; init; } = 1;
    public bool BasePair { get; init; }
    public bool TimeShift { get; init; }
    public bool AgeMirror { get; init; }
    public int AverageWindowBars { get; init; }
    public bool AverageFromFuture { get; init; }
    public bool AverageVolumeWeighted { get; init; }
    public bool DensityPanel { get; init; }
    public int[]? DensityWindows { get; init; }
    public int[]? DensityScalePercents { get; init; }
    public double DensityScalePerPixel { get; init; }
    public int DensitySelected { get; init; }
    public bool SpreadPanel { get; init; }
    public bool VolumePanel { get; init; }
    public bool VolumeWeighted { get; init; }
    public bool VolumeSplitSides { get; init; }
    public ProfileSet? VolumeProfiles { get; init; }
    public int VolumeGroupMinutes { get; init; } = 1;
    public double VolumeBarScale { get; init; } = 1;
    public double VolumeBarUnit { get; init; }
    public bool VolumeGroupLocked { get; init; }
    public bool OrderBookPanel { get; init; }
    public bool OrderBookPositions { get; init; }
    public int SellColorArgb { get; init; }
    public OrderBook.OrderBookSnapshot[]? OrderBookSnapshots { get; init; }
    public OrderBook.DepthSnapshot[]? DepthSnapshots { get; init; }
    public int DepthPipPoints { get; init; } = 10;
    public bool BottomPanel => EntryPanel || AgePanel || DensityPanel || SpreadPanel || VolumePanel
        || OrderBookPanel;
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
        unchecked((int)0xFF8C8C8C),
        unchecked((int)0xFFEAF2FB),
        unchecked((int)0xFFF9EBD2),
        unchecked((int)0xFFFCF3E5),
        unchecked((int)0xFFDCDCDC));

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
    private const long NavigateColumnSeconds = 300;
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
    private const int DensityCandleAlpha = 255;
    private const int BookPriceLineArgb = unchecked((int)0xFFFF0000);

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
    private long _columnSeconds;
    private long _viewStartBucket;
    private double _topPrice;
    private double _pointsPerRow;
    private double _priceOffsetPoints;
    private readonly Dictionary<string, double> _seriesOffsetPoints = new();
    private readonly Dictionary<string, Candle[]> _averageParents = new();
    private readonly HashSet<string> _offsetLockedSymbols = new();
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
    private bool _sessionsVisible;
    private readonly Ellipse _calHoverCircle = new()
    {
        Visibility = Visibility.Collapsed,
        Stroke = Brushes.White,
    };
    private readonly List<ForecastMarker> _forecastMarkers = new();
    private ForecastMark[] _forecastMarks = Array.Empty<ForecastMark>();
    private ForecastMark[] _forecastHover = Array.Empty<ForecastMark>();
    private string[] _forecastDays = Array.Empty<string>();
    private string _forecastDay = "";
    private bool _forecastHidden;
    private Popup? _forecastPopup;
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
    private (object? Series, long ColumnSeconds, long Start, double Top, double Ppr, double OffsetsHash,
        int Hidden, int Pw, int Ph, double DpiX, bool Weekends, object? Flatten) _markerStamp;

    private string? _drawSymbol;
    private bool _drawLevel;
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

    private const double MeasureCellGapDip = 14;
    private const double MeasureChromeDip = 22;
    private const double MeasureCloseDip = 32;
    private const string MeasureWidestPips = "-99999.9 pips";
    private const string MeasureWidestRange = "00-WWW-00 00:00  →  00-WWW-00 00:00     9999d 23h 59m";
    private const string MeasureWidestSymbol = "WWWWWWWW";
    private const string MeasureWidestPrice = "00000.00000";

    private double _measureLabelWidth;
    private bool _measuring;
    private bool _hasMeasure;
    private long _measureAnchorUnix;
    private double _measureAnchorPrice;
    private long _measureCursorUnix;
    private double _measureCursorPrice;
    private Grid? _measureTable;
    private readonly Canvas _measureCanvas = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly Rectangle _measureBand = new()
    {
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };
    private readonly TextBlock _measurePipsText = new()
    {
        FontFamily = StatsNumberFont,
        FontSize = 13,
        FontWeight = FontWeights.Bold,
    };
    private readonly TextBlock _measureTimeText = new() { FontSize = StatsCellFontSize };
    private readonly StackPanel _measureBody = new();
    private readonly Border _measureLabel = new()
    {
        Visibility = Visibility.Collapsed,
        Background = Brushes.White,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 6, 10, 8),
        Cursor = Cursors.Arrow,
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
    private int _densityUsedPx = DensityMaxWidthPx;
    private int _densitySelected;
    private long _densityAnchorColumn = long.MinValue;

    private const int SeriesHitRadiusPx = 2;
    private const int ShiftHotWidthPx = 2;
    private const int SeriesPressWidthPx = 2;
    private readonly List<(string Symbol, RenderLine Line)> _shiftLines = new();
    private readonly List<(string Symbol, RenderLine Line)> _renderedLines = new();
    private readonly List<string> _seriesOrder = new();
    private readonly Dictionary<string, int[]> _renderedSpreadColumns = new();
    private readonly Dictionary<string, VolumeColumnSet> _renderedVolumeColumns = new();
    private readonly Dictionary<string, double> _volumeScales = new();
    private readonly Dictionary<string, int> _volumeGroups = new();
    private readonly Dictionary<string, double> _volumeUnits = new();
    private readonly Dictionary<string, int> _volumePanelBottoms = new();

    private const int VolumeWheelBandPx = 25;

    private const double VolumeScaleMin = 0.25;
    private const double VolumeScaleMax = 200.0;
    private readonly Dictionary<string, double> _densityUnits = new();
    private readonly Dictionary<string, double> _densityAutoPerPixel = new();
    private const double DensityUnitMin = 1e-6;
    private const double DensityUnitMax = 1e9;
    private string? _shiftHotSymbol;
    private string? _pressSymbol;
    private string? _shiftDragSymbol;
    private bool _shiftDragging;
    private int _shiftDragStartX;
    private int _shiftDragStartY;
    private long _shiftDragSentSeconds;
    private double _shiftDragStartOffset;

    public int EditHitRadiusPx { get; set; } = 3;
    public bool EditLocked { get; set; }
    public event Action<PivotEditRequest>? PivotEditRequested;
    public event Action<string, PivotPoint[]>? DrawingCommitted;
    public event Action<string, PivotPoint[][]>? DrawingLinesChanged;

    public event Action<string>? Info;
    public event Action<long, long, int, WeekendCompressor?>? ViewChanged;
    public event Action<long?, double>? CursorTimeChanged;
    public event Action<double[]?, double[]?>? CursorPricesChanged;
    public event Action<ChartViewState>? StateChanged;
    public event Action? SeriesOrderChanged;
    public event Action? SeriesOffsetsChanged;
    public event Action<string, long>? SeriesTimeShiftRequested;
    public event Action<int>? DensitySelectedChanged;
    public event Action<string, int>? VolumeGroupChanged;
    public event Action<string, double>? VolumeScaleChanged;
    public event Action<string, double>? VolumeUnitChanged;
    public event Action<string, double>? DensityScaleChanged;

    private long _renderedColumnSeconds;
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
        BuildMeasureLabel();
        _measureBand.Fill = BrushFor(RangeFillArgb);
        _measureBand.Stroke = BrushFor(RangeEdgeArgb);
        _measureCanvas.Children.Add(_measureBand);
        _measureCanvas.Children.Add(_measureLabel);
        Children.Add(_measureCanvas);
        Cursor = Cursors.None;
        Focusable = true;
        FocusVisualStyle = null;
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
            else if (e.Key == Key.Q && _cursorOnChart)
            {
                BeginMeasure(_cursorPx, _cursorPy);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && (_measuring || _hasMeasure))
            {
                ClearMeasure();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _statsPopup != null)
            {
                CloseStatsPopup();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _forecastPopup != null)
            {
                CloseForecastPopup();
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
            else if (e.Key is Key.Right or Key.Left
                && (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                && (_cursorOnChart || _hasRange))
            {
                KeyRangeSelect(e.Key == Key.Right ? 1 : -1);
                e.Handled = true;
            }
            else if (e.Key is Key.Right or Key.Left && _cursorOnChart)
            {
                MoveMouseColumns(e.Key == Key.Right ? 1 : -1);
                e.Handled = true;
            }
            else if (e.Key is Key.Up or Key.Down && _cursorOnChart)
            {
                MoveMousePips(e.Key == Key.Up ? 1 : -1);
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
            SetForecastHover(Array.Empty<ForecastMark>());
            _drawCursor.Visibility = Visibility.Collapsed;
            CursorTimeChanged?.Invoke(null, 0);
            CursorPricesChanged?.Invoke(null, null);
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
            EndSeriesPress();
        };
        LostMouseCapture += (_, _) =>
        {
            _tiltedDragging = false;
            CancelShiftDrag();
            EndRangeSelect();
            CancelPivotDrag();
            CancelSelDrag();
            EndSeriesPress();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private static void MoveMouseColumns(int columns)
    {
        if (GetCursorPos(out var point)) SetCursorPos(point.X + columns, point.Y);
    }

    private void MoveMousePips(int pips)
    {
        if (_pointsPerRow <= 0 || pips == 0) return;
        double price = _topPrice - _cursorPy * _pointsPerRow;
        double level = pips > 0
            ? Math.Floor(price / PipPoints)
            : Math.Ceiling(price / PipPoints);
        int dy = 0;
        for (int step = 1; step <= 2 && dy == 0; step++)
        {
            double target = (level + pips * step) * PipPoints;
            dy = (int)Math.Round((_topPrice - target) / _pointsPerRow) - _cursorPy;
        }
        if (dy == 0) dy = pips > 0 ? -1 : 1;
        if (GetCursorPos(out var point)) SetCursorPos(point.X, point.Y + dy);
    }

    private void OnCrosshairMove(object sender, MouseEventArgs e)
    {
        using var _ = Perf.FrameStep("input.crosshair");
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
        if (_renderedColumnSeconds > 0 && _renderedStartBucket + cx != _densityAnchorColumn)
            UpdateDensityOverlay();
        if (_renderedColumnSeconds > 0)
        {
            long unix = ToReal((_renderedStartBucket + cx) * _renderedColumnSeconds);
            CursorTimeChanged?.Invoke(unix, cx / dpi.DpiScaleX);
        }
        if (_pointsPerRow > 0 && _series != null)
        {
            double screenPrice = _topPrice - cy * _pointsPerRow;
            if (_renderedColumnSeconds > 0)
                screenPrice -= FlattenShiftVirtual((_renderedStartBucket + cx) * _renderedColumnSeconds);
            var prices = new double[_series.Count];
            var deltas = new double[_series.Count];
            Array.Fill(deltas, double.NaN);
            for (int i = 0; i < _series.Count; i++)
            {
                var s = _series[i];
                if (s.SpreadPanel)
                {
                    prices[i] = _renderedSpreadColumns.TryGetValue(s.Symbol, out var cols)
                        && cx >= 0 && cx < cols.Length && cols[cx] >= 0
                        ? cols[cx] / 10.0
                        : double.NaN;
                    continue;
                }
                if (s.VolumePanel)
                {
                    bool hasVolume = _renderedVolumeColumns.TryGetValue(s.Symbol, out var vols)
                        && cx >= 0 && cx < vols.Total.Length && vols.Total[cx] >= 0;
                    prices[i] = hasVolume ? vols.Total[cx] : double.NaN;
                    deltas[i] = hasVolume && cx < vols.Ask.Length
                        && (vols.Ask[cx] > 0 || vols.Bid[cx] > 0)
                        ? vols.Ask[cx] - vols.Bid[cx]
                        : double.NaN;
                    continue;
                }
                prices[i] = screenPrice - SeriesOffset(s.Symbol);
            }
            CursorPricesChanged?.Invoke(prices, deltas);
        }
    }

    public void RestoreState(ChartViewState state)
    {
        ClearMeasure();
        _weekendsHidden = state.WeekendsHidden;
        _sessionsVisible = state.SessionsVisible;
        long saved = state.RestoredColumnSeconds();
        if (saved > 0)
        {
            long columnSeconds = ChartColumns.Snap(saved);
            _columnSeconds = columnSeconds;
            _viewStartBucket = state.ViewStartBucket * saved / columnSeconds;
        }
        else
        {
            _columnSeconds = 0;
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
            foreach (var locked in _offsetLockedSymbols) _seriesOffsetPoints.Remove(locked);
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
        _forecastHidden = state.ForecastHidden;
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

    public bool IsFitView => _columnSeconds <= 0;

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
        long anchor = _columnSeconds > 0 ? ToReal(_viewStartBucket * _columnSeconds) : 0;
        _weekendsHidden = !_weekendsHidden;
        if (_columnSeconds > 0)
            _viewStartBucket = ToVirtual(anchor) / _columnSeconds;
        CancelPivotDrag();
        CancelSelDrag();
        EndDrag();
        HideHover();
        HideCalendarHover();
        CloseChartPopups();
        RebuildFlatten();
        Rebuild();
        return _weekendsHidden;
    }

    public bool SessionsVisible => _sessionsVisible;

    public bool ToggleSessions()
    {
        _sessionsVisible = !_sessionsVisible;
        Rebuild();
        return _sessionsVisible;
    }

    public bool HasForecasts => _forecastMarks.Length > 0;

    public bool ForecastVisible => !_forecastHidden;

    public string ForecastDay => _forecastDay;

    public event Action? ForecastDaySelected;

    public void SetForecasts(ForecastMark[] marks)
    {
        _forecastMarks = marks ?? Array.Empty<ForecastMark>();
        _forecastDays = _forecastMarks
            .Select(m => m.Day)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();
        if (!_forecastDays.Contains(_forecastDay, StringComparer.Ordinal))
            _forecastDay = _forecastDays.Length > 0 ? _forecastDays[^1] : "";
        _forecastHover = Array.Empty<ForecastMark>();
        CloseForecastPopup();
        Rebuild();
    }

    public bool ToggleForecasts()
    {
        _forecastHidden = !_forecastHidden;
        _forecastHover = Array.Empty<ForecastMark>();
        if (_forecastHidden) CloseForecastPopup();
        Rebuild();
        return !_forecastHidden;
    }

    public void SelectForecastDay(string day)
    {
        if (!_forecastDays.Contains(day, StringComparer.Ordinal)) return;
        if (day == _forecastDay && !_forecastHidden)
        {
            _forecastHidden = true;
        }
        else
        {
            _forecastDay = day;
            _forecastHidden = false;
        }
        _forecastHover = Array.Empty<ForecastMark>();
        CloseForecastPopup();
        Rebuild();
        ForecastDaySelected?.Invoke();
    }

    private ForecastMark[] SelectedDayForecasts() =>
        _forecastDay.Length == 0
            ? Array.Empty<ForecastMark>()
            : _forecastMarks.Where(m => m.Day == _forecastDay).ToArray();

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
        CloseChartPopups();
        Rebuild();
    }

    private bool CalendarLinesDrawn(long columnSeconds) =>
        _calendarSettings.ShowAtAnyZoom || ChartRasterizer.HourGridVisible(columnSeconds);

    private bool CalendarImpactShown(byte impact) =>
        impact < _calendarShown.Length && _calendarShown[impact];

    public bool ToggleCalendar()
    {
        _calendarVisible = !_calendarVisible;
        if (!_calendarVisible)
        {
            HideCalendarHover();
            CloseChartPopups();
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
        if (altDown && TiltedGridVisible && _cursorOnChart && _renderedColumnSeconds > 0 && _pointsPerRow > 0)
        {
            bool up = NearestTiltedLineIsUp(_cursorPx, _cursorPy);
            nearest = up;
            line = NearestTiltedLine(up, _cursorPx, _cursorPy, _renderedColumnSeconds).Line;
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
        string? hot = altDown && _cursorOnChart && _renderedColumnSeconds > 0
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
            double distance = LineDistancePx(line, cx, cy);
            if (distance > SeriesHitRadiusPx || distance >= bestDistance) continue;
            bestDistance = distance;
            best = symbol;
        }
        return best;
    }

    private string? FindSeriesLineAt(int cx, int cy)
    {
        if (_renderedPointsPerRow <= 0) return null;
        string? best = null;
        double bestDistance = double.MaxValue;
        foreach (var (symbol, line) in _renderedLines)
        {
            double distance = LineDistancePx(line, cx, cy);
            if (distance > SeriesHitRadiusPx || distance >= bestDistance) continue;
            bestDistance = distance;
            best = symbol;
        }
        return best;
    }

    private double LineDistancePx(RenderLine line, int cx, int cy)
    {
        int startColumn = (int)(_renderedStartBucket - line.Series.FirstBucket);
        double best = double.MaxValue;
        for (int dx = -SeriesHitRadiusPx; dx <= SeriesHitRadiusPx; dx++)
        {
            double? row = LineRowAt(line, startColumn + cx + dx);
            if (row == null) continue;
            double lo = row.Value;
            double hi = row.Value;
            double? prev = LineRowAt(line, startColumn + cx + dx - 1);
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

    private double? LineRowAt(RenderLine line, int index)
    {
        var columns = line.Series.Columns;
        if (index < 0 || index >= columns.Length || !columns[index].HasData) return null;
        var shift = line.ColumnShift;
        double value = shift == null || index >= shift.Length
            ? line.Chosen[index]
            : line.Chosen[index] + shift[index];
        return (_renderedTopPrice - line.OffsetPoints - value) / _renderedPointsPerRow;
    }

    public IReadOnlyList<string> SeriesOrder => _seriesOrder;

    public void SetSeriesOrder(IEnumerable<string> order)
    {
        _seriesOrder.Clear();
        foreach (var symbol in order)
            if (!_seriesOrder.Contains(symbol)) _seriesOrder.Add(symbol);
        RequestRebuild();
    }

    private void BeginSeriesPress(int cx, int cy)
    {
        var symbol = FindSeriesLineAt(cx, cy);
        if (symbol == null) return;
        _pressSymbol = symbol;
        bool onTop = _seriesOrder.Count > 0 && _seriesOrder[^1] == symbol;
        _seriesOrder.Remove(symbol);
        _seriesOrder.Add(symbol);
        if (!onTop) PruneSeriesOrder();
        Rebuild();
        if (!onTop) SeriesOrderChanged?.Invoke();
    }

    private void EndSeriesPress()
    {
        if (_pressSymbol == null) return;
        _pressSymbol = null;
        Rebuild();
    }

    private int[] SeriesDrawOrder(List<SymbolSeries> visible)
    {
        var order = new int[visible.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        if (_seriesOrder.Count == 0) return order;
        var rank = new Dictionary<string, int>();
        for (int i = 0; i < _seriesOrder.Count; i++) rank[_seriesOrder[i]] = i + 1;
        Array.Sort(order, (a, b) =>
        {
            int ra = rank.GetValueOrDefault(visible[a].Symbol);
            int rb = rank.GetValueOrDefault(visible[b].Symbol);
            return ra != rb ? ra.CompareTo(rb) : a.CompareTo(b);
        });
        return order;
    }

    private void PruneSeriesOrder()
    {
        for (int i = _seriesOrder.Count - 1; i >= 0; i--)
            if (GetSeries(_seriesOrder[i]) == null) _seriesOrder.RemoveAt(i);
    }

    private void BeginShiftDrag(int cx, int cy)
    {
        var symbol = _shiftHotSymbol;
        if (symbol == null || _pointsPerRow <= 0) return;
        _shiftDragSymbol = symbol;
        _shiftDragStartX = cx;
        _shiftDragStartY = cy;
        _shiftDragStartOffset = _seriesOffsetPoints.GetValueOrDefault(symbol);
        _shiftDragSentSeconds = 0;
        _shiftDragging = true;
        CaptureMouse();
        Rebuild();
    }

    private void MoveShiftDrag(int cx, int cy)
    {
        var symbol = _shiftDragSymbol;
        if (symbol == null || _renderedColumnSeconds <= 0 || _pointsPerRow <= 0) return;
        double offset = _shiftDragStartOffset - (cy - _shiftDragStartY) * _pointsPerRow;
        bool changed = false;
        if (_seriesOffsetPoints.GetValueOrDefault(symbol) != offset)
        {
            _seriesOffsetPoints[symbol] = offset;
            SeriesOffsetsChanged?.Invoke();
            changed = true;
        }
        long dragged = (long)(cx - _shiftDragStartX) * _renderedColumnSeconds;
        dragged -= dragged % ChartColumns.MinuteSeconds;
        if (dragged != _shiftDragSentSeconds)
        {
            long step = dragged - _shiftDragSentSeconds;
            _shiftDragSentSeconds = dragged;
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
        if (_renderedColumnSeconds <= 0 || _pointsPerRow <= 0) return;
        double deltaSeconds = (cx - _tiltedDragStartX) * _renderedColumnSeconds;
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
        double bucketSec = _renderedColumnSeconds;
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
        if (!TiltedGridVisible || _renderedColumnSeconds <= 0 || _pointsPerRow <= 0) return;
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
        double bucketSec = _renderedColumnSeconds;
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
        if (_offsetLockedSymbols.Contains(symbol)) return;
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
        if (_offsetLockedSymbols.Contains(symbol)) return;
        var s = GetSeries(symbol);
        if (s == null || s.BottomPanel) return;
        if (_renderedColumnSeconds <= 0 || _pointsPerRow <= 0) return;
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
        DisplayRangeAt(s, _renderedColumnSeconds, _renderedStartBucket, pw);

    private (double Lo, double Hi)? DisplayRangeAt(SymbolSeries s, long columnSeconds, long viewStart, int pw)
    {
        double lo = double.MaxValue;
        double hi = double.MinValue;
        long bucketSec = columnSeconds;
        if (s.History.Minutes.Length > 0)
        {
            var view = ChartColumns.BuildView(s.History, columnSeconds, viewStart, pw, Compressor);
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
        bool wasFitView = _columnSeconds <= 0;
        long bucketSec = wasFitView ? NavigateColumnSeconds : _columnSeconds;
        _columnSeconds = bucketSec;
        long target = ToVirtual(unixSeconds);
        long start = ClampViewStart(
            target / bucketSec - pw / 2, ViewFirstUnix / bucketSec, ViewLastUnix / bucketSec, pw);
        _viewStartBucket = start;
        if (wasFitView) _pointsPerRow = 0;
        else CenterSeriesVertically(centerSymbol, bucketSec, start, pw, ph);
        CancelPivotDrag();
        CancelSelDrag();
        EndDrag();
        HideHover();
        HideCalendarHover();
        CloseChartPopups();
        Rebuild();
        return true;
    }

    private void CenterSeriesVertically(string? symbol, long columnSeconds, long viewStart, int pw, int ph)
    {
        if (symbol == null || _pointsPerRow <= 0) return;
        var s = GetSeries(symbol);
        if (s == null || s.BottomPanel) return;
        if (DisplayRangeAt(s, columnSeconds, viewStart, pw) is not { } range) return;
        double middle = (range.Lo + range.Hi) / 2 + SeriesOffset(symbol);
        _topPrice = middle + _pointsPerRow * (ph - 1) / 2;
    }

    public void RenameSeriesKeys(string oldName, string newName)
    {
        if (oldName == newName) return;
        if (_seriesOffsetPoints.Remove(oldName, out var offset)) _seriesOffsetPoints[newName] = offset;
        if (_offsetLockedSymbols.Remove(oldName)) _offsetLockedSymbols.Add(newName);
        if (_averageParents.Remove(oldName, out var parentMinutes)) _averageParents[newName] = parentMinutes;
        if (_hiddenSymbols.Remove(oldName)) _hiddenSymbols.Add(newName);
        if (_collapsedSources.Remove(oldName)) _collapsedSources.Add(newName);
        RefreshHidden();
        if (_flattenSymbol == oldName) _flattenSymbol = newName;
        if (_shiftHotSymbol == oldName) _shiftHotSymbol = newName;
        if (_pressSymbol == oldName) _pressSymbol = newName;
        if (_shiftDragSymbol == oldName) _shiftDragSymbol = newName;
        int orderIndex = _seriesOrder.IndexOf(oldName);
        if (orderIndex >= 0)
        {
            _seriesOrder[orderIndex] = newName;
            SeriesOrderChanged?.Invoke();
        }
    }

    public void SetSeriesOffset(string symbol, double points)
    {
        if (_offsetLockedSymbols.Contains(symbol)) return;
        if (_seriesOffsetPoints.GetValueOrDefault(symbol) == points) return;
        _seriesOffsetPoints[symbol] = points;
        SeriesOffsetsChanged?.Invoke();
        Rebuild();
    }

    public void ShiftSeriesOffset(string symbol, int wheelDelta)
    {
        if (_offsetLockedSymbols.Contains(symbol)) return;
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
        ClearMeasure();
        ForgetHiddenPivotSegments();
        var list = series.ToList();
        _averageParents.Clear();
        _offsetLockedSymbols.Clear();
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s.AverageWindowBars <= 0) continue;
            _offsetLockedSymbols.Add(s.Symbol);
            if (s.SourceSymbol == null) continue;
            SymbolSeries? parent = null;
            foreach (var candidate in list)
                if (string.Equals(candidate.Symbol, s.SourceSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    parent = candidate;
                    break;
                }
            if (parent == null) continue;
            if (s.History.Minutes.Length == 0 && parent.History.Minutes.Length > 0)
                list[i] = s with
                {
                    History = CandleHistory.Build(AverageSeries.Compute(
                        parent.History.Minutes, s.AverageWindowBars, s.AverageFromFuture,
                        s.AverageVolumeWeighted)),
                };
            _averageParents[s.Symbol] = parent.History.Minutes;
        }
        series = list;
        _series = series.Count > 0 ? series : null;
        _shiftLines.Clear();
        _renderedLines.Clear();
        _shiftHotSymbol = null;
        _pressSymbol = null;
        _shiftDragSymbol = null;
        _shiftDragging = false;
        _columnSeconds = 0;
        _pointsPerRow = 0;
        _priceOffsetPoints = 0;
        _seriesOffsetPoints.Clear();
        _hiddenSymbols.Clear();
        _collapsedSources.Clear();
        _effectiveHidden.Clear();
        _flattenSymbol = null;
        _flattenLine = -1;
        _flatten = null;
        _volumeGroups.Clear();
        _volumeScales.Clear();
        _volumeUnits.Clear();
        _volumePanelBottoms.Clear();
        _densityUnits.Clear();
        _densityAutoPerPixel.Clear();
        foreach (var s in series)
            if (s.VolumePanel)
            {
                _volumeGroups[s.Symbol] = Math.Max(1, s.VolumeGroupMinutes);
                _volumeScales[s.Symbol] = ClampVolumeScale(s.VolumeBarScale);
                if (s.VolumeBarUnit > 0) _volumeUnits[s.Symbol] = s.VolumeBarUnit;
            }
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
        UpdateAveragesOf(symbol);
        if (transform != null) SeriesOffsetsChanged?.Invoke();
        RecomputeGlobalRange();
        RebuildPivots();
        RebuildFlatten();
        Rebuild();
    }

    public void SetSeriesColor(string symbol, int argb)
    {
        var series = _series;
        if (series == null) return;
        for (int i = 0; i < series.Count; i++)
        {
            if (!string.Equals(series[i].Symbol, symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (series[i].ColorArgb == argb) return;
            var list = new List<SymbolSeries>(series);
            list[i] = list[i] with { ColorArgb = argb };
            _series = list;
            Rebuild();
            return;
        }
    }

    public void PatchSeriesHistory(string symbol, CandleHistory history)
    {
        var series = _series;
        if (series == null || history.Minutes.Length == 0) return;
        int idx = -1;
        for (int i = 0; i < series.Count; i++)
            if (series[i].Symbol == symbol) { idx = i; break; }
        if (idx < 0) return;
        var list = new List<SymbolSeries>(series);
        list[idx] = list[idx] with { History = history };
        _series = list;
        UpdateAveragesOf(symbol);
        Rebuild();
    }

    private void UpdateAveragesOf(string parentSymbol)
    {
        var series = _series;
        if (series == null) return;
        SymbolSeries? parent = null;
        foreach (var s in series)
            if (string.Equals(s.Symbol, parentSymbol, StringComparison.OrdinalIgnoreCase))
            {
                parent = s;
                break;
            }
        if (parent == null) return;
        List<SymbolSeries>? list = null;
        for (int i = 0; i < series.Count; i++)
        {
            var s = series[i];
            if (s.AverageWindowBars <= 0
                || !string.Equals(s.SourceSymbol, parentSymbol, StringComparison.OrdinalIgnoreCase))
                continue;
            var minutes = parent.History.Minutes;
            var used = _averageParents.GetValueOrDefault(s.Symbol);
            if (ReferenceEquals(used, minutes)) continue;
            CandleHistory? updated = null;
            if (used is { Length: > 0 } && s.History.Minutes.Length == used.Length)
            {
                var diff = AverageSeries.Diff(
                    used, minutes, s.AverageWindowBars, s.AverageFromFuture, s.AverageVolumeWeighted);
                if (diff == null)
                {
                    _averageParents[s.Symbol] = minutes;
                    continue;
                }
                var replacement = AverageSeries.ComputeRange(
                    minutes, s.AverageWindowBars, s.AverageFromFuture, s.AverageVolumeWeighted,
                    diff.Value.NewFrom, diff.Value.NewToExcl);
                updated = s.History.WithReplacedRange(
                    diff.Value.OldFrom, diff.Value.OldToExcl - diff.Value.OldFrom, replacement);
            }
            updated ??= CandleHistory.Build(AverageSeries.Compute(
                minutes, s.AverageWindowBars, s.AverageFromFuture, s.AverageVolumeWeighted));
            updated.SetLive(AverageSeries.LiveTail(
                minutes, parent.History.Live, s.AverageWindowBars, s.AverageFromFuture,
                s.AverageVolumeWeighted));
            _averageParents[s.Symbol] = minutes;
            list ??= new List<SymbolSeries>(series);
            list[i] = s with { History = updated };
        }
        if (list != null) _series = list;
    }

    public void SetAverageWindow(string symbol, int windowBars)
    {
        var series = _series;
        if (series == null || windowBars <= 0) return;
        int idx = -1;
        for (int i = 0; i < series.Count; i++)
            if (string.Equals(series[i].Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        if (idx < 0) return;
        var s = series[idx];
        if (s.AverageWindowBars <= 0 || s.AverageWindowBars == windowBars || s.SourceSymbol == null)
            return;
        var list = new List<SymbolSeries>(series);
        list[idx] = s with { AverageWindowBars = windowBars };
        _series = list;
        _averageParents.Remove(s.Symbol);
        UpdateAveragesOf(s.SourceSymbol);
        Rebuild();
    }

    public void MergeVolumeProfiles(string sourceSymbol, IReadOnlyList<ProfileRecord> fresh)
    {
        var series = _series;
        if (series == null || fresh.Count == 0) return;
        List<SymbolSeries>? list = null;
        for (int i = 0; i < series.Count; i++)
        {
            var s = series[i];
            if (!s.VolumeWeighted
                || !string.Equals(s.SourceSymbol, sourceSymbol, StringComparison.OrdinalIgnoreCase))
                continue;
            list ??= new List<SymbolSeries>(series);
            list[i] = s with { VolumeProfiles = (s.VolumeProfiles ?? ProfileSet.Empty).Merge(fresh) };
        }
        if (list == null) return;
        _series = list;
        Rebuild();
    }

    public void MergeDepthSnapshots(string sourceSymbol, IReadOnlyList<OrderBook.DepthSnapshot> fresh)
    {
        var series = _series;
        if (series == null || fresh.Count == 0) return;
        List<SymbolSeries>? list = null;
        for (int i = 0; i < series.Count; i++)
        {
            var s = series[i];
            if (!s.OrderBookPanel || s.DepthSnapshots == null
                || !string.Equals(s.SourceSymbol, sourceSymbol, StringComparison.OrdinalIgnoreCase))
                continue;
            long last = s.DepthSnapshots.Length > 0
                ? s.DepthSnapshots[^1].MinuteUnix
                : long.MinValue;
            var added = fresh.Where(x => x.MinuteUnix > last).ToArray();
            if (added.Length == 0) continue;
            var merged = new OrderBook.DepthSnapshot[s.DepthSnapshots.Length + added.Length];
            s.DepthSnapshots.CopyTo(merged, 0);
            added.CopyTo(merged, s.DepthSnapshots.Length);
            list ??= new List<SymbolSeries>(series);
            list[i] = s with { DepthSnapshots = merged };
        }
        if (list == null) return;
        _series = list;
        UpdateDensityOverlay();
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
        CalendarEntry[] entries, long columnSeconds, long startBucket, WeekendCompressor? map,
        bool[] shown)
    {
        long bucketSec = columnSeconds;
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
        foreach (var s in series)
            if (s.AverageWindowBars > 0
                && string.Equals(s.SourceSymbol, symbol, StringComparison.OrdinalIgnoreCase))
                s.History.SetLive(AverageSeries.LiveTail(
                    match.History.Minutes, tail, s.AverageWindowBars, s.AverageFromFuture,
                    s.AverageVolumeWeighted));
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
        if (_measuring)
        {
            e.Handled = true;
            FinishMeasure();
            return;
        }
        if (_hasMeasure)
        {
            e.Handled = true;
            ClearMeasure();
            return;
        }
        if (_drawSymbol != null)
        {
            HandleDrawClick(e);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var pos = e.GetPosition(this);
        int cx = (int)Math.Floor(pos.X * dpi.DpiScaleX);
        int cy = (int)Math.Floor(pos.Y * dpi.DpiScaleY);
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && _renderedColumnSeconds > 0)
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
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _renderedColumnSeconds > 0)
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
        var forecastHits = FindForecastMarkersAt(cx, cy);
        if (forecastHits.Count > 0)
        {
            ShowForecastPopup(forecastHits);
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
        CloseChartPopups();
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
        BeginSeriesPress(cx, cy);
        int pw = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        if (pw < 1) return;
        _dragStartX = cx;
        _dragStartY = cy;
        if (_columnSeconds <= 0)
        {
            long fit = ChartColumns.FitColumnSeconds(ViewFirstUnix, ViewLastUnix, pw);
            _columnSeconds = fit;
            _viewStartBucket = ViewFirstUnix / fit;
        }
        _dragStartViewBucket = _viewStartBucket;
        _dragStartTopPrice = _topPrice;
        _dragging = true;
        CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        using var _ = Perf.FrameStep("input.drag");
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
        if (dx != 0 && _columnSeconds > 0)
        {
            long fb = ViewFirstUnix / _columnSeconds;
            long lb = ViewLastUnix / _columnSeconds;
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
        return ToReal((_renderedStartBucket + x) * _renderedColumnSeconds);
    }

    private void BeginRangeSelect(int cx)
    {
        EndDrag();
        CloseStatsPopup();
        HideHover();
        HideCalendarHover();
        CloseChartPopups();
        DeselectLine();
        _rangeSelecting = true;
        _hasRange = true;
        _rangeStartUnix = ColumnTime(cx);
        _rangeEndUnix = _rangeStartUnix;
        UpdateRangeVisuals();
        UpdateDensityOverlay();
        Focusable = true;
        Focus();
        CaptureMouse();
    }

    private void KeyRangeSelect(int direction)
    {
        if (_renderedColumnSeconds <= 0) return;
        CloseStatsPopup();
        if (!_hasRange)
        {
            if (!_cursorOnChart) return;
            CloseChartPopups();
            DeselectLine();
            _hasRange = true;
            _rangeStartUnix = ColumnTime(_cursorPx);
            _rangeEndUnix = _rangeStartUnix;
        }
        else
        {
            long bucket = ToVirtual(_rangeEndUnix) / _renderedColumnSeconds + direction;
            _rangeEndUnix = ToReal(bucket * _renderedColumnSeconds);
        }
        UpdateRangeVisuals();
        UpdateDensityOverlay();
    }

    private void UpdateRangeSelect(int cx)
    {
        _rangeEndUnix = ColumnTime(cx);
        UpdateRangeVisuals();
        UpdateDensityOverlay();
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
        UpdateDensityOverlay();
    }

    private void UpdateRangeVisuals()
    {
        double viewW = ActualWidth;
        double viewH = ActualHeight;
        if (!_hasRange || _renderedColumnSeconds <= 0 || viewW < 1 || viewH < 1)
        {
            _rangeBand.Visibility = Visibility.Collapsed;
            _rangeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        long bucketSec = _renderedColumnSeconds;
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
        if (series == null || _renderedColumnSeconds <= 0) return;
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
        var rows = ComputeRangeStats(MinuteFloor(lo), hi + _renderedColumnSeconds - 1);
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

    private const double StatsTenthFontSize = 8;

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
            string tenthFormat = "0." + new string('0', row.Digits + 1);
            table.Children.Add(StatsTenthCell(PriceText(row.Min, tenthFormat), r + 1, 1, brush));
            table.Children.Add(StatsTenthCell(PriceText(row.Max, tenthFormat), r + 1, 2, brush));
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

    private const double StatsCellFontSize = 12;

    private static readonly FontFamily StatsNumberFont = new("Consolas");

    private static TextBlock StatsCell(string text, int row, int column, Brush brush, bool numeric)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = StatsCellFontSize,
            Margin = new Thickness(0, 1, MeasureCellGapDip, 1),
        };
        if (numeric)
        {
            block.FontFamily = StatsNumberFont;
            block.HorizontalAlignment = HorizontalAlignment.Right;
        }
        Grid.SetRow(block, row);
        Grid.SetColumn(block, column);
        return block;
    }

    private static TextBlock StatsTenthCell(string text, int row, int column, Brush brush)
    {
        var block = StatsCell(text[..^1], row, column, brush, true);
        block.Inlines.Add(new Run(text[^1..]) { FontSize = StatsTenthFontSize });
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

    private MenuItem MeasureMenuItem(int cx, int cy)
    {
        var item = new MenuItem { Header = "Measure distance" };
        item.Click += (_, _) => BeginMeasure(cx, cy);
        return item;
    }

    private void BuildMeasureLabel()
    {
        _measureLabel.BorderBrush = BrushFor(RangeEdgeArgb);
        _measurePipsText.Foreground = BrushFor(RangeEdgeArgb);
        _measureTimeText.Margin = new Thickness(0, 2, 0, 0);
        _measureBody.Children.Add(_measurePipsText);
        _measureBody.Children.Add(_measureTimeText);
        var close = new Button
        {
            Content = "✕",
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            FontSize = 11,
            Margin = new Thickness(14, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ToolTip = "Close",
        };
        close.Click += (_, _) => ClearMeasure();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_measureBody, 0);
        Grid.SetColumn(close, 1);
        grid.Children.Add(_measureBody);
        grid.Children.Add(close);
        _measureLabel.Child = grid;
        _measureLabel.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _measureLabel.MouseRightButtonDown += (_, e) => e.Handled = true;
        _measureLabel.MouseLeftButtonUp += (_, e) => e.Handled = true;
        _measureLabel.MouseRightButtonUp += (_, e) => e.Handled = true;
    }

    private void BeginMeasure(int cx, int cy)
    {
        if (_renderedColumnSeconds <= 0 || _renderedPointsPerRow <= 0) return;
        CancelPivotDrag();
        CancelDrawing();
        EndDrag();
        DeselectLine();
        HideHover();
        HideCalendarHover();
        CloseChartPopups();
        CloseStatsPopup();
        ClearMeasure();
        _measureAnchorUnix = ColumnTime(cx);
        _measureAnchorPrice = _renderedTopPrice - cy * _renderedPointsPerRow;
        _measureCursorUnix = _measureAnchorUnix;
        _measureCursorPrice = _measureAnchorPrice;
        _measuring = true;
        Focusable = true;
        Focus();
        UpdateMeasureVisuals();
    }

    private void MoveMeasureCursor(int cx, int cy)
    {
        if (_renderedColumnSeconds <= 0 || _renderedPointsPerRow <= 0) return;
        _measureCursorUnix = ColumnTime(cx);
        _measureCursorPrice = _renderedTopPrice - cy * _renderedPointsPerRow;
        UpdateMeasureVisuals();
    }

    private void FinishMeasure()
    {
        if (!_measuring) return;
        _measuring = false;
        _hasMeasure = true;
        _measureCanvas.IsHitTestVisible = true;
        UpdateMeasureVisuals();
    }

    public void ClearMeasure()
    {
        _measuring = false;
        _hasMeasure = false;
        _measureCanvas.IsHitTestVisible = false;
        _measureBand.Visibility = Visibility.Collapsed;
        _measureLabel.Visibility = Visibility.Collapsed;
        if (_measureTable != null)
        {
            _measureBody.Children.Remove(_measureTable);
            _measureTable = null;
        }
    }

    private void UpdateMeasureVisuals()
    {
        if (!_measuring && !_hasMeasure) return;
        double viewW = ActualWidth;
        double viewH = ActualHeight;
        if (_renderedColumnSeconds <= 0 || _renderedPointsPerRow <= 0 || viewW < 1 || viewH < 1)
        {
            HideMeasureVisuals();
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        long bucketSec = _renderedColumnSeconds;
        long lo = Math.Min(_measureAnchorUnix, _measureCursorUnix);
        long hi = Math.Max(_measureAnchorUnix, _measureCursorUnix);
        double x1 = (ToVirtual(lo) / bucketSec - _renderedStartBucket) / dpi.DpiScaleX;
        double x2 = (ToVirtual(hi) / bucketSec - _renderedStartBucket + 1) / dpi.DpiScaleX;
        double loPrice = Math.Min(_measureAnchorPrice, _measureCursorPrice);
        double hiPrice = Math.Max(_measureAnchorPrice, _measureCursorPrice);
        double y1 = (_renderedTopPrice - hiPrice) / _renderedPointsPerRow / dpi.DpiScaleY;
        double y2 = (_renderedTopPrice - loPrice) / _renderedPointsPerRow / dpi.DpiScaleY;
        if (x2 <= 0 || x1 >= viewW || y2 <= 0 || y1 >= viewH)
        {
            HideMeasureVisuals();
            return;
        }
        double left = Math.Clamp(x1, 0, viewW);
        double right = Math.Clamp(x2, 0, viewW);
        double top = Math.Clamp(y1, 0, viewH);
        double bottom = Math.Clamp(y2, 0, viewH);
        _measureBand.Width = Math.Max(1, right - left);
        _measureBand.Height = Math.Max(1, bottom - top);
        _measureBand.StrokeThickness = 1 / dpi.DpiScaleX;
        Canvas.SetLeft(_measureBand, left);
        Canvas.SetTop(_measureBand, top);
        _measureBand.Visibility = Visibility.Visible;
        UpdateMeasureLabel(lo, hi, loPrice, hiPrice, viewW);
    }

    private void HideMeasureVisuals()
    {
        _measureBand.Visibility = Visibility.Collapsed;
        _measureLabel.Visibility = Visibility.Collapsed;
    }

    private void UpdateMeasureLabel(long lo, long hi, double loPrice, double hiPrice, double viewW)
    {
        _measurePipsText.Text = MeasurePipsText(
            (_measureCursorPrice - _measureAnchorPrice) / PipPoints);
        _measureTimeText.Text = RangeText(lo, hi);
        if (_measureTable != null) _measureBody.Children.Remove(_measureTable);
        var rows = ComputeMeasureRows(loPrice, hiPrice);
        _measureTable = rows.Count > 0 ? BuildMeasureTable(rows) : null;
        if (_measureTable != null) _measureBody.Children.Add(_measureTable);
        double w = MeasureLabelWidth();
        _measureLabel.Width = w;
        _measureLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(_measureLabel, Math.Max(0, (viewW - w) / 2));
        Canvas.SetTop(_measureLabel, 0);
    }

    private double MeasureLabelWidth()
    {
        if (_measureLabelWidth > 0) return _measureLabelWidth;
        double pips = TextWidth(MeasureWidestPips, _measurePipsText.FontFamily,
            _measurePipsText.FontSize, FontWeights.Bold);
        double range = TextWidth(MeasureWidestRange, _measureTimeText.FontFamily,
            _measureTimeText.FontSize, FontWeights.Normal);
        double symbol = TextWidth(MeasureWidestSymbol, _measureTimeText.FontFamily,
            StatsCellFontSize, FontWeights.Normal) + MeasureCellGapDip;
        double price = TextWidth(MeasureWidestPrice, StatsNumberFont,
            StatsCellFontSize, FontWeights.Normal) + MeasureCellGapDip;
        double body = Math.Max(Math.Max(pips, range), symbol + price * 2);
        _measureLabelWidth = Math.Ceiling(body + MeasureChromeDip + MeasureCloseDip);
        return _measureLabelWidth;
    }

    private double TextWidth(string text, FontFamily family, double size, FontWeight weight)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal),
            size, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static string MeasurePipsText(double pips)
    {
        string sign = pips > 0 ? "+" : pips < 0 ? "-" : "";
        return sign + Math.Abs(pips).ToString("0.0", CultureInfo.InvariantCulture) + " pips";
    }

    private sealed record MeasureRow(string Symbol, int ColorArgb, int Digits, double Low, double High);

    private static readonly string[] MeasureHeaders = { "", "min", "max" };

    private Grid BuildMeasureTable(List<MeasureRow> rows)
    {
        var table = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        for (int c = 0; c < MeasureHeaders.Length; c++)
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r <= rows.Count; r++)
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var headBrush = BrushFor(unchecked((int)0xFF808080));
        for (int c = 0; c < MeasureHeaders.Length; c++)
            table.Children.Add(StatsCell(MeasureHeaders[c], 0, c, headBrush, c > 0));
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var brush = BrushFor(row.ColorArgb);
            string tenthFormat = "0." + new string('0', row.Digits + 1);
            table.Children.Add(StatsCell(row.Symbol, r + 1, 0, brush, false));
            table.Children.Add(StatsTenthCell(PriceText(row.Low, tenthFormat), r + 1, 1, brush));
            table.Children.Add(StatsTenthCell(PriceText(row.High, tenthFormat), r + 1, 2, brush));
        }
        return table;
    }

    private List<MeasureRow> ComputeMeasureRows(double loPrice, double hiPrice)
    {
        var result = new List<MeasureRow>();
        var series = _series;
        if (series == null) return result;
        double shift = FlattenShift(_measureAnchorUnix);
        foreach (var s in series)
        {
            if (!s.BasePair || s.BottomPanel || IsHidden(s.Symbol)) continue;
            double offset = SeriesOffset(s.Symbol);
            double a = ToTruePoints(s.Transform, loPrice - offset - shift) * s.PriceMul;
            double b = ToTruePoints(s.Transform, hiPrice - offset - shift) * s.PriceMul;
            result.Add(new MeasureRow(s.Symbol, s.ColorArgb, PriceDigits(s.PipPoints * s.PriceMul),
                Math.Min(a, b), Math.Max(a, b)));
        }
        return result;
    }

    private bool RangeContainsColumn(int cx)
    {
        if (!_hasRange || _rangeSelecting || _renderedColumnSeconds <= 0) return false;
        long bucketSec = _renderedColumnSeconds;
        long lo = Math.Min(_rangeStartUnix, _rangeEndUnix);
        long hi = Math.Max(_rangeStartUnix, _rangeEndUnix);
        long x1 = ToVirtual(lo) / bucketSec - _renderedStartBucket;
        long x2 = ToVirtual(hi) / bucketSec - _renderedStartBucket;
        return cx >= x1 && cx <= x2;
    }

    private void ShowRangeMenu(Point pos, int cx, int cy)
    {
        var menu = new ContextMenu { PlacementTarget = this };
        var toMax = new MenuItem { Header = "Align to max" };
        toMax.Click += (_, _) => AlignRangeExtremeToGrid(pos, true);
        menu.Items.Add(toMax);
        var toMin = new MenuItem { Header = "Align to min" };
        toMin.Click += (_, _) => AlignRangeExtremeToGrid(pos, false);
        menu.Items.Add(toMin);
        AddChartMenuItems(menu, cx, cy);
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
        if (_pointsPerRow <= 0 || _renderedColumnSeconds <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int ph = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (ph < 2) return;
        int cy = Math.Clamp((int)Math.Floor(pos.Y * dpi.DpiScaleY), 0, ph - 1);
        long loUnix = MinuteFloor(range.StartUnix);
        long hiUnix = range.EndUnix + _renderedColumnSeconds - 1;
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

    public void ShiftDensityScale(string symbol, int delta)
    {
        double current = _densityUnits.TryGetValue(symbol, out var live) && live > 0
            ? live
            : DensityPerPixelOf(symbol);
        if (current <= 0) return;
        double next = Math.Clamp(
            delta > 0 ? current / ZoomStep : current * ZoomStep, DensityUnitMin, DensityUnitMax);
        _densityUnits[symbol] = next;
        DensityScaleChanged?.Invoke(symbol, next);
        UpdateDensityOverlay();
    }

    private double DensityPerPixelOf(string symbol)
    {
        var s = GetSeries(symbol);
        if (s != null && s.DensityScalePerPixel > 0) return s.DensityScalePerPixel;
        return _densityAutoPerPixel.TryGetValue(symbol, out var auto) ? auto : 0;
    }

    public void ShiftVolumeScale(string symbol, int delta)
    {
        var series = VolumeSeries(symbol);
        if (series == null) return;
        double scale = VolumeScaleOf(series);
        double next = ClampVolumeScale(delta > 0 ? scale * ZoomStep : scale / ZoomStep);
        if (next == scale) return;
        _volumeScales[symbol] = next;
        VolumeScaleChanged?.Invoke(symbol, next);
        Rebuild();
    }

    private static double ClampVolumeScale(double scale) =>
        double.IsFinite(scale) && scale > 0
            ? Math.Clamp(scale, VolumeScaleMin, VolumeScaleMax)
            : 1.0;

    private double VolumeScaleOf(SymbolSeries s) =>
        _volumeScales.TryGetValue(s.Symbol, out var v)
            ? ClampVolumeScale(v)
            : ClampVolumeScale(s.VolumeBarScale);

    public void ShiftVolumeGroup(string symbol, int delta)
    {
        var series = VolumeSeries(symbol);
        if (series == null || series.VolumeGroupLocked) return;
        int current = VolumeGroupOf(series);
        int next = IndicatorSymbol.StepVolumeGroup(current, delta);
        if (next == current) return;
        _volumeGroups[symbol] = next;
        VolumeGroupChanged?.Invoke(symbol, next);
        if (_volumeUnits.TryGetValue(symbol, out var unit) && unit > 0)
        {
            double scaled = IndicatorSymbol.ScaleVolumeBarUnit(unit, current, next);
            _volumeUnits[symbol] = scaled;
            VolumeUnitChanged?.Invoke(symbol, scaled);
        }
        Rebuild();
    }

    private string? VolumeBandSymbolAt(double y)
    {
        foreach (var (symbol, bottom) in _volumePanelBottoms)
            if (y <= bottom && y > bottom - VolumeWheelBandPx) return symbol;
        return null;
    }

    private SymbolSeries? VolumeSeries(string symbol) =>
        _series?.FirstOrDefault(s => s.VolumePanel && s.Symbol == symbol);

    private int VolumeGroupOf(SymbolSeries s) =>
        _volumeGroups.TryGetValue(s.Symbol, out var g) ? g : Math.Max(1, s.VolumeGroupMinutes);

    private void OnZoom(object sender, MouseWheelEventArgs e)
    {
        using var _ = Perf.FrameStep("input.zoom");
        if (_pivotDragging || _selDragging)
        {
            e.Handled = true;
            return;
        }
        HideHover();
        CloseChartPopups();
        if (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Alt)
        {
            var dpiBand = VisualTreeHelper.GetDpi(this);
            string? banded = VolumeBandSymbolAt(e.GetPosition(this).Y * dpiBand.DpiScaleY);
            if (banded != null)
            {
                e.Handled = true;
                if (Keyboard.Modifiers == ModifierKeys.Alt) ShiftVolumeGroup(banded, e.Delta);
                else ShiftVolumeScale(banded, e.Delta);
                return;
            }
        }
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
        long fit = ChartColumns.FitColumnSeconds(firstUnix, lastUnix, pw);
        long cs = _columnSeconds <= 0 || _columnSeconds > fit ? fit : _columnSeconds;
        long startBucket = _columnSeconds <= 0 ? firstUnix / cs : _viewStartBucket;
        var pos = e.GetPosition(this);
        int xm = Math.Clamp((int)Math.Floor(pos.X * dpi.DpiScaleX), 0, pw - 1);
        long anchorTime = (startBucket + xm) * cs;
        long next = ChartColumns.Zoomed(cs, e.Delta > 0, ZoomStep);
        e.Handled = true;
        bool subMinute = cs <= ChartColumns.MinuteSeconds || next <= ChartColumns.MinuteSeconds;
        bool zoomVertical = !horizontalOnly && !subMinute;
        if (next >= fit)
        {
            if (_columnSeconds <= 0) return;
            _columnSeconds = 0;
            if (zoomVertical) ApplyVerticalZoom(PairedVerticalFactor((double)fit / cs), pos.Y);
            Rebuild();
            return;
        }
        if (next == cs && _columnSeconds > 0) return;
        long newFirstBucket = firstUnix / next;
        long newLastBucket = lastUnix / next;
        long newStart = ClampViewStart(anchorTime / next - xm, newFirstBucket, newLastBucket, pw);
        _columnSeconds = next;
        _viewStartBucket = newStart;
        if (_dragging)
        {
            _dragStartX = (int)Math.Floor(pos.X * dpi.DpiScaleX);
            _dragStartViewBucket = newStart;
        }
        if (zoomVertical) ApplyVerticalZoom(PairedVerticalFactor((double)next / cs), pos.Y);
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
            Perf.Count("rebuild.coalesced");
            return;
        }
        _computing = true;
        long frameTicks = Perf.Now();
        long frameSeq = Perf.Mark();
        long k;
        long startBucket;
        if (_columnSeconds <= 0)
        {
            k = ChartColumns.FitColumnSeconds(ViewFirstUnix, ViewLastUnix, pw);
            startBucket = ViewFirstUnix / k;
        }
        else
        {
            k = _columnSeconds;
            long fb = ViewFirstUnix / k;
            long lb = ViewLastUnix / k;
            startBucket = ClampViewStart(_viewStartBucket, fb, lb, pw);
            _viewStartBucket = startBucket;
        }
        double topPrice = _topPrice;
        double pointsPerRow = _pointsPerRow;
        var forecastMarkers = new List<ForecastMarker>();
        var forecastMarks = _forecastHidden ? Array.Empty<ForecastMark>() : SelectedDayForecasts();
        var forecastHover =
            _forecastHidden ? Array.Empty<ForecastMark>() : _forecastHover;
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
        bool sessionBands = _sessionsVisible;
        var flatten = _flatten;
        var tiltedGrid = new TiltedGridSettings(
            TiltedRenderSettings(true), TiltedRenderSettings(false));
        var edges = ChartColumns.ColumnEdges(map, k, startBucket, pw);
        var swRender = _firstRenderLogged ? null : Stopwatch.StartNew();
        string? hotShift = _shiftHotSymbol;
        int hotShiftWidth = _shiftDragging ? 1 : ShiftHotWidthPx;
        string? pressSymbol = _pressSymbol;
        var drawOrder = SeriesDrawOrder(visibleSeries);
        var shiftLines = new List<(string Symbol, RenderLine Line)>();
        var renderedLines = new List<(string Symbol, RenderLine Line)>();
        var spreadCols = new List<(string Symbol, int[] Columns)>();
        var volumeCols = new List<(string Symbol, VolumeColumnSet Columns)>();
        var volumeUnitsUsed = new List<(string Symbol, double Unit, int Bottom)>();
        var volumeScales = new Dictionary<string, double>(_volumeScales);
        var volumeGroups = new Dictionary<string, int>(_volumeGroups);
        var volumeUnits = new Dictionary<string, double>(_volumeUnits);
        RenderLine? hotLine = null;
        try
        {
            await Task.Run(() =>
            {
                long t = Perf.Now();
                var lines = new List<RenderLine>(visibleSeries.Count);
                var columnShifts = flatten?.ColumnShifts(startBucket - pw, pw * 2, k);
                int hotIndex = -1;
                foreach (int si in drawOrder)
                {
                    var s = visibleSeries[si];
                    if (s.History.Minutes.Length == 0 || s.BottomPanel) continue;
                    var (view, chosen, fullRange) = ChartColumns.BuildLine(
                        s.History, k, startBucket - pw, pw * 2, map, pw, NoiseThreshold);
                    var last = s.History.LastCandle;
                    int lastPrice = s.History.HasLastTick ? s.History.LastTick : last.Avg;
                    long lastVirtual = map?.ToVirtual(last.MinuteUnixSeconds) ?? last.MinuteUnixSeconds;
                    var line = new RenderLine(view, chosen, s.ColorArgb, lastPrice,
                        (lastVirtual + ChartColumns.MinuteSeconds - 1) / k,
                        lineOffsets[si], columnShifts,
                        s.Symbol == pressSymbol ? SeriesPressWidthPx : 1, fullRange);
                    if (s.TimeShift) shiftLines.Add((s.Symbol, line));
                    renderedLines.Add((s.Symbol, line));
                    if (s.Symbol == hotShift) hotIndex = lines.Count;
                    lines.Add(line);
                }
                t = Perf.Since("chart.buildlines", t);
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
                                long b = v / k;
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
                t = Perf.Since("chart.fitscale", t);
                ChartRasterizer.Render(_staging, pw, ph, lines, Palette, k, startBucket, edges,
                    topPrice, pointsPerRow, tiltedGrid, sessionBands);
                t = Perf.Since("chart.raster", t);
                if (pointsPerRow > 0)
                    foreach (int si in drawOrder)
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
                t = Perf.Since("chart.vectors", t);
                if (pointsPerRow > 0)
                    for (int si = 0; si < visibleSeries.Count; si++)
                    {
                        var s = visibleSeries[si];
                        if (s.DealMarks is not { Length: > 0 } || s.Transform == null) continue;
                        DrawDealMarks(_staging, pw, ph, s, flatten, map, k, startBucket,
                            topPrice, pointsPerRow, lineOffsets[si]);
                    }
                if (pointsPerRow > 0 && forecastMarks.Length > 0)
                    DrawForecasts(_staging, pw, ph, forecastMarks, forecastHover, visibleSeries,
                        lineOffsets, flatten, map, k, startBucket, topPrice, pointsPerRow,
                        forecastMarkers);
                if (calVisible && calEntries.Length > 0)
                    DrawCalendar(_staging, pw, ph, calEntries, k, startBucket, map, calShown);
                if (hotLine != null && pointsPerRow > 0)
                    ChartRasterizer.DrawSeries(_staging, pw, ph, hotLine.Value, startBucket,
                        topPrice, pointsPerRow);
                t = Perf.Since("chart.overlays", t);
                int panelBottom = ph - 1;
                foreach (var s in visibleSeries)
                {
                    if (!s.BottomPanel) continue;
                    if (s.SpreadPanel)
                    {
                        var src = s.SourceSymbol == null
                            ? null
                            : seriesList.FirstOrDefault(x => x.Symbol == s.SourceSymbol);
                        if (src == null
                            || (src.History.Minutes.Length == 0 && src.History.Live.Length == 0))
                            continue;
                        var tenths = SpreadColumns.Build(src.History, k, startBucket, pw, map);
                        spreadCols.Add((s.Symbol, tenths));
                        ChartRasterizer.DrawSpreadPanel(
                            _staging, pw, ph, tenths, panelBottom, s.ColorArgb, pointsPerRow);
                        t = Perf.Since("panel.spread", t);
                        panelBottom -=
                            ChartRasterizer.SpreadPanelHeightPx + ChartRasterizer.EntryPanelGapPx;
                        continue;
                    }
                    if (s.VolumePanel)
                    {
                        var src = s.SourceSymbol == null
                            ? null
                            : seriesList.FirstOrDefault(x => x.Symbol == s.SourceSymbol);
                        if (src == null
                            || (src.History.Minutes.Length == 0 && src.History.Live.Length == 0))
                            continue;
                        int group = volumeGroups.TryGetValue(s.Symbol, out var vg)
                            ? vg
                            : Math.Max(1, s.VolumeGroupMinutes);
                        var vols = VolumeColumns.Build(src.History, s.VolumeProfiles,
                            k, startBucket, pw, map, group);
                        t = Perf.Since("panel.volume.build", t);
                        volumeCols.Add((s.Symbol, vols));
                        double used = ChartRasterizer.DrawVolumePanel(
                            _staging, pw, ph, vols, panelBottom, s.ColorArgb,
                            volumeScales.TryGetValue(s.Symbol, out var vs)
                                ? vs
                                : ClampVolumeScale(s.VolumeBarScale),
                            s.VolumeSplitSides ? s.SellColorArgb : 0,
                            volumeUnits.TryGetValue(s.Symbol, out var vu) ? vu : 0);
                        t = Perf.Since("panel.volume.draw", t);
                        volumeUnitsUsed.Add((s.Symbol, used, panelBottom));
                        panelBottom -=
                            ChartRasterizer.VolumePanelHeightPx + ChartRasterizer.EntryPanelGapPx;
                        continue;
                    }
                    if (s.History.Minutes.Length == 0) continue;
                    if (s.AgePanel)
                    {
                        var ages = PriceAgeColumns.Build(
                            s.History, k, startBucket, pw, map, s.AgeMirror);
                        ChartRasterizer.DrawAgePanel(_staging, pw, ph, ages, panelBottom, s.ColorArgb);
                        t = Perf.Since("panel.age", t);
                        panelBottom -= ChartRasterizer.AgePanelHeightPx + ChartRasterizer.EntryPanelGapPx;
                    }
                    else if (s.EntryPanel)
                    {
                        var states = EntryPointsColumns.Build(s.History, k, startBucket, pw, map);
                        ChartRasterizer.DrawEntryPanel(
                            _staging, pw, ph, states, panelBottom, Palette.Background);
                        t = Perf.Since("panel.entry", t);
                        panelBottom -= ChartRasterizer.EntryPanelHeightPx + ChartRasterizer.EntryPanelGapPx;
                    }
                }
            });
            long tUi = Perf.Now();
            if (version == _version)
            {
                if (scaleInit && pointsPerRow > 0)
                {
                    _topPrice = topPrice;
                    _pointsPerRow = pointsPerRow;
                }
                _renderedColumnSeconds = k;
                _renderedStartBucket = startBucket;
                _renderedTopPrice = topPrice;
                _renderedPointsPerRow = pointsPerRow;
                _shiftLines.Clear();
                _shiftLines.AddRange(shiftLines);
                _renderedLines.Clear();
                _renderedLines.AddRange(renderedLines);
                _renderedSpreadColumns.Clear();
                foreach (var (sym, cols) in spreadCols) _renderedSpreadColumns[sym] = cols;
                _renderedVolumeColumns.Clear();
                foreach (var (sym, cols) in volumeCols) _renderedVolumeColumns[sym] = cols;
                _volumePanelBottoms.Clear();
                foreach (var (sym, unit, bottom) in volumeUnitsUsed)
                {
                    _volumePanelBottoms[sym] = bottom;
                    if (unit <= 0 || (_volumeUnits.TryGetValue(sym, out var have) && have > 0)) continue;
                    _volumeUnits[sym] = unit;
                    VolumeUnitChanged?.Invoke(sym, unit);
                }
                _forecastMarkers.Clear();
                _forecastMarkers.AddRange(forecastMarkers);
                tUi = Perf.Since("chart.publish", tUi);
                Present(pw, ph, dpi);
                tUi = Perf.Since("chart.present", tUi);
                if (swRender != null)
                {
                    _firstRenderLogged = true;
                    Info?.Invoke($"Chart first render: {swRender.ElapsedMilliseconds} ms " +
                        $"({visibleSeries.Count} series, {pw}x{ph}px, k={k})");
                }
                ViewChanged?.Invoke(k, startBucket, pw, map);
                tUi = Perf.Since("chart.viewchanged", tUi);
                StateChanged?.Invoke(new ChartViewState
                {
                    WeekendsHidden = _weekendsHidden,
                    SessionsVisible = _sessionsVisible,
                    ColumnSeconds = _columnSeconds,
                    ViewStartBucket = _viewStartBucket,
                    TopPrice = _topPrice,
                    PointsPerRow = _pointsPerRow,
                    PriceOffsetPoints = _priceOffsetPoints,
                    SymbolOffsetPoints = new Dictionary<string, double>(_seriesOffsetPoints),
                    RelativeIndicatorOffsets = true,
                    HiddenSymbols = new List<string>(_hiddenSymbols),
                    CollapsedSymbols = new List<string>(_collapsedSources),
                    CalendarVisible = _calendarVisible,
                    ForecastHidden = _forecastHidden,
                    FlattenSymbol = _flattenSymbol,
                    FlattenLine = _flattenLine,
                    TiltedUpGridIndex = _tiltedUpIndex,
                    TiltedDownGridIndex = _tiltedDownIndex,
                    TiltedGrids = _tiltedGrids.Select(g => g.Clone()).ToList(),
                });
                Perf.Since("chart.statechanged", tUi);
            }
        }
        catch (Exception ex)
        {
            Info?.Invoke("Chart failed: " + ex);
        }
        finally
        {
            _computing = false;
            Perf.Frame("chart.rebuild", frameTicks, frameSeq);
        }
        if (_pending)
        {
            _pending = false;
            Rebuild();
        }
    }

    private static void DrawForecasts(int[] buffer, int pw, int ph, ForecastMark[] marks,
        ForecastMark[] hover, List<SymbolSeries> all, double[] offsets, FlattenMap? flatten,
        WeekendCompressor? map, long columnSeconds, long startBucket, double topPrice,
        double pointsPerRow, List<ForecastMarker> markers)
    {
        double bucketSec = columnSeconds;
        int half = ChartRasterizer.ForecastMarkerHalfPx;
        foreach (var mark in marks)
        {
            int ti = -1;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Transform == null || all[i].BottomPanel) continue;
                if (IndicatorSymbol.NameKey(all[i].Symbol) != IndicatorSymbol.NameKey(mark.Pair))
                    continue;
                ti = i;
                break;
            }
            if (ti < 0) continue;
            var target = all[ti];
            double offset = offsets[ti];
            int color = target.ColorArgb;
            long va = map?.ToVirtual(mark.FromUnix) ?? mark.FromUnix;
            long vb = map?.ToVirtual(mark.ToUnix) ?? mark.ToUnix;
            if (vb <= va) continue;
            double xa = va / bucketSec - startBucket;
            double xb = vb / bucketSec - startBucket;
            if (xb < -half || xa >= pw + half) continue;
            double d1 = target.Transform!.ToDisplay(mark.TopValue);
            double d2 = target.Transform.ToDisplay(mark.BottomValue);
            double hiDisplay = Math.Max(d1, d2);
            double loDisplay = Math.Min(d1, d2);
            bool filled = mark.Band
                && hiDisplay - loDisplay <= ChartRasterizer.ForecastFillMaxPoints;
            bool hot = HasMark(hover, mark);
            int xs = Math.Max(0, (int)Math.Floor(xa + 0.5));
            int xe = Math.Min(pw - 1, (int)Math.Floor(xb + 0.5));
            for (int px = xs; px <= xe; px++)
            {
                long v = (long)((px + startBucket) * bucketSec);
                double shift = flatten?.ShiftAt(v) ?? 0;
                int yHi = (int)Math.Floor((topPrice - offset - hiDisplay - shift) / pointsPerRow + 0.5);
                int yLo = (int)Math.Floor((topPrice - offset - loDisplay - shift) / pointsPerRow + 0.5);
                if (filled && yLo - yHi > 1)
                    ChartRasterizer.BlendColumn(buffer, pw, ph, px, yHi + 1, yLo - 1,
                        color, ChartRasterizer.ForecastBandAlpha);
                ChartRasterizer.BlendPixel(buffer, pw, ph, px, yHi, color,
                    ChartRasterizer.ForecastEdgeAlpha);
                if (hot && (!mark.Band || yHi + 1 < yLo))
                    ChartRasterizer.BlendPixel(buffer, pw, ph, px, yHi + 1, color,
                        ChartRasterizer.ForecastEdgeAlpha);
                if (mark.Band)
                {
                    ChartRasterizer.BlendPixel(buffer, pw, ph, px, yLo, color,
                        ChartRasterizer.ForecastEdgeAlpha);
                    if (hot && yLo - 1 > yHi + 1)
                        ChartRasterizer.BlendPixel(buffer, pw, ph, px, yLo - 1, color,
                            ChartRasterizer.ForecastEdgeAlpha);
                }
            }
            if (xb >= 0 && xb < pw)
            {
                double endShift = flatten?.ShiftAt(vb) ?? 0;
                int xEnd = (int)Math.Round(xb);
                DrawForecastEndCap(buffer, pw, ph, xEnd,
                    (topPrice - offset - hiDisplay - endShift) / pointsPerRow, color);
                if (mark.Band)
                    DrawForecastEndCap(buffer, pw, ph, xEnd,
                        (topPrice - offset - loDisplay - endShift) / pointsPerRow, color);
            }
            if (xa < -half || xa >= pw + half) continue;
            double startShift = flatten?.ShiftAt(va) ?? 0;
            double Y(double display) =>
                (topPrice - offset - display - startShift) / pointsPerRow;
            if (mark.Band && !filled)
            {
                AddForecastMarker(buffer, pw, ph, markers, mark, color, half, xa, Y(hiDisplay));
                AddForecastMarker(buffer, pw, ph, markers, mark, color, half, xa, Y(loDisplay));
                continue;
            }
            AddForecastMarker(buffer, pw, ph, markers, mark, color, half, xa,
                Y(mark.Band ? (hiDisplay + loDisplay) / 2 : hiDisplay));
        }
    }

    private static void DrawForecastEndCap(int[] buffer, int pw, int ph,
        int x, double y, int color)
    {
        int cy = (int)Math.Round(y);
        int half = ChartRasterizer.ForecastEndCapHalfPx;
        for (int dy = -half; dy <= half; dy++)
            ChartRasterizer.BlendPixel(buffer, pw, ph, x, cy + dy, color,
                ChartRasterizer.ForecastEdgeAlpha);
    }

    private static void AddForecastMarker(int[] buffer, int pw, int ph,
        List<ForecastMarker> markers, ForecastMark mark, int color, int half,
        double x, double y)
    {
        int cxp = (int)Math.Round(x);
        int cyp = (int)Math.Round(y);
        if (cyp < -half || cyp >= ph + half) return;
        int outline = unchecked((int)0xFFFFFFFF);
        if (mark.Record.IsOwn)
        {
            ChartRasterizer.FillRightTriangle(buffer, pw, ph, cxp, cyp, half + 1, outline);
            ChartRasterizer.FillRightTriangle(buffer, pw, ph, cxp, cyp, half, color);
        }
        else
        {
            ChartRasterizer.StrokeDisc(buffer, pw, ph, cxp, cyp, half + 1, outline);
            ChartRasterizer.FillDisc(buffer, pw, ph, cxp, cyp, half, color);
        }
        markers.Add(new ForecastMarker(cxp, cyp, half, mark, color));
    }

    private List<ForecastMarker> FindForecastMarkersAt(int x, int y)
    {
        var hits = new List<(double Dist, ForecastMarker Marker)>();
        foreach (var c in _forecastMarkers)
        {
            double dx = x - c.X;
            double dy = y - c.Y;
            double r = c.HalfPx + ChartRasterizer.ForecastMarkerHitPx;
            double d = dx * dx + dy * dy;
            if (d <= r * r) hits.Add((d, c));
        }
        return hits.OrderBy(h => h.Dist).Select(h => h.Marker).ToList();
    }

    private void ShowForecastPopup(List<ForecastMarker> markers)
    {
        if (markers.Count == 0) return;
        var marker = markers[0];
        CloseChartPopups();
        var dpi = VisualTreeHelper.GetDpi(this);
        double cxDip = marker.X / dpi.DpiScaleX;
        double cyDip = marker.Y / dpi.DpiScaleY;
        _forecastPopup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Top,
            PlacementRectangle = new Rect(
                cxDip - marker.HalfPx, cyDip - marker.HalfPx,
                2 * marker.HalfPx, 2 * marker.HalfPx),
            StaysOpen = true,
            AllowsTransparency = true,
            Child = BuildForecastPopupContent(markers),
        };
        _forecastPopup.IsOpen = true;
    }

    private void CloseForecastPopup()
    {
        if (_forecastPopup == null) return;
        _forecastPopup.IsOpen = false;
        _forecastPopup = null;
    }

    private void CloseChartPopups()
    {
        CloseCalendarPopup();
        CloseForecastPopup();
    }

    private static string ForecastPriceText(double price, string pair)
    {
        int digits = pair.EndsWith("JPY", StringComparison.OrdinalIgnoreCase) ? 3 : 5;
        return price.ToString("F" + digits, CultureInfo.InvariantCulture);
    }

    private FrameworkElement BuildForecastPopupContent(List<ForecastMarker> markers)
    {
        var panel = new StackPanel();
        for (int ci = 0; ci < markers.Count; ci++)
        {
            if (ci > 0)
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Background = BrushFor(unchecked((int)0xFFDDDDDD)),
                    Margin = new Thickness(0, 10, 0, 10),
                });
            AddForecastBlock(panel, markers[ci]);
        }
        var closeButton = new Button
        {
            Content = "\u2715",
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ToolTip = "Close",
        };
        closeButton.Click += (_, _) => CloseForecastPopup();
        var scroll = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 420,
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
            MaxWidth = 420,
            Child = grid,
        };
    }

    private void AddForecastBlock(StackPanel panel, ForecastMarker marker)
    {
        var mark = marker.Mark;
        var r = mark.Record;
        var made = DateTimeOffset.FromUnixTimeSeconds(r.MadeAtUnix).UtcDateTime;
        var until = DateTimeOffset.FromUnixTimeSeconds(r.UntilUnix).UtcDateTime;
        string levelText = mark.Band
            ? ForecastPriceText(r.BottomPrice, r.Pair) + " - " + ForecastPriceText(r.TopPrice, r.Pair)
            : ForecastPriceText(r.Price, r.Pair);
        panel.Children.Add(new TextBlock
        {
            Text = $"{r.Pair}  \u00b7  {levelText}",
            Foreground = BrushFor(marker.ColorArgb),
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(r.Title))
            panel.Children.Add(new TextBlock
            {
                Text = r.Title,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        var head = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Horizon)) head.Add(r.Horizon);
        if (r.Probability > 0) head.Add(r.Probability.ToString("0.#", CultureInfo.InvariantCulture) + "%");
        head.Add($"{made:yyyy-MM-dd HH:mm} - {until:yyyy-MM-dd HH:mm} UTC");
        panel.Children.Add(new TextBlock
        {
            Text = string.Join("  \u00b7  ", head),
            Foreground = BrushFor(unchecked((int)0xFF555555)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        });
        void AddField(string label, string value, bool small)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = BrushFor(unchecked((int)0xFF777777)),
                Margin = new Thickness(0, 8, 0, 1),
            });
            panel.Children.Add(new TextBlock
            {
                Text = value,
                TextWrapping = TextWrapping.Wrap,
                FontSize = small ? 11 : 12,
            });
        }
        AddField("Author", r.Author, true);
        AddField("Basis", r.Basis, false);
        AddField("Source", r.Source, true);
        AddField("Source URL", r.SourceUrl, true);
        AddField("Note", r.Note, true);
        AddField("Outcome", r.Outcome, false);
    }

    private static void DrawDealMarks(int[] buffer, int pw, int ph, SymbolSeries s,
        FlattenMap? flatten, WeekendCompressor? map, long columnSeconds, long startBucket,
        double topPrice, double pointsPerRow, double offset)
    {
        double bucketSec = columnSeconds;
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
        long columnSeconds, long startBucket, double topPrice, double pointsPerRow, double offset,
        long fromVirtual, double fromDisplay, long toVirtual, double toDisplay, int color)
    {
        double X(long v) => v / (double)columnSeconds - startBucket;
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
        long t = Perf.Now();
        _bitmap.WritePixels(new Int32Rect(0, 0, pw, ph), _staging, pw * 4, 0);
        t = Perf.Since("present.blit", t);
        UpdateRangeVisuals();
        UpdateMeasureVisuals();
        t = Perf.Since("present.range", t);
        UpdateEditMarkers(dpi, pw, ph);
        t = Perf.Since("present.markers", t);
        UpdateDensityOverlay();
        t = Perf.Since("present.density", t);
        if (_drawSymbol != null)
        {
            UpdateDrawPreview(dpi);
            ReanchorDrawCursor(dpi);
        }
        if (_selSymbol != null) UpdateSelectionVisuals(dpi);
        Perf.Since("present.rest", t);
        if (!_calendarVisible || _renderedColumnSeconds <= 0 || !CalendarLinesDrawn(_renderedColumnSeconds))
        {
            HideCalendarHover();
            CloseCalendarPopup();
        }
    }

    private void ReanchorDrawCursor(DpiScale dpi)
    {
        if (_drawPoints.Count == 0 || _drawCursor.Visibility != Visibility.Visible) return;
        var s = _drawSymbol == null ? null : GetSeries(_drawSymbol);
        if (s?.Transform == null || _renderedColumnSeconds <= 0 || _renderedPointsPerRow <= 0) return;
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
        if (series != null && _renderedColumnSeconds > 0 && _renderedPointsPerRow > 0
            && pw >= DensityMaxWidthPx && ph >= 1)
            foreach (var s in series)
                if (!IsHidden(s.Symbol) && s.SourceSymbol != null
                    && ((s.DensityPanel && s.DensityWindows is { Length: > 0 })
                        || (s.OrderBookPanel && (s.OrderBookSnapshots is { Length: > 0 }
                            || s.DepthSnapshots is { Length: > 0 }))))
                    (panels ??= new List<SymbolSeries>()).Add(s);
        if (panels == null)
        {
            _densityImage.Visibility = Visibility.Collapsed;
            _densityLabelPanel.Visibility = Visibility.Collapsed;
            _densityAnchorColumn = long.MinValue;
            return;
        }
        var range = SelectedRange;
        int cx = _cursorOnChart ? Math.Clamp(_cursorPx, 0, pw - 1) : pw - 1;
        _densityAnchorColumn = _renderedStartBucket + cx;
        long bucketSec = _renderedColumnSeconds;
        long rangeStartUnix = 0;
        long rangeEndUnix = 0;
        if (range is { } r)
        {
            rangeStartUnix = MinuteFloor(ToReal(ToVirtual(r.StartUnix) / bucketSec * bucketSec));
            rangeEndUnix = ToRealEnd((ToVirtual(r.EndUnix) / bucketSec + 1) * bucketSec) - 1;
        }
        long anchorUnix = range != null ? rangeEndUnix : MinuteFloor(ColumnEndUnix(_densityAnchorColumn));
        long tDensity = Perf.Now();
        if (_densityStaging.Length < pw * ph)
            _densityStaging = new int[pw * ph];
        int stale = Math.Clamp(_densityUsedPx, DensityMaxWidthPx, pw);
        for (int y = 0; y < ph; y++)
            Array.Clear(_densityStaging, y * pw + pw - stale, stale);
        int used = DensityMaxWidthPx;
        bool drew = false;
        foreach (var s in panels)
        {
            DensityHistogram histogram;
            DensityHistogram candle = default;
            if (s.OrderBookPanel)
            {
                var source = GetSeries(s.SourceSymbol!);
                var transform = source?.Transform
                    ?? new SeriesTransform(false, 0, source?.PipPoints ?? PipPoints);
                OrderBookSides sides;
                long? bookUnix;
                int? price;
                if (s.DepthSnapshots is { Length: > 0 } depth)
                {
                    var book = DepthProfile.At(depth, anchorUnix);
                    bookUnix = book?.MinuteUnix;
                    price = bookUnix == null || source == null
                        ? null
                        : OrderBookProfile.PricePointsAt(source.History.Minutes, bookUnix.Value);
                    sides = DepthProfile.BuildSides(book, s.DepthPipPoints, transform, price);
                }
                else
                {
                    var snapshot = OrderBookProfile.At(s.OrderBookSnapshots!, anchorUnix);
                    sides = OrderBookProfile.BuildSides(snapshot, s.OrderBookPositions, transform);
                    bookUnix = snapshot?.TimeUnix;
                    price = bookUnix == null || source == null
                        ? null
                        : OrderBookProfile.PricePointsAt(source.History.Minutes, bookUnix.Value);
                }
                tDensity = Perf.Since("density.book.build", tDensity);
                if (sides.Buy.MaxCount <= 0) continue;
                DrawOrderBook(_densityStaging, pw, ph, sides,
                    SeriesOffset(s.SourceSymbol!), anchorUnix, s.ColorArgb, s.SellColorArgb,
                    price == null ? null : transform.ToDisplay(price.Value));
                tDensity = Perf.Since("density.book.draw", tDensity);
                drew = true;
                continue;
            }
            {
                var sourceSeries = GetSeries(s.SourceSymbol!);
                var minutes = sourceSeries?.History.Minutes;
                if (minutes == null || minutes.Length == 0) continue;
                var profileTransform = sourceSeries!.Transform;
                int option = Math.Clamp(_densitySelected, 0, s.DensityWindows!.Length);
                int window = range != null
                    ? DensityProfile.CountInRange(minutes, rangeStartUnix, rangeEndUnix)
                    : option == s.DensityWindows.Length
                        ? int.MaxValue
                        : s.DensityWindows[option];
                if (window <= 0) continue;
                histogram = s.VolumeWeighted
                    ? s.VolumeProfiles is { } profiles
                        ? DensityProfile.BuildProfiled(minutes, profiles, anchorUnix, window, profileTransform)
                        : DensityProfile.BuildWeighted(minutes, anchorUnix, window)
                    : DensityProfile.Build(minutes, anchorUnix, window);
                if (s.VolumeWeighted && range == null)
                {
                    var (from, to) = VolumeCandleRange(s, bucketSec);
                    candle = s.VolumeProfiles is { } candleProfiles
                        ? DensityProfile.BuildProfiledRange(minutes, candleProfiles, from, to, profileTransform)
                        : DensityProfile.BuildWeightedRange(minutes, from, to);
                }
            }
            tDensity = Perf.Since("density.histogram", tDensity);
            if (histogram.MaxCount <= 0) continue;
            int bidColor = s.VolumeWeighted && s.VolumeSplitSides ? s.SellColorArgb : 0;
            double max = DensityScaleMax(s, histogram.MaxCount);
            used = Math.Max(used, DrawDensity(_densityStaging, pw, ph, histogram,
                SeriesOffset(s.SourceSymbol!), anchorUnix, s.ColorArgb,
                max, DensityFillAlpha, bidColor));
            if (candle.MaxCount > 0)
                used = Math.Max(used, DrawDensity(_densityStaging, pw, ph, candle,
                    SeriesOffset(s.SourceSymbol!), anchorUnix, s.ColorArgb,
                    max, DensityCandleAlpha, bidColor));
            tDensity = Perf.Since("density.draw", tDensity);
            drew = true;
        }
        if (_densityBmp == null || _densityBmp.PixelWidth != pw
            || _densityBmp.PixelHeight != ph)
        {
            _densityBmp = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32, null);
            _densityImage.Source = _densityBmp;
        }
        int band = Math.Clamp(Math.Max(used, _densityUsedPx), DensityMaxWidthPx, pw);
        _densityUsedPx = used;
        _densityBmp.WritePixels(
            new Int32Rect(pw - band, 0, band, ph), _densityStaging, pw * 4, pw - band, 0);
        _densityImage.Width = pw / dpi.DpiScaleX;
        _densityImage.Height = ph / dpi.DpiScaleY;
        Canvas.SetLeft(_densityImage, 0);
        Canvas.SetTop(_densityImage, 0);
        _densityImage.Visibility = drew ? Visibility.Visible : Visibility.Collapsed;
        tDensity = Perf.Since("density.blit", tDensity);
        UpdateDensityLabels(panels, anchorUnix);
        Perf.Since("density.labels", tDensity);
    }

    private void DrawOrderBook(int[] buffer, int width, int height, OrderBookSides sides,
        double offset, long anchorUnix, int buyArgb, int sellArgb, int? priceDisplay)
    {
        int buyFill = PremultiplyArgb(buyArgb, DensityFillAlpha);
        int sellFill = PremultiplyArgb(sellArgb, DensityFillAlpha);
        int buyEdge = buyArgb | unchecked((int)0xFF000000);
        int sellEdge = sellArgb | unchecked((int)0xFF000000);
        double max = sides.Buy.MaxCount;
        for (int y = 0; y < height; y++)
        {
            double top = YToDisplay(y, offset, anchorUnix);
            double bottom = YToDisplay(y + 1, offset, anchorUnix);
            int pipLo = DensityProfile.PipLevel((int)Math.Round(bottom));
            int pipHi = DensityProfile.PipLevel((int)Math.Round(top));
            double buyCount = DensityProfile.MaxCountIn(sides.Buy, pipLo, pipHi);
            double sellCount = DensityProfile.MaxCountIn(sides.Sell, pipLo, pipHi);
            if (buyCount <= 0 && sellCount <= 0) continue;
            int buyWidth = buyCount > 0
                ? Math.Clamp((int)Math.Round(buyCount * DensityMaxWidthPx / max), 1, width)
                : 0;
            int sellWidth = sellCount > 0
                ? Math.Clamp((int)Math.Round(sellCount * DensityMaxWidthPx / max), 1, width)
                : 0;
            bool buySmaller = buyWidth <= sellWidth;
            int smallWidth = buySmaller ? buyWidth : sellWidth;
            int largeWidth = buySmaller ? sellWidth : buyWidth;
            int row = y * width;
            int smallStart = width - smallWidth;
            int largeStart = width - largeWidth;
            int largeFill = buySmaller ? sellFill : buyFill;
            int smallFill = buySmaller ? buyFill : sellFill;
            for (int x = largeStart; x < smallStart; x++)
                buffer[row + x] = BlendOverPremultiplied(buffer[row + x], largeFill);
            if (largeWidth > smallWidth)
                buffer[row + largeStart] = BlendOverPremultiplied(
                    buffer[row + largeStart], buySmaller ? sellEdge : buyEdge);
            for (int x = smallStart; x < width; x++)
                buffer[row + x] = BlendOverPremultiplied(buffer[row + x], smallFill);
            if (smallWidth > 0)
                buffer[row + smallStart] = BlendOverPremultiplied(
                    buffer[row + smallStart], buySmaller ? buyEdge : sellEdge);
        }
        if (priceDisplay != null)
            DrawBookPriceLine(buffer, width, height, priceDisplay.Value, offset, anchorUnix);
    }

    private void DrawBookPriceLine(int[] buffer, int width, int height, int priceDisplay,
        double offset, long anchorUnix)
    {
        for (int y = 0; y < height; y++)
        {
            double top = YToDisplay(y, offset, anchorUnix);
            double bottom = YToDisplay(y + 1, offset, anchorUnix);
            if (priceDisplay > top || priceDisplay <= bottom) continue;
            int row = y * width;
            for (int x = Math.Max(0, width - DensityMaxWidthPx); x < width; x++)
                buffer[row + x] = BlendOverPremultiplied(buffer[row + x], BookPriceLineArgb);
            return;
        }
    }

    private (long From, long To) VolumeCandleRange(SymbolSeries s, long bucketSec)
    {
        long groupSec = Math.Max(1, VolumeGroupOf(s)) * ChartColumns.MinuteSeconds;
        long columnStart = MinuteFloor(ToReal(_densityAnchorColumn * bucketSec));
        long columnEnd = MinuteFloor(ColumnEndUnix(_densityAnchorColumn));
        long groupStart = columnEnd - columnEnd % groupSec;
        return (Math.Min(groupStart, columnStart),
            Math.Max(groupStart + groupSec - ChartColumns.MinuteSeconds, columnEnd));
    }

    private long ColumnEndUnix(long column) =>
        ToRealEnd((column + 1) * _renderedColumnSeconds) - 1;

    private static long MinuteFloor(long unixSeconds) =>
        unixSeconds - ((unixSeconds % ChartColumns.MinuteSeconds) + ChartColumns.MinuteSeconds)
            % ChartColumns.MinuteSeconds;

    private double DensityScaleMax(SymbolSeries s, double windowMax)
    {
        int option = Math.Clamp(_densitySelected, 0, IndicatorSymbol.DensityAllOption);
        int percent = s.DensityScalePercents is { } percents && option < percents.Length
            ? Math.Max(1, percents[option])
            : 100;
        double perPixel = _densityUnits.TryGetValue(s.Symbol, out var live) && live > 0
            ? live
            : s.DensityScalePerPixel;
        if (perPixel > 0) return perPixel * DensityMaxWidthPx * 100.0 / percent;
        _densityAutoPerPixel[s.Symbol] = windowMax / DensityMaxWidthPx;
        return windowMax * 100.0 / percent;
    }

    private int DrawDensity(int[] buffer, int width, int height, DensityHistogram histogram,
        double offset, long anchorUnix, int colorArgb, double max, int alpha, int bidColorArgb = 0)
    {
        int widest = 0;
        int fill = PremultiplyArgb(colorArgb, alpha);
        int edge = alpha >= 255 ? fill : colorArgb | unchecked((int)0xFF000000);
        bool sides = histogram.HasSides && bidColorArgb != 0;
        int bidFill = sides ? PremultiplyArgb(bidColorArgb, alpha) : 0;
        int bidEdge = alpha >= 255 ? bidFill : bidColorArgb | unchecked((int)0xFF000000);
        for (int y = 0; y < height; y++)
        {
            double top = YToDisplay(y, offset, anchorUnix);
            double bottom = YToDisplay(y + 1, offset, anchorUnix);
            int pipLo = DensityProfile.PipLevel((int)Math.Round(bottom));
            int pipHi = DensityProfile.PipLevel((int)Math.Round(top));
            var value = sides
                ? DensityProfile.RowValueIn(histogram, pipLo, pipHi)
                : new DensityRowValue(DensityProfile.MaxCountIn(histogram, pipLo, pipHi), 0, 0);
            if (value.Total <= 0) continue;
            int barWidth = Math.Clamp(
                (int)Math.Round(value.Total * DensityMaxWidthPx / max), 1, width);
            if (barWidth > widest) widest = barWidth;
            int row = y * width;
            int x0 = width - barWidth;
            int bidWidth = value.Bid <= 0
                ? 0
                : Math.Min(barWidth, (int)Math.Round(value.Bid * barWidth / value.Total));
            buffer[row + x0] = BlendOverPremultiplied(buffer[row + x0], bidWidth > 0 ? bidEdge : edge);
            for (int x = x0 + 1; x < x0 + bidWidth; x++)
                buffer[row + x] = BlendOverPremultiplied(buffer[row + x], bidFill);
            for (int x = Math.Max(x0 + 1, x0 + bidWidth); x < width; x++)
                buffer[row + x] = BlendOverPremultiplied(buffer[row + x], fill);
        }
        return widest;
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

    private void UpdateDensityLabels(List<SymbolSeries> panels, long anchorUnix)
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
            if (s.OrderBookPanel)
            {
                long? bookUnix = s.DepthSnapshots is { Length: > 0 } depth
                    ? DepthProfile.At(depth, anchorUnix)?.MinuteUnix
                    : OrderBookProfile.At(s.OrderBookSnapshots!, anchorUnix)?.TimeUnix;
                label.Text = bookUnix == null
                    ? $"{s.Symbol}: no book here"
                    : $"{s.Symbol}: {DateTimeOffset.FromUnixTimeSeconds(bookUnix.Value).UtcDateTime:MM-dd HH:mm}";
            }
            else
            {
                int option = Math.Clamp(_densitySelected, 0, s.DensityWindows!.Length);
                label.Text = SelectedRange != null
                    ? $"{s.Symbol}: selection"
                    : option == s.DensityWindows.Length
                        ? $"{s.Symbol} 0: all"
                        : $"{s.Symbol} {option + 1}: {FormatDensityWindow(s.DensityWindows[option])}";
                if (s.VolumePanel)
                    label.Text += $" | {FormatDensityWindow(VolumeGroupOf(s))}"
                        + (s.VolumeGroupLocked ? "*" : "");
            }
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
        if (series == null || EditLocked || _renderedColumnSeconds <= 0 || _renderedPointsPerRow <= 0) return null;
        long bucketSec = _renderedColumnSeconds;
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
        using var _ = Perf.FrameStep("input.edithover");
        if (_series == null) return;
        if (_measuring)
        {
            var pm = e.GetPosition(this);
            var dpiM = VisualTreeHelper.GetDpi(this);
            MoveMeasureCursor(
                (int)Math.Floor(pm.X * dpiM.DpiScaleX),
                (int)Math.Floor(pm.Y * dpiM.DpiScaleY));
            return;
        }
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
            SetForecastHover(Array.Empty<ForecastMark>());
            return;
        }
        SetForecastHover(FindForecastMarkersAt(cx, cy).Select(c => c.Mark).ToArray());
        if (UpdateHover(cx, cy, dpi)) HideCalendarHover();
        else UpdateCalendarHover(cx, cy, dpi);
    }

    private void SetForecastHover(ForecastMark[] marks)
    {
        if (SameMarks(marks, _forecastHover)) return;
        _forecastHover = marks;
        Rebuild();
    }

    private static bool SameMarks(ForecastMark[] a, ForecastMark[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    private static bool HasMark(ForecastMark[] marks, ForecastMark mark)
    {
        foreach (var m in marks)
            if (ReferenceEquals(m, mark)) return true;
        return false;
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
        long bucketSec = _renderedColumnSeconds;
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
            || _renderedColumnSeconds <= 0 || !CalendarLinesDrawn(_renderedColumnSeconds))
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
        long bucketSec = _renderedColumnSeconds;
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
        if (_renderedColumnSeconds <= 0) return result;
        long bucketSec = _renderedColumnSeconds;
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
        CloseChartPopups();
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
        closeButton.Click += (_, _) => CloseChartPopups();
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
        long bucketSec = _renderedColumnSeconds;
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
        long bucketSec = _renderedColumnSeconds;
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
        if (_measuring)
        {
            e.Handled = true;
            FinishMeasure();
            return;
        }
        if (_hasMeasure) ClearMeasure();
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
                ShowDrawingVertexMenu(vhit.Value.Symbol, vhit.Value.Line, vhit.Value.Vertex, cx, cy);
                return;
            }
            var lhit = FindDrawingLineAt(cx, cy);
            if (lhit != null)
            {
                e.Handled = true;
                ShowDrawingLineMenu(lhit.Value.Symbol, lhit.Value.Line, cx, cy);
                return;
            }
            if (RangeContainsColumn(cx))
            {
                e.Handled = true;
                ShowRangeMenu(pos, cx, cy);
                return;
            }
            e.Handled = true;
            ShowChartMenu(cx, cy);
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
        AddChartMenuItems(menu, cx, cy);
        menu.IsOpen = true;
    }

    private void ShowChartMenu(int cx, int cy)
    {
        var menu = new ContextMenu { PlacementTarget = this };
        AddChartMenuItems(menu, cx, cy);
        menu.IsOpen = true;
    }

    private void AddChartMenuItems(ContextMenu menu, int cx, int cy)
    {
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        long t = ColumnTime(cx);
        string day = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime.ToString("yyyy-MM-dd");
        bool has = _forecastDays.Contains(day, StringComparer.Ordinal);
        menu.Items.Add(MeasureMenuItem(cx, cy));
        menu.Items.Add(new Separator());
        var series = _series;
        if (series != null)
        {
            int drawCount = 0;
            foreach (var s in series)
            {
                if (s.DrawingLines == null || s.Transform == null || IsHidden(s.Symbol)) continue;
                string symbol = s.Symbol;
                var draw = new MenuItem { Header = $"Draw line - {symbol}" };
                draw.Click += (_, _) => BeginDraw(symbol, false, (cx, cy));
                menu.Items.Add(draw);
                var level = new MenuItem { Header = $"Draw level - {symbol}" };
                level.Click += (_, _) => BeginDraw(symbol, true, (cx, cy));
                menu.Items.Add(level);
                drawCount++;
            }
            if (drawCount > 0) menu.Items.Add(new Separator());
        }
        var forecast = new MenuItem
        {
            Header = $"Forecast {day}",
            IsEnabled = has,
            IsChecked = has && day == _forecastDay && !_forecastHidden,
        };
        if (has) forecast.Click += (_, _) => SelectForecastDay(day);
        menu.Items.Add(forecast);
    }

    public void BeginDrawLine(string symbol) => BeginDraw(symbol, false, null);

    public void BeginDrawLevel(string symbol) => BeginDraw(symbol, true, null);

    private void BeginDraw(string symbol, bool level, (int X, int Y)? start)
    {
        var s = GetSeries(symbol);
        if (s?.Transform == null || s.DrawingLines == null) return;
        CancelPivotDrag();
        EndDrag();
        DeselectLine();
        HideHover();
        _drawSymbol = symbol;
        _drawLevel = level;
        _drawPoints.Clear();
        var brush = BrushFor(s.ColorArgb);
        _drawPreview.Stroke = brush;
        _drawCursor.Stroke = brush;
        _drawPreview.Points.Clear();
        _drawPreview.Visibility = Visibility.Collapsed;
        Focusable = true;
        Focus();
        if (start != null) AddDrawPoint(s, start.Value.X, start.Value.Y);
    }

    public void CancelDrawing()
    {
        _drawSymbol = null;
        _drawLevel = false;
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
        if (!AddDrawPoint(s, cx, cy)) return;
        bool more = !_drawLevel && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (!more && _drawPoints.Count >= 2) FinishDrawing();
    }

    private bool AddDrawPoint(SymbolSeries s, int cx, int cy)
    {
        if (s.Transform == null || _renderedColumnSeconds <= 0 || _renderedPointsPerRow <= 0)
            return false;
        long t = ToReal((_renderedStartBucket + cx) * _renderedColumnSeconds);
        t -= t % 60;
        int display = (int)Math.Round(YToDisplay(cy, SeriesOffset(s.Symbol), t));
        int raw = _drawLevel && _drawPoints.Count > 0
            ? _drawPoints[0].Value
            : s.Transform.ToRaw(display);
        _drawPoints.Add(new PivotPoint(t, raw, _drawLevel));
        UpdateDrawPreview(VisualTreeHelper.GetDpi(this));
        return true;
    }

    private Point ProjectDrawPoint(SeriesTransform transform, double offset, PivotPoint p, DpiScale dpi)
    {
        var (x, y) = ProjectDrawPointPx(transform, offset, p);
        return new Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY);
    }

    private void UpdateDrawPreview(DpiScale dpi)
    {
        var s = _drawSymbol == null ? null : GetSeries(_drawSymbol);
        if (s?.Transform == null || _renderedColumnSeconds <= 0 || _renderedPointsPerRow <= 0) return;
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
        if (s?.Transform == null || _renderedColumnSeconds <= 0 || _renderedPointsPerRow <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var from = ProjectDrawPoint(s.Transform, SeriesOffset(s.Symbol), _drawPoints[^1], dpi);
        var pos = e.GetPosition(this);
        _drawCursor.X1 = from.X;
        _drawCursor.Y1 = from.Y;
        _drawCursor.X2 = pos.X;
        _drawCursor.Y2 = _drawLevel ? from.Y : pos.Y;
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
        if (_renderedColumnSeconds <= 0) return null;
        int pw = (int)Math.Round(ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (pw < 1) return null;
        long bucketSec = _renderedColumnSeconds;
        return (ToReal(_renderedStartBucket * bucketSec),
            ToReal((_renderedStartBucket + pw) * bucketSec));
    }

    private (double X, double Y) ProjectDrawPointPx(SeriesTransform transform, double offset, PivotPoint p)
    {
        double bucketSec = _renderedColumnSeconds;
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
        if (series == null || _renderedColumnSeconds <= 0 || _renderedPointsPerRow <= 0) return null;
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
            || _renderedColumnSeconds <= 0 || _renderedPointsPerRow <= 0) return -1;
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
        if (series == null || _renderedColumnSeconds <= 0 || _renderedPointsPerRow <= 0) return null;
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

    private void ShowDrawingVertexMenu(string symbol, int line, int vertex, int cx, int cy)
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
        AddCloneMenuItem(menu, symbol, line);
        menu.Items.Add(new Separator());
        menu.Items.Add(FlattenMenuItem(symbol, line));
        AddChartMenuItems(menu, cx, cy);
        menu.IsOpen = true;
    }

    private void ShowDrawingLineMenu(string symbol, int line, int cx, int cy)
    {
        SelectLine(symbol, line);
        var menu = new ContextMenu { PlacementTarget = this };
        AddCloneMenuItem(menu, symbol, line);
        menu.Items.Add(FlattenMenuItem(symbol, line));
        AddChartMenuItems(menu, cx, cy);
        menu.IsOpen = true;
    }

    private void AddCloneMenuItem(ContextMenu menu, string symbol, int line)
    {
        if (IsLevelLine(symbol, line)) return;
        var clone = new MenuItem { Header = $"Clone {CloneOffsetPips} pips up" };
        clone.Click += (_, _) => CloneDrawingLine(symbol, line);
        menu.Items.Add(clone);
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

    private const int CloneOffsetPips = 20;

    private bool IsLevelLine(string symbol, int line)
    {
        var s = GetSeries(symbol);
        if (s?.DrawingLines == null || line < 0 || line >= s.DrawingLines.Length) return false;
        var poly = s.DrawingLines[line];
        return poly.Length > 0 && poly[0].Level;
    }

    private void CloneDrawingLine(string symbol, int line)
    {
        var s = GetSeries(symbol);
        if (s?.Transform == null || s.DrawingLines == null
            || line < 0 || line >= s.DrawingLines.Length) return;
        var poly = s.DrawingLines[line];
        if (poly.Length == 0 || poly[0].Level) return;
        int shift = CloneOffsetPips * PipPoints;
        var copy = new PivotPoint[poly.Length];
        for (int i = 0; i < poly.Length; i++)
            copy[i] = poly[i] with
            {
                Value = s.Transform.ToRaw(s.Transform.ToDisplay(poly[i].Value) + shift),
            };
        var lines = new PivotPoint[s.DrawingLines.Length + 1][];
        Array.Copy(s.DrawingLines, lines, s.DrawingLines.Length);
        lines[^1] = copy;
        DrawingLinesChanged?.Invoke(symbol, lines);
        var after = GetSeries(symbol);
        if (after?.DrawingLines != null && after.DrawingLines.Length == lines.Length)
            SelectLine(symbol, lines.Length - 1);
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
        return a with { UnixSeconds = t, Value = v };
    }

    private static PivotPoint ExtrapolateDraw(PivotPoint anchor, PivotPoint neighbor)
    {
        long t = 2 * anchor.UnixSeconds - neighbor.UnixSeconds;
        t -= ((t % 60) + 60) % 60;
        long v = 2L * anchor.Value - neighbor.Value;
        return anchor with { UnixSeconds = t, Value = (int)Math.Clamp(v, int.MinValue, int.MaxValue) };
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
        if (s?.Transform == null || _renderedColumnSeconds <= 0 || _renderedPointsPerRow <= 0)
        {
            CancelSelDrag();
            return;
        }
        if (!_selMoved && (cx != _selPressX || cy != _selPressY))
        {
            _selMoved = true;
            Rebuild();
        }
        long bucketSec = _renderedColumnSeconds;
        double offset = SeriesOffset(s.Symbol);
        var points = new PivotPoint[_selOrigPoints.Length];
        bool isLevel = _selOrigPoints.Length > 0 && _selOrigPoints[0].Level;
        if (_selDragVertex >= 0 && _selDragVertex < points.Length)
        {
            Array.Copy(_selOrigPoints, points, points.Length);
            long t = ToReal((_renderedStartBucket + cx) * bucketSec);
            t -= t % 60;
            int raw;
            if (isLevel)
            {
                raw = _selOrigPoints[_selDragVertex].Value;
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && points.Length > 1)
            {
                raw = SlopePreservedValue(_selOrigPoints, _selDragVertex, t);
            }
            else
            {
                int display = (int)Math.Round(YToDisplay(cy, offset, t));
                raw = s.Transform.ToRaw(display);
            }
            points[_selDragVertex] = _selOrigPoints[_selDragVertex] with
            {
                UnixSeconds = t,
                Value = raw,
            };
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
                points[i] = _selOrigPoints[i] with
                {
                    UnixSeconds = t,
                    Value = s.Transform.ToRaw(display),
                };
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
            && _renderedColumnSeconds > 0 && _renderedPointsPerRow > 0;
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
            if (_offsetLockedSymbols.Contains(symbol)) continue;
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
        var stamp = (_series as object, _renderedColumnSeconds, _renderedStartBucket, _renderedTopPrice,
            _renderedPointsPerRow, offsetsHash, _effectiveHidden.Count, pw, ph, dpi.DpiScaleX,
            _weekendsHidden, _flatten as object);
        if (stamp == _markerStamp) return;
        _markerStamp = stamp;
        _markerCanvas.Children.Clear();
        var series = _series;
        if (series == null || _renderedColumnSeconds <= 0 || _renderedPointsPerRow <= 0) return;
        long bucketSec = _renderedColumnSeconds;
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
