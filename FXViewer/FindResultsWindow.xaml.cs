using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FXViewer.Chart;
using FXViewer.Storage;

namespace FXViewer;

public partial class FindResultsWindow : Window
{
    public sealed record Row(
        string Rank, string Symbol, string Window, string Fit, string Sim, string Rho, string Zoom,
        string Mirror, string Overlap, FindResult Result);

    private Action<FindResult>? _apply;
    private bool _searching;

    public event Action? CancelRequested;

    public FindResultsWindow(string indicatorName)
    {
        InitializeComponent();
        Title = "Find similar - " + indicatorName;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    public void ShowSearching(string headerText)
    {
        _searching = true;
        _apply = null;
        HeaderText.Text = headerText;
        StatusText.Text = "Searching...";
        CancelBtn.Content = "Cancel";
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.Value = 0;
        ProgressPanel.Visibility = Visibility.Visible;
        ResultsList.Visibility = Visibility.Collapsed;
    }

    public void SetProgress(double p) => ProgressBar.Value = Math.Clamp(p * 100, 0, 100);

    public void ShowResults(FindData data, Action<FindResult> apply)
    {
        _searching = false;
        _apply = null;
        ProgressPanel.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Visible;
        bool zigzag = FindSearchTypes.IsZigZag(data.SearchType);
        if (zigzag && !ResultsView.Columns.Contains(FitColumn)) ResultsView.Columns.Insert(3, FitColumn);
        else if (!zigzag) ResultsView.Columns.Remove(FitColumn);
        string targets = data.Targets is { Count: > 0 } ? string.Join(", ", data.Targets) : data.Source;
        HeaderText.Text = $"{FragmentText(data)} - {data.Results.Count} matches in {targets}";
        var w = WeekendCompressor.Instance;
        var rows = new List<Row>(data.Results.Count);
        for (int i = 0; i < data.Results.Count; i++)
        {
            var r = data.Results[i];
            long endUnix = w.ToRealEnd(w.ToVirtual(r.SourceUnix) + data.WindowMinutes * 60L);
            rows.Add(new Row(
                (i + 1).ToString(CultureInfo.InvariantCulture),
                r.Symbol.Length > 0 ? r.Symbol : data.Source,
                $"{Utc(r.SourceUnix):ddd dd MMM yyyy HH:mm} .. {Utc(endUnix):dd MMM HH:mm}",
                r.TimeFit.ToString("F3", CultureInfo.InvariantCulture),
                r.Sim.ToString("F4", CultureInfo.InvariantCulture),
                r.Rho.ToString("F3", CultureInfo.InvariantCulture),
                r.Zoom.ToString("F2", CultureInfo.InvariantCulture),
                r.Mirrored ? "M" : "",
                r.Overlap.ToString(CultureInfo.InvariantCulture),
                r));
        }
        ResultsList.ItemsSource = rows;
        _apply = apply;
    }

    public void ShowFailed(string message)
    {
        _searching = false;
        StatusText.Text = message;
        ProgressBar.Visibility = Visibility.Collapsed;
        CancelBtn.Content = "Close";
    }

    public static string FragmentText(FindData data) =>
        $"Fragment: {Utc(data.FragStartUnix):ddd dd MMM yyyy HH:mm} .. {Utc(data.FragEndUnix):ddd dd MMM yyyy HH:mm} UTC";

    private static DateTime Utc(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_searching) CancelRequested?.Invoke();
        else Close();
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_apply != null && ResultsList.SelectedItem is Row row) _apply(row.Result);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_searching) CancelRequested?.Invoke();
    }
}
