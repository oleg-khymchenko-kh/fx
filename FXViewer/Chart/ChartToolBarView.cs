using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FXViewer.Calendar;

namespace FXViewer.Chart;

public sealed class ChartToolBarView : FrameworkElement
{
    private const double ButtonSizeDip = 22;
    private const double ButtonGapDip = 4;
    private const double PadLeftDip = 8;
    private const double PadRightDip = 8;
    private const double PadTopDip = 6;
    private const double PadBottomDip = 6;
    private const double ConnGapDip = 10;
    private const double ConnDotDip = 16;

    private const int CalendarButton = 0;
    private const int ForecastButton = 1;
    private const int WeekendsButton = 2;
    private const int SessionsButton = 3;
    private const int CommentsButton = 4;
    private const int AddButton = 5;
    private const int SettingsButton = 6;
    private const int ButtonCount = 7;
    private const int ConnHoverIndex = ButtonCount;

    private static readonly Pen BorderPen = CreateFrozenPen(0xE0, 0xE0, 0xE0, 1);
    private static readonly Pen ButtonBorderPen = CreateFrozenPen(0xC8, 0xC8, 0xC8, 1);
    private static readonly Brush OffFillBrush = CreateFrozenBrush(0xF5, 0xF5, 0xF5);
    private static readonly Brush HoverFillBrush = CreateFrozenBrush(0xE4, 0xE4, 0xE4);
    private static readonly Brush OnFillBrush = CreateFrozenBrush(0x50, 0x50, 0x50);
    private static readonly Brush OffIconBrush = CreateFrozenBrush(0x50, 0x50, 0x50);
    private static readonly Brush OnIconBrush = CreateFrozenBrush(0xFF, 0xFF, 0xFF);
    private static readonly Brush DisabledIconBrush = CreateFrozenBrush(0xB4, 0xB4, 0xB4);
    private static readonly Pen OffIconPen = CreateFrozenPen(0x50, 0x50, 0x50, 1);
    private static readonly Pen OnIconPen = CreateFrozenPen(0xFF, 0xFF, 0xFF, 1);
    private static readonly Pen DisabledIconPen = CreateFrozenPen(0xB4, 0xB4, 0xB4, 1);
    private static readonly Pen OffIconThickPen = CreateFrozenPen(0x50, 0x50, 0x50, 1.6);
    private static readonly Pen OnIconThickPen = CreateFrozenPen(0xFF, 0xFF, 0xFF, 1.6);
    private static readonly Pen DisabledIconThickPen = CreateFrozenPen(0xB4, 0xB4, 0xB4, 1.6);

    private static readonly string[] ButtonTips =
    {
        "Calendar", "Forecast", "No weekends", "Sessions", "Comments", "Add symbol", "Settings",
    };

    private bool _calendarPresent;
    private bool _calendarEnabled;
    private bool _forecastPresent;
    private bool _forecastEnabled;
    private bool _weekendsHidden;
    private bool _sessionsVisible;
    private bool _commentsVisible;
    private int _hoverButton = -1;
    private string? _connText;
    private Brush _connBrush = CreateFrozenBrush(0x80, 0x80, 0x80);

    public event Action? CalendarClick;
    public event Action? ForecastClick;
    public event Action? WeekendsClick;
    public event Action? SessionsClick;
    public event Action? CommentsClick;
    public event Action? AddClick;
    public event Action? SettingsClick;
    public event Action? CalendarSettingsRequested;
    public event Action? CalendarFindRequested;
    public event Action<CalendarImpact>? CalendarLevelSelected;
    public Func<CalendarImpact?>? CalendarLevelChecked { get; set; }
    public event Action? ForecastReloadRequested;

    public ChartToolBarView()
    {
        MouseLeftButtonDown += (_, e) =>
        {
            int button = ButtonAt(e.GetPosition(this));
            if (button < 0 || !IsButtonEnabled(button)) return;
            e.Handled = true;
            switch (button)
            {
                case CalendarButton: CalendarClick?.Invoke(); break;
                case ForecastButton: ForecastClick?.Invoke(); break;
                case WeekendsButton: WeekendsClick?.Invoke(); break;
                case SessionsButton: SessionsClick?.Invoke(); break;
                case CommentsButton: CommentsClick?.Invoke(); break;
                case AddButton: AddClick?.Invoke(); break;
                case SettingsButton: SettingsClick?.Invoke(); break;
            }
        };
        MouseRightButtonUp += (_, e) =>
        {
            int button = ButtonAt(e.GetPosition(this));
            if (button == CalendarButton || button == ForecastButton) e.Handled = true;
        };
        MouseRightButtonDown += (_, e) =>
        {
            int button = ButtonAt(e.GetPosition(this));
            if (button == CalendarButton && _calendarPresent)
            {
                e.Handled = true;
                var calMenu = new ContextMenu { PlacementTarget = this };
                var checkedLevel = _calendarEnabled ? CalendarLevelChecked?.Invoke() : null;
                foreach (var (header, level) in new[]
                {
                    ("High", CalendarImpact.High),
                    ("Medium", CalendarImpact.Medium),
                    ("Low", CalendarImpact.Low),
                })
                {
                    var item = new MenuItem { Header = header, IsChecked = checkedLevel == level };
                    item.Click += (_, _) => CalendarLevelSelected?.Invoke(level);
                    calMenu.Items.Add(item);
                }
                calMenu.Items.Add(new Separator());
                var settings = new MenuItem { Header = "Settings..." };
                settings.Click += (_, _) => CalendarSettingsRequested?.Invoke();
                calMenu.Items.Add(settings);
                var find = new MenuItem { Header = "Find..." };
                find.Click += (_, _) => CalendarFindRequested?.Invoke();
                calMenu.Items.Add(find);
                calMenu.IsOpen = true;
                return;
            }
            if (button == ForecastButton && _forecastPresent)
            {
                e.Handled = true;
                var fcMenu = new ContextMenu { PlacementTarget = this };
                var reload = new MenuItem { Header = "Reload forecasts" };
                reload.Click += (_, _) => ForecastReloadRequested?.Invoke();
                fcMenu.Items.Add(reload);
                fcMenu.IsOpen = true;
            }
        };
        MouseMove += (_, e) => SetHover(HoverAt(e.GetPosition(this)));
        MouseLeave += (_, _) => SetHover(-1);
    }

    public void SetCalendarRow(bool present, bool enabled)
    {
        _calendarPresent = present;
        _calendarEnabled = enabled;
        InvalidateVisual();
    }

    public void SetForecastRow(bool present, bool enabled)
    {
        _forecastPresent = present;
        _forecastEnabled = enabled;
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

    public void SetCommentsRow(bool visible)
    {
        _commentsVisible = visible;
        InvalidateVisual();
    }

    public void SetConnStatus(string text, int colorArgb)
    {
        _connText = text;
        _connBrush = CreateFrozenBrush(
            (byte)(colorArgb >> 16), (byte)(colorArgb >> 8), (byte)colorArgb);
        if (_hoverButton == ConnHoverIndex) ToolTip = text;
        InvalidateVisual();
    }

    private static double ButtonsWidth =>
        ButtonCount * ButtonSizeDip + (ButtonCount - 1) * ButtonGapDip;

    private static Rect ConnBounds()
    {
        double x = PadLeftDip + ButtonsWidth + ConnGapDip;
        return new Rect(x, PadTopDip + (ButtonSizeDip - ConnDotDip) / 2, ConnDotDip, ConnDotDip);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(ConnBounds().Right + PadRightDip, PadTopDip + ButtonSizeDip + PadBottomDip);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.DrawLine(BorderPen, new Point(0.5, 0), new Point(0.5, ActualHeight));
        dc.DrawLine(BorderPen,
            new Point(0, ActualHeight - 0.5), new Point(ActualWidth, ActualHeight - 0.5));
        for (int button = 0; button < ButtonCount; button++)
        {
            var rect = ButtonBounds(button);
            bool enabled = IsButtonEnabled(button);
            bool active = enabled && IsButtonActive(button);
            var fill = active ? OnFillBrush
                : enabled && _hoverButton == button ? HoverFillBrush
                : OffFillBrush;
            dc.DrawRoundedRectangle(fill, ButtonBorderPen, rect, 3, 3);
            var brush = !enabled ? DisabledIconBrush : active ? OnIconBrush : OffIconBrush;
            var pen = !enabled ? DisabledIconPen : active ? OnIconPen : OffIconPen;
            var thick = !enabled ? DisabledIconThickPen : active ? OnIconThickPen : OffIconThickPen;
            switch (button)
            {
                case CalendarButton: DrawCalendarIcon(dc, rect, pen, brush); break;
                case ForecastButton: DrawForecastIcon(dc, rect, pen, brush); break;
                case WeekendsButton: DrawWeekendsIcon(dc, rect, brush, thick); break;
                case SessionsButton: DrawSessionsIcon(dc, rect, brush); break;
                case CommentsButton: DrawCommentsIcon(dc, rect, pen, brush); break;
                case AddButton: DrawAddIcon(dc, rect, thick); break;
                case SettingsButton: DrawSettingsIcon(dc, rect, thick); break;
            }
        }
        if (_connText == null) return;
        var dot = ConnBounds();
        double r = ConnDotDip / 2;
        dc.DrawEllipse(_connBrush, null, new Point(dot.X + r, dot.Y + r), r, r);
    }

    private static void DrawCalendarIcon(DrawingContext dc, Rect r, Pen pen, Brush brush)
    {
        double x = r.X, y = r.Y;
        var body = new Rect(x + 4.5, y + 6.5, 13, 11);
        dc.DrawRectangle(null, pen, body);
        dc.DrawLine(pen, new Point(body.Left, y + 9.5), new Point(body.Right, y + 9.5));
        dc.DrawLine(pen, new Point(x + 8.5, y + 4.5), new Point(x + 8.5, y + 7.5));
        dc.DrawLine(pen, new Point(x + 13.5, y + 4.5), new Point(x + 13.5, y + 7.5));
        for (int i = 0; i < 3; i++)
            dc.DrawRectangle(brush, null, new Rect(x + 6 + i * 4, y + 12, 2, 2));
    }

    private static void DrawForecastIcon(DrawingContext dc, Rect r, Pen pen, Brush brush)
    {
        double x = r.X, y = r.Y;
        dc.DrawLine(pen, new Point(x + 4, y + 17), new Point(x + 11, y + 10));
        var dashed = pen.Clone();
        dashed.DashStyle = new DashStyle(new double[] { 1.6, 1.6 }, 0);
        dashed.Freeze();
        dc.DrawLine(dashed, new Point(x + 11, y + 10), new Point(x + 16, y + 5.5));
        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(new Point(x + 18.5, y + 4), true, true);
            g.LineTo(new Point(x + 13.6, y + 5.1), true, false);
            g.LineTo(new Point(x + 16.4, y + 8.5), true, false);
        }
        arrow.Freeze();
        dc.DrawGeometry(brush, null, arrow);
    }

    private static void DrawWeekendsIcon(DrawingContext dc, Rect r, Brush brush, Pen edgePen)
    {
        double x = r.X, y = r.Y;
        dc.DrawLine(edgePen, new Point(x + 4.5, y + 5.5), new Point(x + 4.5, y + 17.5));
        dc.DrawLine(edgePen, new Point(x + 17.5, y + 5.5), new Point(x + 17.5, y + 17.5));
        var arrows = new StreamGeometry();
        using (var g = arrows.Open())
        {
            g.BeginFigure(new Point(x + 10.5, y + 11.5), true, true);
            g.LineTo(new Point(x + 6.5, y + 8.5), true, false);
            g.LineTo(new Point(x + 6.5, y + 14.5), true, false);
            g.BeginFigure(new Point(x + 11.5, y + 11.5), true, true);
            g.LineTo(new Point(x + 15.5, y + 8.5), true, false);
            g.LineTo(new Point(x + 15.5, y + 14.5), true, false);
        }
        arrows.Freeze();
        dc.DrawGeometry(brush, null, arrows);
    }

    private static void DrawSessionsIcon(DrawingContext dc, Rect r, Brush brush)
    {
        double x = r.X, y = r.Y;
        dc.DrawRectangle(FadeBrush(brush, 0.3), null, new Rect(x + 4, y + 6, 4, 11));
        dc.DrawRectangle(FadeBrush(brush, 0.6), null, new Rect(x + 9, y + 6, 4, 11));
        dc.DrawRectangle(brush, null, new Rect(x + 14, y + 6, 4, 11));
    }

    private static void DrawCommentsIcon(DrawingContext dc, Rect r, Pen pen, Brush brush)
    {
        double x = r.X, y = r.Y;
        var body = new Rect(x + 4.5, y + 5.5, 13, 9.5);
        dc.DrawRoundedRectangle(null, pen, body, 2.5, 2.5);
        var tail = new StreamGeometry();
        using (var g = tail.Open())
        {
            g.BeginFigure(new Point(x + 7.5, y + 14), true, true);
            g.LineTo(new Point(x + 7.5, y + 18), true, false);
            g.LineTo(new Point(x + 11.5, y + 14), true, false);
        }
        tail.Freeze();
        dc.DrawGeometry(brush, null, tail);
        for (int i = 0; i < 3; i++)
            dc.DrawRectangle(brush, null, new Rect(x + 7 + i * 3, y + 9.5, 2, 1.5));
    }

    private static void DrawAddIcon(DrawingContext dc, Rect r, Pen pen)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        dc.DrawLine(pen, new Point(cx, cy - 5.5), new Point(cx, cy + 5.5));
        dc.DrawLine(pen, new Point(cx - 5.5, cy), new Point(cx + 5.5, cy));
    }

    private static void DrawSettingsIcon(DrawingContext dc, Rect r, Pen pen)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        dc.DrawEllipse(null, pen, new Point(cx, cy), 3.1, 3.1);
        for (int i = 0; i < 8; i++)
        {
            double a = Math.PI * i / 4;
            dc.DrawLine(pen,
                new Point(cx + 4.6 * Math.Cos(a), cy + 4.6 * Math.Sin(a)),
                new Point(cx + 6.8 * Math.Cos(a), cy + 6.8 * Math.Sin(a)));
        }
    }

    private static Brush FadeBrush(Brush brush, double opacity)
    {
        var faded = brush.Clone();
        faded.Opacity = opacity;
        faded.Freeze();
        return faded;
    }

    private static Rect ButtonBounds(int button) =>
        new(PadLeftDip + button * (ButtonSizeDip + ButtonGapDip), PadTopDip,
            ButtonSizeDip, ButtonSizeDip);

    private static int ButtonAt(Point p)
    {
        if (p.Y < PadTopDip || p.Y > PadTopDip + ButtonSizeDip) return -1;
        double offset = p.X - PadLeftDip;
        if (offset < 0) return -1;
        int button = (int)Math.Floor(offset / (ButtonSizeDip + ButtonGapDip));
        if (button < 0 || button >= ButtonCount) return -1;
        return offset - button * (ButtonSizeDip + ButtonGapDip) <= ButtonSizeDip ? button : -1;
    }

    private bool IsButtonEnabled(int button) => button switch
    {
        CalendarButton => _calendarPresent,
        ForecastButton => _forecastPresent,
        _ => true,
    };

    private bool IsButtonActive(int button) => button switch
    {
        CalendarButton => _calendarEnabled,
        ForecastButton => _forecastEnabled,
        WeekendsButton => _weekendsHidden,
        SessionsButton => _sessionsVisible,
        CommentsButton => _commentsVisible,
        _ => false,
    };

    private int HoverAt(Point p)
    {
        int button = ButtonAt(p);
        if (button >= 0) return button;
        return _connText != null && ConnBounds().Contains(p) ? ConnHoverIndex : -1;
    }

    private void SetHover(int button)
    {
        if (_hoverButton == button) return;
        _hoverButton = button;
        ToolTip = button < 0 ? null
            : button == ConnHoverIndex ? _connText
            : ButtonTips[button];
        InvalidateVisual();
    }

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(byte r, byte g, byte b, double thickness)
    {
        var pen = new Pen(CreateFrozenBrush(r, g, b), thickness);
        pen.Freeze();
        return pen;
    }
}
