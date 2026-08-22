using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FXViewer.Chart;

public sealed class SymbolBarView : FrameworkElement
{
    private const double EntryFontSize = 12;
    private const double PadLeftDip = 8;
    private const double PadRightDip = 8;
    private const double PadTopDip = 4;
    private const double PadBottomDip = 6;
    private const double RowGapDip = 6;
    private const double ConnDotDip = 9;
    private const double ConnDotGapDip = 5;
    private const double AddInnerPadX = 6;
    private const double AddInnerPadY = 2;
    private const string AddLabel = "+ Add";
    private const string CalendarLabel = "Calendar";
    private const string ForecastLabel = "Forecast";
    private const string WeekendsLabel = "No weekends";
    private const string SessionsLabel = "Sessions";
    private const string UnflattenLabel = "Unflatten";
    private const double TiltedButtonSizeDip = 16;
    private const double TiltedButtonGapDip = 3;
    private const double TiltedButtonFontSize = 10;
    private const double GroupButtonSizeDip = 13;
    private const double GroupButtonGapDip = 8;
    private const double GroupButtonFontSize = 10;
    private const int MaxSourceDepth = 16;

    private static readonly Typeface EntryTypeface = new("Consolas");
    private static readonly Pen BorderPen = CreateFrozenPen(0xE0, 0xE0, 0xE0);
    private static readonly Brush DisabledBrush = CreateFrozenBrush(0xB4, 0xB4, 0xB4);
    private static readonly Brush ConnTextBrush = CreateFrozenBrush(0x30, 0x30, 0x30);
    private static readonly Brush AddFillBrush = CreateFrozenBrush(0xF5, 0xF5, 0xF5);
    private static readonly Brush AddTextBrush = CreateFrozenBrush(0x50, 0x50, 0x50);
    private static readonly Pen AddBorderPen = CreateFrozenPen(0xC8, 0xC8, 0xC8);
    private static readonly Brush TiltedOnFillBrush = CreateFrozenBrush(0x50, 0x50, 0x50);
    private static readonly Brush TiltedOnTextBrush = CreateFrozenBrush(0xFF, 0xFF, 0xFF);

    private readonly List<(string Symbol, int LastPricePoints, Brush Brush, string Format, int PriceMul)> _entries = new();
    private readonly HashSet<string> _disabledSymbols = new();
    private readonly HashSet<string> _indicatorSymbols = new();
    private readonly HashSet<string> _drawingSymbols = new();
    private readonly HashSet<string> _shiftSymbols = new();
    private readonly HashSet<string> _spreadSymbols = new();
    private readonly Dictionary<string, (int GroupMinutes, bool Locked)> _volumeGroups = new();
    private readonly HashSet<string> _densitySymbols = new();
    private readonly Dictionary<string, int> _averageWindows = new();
    private readonly HashSet<string> _sourcedSymbols = new();
    private readonly HashSet<string> _standaloneSymbols = new();
    private readonly Dictionary<string, List<string>> _alignableBySource = new();
    private readonly Dictionary<string, string> _sourceOf = new();
    private readonly HashSet<string> _groupSymbols = new();
    private readonly HashSet<string> _collapsedSources = new();
    private readonly List<int> _visibleRows = new();
    private double[]? _cursorPrices;
    private double[]? _cursorDeltas;
    private bool _calendarPresent;
    private bool _calendarEnabled;
    private bool _forecastPresent;
    private bool _forecastEnabled;
    private bool _weekendsHidden;
    private bool _sessionsVisible;
    private bool _flattenActive;
    private int _tiltedUpIndex;
    private int _tiltedDownIndex;
    private string? _connText;
    private Brush _connBrush = CreateFrozenBrush(0x80, 0x80, 0x80);

    public event Action<string, int>? PriceOffsetWheel;
    public event Action<string, int, bool>? TimeShiftWheel;
    public event Action<string, int>? VolumeScaleWheel;
    public event Action<string, int>? VolumeGroupWheel;
    public event Action<string, int>? DensityScaleWheel;
    public event Action<string, int, bool>? AveragePeriodWheel;
    public event Action<string>? SymbolClick;
    public event Action<string>? CollapseToggled;
    public event Action<string>? AlignToGrid;
    public event Action<string>? AutoAlign;
    public event Action<string>? AlignToSource;
    public event Action<IReadOnlyList<string>>? AlignIndicatorsToSource;
    public event Action<string?>? AddSymbolRequested;
    public event Action<string>? EditSymbolRequested;
    public event Action<string>? DeleteSymbolRequested;
    public event Action<string>? RefreshSymbolRequested;
    public event Action<string>? RebuildSymbolRequested;
    public event Action<string>? DrawRequested;
    public event Action<string>? DrawLevelRequested;
    public event Action<string>? FindRequested;
    public event Action<string>? ShowResultsRequested;
    public event Action? CalendarClick;
    public event Action? ForecastClick;
    public event Action? ForecastReloadRequested;
    public event Action? CalendarSettingsRequested;
    public event Action? CalendarFindRequested;
    public event Action? WeekendsClick;
    public event Action? SessionsClick;
    public event Action? UnflattenClick;
    public event Action<bool, int>? TiltedGridSelected;
    public event Action? TiltedGridSettingsRequested;
    public event Action? TiltedGridResetRequested;

    public Func<bool>? HasChartSelection { get; set; }
    public Func<string, bool>? HasFindResults { get; set; }
    public Func<string, bool>? CanRefresh { get; set; }
    public Func<string, bool>? CanRebuild { get; set; }
    public Func<string, bool>? IsAlignedToSource { get; set; }

    public SymbolBarView()
    {
        MouseWheel += (_, e) =>
        {
            var symbol = SymbolLabelAt(e.GetPosition(this));
            if (symbol == null) return;
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && _shiftSymbols.Contains(symbol))
            {
                TimeShiftWheel?.Invoke(
                    symbol, e.Delta, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                return;
            }
            if (_averageWindows.ContainsKey(symbol))
            {
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
                    AveragePeriodWheel?.Invoke(
                        symbol, e.Delta, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0
                && (Keyboard.Modifiers & ModifierKeys.Alt) == 0
                && _densitySymbols.Contains(symbol))
            {
                DensityScaleWheel?.Invoke(symbol, e.Delta);
                return;
            }
            if (_volumeGroups.ContainsKey(symbol))
            {
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
                    VolumeGroupWheel?.Invoke(symbol, e.Delta);
                else
                    VolumeScaleWheel?.Invoke(symbol, e.Delta);
                return;
            }
            PriceOffsetWheel?.Invoke(symbol, e.Delta);
        };
        MouseLeftButtonDown += (_, e) =>
        {
            var pos = e.GetPosition(this);
            if (AddButtonBounds().Contains(pos))
            {
                AddSymbolRequested?.Invoke(null);
                return;
            }
            if (_calendarPresent && RowHit(pos.Y, _visibleRows.Count))
            {
                CalendarClick?.Invoke();
                return;
            }
            if (_forecastPresent && RowHit(pos.Y, ForecastRowIndex))
            {
                ForecastClick?.Invoke();
                return;
            }
            if (RowHit(pos.Y, WeekendsRowIndex))
            {
                WeekendsClick?.Invoke();
                return;
            }
            if (RowHit(pos.Y, SessionsRowIndex))
            {
                SessionsClick?.Invoke();
                return;
            }
            if (RowHit(pos.Y, TiltedUpRowIndex) || RowHit(pos.Y, TiltedDownRowIndex))
            {
                int button = TiltedButtonAt(pos.X);
                if (button >= 0)
                    TiltedGridSelected?.Invoke(RowHit(pos.Y, TiltedUpRowIndex), button + 1);
                return;
            }
            if (_flattenActive && RowHit(pos.Y, UnflattenRowIndex))
            {
                UnflattenClick?.Invoke();
                return;
            }
            int row = RowIndexAt(pos.Y);
            if (row < 0) return;
            var symbol = _entries[_visibleRows[row]].Symbol;
            if (_groupSymbols.Contains(symbol) && GroupButtonBounds(row).Contains(pos))
            {
                CollapseToggled?.Invoke(symbol);
                return;
            }
            SymbolClick?.Invoke(symbol);
        };
        MouseRightButtonUp += (_, e) =>
        {
            if (OwnsRightClick(e.GetPosition(this))) e.Handled = true;
        };
        MouseRightButtonDown += (_, e) =>
        {
            var pos = e.GetPosition(this);
            if (_calendarPresent && RowHit(pos.Y, _visibleRows.Count))
            {
                e.Handled = true;
                var calMenu = new ContextMenu { PlacementTarget = this };
                var calSettings = new MenuItem { Header = "Settings..." };
                calSettings.Click += (_, _) => CalendarSettingsRequested?.Invoke();
                calMenu.Items.Add(calSettings);
                var calFind = new MenuItem { Header = "Find..." };
                calFind.Click += (_, _) => CalendarFindRequested?.Invoke();
                calMenu.Items.Add(calFind);
                calMenu.IsOpen = true;
                return;
            }
            if (_forecastPresent && RowHit(pos.Y, ForecastRowIndex))
            {
                e.Handled = true;
                var fcMenu = new ContextMenu { PlacementTarget = this };
                var fcReload = new MenuItem { Header = "Reload forecasts" };
                fcReload.Click += (_, _) => ForecastReloadRequested?.Invoke();
                fcMenu.Items.Add(fcReload);
                fcMenu.IsOpen = true;
                return;
            }
            if (TiltedRowHit(pos.Y))
            {
                e.Handled = true;
                var gridMenu = new ContextMenu { PlacementTarget = this };
                var settings = new MenuItem { Header = "Settings" };
                settings.Click += (_, _) => TiltedGridSettingsRequested?.Invoke();
                gridMenu.Items.Add(settings);
                var reset = new MenuItem { Header = "Reset placement" };
                reset.Click += (_, _) => TiltedGridResetRequested?.Invoke();
                gridMenu.Items.Add(reset);
                gridMenu.IsOpen = true;
                return;
            }
            var symbol = SymbolAt(pos.Y);
            if (symbol == null) return;
            e.Handled = true;
            var menu = new ContextMenu { PlacementTarget = this };
            if (_drawingSymbols.Contains(symbol))
            {
                var draw = new MenuItem { Header = "Draw line" };
                draw.Click += (_, _) => DrawRequested?.Invoke(symbol);
                menu.Items.Add(draw);
                var level = new MenuItem { Header = "Draw level" };
                level.Click += (_, _) => DrawLevelRequested?.Invoke(symbol);
                menu.Items.Add(level);
                menu.Items.Add(new Separator());
            }
            if (_shiftSymbols.Contains(symbol))
            {
                var find = new MenuItem { Header = "Find", IsEnabled = HasChartSelection?.Invoke() == true };
                find.Click += (_, _) => FindRequested?.Invoke(symbol);
                menu.Items.Add(find);
                var show = new MenuItem
                {
                    Header = "Show results",
                    IsEnabled = HasFindResults?.Invoke(symbol) == true,
                };
                show.Click += (_, _) => ShowResultsRequested?.Invoke(symbol);
                menu.Items.Add(show);
                menu.Items.Add(new Separator());
            }
            if (!_averageWindows.ContainsKey(symbol))
            {
                var align = new MenuItem { Header = "Align to grid" };
                align.Click += (_, _) => AlignToGrid?.Invoke(symbol);
                menu.Items.Add(align);
                var autoAlign = new MenuItem { Header = "Auto align" };
                autoAlign.Click += (_, _) => AutoAlign?.Invoke(symbol);
                menu.Items.Add(autoAlign);
                if (_sourcedSymbols.Contains(symbol))
                {
                    var alignSource = new MenuItem { Header = "Align to source" };
                    alignSource.Click += (_, _) => AlignToSource?.Invoke(symbol);
                    menu.Items.Add(alignSource);
                }
                if (_alignableBySource.TryGetValue(symbol, out var targets) && targets.Count > 0)
                {
                    var alignAll = new MenuItem { Header = "Align indicators" };
                    alignAll.Click += (_, _) => AlignIndicatorsToSource?.Invoke(targets);
                    menu.Items.Add(alignAll);
                }
            }
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            bool isIndicator = _indicatorSymbols.Contains(symbol);
            if (!isIndicator || _standaloneSymbols.Contains(symbol))
            {
                var add = new MenuItem { Header = "Add" };
                add.Click += (_, _) => AddSymbolRequested?.Invoke(symbol);
                menu.Items.Add(add);
            }
            if (isIndicator)
            {
                var edit = new MenuItem { Header = "Edit" };
                edit.Click += (_, _) => EditSymbolRequested?.Invoke(symbol);
                menu.Items.Add(edit);
                if (CanRefresh?.Invoke(symbol) == true)
                {
                    var refresh = new MenuItem { Header = "Refresh" };
                    refresh.Click += (_, _) => RefreshSymbolRequested?.Invoke(symbol);
                    menu.Items.Add(refresh);
                }
                if (CanRebuild?.Invoke(symbol) == true)
                {
                    var rebuild = new MenuItem { Header = "Rebuild" };
                    rebuild.Click += (_, _) => RebuildSymbolRequested?.Invoke(symbol);
                    menu.Items.Add(rebuild);
                }
                var delete = new MenuItem { Header = "Delete" };
                delete.Click += (_, _) => DeleteSymbolRequested?.Invoke(symbol);
                menu.Items.Add(delete);
            }
            menu.IsOpen = true;
        };
    }

    private bool OwnsRightClick(Point pos) =>
        (_calendarPresent && RowHit(pos.Y, _visibleRows.Count))
        || (_forecastPresent && RowHit(pos.Y, ForecastRowIndex))
        || TiltedRowHit(pos.Y)
        || SymbolAt(pos.Y) != null;

    private bool TiltedRowHit(double y) =>
        RowHit(y, TiltedUpRowIndex) || RowHit(y, TiltedDownRowIndex);

    public void SetSymbolEnabled(string symbol, bool enabled)
    {
        if (enabled) _disabledSymbols.Remove(symbol);
        else _disabledSymbols.Add(symbol);
        InvalidateVisual();
    }

    public void SetCalendarRow(bool present, bool enabled)
    {
        bool changed = _calendarPresent != present;
        _calendarPresent = present;
        _calendarEnabled = enabled;
        if (changed) InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetForecastRow(bool present, bool enabled)
    {
        bool changed = _forecastPresent != present;
        _forecastPresent = present;
        _forecastEnabled = enabled;
        if (changed) InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetWeekendsRow(bool hidden)
    {
        _weekendsHidden = hidden;
        InvalidateVisual();
    }

    public void SetSessionsRow(bool visible)
    {
        _sessionsVisible = visible;
        InvalidateVisual();
    }

    public void SetFlattenRow(bool active)
    {
        if (_flattenActive == active) return;
        _flattenActive = active;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetTiltedGridRow(int upIndex, int downIndex)
    {
        _tiltedUpIndex = upIndex;
        _tiltedDownIndex = downIndex;
        InvalidateVisual();
    }

    private static int TiltedButtonCount => ChartViewState.TiltedGridCount;

    private static double TiltedButtonsWidth =>
        TiltedButtonCount * (TiltedButtonSizeDip + TiltedButtonGapDip) - TiltedButtonGapDip;

    private static int TiltedButtonAt(double x)
    {
        double offset = x - PadLeftDip;
        if (offset < 0) return -1;
        int button = (int)Math.Floor(offset / (TiltedButtonSizeDip + TiltedButtonGapDip));
        if (button < 0 || button >= TiltedButtonCount) return -1;
        return offset - button * (TiltedButtonSizeDip + TiltedButtonGapDip) <= TiltedButtonSizeDip
            ? button
            : -1;
    }

    private void DrawTiltedButtons(DrawingContext dc, double top, double lineHeight, int selectedIndex)
    {
        double y = top + Math.Max(0, (lineHeight - TiltedButtonSizeDip) / 2);
        for (int button = 0; button < TiltedButtonCount; button++)
        {
            bool selected = selectedIndex == button + 1;
            var rect = new Rect(
                PadLeftDip + button * (TiltedButtonSizeDip + TiltedButtonGapDip), y,
                TiltedButtonSizeDip, TiltedButtonSizeDip);
            dc.DrawRoundedRectangle(
                selected ? TiltedOnFillBrush : AddFillBrush, AddBorderPen, rect, 2, 2);
            var ft = Format((button + 1).ToString(CultureInfo.InvariantCulture),
                selected ? TiltedOnTextBrush : AddTextBrush, TiltedButtonFontSize);
            dc.DrawText(ft, new Point(
                rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
        }
    }

    private int ForecastRowIndex => _visibleRows.Count + (_calendarPresent ? 1 : 0);
    private int WeekendsRowIndex => ForecastRowIndex + (_forecastPresent ? 1 : 0);
    private int SessionsRowIndex => WeekendsRowIndex + 1;
    private int TiltedUpRowIndex => SessionsRowIndex + 1;
    private int TiltedDownRowIndex => TiltedUpRowIndex + 1;
    private int UnflattenRowIndex => TiltedDownRowIndex + 1;

    public void SetConnStatus(string text, int colorArgb)
    {
        bool widthMayChange = _connText == null || _connText.Length != text.Length;
        _connText = text;
        _connBrush = CreateFrozenBrush((byte)(colorArgb >> 16), (byte)(colorArgb >> 8), (byte)colorArgb);
        if (widthMayChange) InvalidateMeasure();
        InvalidateVisual();
    }

    private int RowIndexAt(double y)
    {
        if (_visibleRows.Count == 0) return -1;
        double rowHeight = Format("X", Brushes.Black).Height + RowGapDip;
        int index = (int)Math.Floor((y - PadTopDip) / rowHeight);
        return index >= 0 && index < _visibleRows.Count ? index : -1;
    }

    private string? SymbolAt(double y)
    {
        int row = RowIndexAt(y);
        return row < 0 ? null : _entries[_visibleRows[row]].Symbol;
    }

    private string? SymbolLabelAt(Point p)
    {
        if (p.X < PadLeftDip) return null;
        double y = PadTopDip;
        int row = 0;
        foreach (var line in Rows())
        {
            var ft = Format(line.Text, line.Brush);
            if (p.Y >= y && p.Y < y + ft.Height && p.X <= PadLeftDip + ft.Width)
                return _entries[_visibleRows[row]].Symbol;
            y += ft.Height + RowGapDip;
            row++;
        }
        return null;
    }

    private Rect GroupButtonBounds(int row)
    {
        double rowHeight = Format("X", Brushes.Black).Height + RowGapDip;
        double top = PadTopDip + row * rowHeight;
        double lineHeight = rowHeight - RowGapDip;
        return new Rect(
            Math.Max(PadLeftDip, ActualWidth - PadRightDip - GroupButtonSizeDip),
            top + Math.Max(0, (lineHeight - GroupButtonSizeDip) / 2),
            GroupButtonSizeDip, GroupButtonSizeDip);
    }

    private void DrawGroupButton(DrawingContext dc, int row, bool collapsed)
    {
        var rect = GroupButtonBounds(row);
        dc.DrawRoundedRectangle(AddFillBrush, AddBorderPen, rect, 2, 2);
        var ft = Format(collapsed ? "+" : "-", AddTextBrush, GroupButtonFontSize);
        dc.DrawText(ft, new Point(
            rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
    }

    private Rect AddButtonBounds()
    {
        double rowHeight = Format("X", Brushes.Black).Height + RowGapDip;
        int rows = UnflattenRowIndex + (_flattenActive ? 1 : 0);
        double top = PadTopDip + rows * rowHeight;
        var ft = Format(AddLabel, AddTextBrush);
        return new Rect(PadLeftDip, top, ft.Width + 2 * AddInnerPadX, ft.Height + 2 * AddInnerPadY);
    }

    private bool RowHit(double y, int rowIndex)
    {
        double rowHeight = Format("X", Brushes.Black).Height + RowGapDip;
        double top = PadTopDip + rowIndex * rowHeight;
        return y >= top && y < top + rowHeight;
    }

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(byte r, byte g, byte b)
    {
        var pen = new Pen(CreateFrozenBrush(r, g, b), 1);
        pen.Freeze();
        return pen;
    }

    public void SetSymbols(IReadOnlyList<(string Symbol, int LastPricePoints, int ColorArgb, int Digits, int PriceMul, bool IsIndicator, bool IsDrawing, bool IsShift, string? Source, bool IsStandalone)> entries)
    {
        _entries.Clear();
        _indicatorSymbols.Clear();
        _drawingSymbols.Clear();
        _shiftSymbols.Clear();
        _sourcedSymbols.Clear();
        _standaloneSymbols.Clear();
        _alignableBySource.Clear();
        _sourceOf.Clear();
        _groupSymbols.Clear();
        foreach (var e in entries)
        {
            _entries.Add((e.Symbol, e.LastPricePoints, CreateFrozenBrush(
                (byte)(e.ColorArgb >> 16), (byte)(e.ColorArgb >> 8), (byte)e.ColorArgb),
                e.Digits > 0 ? "0." + new string('0', e.Digits) : "0", e.PriceMul));
            if (e.IsIndicator) _indicatorSymbols.Add(e.Symbol);
            if (e.IsDrawing) _drawingSymbols.Add(e.Symbol);
            if (e.IsShift) _shiftSymbols.Add(e.Symbol);
            if (e.IsIndicator && e.Source != null) _sourcedSymbols.Add(e.Symbol);
            if (e.IsStandalone) _standaloneSymbols.Add(e.Symbol);
            if (e.Source != null)
            {
                _sourceOf[e.Symbol] = e.Source;
                _groupSymbols.Add(e.Source);
            }
            if (e.IsIndicator && !e.IsShift && e.Source != null)
            {
                if (!_alignableBySource.TryGetValue(e.Source, out var list))
                    _alignableBySource[e.Source] = list = new List<string>();
                list.Add(e.Symbol);
            }
        }
        RebuildVisibleRows();
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void RefreshAlignment()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetLastPrice(string symbol, int lastPricePoints)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.Symbol != symbol) continue;
            if (e.LastPricePoints == lastPricePoints) return;
            _entries[i] = (e.Symbol, lastPricePoints, e.Brush, e.Format, e.PriceMul);
            if (_cursorPrices != null) return;
            InvalidateVisual();
            return;
        }
    }

    public void SetCursorPrices(double[]? prices, double[]? deltas = null)
    {
        _cursorPrices = prices;
        _cursorDeltas = deltas;
        InvalidateVisual();
    }

    public void SetSpreadSymbols(IEnumerable<string> symbols)
    {
        _spreadSymbols.Clear();
        foreach (var s in symbols) _spreadSymbols.Add(s);
        InvalidateVisual();
    }

    public void SetDensitySymbols(IEnumerable<string> symbols)
    {
        _densitySymbols.Clear();
        foreach (var s in symbols) _densitySymbols.Add(s);
    }

    public void SetAverageSymbols(IEnumerable<(string Symbol, int WindowMinutes)> symbols)
    {
        _averageWindows.Clear();
        foreach (var (symbol, window) in symbols) _averageWindows[symbol] = Math.Max(1, window);
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetAverageWindow(string symbol, int windowMinutes)
    {
        if (!_averageWindows.ContainsKey(symbol)) return;
        _averageWindows[symbol] = Math.Max(1, windowMinutes);
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetVolumeSymbols(IEnumerable<(string Symbol, int GroupMinutes, bool Locked)> symbols)
    {
        _volumeGroups.Clear();
        foreach (var (symbol, group, locked) in symbols)
            _volumeGroups[symbol] = (Math.Max(1, group), locked);
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetVolumeGroup(string symbol, int groupMinutes)
    {
        if (!_volumeGroups.TryGetValue(symbol, out var current)) return;
        _volumeGroups[symbol] = (Math.Max(1, groupMinutes), current.Locked);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxWidth = 0;
        foreach (var line in Rows())
        {
            var ft = Format(line.Widest, line.Brush);
            double w = ft.Width + (line.Group ? GroupButtonGapDip + GroupButtonSizeDip : 0);
            if (w > maxWidth) maxWidth = w;
        }
        if (_connText != null)
        {
            double w = ConnDotDip + ConnDotGapDip + Format(_connText, ConnTextBrush).Width;
            if (w > maxWidth) maxWidth = w;
        }
        double addW = Format(AddLabel, AddTextBrush).Width + 2 * AddInnerPadX;
        if (addW > maxWidth) maxWidth = addW;
        if (_calendarPresent)
        {
            double calW = Format(CalendarLabel, ConnTextBrush).Width;
            if (calW > maxWidth) maxWidth = calW;
        }
        if (_forecastPresent)
        {
            double fcW = Format(ForecastLabel, ConnTextBrush).Width;
            if (fcW > maxWidth) maxWidth = fcW;
        }
        double weekW = Format(WeekendsLabel, ConnTextBrush).Width;
        if (weekW > maxWidth) maxWidth = weekW;
        double sessionW = Format(SessionsLabel, ConnTextBrush).Width;
        if (sessionW > maxWidth) maxWidth = sessionW;
        if (TiltedButtonsWidth > maxWidth) maxWidth = TiltedButtonsWidth;
        if (_flattenActive)
        {
            double flatW = Format(UnflattenLabel, ConnTextBrush).Width;
            if (flatW > maxWidth) maxWidth = flatW;
        }
        if (maxWidth <= 0) return new Size(0, 0);
        return new Size(PadLeftDip + maxWidth + PadRightDip, ContentHeight());
    }

    private double ContentHeight()
    {
        double height = AddButtonBounds().Bottom + PadBottomDip;
        if (_connText != null)
            height += RowGapDip + Format(_connText, ConnTextBrush).Height;
        return height;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.DrawLine(BorderPen, new Point(0.5, 0), new Point(0.5, ActualHeight));
        double y = PadTopDip;
        int row = 0;
        foreach (var line in Rows())
        {
            var ft = Format(line.Text, line.Brush);
            dc.DrawText(ft, new Point(PadLeftDip, y));
            if (line.Group) DrawGroupButton(dc, row, line.Collapsed);
            y += ft.Height + RowGapDip;
            row++;
        }
        if (_calendarPresent)
        {
            var brush = _calendarEnabled ? ConnTextBrush : DisabledBrush;
            var ft = Format(CalendarLabel, brush);
            dc.DrawText(ft, new Point(PadLeftDip, y));
            y += ft.Height + RowGapDip;
        }
        if (_forecastPresent)
        {
            var brush = _forecastEnabled ? ConnTextBrush : DisabledBrush;
            var ft = Format(ForecastLabel, brush);
            dc.DrawText(ft, new Point(PadLeftDip, y));
            y += ft.Height + RowGapDip;
        }
        var weekFt = Format(WeekendsLabel, _weekendsHidden ? ConnTextBrush : DisabledBrush);
        dc.DrawText(weekFt, new Point(PadLeftDip, y));
        y += weekFt.Height + RowGapDip;
        var sessionFt = Format(SessionsLabel, _sessionsVisible ? ConnTextBrush : DisabledBrush);
        dc.DrawText(sessionFt, new Point(PadLeftDip, y));
        y += sessionFt.Height + RowGapDip;
        double tiltedLineHeight = Format("X", Brushes.Black).Height;
        DrawTiltedButtons(dc, y, tiltedLineHeight, _tiltedUpIndex);
        y += tiltedLineHeight + RowGapDip;
        DrawTiltedButtons(dc, y, tiltedLineHeight, _tiltedDownIndex);
        y += tiltedLineHeight + RowGapDip;
        if (_flattenActive)
        {
            var flatFt = Format(UnflattenLabel, ConnTextBrush);
            dc.DrawText(flatFt, new Point(PadLeftDip, y));
            y += flatFt.Height + RowGapDip;
        }
        var addRect = AddButtonBounds();
        dc.DrawRoundedRectangle(AddFillBrush, AddBorderPen, addRect, 3, 3);
        var addFt = Format(AddLabel, AddTextBrush);
        dc.DrawText(addFt, new Point(addRect.X + AddInnerPadX, addRect.Y + AddInnerPadY));
        if (_connText != null)
        {
            var ft = Format(_connText, ConnTextBrush);
            double ty = ActualHeight - ft.Height - PadBottomDip;
            double r = ConnDotDip / 2;
            dc.DrawEllipse(_connBrush, null, new Point(PadLeftDip + r, ty + ft.Height / 2), r, r);
            dc.DrawText(ft, new Point(PadLeftDip + ConnDotDip + ConnDotGapDip, ty));
        }
    }

    private static string FormatVolumeGroup(int minutes) =>
        minutes % 1440 == 0 ? minutes / 1440 + "d"
        : minutes % 60 == 0 ? minutes / 60 + "h"
        : minutes + "m";

    private const string SpreadWidest = "00.0";
    private const string VolumeWidest = "0,000,000  +0,000,000";

    private static string PriceWidest(string format) => new('0', format.Length + 3);

    private IEnumerable<(string Text, string Widest, Brush Brush, bool Group, bool Collapsed)> Rows()
    {
        foreach (int i in _visibleRows)
        {
            var e = _entries[i];
            var brush = _disabledSymbols.Contains(e.Symbol) ? DisabledBrush : e.Brush;
            string prefix = Unaligned(e.Symbol) ? "*" : "";
            if (_spreadSymbols.Contains(e.Symbol))
            {
                double pips = _cursorPrices != null && i < _cursorPrices.Length
                    ? _cursorPrices[i]
                    : double.NaN;
                string spreadText = double.IsNaN(pips)
                    ? "-"
                    : pips.ToString("0.0", CultureInfo.InvariantCulture);
                yield return (prefix + e.Symbol + ": " + spreadText,
                    prefix + e.Symbol + ": " + SpreadWidest, brush,
                    _groupSymbols.Contains(e.Symbol), _collapsedSources.Contains(e.Symbol));
                continue;
            }
            if (_volumeGroups.TryGetValue(e.Symbol, out var volumeGroup))
            {
                double volume = _cursorPrices != null && i < _cursorPrices.Length
                    ? _cursorPrices[i]
                    : double.NaN;
                string volumeText = double.IsNaN(volume)
                    ? "-"
                    : ((long)Math.Round(volume)).ToString("N0", CultureInfo.InvariantCulture);
                double delta = _cursorDeltas != null && i < _cursorDeltas.Length
                    ? _cursorDeltas[i]
                    : double.NaN;
                if (!double.IsNaN(delta))
                {
                    long d = (long)Math.Round(delta);
                    volumeText += "  " + (d >= 0 ? "+" : "-")
                        + Math.Abs(d).ToString("N0", CultureInfo.InvariantCulture);
                }
                string groupText = FormatVolumeGroup(volumeGroup.GroupMinutes)
                    + (volumeGroup.Locked ? "*" : "");
                yield return (prefix + e.Symbol + " " + groupText + ": " + volumeText,
                    prefix + e.Symbol + " " + groupText + ": " + VolumeWidest, brush,
                    _groupSymbols.Contains(e.Symbol), _collapsedSources.Contains(e.Symbol));
                continue;
            }
            double points = _cursorPrices != null && i < _cursorPrices.Length
                ? _cursorPrices[i]
                : e.LastPricePoints;
            if (_averageWindows.TryGetValue(e.Symbol, out var window))
            {
                string windowText = FormatAverageWindow(window);
                yield return (prefix + e.Symbol + " " + windowText + ": " + Price(points, e.Format, e.PriceMul),
                    prefix + e.Symbol + " " + windowText + ": " + PriceWidest(e.Format), brush,
                    _groupSymbols.Contains(e.Symbol), _collapsedSources.Contains(e.Symbol));
                continue;
            }
            yield return (prefix + e.Symbol + ": " + Price(points, e.Format, e.PriceMul),
                prefix + e.Symbol + ": " + PriceWidest(e.Format), brush,
                _groupSymbols.Contains(e.Symbol), _collapsedSources.Contains(e.Symbol));
        }
    }

    private static string FormatAverageWindow(int minutes)
    {
        int days = minutes / 1440;
        int hours = minutes % 1440 / 60;
        int mins = minutes % 60;
        return days > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{days}:{hours:00}:{mins:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{hours}:{mins:00}");
    }

    private void RebuildVisibleRows()
    {
        _visibleRows.Clear();
        for (int i = 0; i < _entries.Count; i++)
            if (!CollapsedAway(_entries[i].Symbol)) _visibleRows.Add(i);
    }

    private bool CollapsedAway(string symbol)
    {
        for (int depth = 0; depth < MaxSourceDepth; depth++)
        {
            if (!_sourceOf.TryGetValue(symbol, out var source)) return false;
            if (_collapsedSources.Contains(source)) return true;
            symbol = source;
        }
        return false;
    }

    public void SetCollapsedSources(IEnumerable<string> symbols)
    {
        _collapsedSources.Clear();
        foreach (var symbol in symbols) _collapsedSources.Add(symbol);
        RebuildVisibleRows();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private bool Unaligned(string symbol) =>
        _sourcedSymbols.Contains(symbol)
        && !_shiftSymbols.Contains(symbol)
        && IsAlignedToSource?.Invoke(symbol) == false;

    private static string Price(double points, string format, int priceMul) =>
        (points * priceMul / 100000.0).ToString(format, CultureInfo.InvariantCulture);

    private FormattedText Format(string text, Brush brush) => Format(text, brush, EntryFontSize);

    private FormattedText Format(string text, Brush brush, double fontSize) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            EntryTypeface, fontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
