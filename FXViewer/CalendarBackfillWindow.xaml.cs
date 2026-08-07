using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FXViewer.Calendar;

namespace FXViewer;

public partial class CalendarBackfillWindow : Window
{
    private const string DateFormat = "yyyy-MM-dd";

    public DateTime FirstWeekStart { get; private set; }
    public DateTime LastWeekStart { get; private set; }

    public CalendarBackfillWindow(DateTime from, DateTime to)
    {
        InitializeComponent();
        FromBox.Text = from.ToString(DateFormat, CultureInfo.InvariantCulture);
        ToBox.Text = to.ToString(DateFormat, CultureInfo.InvariantCulture);
        UpdateSummary();
    }

    private void Range_Changed(object sender, TextChangedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        if (!IsInitialized) return;
        if (ParseRange() is not { } range)
        {
            SummaryText.Text = $"Enter dates as {DateFormat}, From not after To.";
            OkBtn.IsEnabled = false;
            return;
        }
        int weeks = (int)((range.Last - range.First).TotalDays / 7) + 1;
        SummaryText.Text =
            $"{weeks} weeks: {range.First.ToString(DateFormat, CultureInfo.InvariantCulture)}"
            + $" .. {range.Last.AddDays(6).ToString(DateFormat, CultureInfo.InvariantCulture)}"
            + $", about {WaitText(weeks)}.";
        OkBtn.IsEnabled = true;
    }

    private static string WaitText(int weeks)
    {
        int seconds = weeks * 6;
        return seconds < 120
            ? $"{seconds} s"
            : $"{seconds / 60} min";
    }

    private (DateTime First, DateTime Last)? ParseRange()
    {
        if (!DateTime.TryParseExact(FromBox.Text.Trim(), DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var from)) return null;
        if (!DateTime.TryParseExact(ToBox.Text.Trim(), DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var to)) return null;
        var first = CalendarDownloader.WeekStartOf(from);
        var last = CalendarDownloader.WeekStartOf(to);
        return first <= last ? (first, last) : null;
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ParseRange() is not { } range) return;
        FirstWeekStart = range.First;
        LastWeekStart = range.Last;
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
