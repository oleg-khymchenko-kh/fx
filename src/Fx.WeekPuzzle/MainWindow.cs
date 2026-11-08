using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Fx.WeekPuzzle;

internal sealed class MainWindow : Window
{
    private readonly PuzzleOptions _options;
    private readonly DayStore _store;
    private readonly TabControl _tabs = new();
    private readonly TabItem _weekItem;
    private readonly TabItem _dayItem;
    private PuzzleApi? _api;

    public MainWindow(PuzzleOptions options)
    {
        _options = options;
        Title = $"Puzzle {options.Symbol}, {options.LineName}";
        Width = 1400;
        Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _store = DayStore.Load(options.DayStatePath);
        Days = new DayTab(options, _store);
        Weeks = new WeekTab(options, _store, () =>
        {
            Days.EnsureLoaded();
            return Days.Board;
        });
        _weekItem = new TabItem { Header = "Weeks", Content = Weeks };
        _dayItem = new TabItem { Header = "Days", Content = Days };
        _tabs.Items.Add(_weekItem);
        _tabs.Items.Add(_dayItem);
        _tabs.SelectedItem = _store.Current.Tab == DayStore.WeeksTab ? _weekItem : _dayItem;
        _tabs.SelectionChanged += OnTabChanged;
        Content = _tabs;
        Loaded += OnLoaded;
        Closed += (_, _) => _api?.Dispose();
    }

    public WeekTab Weeks { get; }

    public DayTab Days { get; }

    public PuzzleView View => Weeks.View;

    public bool DaysShown => _tabs.SelectedItem == _dayItem;

    public void ShowTab(bool days)
    {
        _tabs.SelectedItem = days ? _dayItem : _weekItem;
        UpdateLayout();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _api = new PuzzleApi(this, _options.ApiPort);
            _api.Start();
            Days.Api = _api.Url;
        }
        catch (HttpListenerException ex)
        {
            _api = null;
            Days.Api = $"off ({ex.Message})";
        }
        try
        {
            Days.EnsureLoaded();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            MessageBox.Show(this, ex.Message, "Day cards");
        }
        FocusView();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != _tabs) return;
        _store.Current.Tab = DaysShown ? DayStore.DaysTab : DayStore.WeeksTab;
        _store.Save();
        FocusView();
    }

    private void FocusView() =>
        Dispatcher.InvokeAsync(() =>
        {
            if (DaysShown) Days.View.Focus();
            else Weeks.View.Focus();
        }, DispatcherPriority.Input);
}
