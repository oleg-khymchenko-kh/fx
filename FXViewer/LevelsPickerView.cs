using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FXViewer.Chart;
using FXViewer.Compute;
using FXViewer.Storage;

namespace FXViewer;

public sealed class LevelsPickerView : FrameworkElement
{
    private const double DotRadius = 6;
    private const double Pad = 10;

    private static readonly Brush BackBrush = Brushes.White;
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)), 1);
    private static readonly Pen BarPen = new(new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0xDD)), 1);
    private static readonly Brush PlainDot = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush RequiredDot = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly Brush PreferredDot = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));
    private static readonly Pen PlainDotPen = new(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)), 1);
    private static readonly Pen RequiredDotPen = new(new SolidColorBrush(Color.FromRgb(0xB7, 0x1C, 0x1C)), 1);
    private static readonly Pen PreferredDotPen = new(new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2)), 1);
    private const double VisibleArmPixels = 4;
    private const double AddRadiusPixels = 40;

    private readonly record struct Dot(Level2 Level, Point Center);

    private IReadOnlyList<Candle> _minutes = Array.Empty<Candle>();
    private IReadOnlyList<Level2> _levels = Array.Empty<Level2>();
    private IReadOnlyList<Level2> _extremes = Array.Empty<Level2>();
    private Func<Level2, LevelUse> _use = _ => LevelUse.Off;
    private Func<Level2, string> _label = _ => "";
    private Func<Level2, string> _badge = _ => "";
    private static readonly Typeface BadgeFace = new("Segoe UI");
    private static readonly Brush BadgeBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x21, 0x21));
    private long _vStart;
    private long _vEnd;
    private int _first;
    private int _last;
    private readonly List<Dot> _dots = new();
    private double _plotHi;
    private double _xScale;
    private double _yScale;
    private Point _menuPoint;

    static LevelsPickerView()
    {
        BorderPen.Freeze();
        BarPen.Freeze();
        BadgeBrush.Freeze();
        PlainDot.Freeze();
        RequiredDot.Freeze();
        PreferredDot.Freeze();
        PlainDotPen.Freeze();
        RequiredDotPen.Freeze();
        PreferredDotPen.Freeze();
    }

    public LevelsPickerView()
    {
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetShowDuration(this, 10000);
        var add = new MenuItem { Header = "Add level" };
        add.Click += (_, _) => AddNearest(_menuPoint);
        ContextMenu = new ContextMenu();
        ContextMenu.Items.Add(add);
    }

    public event Action<Level2>? LevelClicked;

    public event Action<Level2>? LevelAdded;

    public void Show(IReadOnlyList<Candle> minutes, IReadOnlyList<Level2> levels, IReadOnlyList<Level2> extremes,
        long fragStartUnix, long fragEndUnix, Func<Level2, LevelUse> use, Func<Level2, string> label,
        Func<Level2, string> badge)
    {
        _badge = badge;
        _minutes = minutes;
        _levels = levels;
        _extremes = extremes;
        _use = use;
        _label = label;
        var w = WeekendCompressor.Instance;
        _vStart = SimilarSearch.FloorHour(w.ToVirtual(fragStartUnix));
        _vEnd = SimilarSearch.CeilHour(w.ToVirtual(fragEndUnix));
        _first = IndexAfter(w.ToReal(_vStart) - 1);
        _last = IndexAfter(w.ToReal(_vEnd) - 1) - 1;
        InvalidateVisual();
    }

    public void Clear() =>
        Show(Array.Empty<Candle>(), Array.Empty<Level2>(), Array.Empty<Level2>(), 0, 60, _ => LevelUse.Off, _ => "", _ => "");

    public void Refresh() => InvalidateVisual();

    private int IndexAfter(long unixSeconds)
    {
        int lo = 0;
        int hi = _minutes.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_minutes[mid].MinuteUnixSeconds <= unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? Height : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        dc.DrawRectangle(BackBrush, BorderPen, new Rect(0.5, 0.5, width - 1, height - 1));
        _dots.Clear();
        if (width < 2 * Pad + 10 || height < 2 * Pad + 10 || _last < _first || _vEnd <= _vStart) return;
        int lo = int.MaxValue;
        int hi = int.MinValue;
        for (int i = _first; i <= _last; i++)
        {
            lo = Math.Min(lo, _minutes[i].Min);
            hi = Math.Max(hi, _minutes[i].Max);
        }
        foreach (var level in _levels)
        {
            lo = Math.Min(lo, level.Value);
            hi = Math.Max(hi, level.Value);
        }
        if (hi <= lo) hi = lo + 1;
        double plotW = width - 2 * Pad;
        double plotH = height - 2 * Pad;
        double xScale = plotW / (_vEnd - _vStart);
        double yScale = plotH / (hi - lo);
        _plotHi = hi;
        _xScale = xScale;
        _yScale = yScale;
        double X(long unix) => Pad + (WeekendCompressor.Instance.ToVirtual(unix) - _vStart) * xScale;
        double Y(double value) => Pad + (hi - value) * yScale;

        int columns = (int)Math.Ceiling(plotW);
        var colLo = new int[columns];
        var colHi = new int[columns];
        var colCount = new int[columns];
        Array.Fill(colLo, int.MaxValue);
        Array.Fill(colHi, int.MinValue);
        int cursor = 0;
        var w = WeekendCompressor.Instance;
        for (int i = _first; i <= _last; i++)
        {
            var c = _minutes[i];
            long v = w.ToVirtual(c.MinuteUnixSeconds, ref cursor);
            int col = (int)((v - _vStart) * xScale);
            if (col < 0 || col >= columns) continue;
            colLo[col] = Math.Min(colLo[col], c.Min);
            colHi[col] = Math.Max(colHi[col], c.Max);
            colCount[col]++;
        }

        foreach (var level in _levels)
        {
            if (level.Index < 0 || level.Index >= _minutes.Count) continue;
            var center = new Point(X(_minutes[level.Index].MinuteUnixSeconds), Y(level.Value));
            _dots.Add(new Dot(level, center));
            var use = _use(level);
            var fill = use switch
            {
                LevelUse.Required => RequiredDot,
                LevelUse.Preferred => PreferredDot,
                _ => PlainDot,
            };
            var pen = use switch
            {
                LevelUse.Required => RequiredDotPen,
                LevelUse.Preferred => PreferredDotPen,
                _ => PlainDotPen,
            };
            dc.DrawEllipse(fill, pen, center, DotRadius, DotRadius);
        }

        for (int col = 0; col < columns; col++)
        {
            if (colCount[col] == 0) continue;
            double x = Pad + col + 0.5;
            dc.DrawLine(BarPen, new Point(x, Y(colHi[col])), new Point(x, Y(colLo[col])));
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var dot in _dots)
        {
            string badge = _badge(dot.Level);
            if (badge.Length == 0) continue;
            var text = new FormattedText(badge, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                BadgeFace, 11, BadgeBrush, pixelsPerDip);
            double y = dot.Level.Top ? dot.Center.Y - DotRadius - text.Height + 1 : dot.Center.Y + DotRadius - 1;
            var at = new Point(dot.Center.X + DotRadius - 1, y);
            dc.DrawRectangle(BackBrush, null, new Rect(at.X - 1, at.Y + 1, text.Width + 2, text.Height - 2));
            dc.DrawText(text, at);
        }
    }

    private Dot? HitDot(Point p)
    {
        Dot? best = null;
        double bestDist = DotRadius + 3;
        foreach (var dot in _dots)
        {
            double d = (dot.Center - p).Length;
            if (d < bestDist)
            {
                bestDist = d;
                best = dot;
            }
        }
        return best;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitDot(e.GetPosition(this));
        string text = hit is { } dot ? _label(dot.Level) : "";
        Cursor = hit != null ? Cursors.Hand : Cursors.Arrow;
        if ((ToolTip as string) != (text.Length == 0 ? null : text)) ToolTip = text.Length == 0 ? null : text;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ToolTip = null;
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (HitDot(e.GetPosition(this)) is { } dot)
        {
            LevelClicked?.Invoke(dot.Level);
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        _menuPoint = e.GetPosition(this);
    }

    private Point Project(Level2 level) => new(
        Pad + (WeekendCompressor.Instance.ToVirtual(_minutes[level.Index].MinuteUnixSeconds) - _vStart) * _xScale,
        Pad + (_plotHi - level.Value) * _yScale);

    private void AddNearest(Point p)
    {
        if (_yScale <= 0 || _extremes.Count == 0) return;
        double minArm = VisibleArmPixels / _yScale;
        Level2? best = null;
        double bestDist = AddRadiusPixels;
        foreach (var level in _extremes)
        {
            if (level.ApproachPoints < minArm || level.ReversalPoints < minArm) continue;
            if (level.Index < 0 || level.Index >= _minutes.Count) continue;
            double d = (Project(level) - p).Length;
            if (d < bestDist)
            {
                bestDist = d;
                best = level;
            }
        }
        if (best is { } found) LevelAdded?.Invoke(found);
    }

    public static string LevelLabel(string price, Level2 level) =>
        string.Format(CultureInfo.InvariantCulture, "{0} {1}, left {2:F0} right {3:F0} pips", price,
            level.Top ? "top" : "bottom", level.ApproachPoints / (double)DensityProfile.PipPoints,
            level.ReversalPoints / (double)DensityProfile.PipPoints);
}

public enum LevelUse
{
    Off,
    Required,
    Preferred,
}
