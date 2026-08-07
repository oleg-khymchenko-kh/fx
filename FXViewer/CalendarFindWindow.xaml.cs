using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FXViewer.Calendar;

namespace FXViewer;

public partial class CalendarFindWindow : Window
{
    public sealed record Row(
        string Time, string Currency, string Impact, string Event,
        string Actual, string Forecast, string Previous, long UnixSeconds);

    private const int MaxRows = 3000;

    private readonly List<CalendarSummary> _all;
    private readonly Action<long> _navigate;
    private bool _applying;

    public CalendarFindWindow(List<CalendarSummary> events, Action<long> navigate)
    {
        InitializeComponent();
        _all = events;
        _navigate = navigate;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Apply();
        SearchBox.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Apply();

    private void Filter_Changed(object sender, RoutedEventArgs e) => Apply();

    private void Apply()
    {
        if (!IsInitialized) return;
        string query = SearchBox.Text.Trim();
        bool rateOnly = RateOnlyBox.IsChecked == true;
        bool highOnly = HighOnlyBox.IsChecked == true;
        var rows = new List<Row>();
        int matched = 0;
        for (int i = _all.Count - 1; i >= 0; i--)
        {
            var e = _all[i];
            if (rateOnly && e.Impact != CalendarImpact.Rate) continue;
            if (highOnly && e.Impact != CalendarImpact.Rate && e.Impact != CalendarImpact.High) continue;
            if (query.Length > 0 && !Matches(e, query)) continue;
            matched++;
            if (rows.Count < MaxRows) rows.Add(ToRow(e));
        }
        _applying = true;
        EventsList.ItemsSource = rows;
        EventsList.SelectedIndex = -1;
        _applying = false;
        CountText.Text = matched > rows.Count
            ? $"{matched} events, newest {rows.Count} shown"
            : $"{matched} events";
    }

    private static bool Matches(CalendarSummary e, string query) =>
        e.Event.Contains(query, StringComparison.OrdinalIgnoreCase)
        || e.Currency.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static Row ToRow(CalendarSummary e) => new(
        DateTimeOffset.FromUnixTimeSeconds(e.UnixSeconds).UtcDateTime
            .ToString("ddd dd MMM yyyy HH:mm", CultureInfo.InvariantCulture),
        e.Currency,
        ImpactText(e.Impact),
        e.Event,
        e.Actual,
        e.Forecast,
        e.Previous,
        e.UnixSeconds);

    private static string ImpactText(CalendarImpact impact) => impact switch
    {
        CalendarImpact.Rate => "Rate",
        CalendarImpact.High => "High",
        CalendarImpact.Medium => "Medium",
        CalendarImpact.Low => "Low",
        CalendarImpact.Holiday => "Holiday",
        _ => "",
    };

    private void EventsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applying) return;
        if (EventsList.SelectedItem is Row row) _navigate(row.UnixSeconds);
    }

    private void EventRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: Row row } item && item.IsSelected)
            _navigate(row.UnixSeconds);
    }
}
