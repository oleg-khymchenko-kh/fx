using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FXViewer.Chart;

public sealed class TimeAxisView : FrameworkElement
{
    private const double LabelFontSize = 11;
    private const int MinLabelSpacingDip = 50;
    private const double LabelGapDip = 6;

    private const double CursorLabelPaddingDip = 5;

    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly Typeface CursorLabelTypeface =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Brush LabelBrush = CreateFrozenBrush(0x60, 0x60, 0x60);
    private static readonly Brush CursorLabelBrush = CreateFrozenBrush(0x30, 0x30, 0x30);

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private long _columnSeconds;
    private long _startBucket;
    private int _widthPx;
    private WeekendCompressor? _map;
    private long? _cursorUnix;
    private double _cursorXDip;

    public void Update(long columnSeconds, long startBucket, int widthPx, WeekendCompressor? map)
    {
        _columnSeconds = columnSeconds;
        _startBucket = startBucket;
        _widthPx = widthPx;
        _map = map;
        InvalidateVisual();
    }

    public void SetCursor(long? unixSeconds, double xDip)
    {
        _cursorUnix = unixSeconds;
        _cursorXDip = xDip;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_columnSeconds <= 0 || _widthPx <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        long bucketSec = _columnSeconds;
        long viewStart = _startBucket * bucketSec;
        long viewEnd = (_startBucket + _widthPx) * bucketSec;
        double lastRight = double.MinValue;
        double labelScale = _map == null ? 1 : 5.0 / 7.0;
        foreach (var (unix, text) in Labels(bucketSec, _map?.ToReal(viewStart) ?? viewStart,
                     _map?.ToReal(viewEnd) ?? viewEnd, dpi.DpiScaleX, labelScale))
        {
            if (_map != null && _map.InGap(unix)) continue;
            long col = (_map?.ToVirtual(unix) ?? unix) / bucketSec - _startBucket;
            if (col < 0 || col >= _widthPx) continue;
            double xDip = col / dpi.DpiScaleX;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelTypeface, LabelFontSize, LabelBrush, dpi.PixelsPerDip);
            double x = Math.Clamp(xDip - ft.Width / 2, 0, Math.Max(0, ActualWidth - ft.Width));
            if (x < lastRight + LabelGapDip) continue;
            dc.DrawText(ft, new Point(x, 2));
            lastRight = x + ft.Width;
        }
        DrawCursorLabel(dc, dpi);
    }

    private void DrawCursorLabel(DrawingContext dc, DpiScale dpi)
    {
        if (_cursorUnix == null) return;
        var d = DateTimeOffset.FromUnixTimeSeconds(_cursorUnix.Value).UtcDateTime;
        var text = d.ToString("dd-MMM-yy HH:mm", CultureInfo.InvariantCulture);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            CursorLabelTypeface, LabelFontSize, CursorLabelBrush, dpi.PixelsPerDip);
        double boxWidth = ft.Width + 2 * CursorLabelPaddingDip;
        double x = Math.Clamp(_cursorXDip - boxWidth / 2, 0, Math.Max(0, ActualWidth - boxWidth));
        dc.DrawRectangle(Brushes.White, null, new Rect(x, 0, boxWidth, ActualHeight));
        dc.DrawText(ft, new Point(x + CursorLabelPaddingDip, 2));
    }

    private static IEnumerable<(long Unix, string Text)> Labels(long bucketSec, long viewStart, long viewEnd,
        double dpiScaleX, double scale)
    {
        double DipSpacing(double stepSec) => stepSec * scale / bucketSec / dpiScaleX;
        if (bucketSec < ChartRasterizer.MinuteSeconds)
        {
            foreach (int m in new[] { 1, 2, 5, 10, 15, 30 })
            {
                long stepSec = m * ChartRasterizer.MinuteSeconds;
                if (DipSpacing(stepSec) < MinLabelSpacingDip) continue;
                for (long t = (viewStart + stepSec - 1) / stepSec * stepSec; t < viewEnd; t += stepSec)
                    yield return (t, MinuteText(t));
                yield break;
            }
        }
        if (ChartRasterizer.HourSeconds >= ChartRasterizer.MinHourGridSpacingPixels * bucketSec)
        {
            foreach (int h in new[] { 1, 2, 3, 6, 12 })
            {
                long stepSec = h * ChartRasterizer.HourSeconds;
                if (DipSpacing(stepSec) < MinLabelSpacingDip) continue;
                for (long t = (viewStart + stepSec - 1) / stepSec * stepSec; t < viewEnd; t += stepSec)
                    yield return (t, HourText(t));
                yield break;
            }
        }
        if (ChartRasterizer.DaySeconds >= ChartRasterizer.MinDayGridSpacingPixels * bucketSec)
        {
            long day = ChartRasterizer.DaySeconds;
            foreach (int stepDays in new[] { 1, 2, 7, 14 })
            {
                if (DipSpacing(stepDays * (double)day) < MinLabelSpacingDip) continue;
                for (long t = (viewStart + day - 1) / day * day; t < viewEnd; t += day)
                {
                    long dayIndex = t / day;
                    bool hit = stepDays switch
                    {
                        1 => true,
                        2 => dayIndex % 2 == 0,
                        7 => dayIndex % 7 == 4,
                        _ => dayIndex % 14 == 4,
                    };
                    if (hit) yield return (t, stepDays >= 7 ? WeekText(t) : DayText(t));
                }
                yield break;
            }
        }
        double monthDip = DipSpacing(30.44 * ChartRasterizer.DaySeconds);
        foreach (int m in new[] { 1, 2, 3, 6 })
        {
            if (monthDip * m < MinLabelSpacingDip) continue;
            foreach (var label in MonthLabels(viewStart, viewEnd, m)) yield return label;
            yield break;
        }
        double yearDip = DipSpacing(365.25 * ChartRasterizer.DaySeconds);
        foreach (int y in new[] { 1, 2, 5, 10, 20, 50 })
        {
            if (yearDip * y < MinLabelSpacingDip) continue;
            foreach (var label in YearLabels(viewStart, viewEnd, y)) yield return label;
            yield break;
        }
    }

    private static string WeekText(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("d MMM", CultureInfo.InvariantCulture);

    private static IEnumerable<(long Unix, string Text)> MonthLabels(long viewStart, long viewEnd, int step)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(viewStart).UtcDateTime;
        var m = new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        while (true)
        {
            long unix = new DateTimeOffset(m).ToUnixTimeSeconds();
            if (unix >= viewEnd) yield break;
            int monthIndex = m.Year * 12 + m.Month - 1;
            if (unix >= viewStart && monthIndex % step == 0)
                yield return (unix, m.ToString("MMM yy", CultureInfo.InvariantCulture));
            m = m.AddMonths(1);
        }
    }

    private static IEnumerable<(long Unix, string Text)> YearLabels(long viewStart, long viewEnd, int step)
    {
        int year = DateTimeOffset.FromUnixTimeSeconds(viewStart).UtcDateTime.Year;
        while (true)
        {
            var dt = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            long unix = new DateTimeOffset(dt).ToUnixTimeSeconds();
            if (unix >= viewEnd) yield break;
            if (unix >= viewStart && year % step == 0)
                yield return (unix, year.ToString(CultureInfo.InvariantCulture));
            year++;
        }
    }

    private static string MinuteText(long unix)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return d.Minute == 0
            ? HourText(unix)
            : d.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static string HourText(long unix)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return d.Hour == 0
            ? d.ToString("d MMM", CultureInfo.InvariantCulture)
            : d.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static string DayText(long unix)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        if (d.Day == 1) return d.ToString("MMM yy", CultureInfo.InvariantCulture);
        return d.Day.ToString(CultureInfo.InvariantCulture);
    }
}
