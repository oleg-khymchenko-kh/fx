using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Fx.WeekPuzzle;

internal sealed class WeekTab : DockPanel
{
    private const string Caption = "Week puzzle";
    private readonly PuzzleOptions _options;
    private readonly DayStore _store;
    private readonly Func<DayBoard> _dayBoard;
    private readonly PuzzleView _view = new();
    private readonly CheckBox _fillBox = new() { Content = "Mark days", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) };
    private readonly ComboBox _fillGroup = new() { Width = 320, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _weeksBox = new() { Width = 40, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _startBox = new() { Width = 80, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) };
    private readonly Stopwatch _clock = new();
    private readonly Random _random = new();
    private bool _started;
    private bool _syncing;
    private int _checks;

    public WeekTab(PuzzleOptions options, DayStore store, Func<DayBoard> dayBoard)
    {
        _options = options;
        _store = store;
        _dayBoard = dayBoard;
        _weeksBox.Text = options.Weeks > 0 ? options.Weeks.ToString(CultureInfo.InvariantCulture) : "";
        _startBox.Text = options.FirstMonday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
        bar.Children.Add(Toolbar.Button("New game", NewGame));
        bar.Children.Add(Toolbar.Label("Start"));
        bar.Children.Add(_startBox);
        bar.Children.Add(Toolbar.Label("Weeks"));
        bar.Children.Add(_weeksBox);
        bar.Children.Add(Toolbar.Button("Sort", _view.Sort));
        bar.Children.Add(Toolbar.Button("Check", Check));
        bar.Children.Add(Toolbar.Button("Fit", _view.Fit));
        bar.Children.Add(_fillBox);
        bar.Children.Add(_fillGroup);
        bar.Children.Add(_status);

        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(_view);

        _fillBox.IsChecked = store.Current.WeekDayFill;
        _fillBox.Click += (_, _) => SetDayFill(_fillBox.IsChecked == true, null);
        _fillGroup.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _fillGroup.SelectedItem is string name) SetDayFill(null, name);
        };
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) RefreshDayFill();
        };
        _view.Changed += UpdateStatus;
        Loaded += (_, _) =>
        {
            if (_started) return;
            _started = true;
            NewGame();
        };
        KeyDown += OnKeyDown;
    }

    public PuzzleView View => _view;

    public void NewGame()
    {
        DateTime start = _options.FirstMonday;
        int weeks = _options.Weeks;
        var owner = Window.GetWindow(this);
        try
        {
            if (_startBox.Text.Trim().Length > 0) start = PuzzleOptions.ParseDate(_startBox.Text.Trim());
            weeks = _weeksBox.Text.Trim().Length > 0 ? int.Parse(_weeksBox.Text.Trim(), CultureInfo.InvariantCulture) : 0;
        }
        catch (FormatException)
        {
            MessageBox.Show(owner, "Start must be a date yyyy-MM-dd and Weeks a number", Caption);
            return;
        }
        List<CardShape> shapes;
        Cursor = Cursors.Wait;
        try
        {
            shapes = WeekCards.Load(_options with { FirstMonday = start, Weeks = weeks });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, ex.Message, Caption);
            return;
        }
        finally
        {
            Cursor = null;
        }
        _checks = 0;
        _clock.Restart();
        _view.SetCards(shapes, _random);
        _view.Focus();
    }

    public void SetDayFill(bool? show, string? group)
    {
        if (show is { } on) _store.Current.WeekDayFill = on;
        if (group is not null) _store.Current.WeekDayFillGroup = group;
        _store.Save();
        RefreshDayFill();
    }

    public object DayFillInfo()
    {
        var group = FillGroup(LoadDayBoard());
        return new
        {
            show = _store.Current.WeekDayFill,
            group = group?.Name,
            days = group is null ? 0 : GroupDays(group).Count(),
        };
    }

    public void RefreshDayFill()
    {
        var board = LoadDayBoard();
        var group = FillGroup(board);
        var names = board.Groups.Select(g => g.Name).ToList();
        _syncing = true;
        try
        {
            if (!_fillGroup.Items.Cast<string>().SequenceEqual(names))
            {
                _fillGroup.Items.Clear();
                foreach (string name in names) _fillGroup.Items.Add(name);
            }
            _fillGroup.SelectedItem = group?.Name;
            _fillBox.IsChecked = _store.Current.WeekDayFill;
        }
        finally
        {
            _syncing = false;
        }
        _view.SetFilledDays(_store.Current.WeekDayFill && group is not null ? GroupDays(group) : Array.Empty<DateTime>());
    }

    private DayBoard LoadDayBoard()
    {
        try
        {
            return _dayBoard();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            return new DayBoard(Array.Empty<DayShape>());
        }
    }

    private DayGroup? FillGroup(DayBoard board) =>
        (_store.Current.WeekDayFillGroup is { } name ? board.FindGroup(name) : null) ?? board.Groups.FirstOrDefault();

    private static IEnumerable<DateTime> GroupDays(DayGroup group) =>
        group.Cards.Select(c => c.Shape.Date)
            .Concat(group.Absent
                .Select(id => DateTime.TryParseExact(id, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    ? day
                    : (DateTime?)null)
                .OfType<DateTime>());

    public void Check()
    {
        if (_view.Board.Cards.Count == 0) return;
        _checks++;
        _view.ShowMarks();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var board = _view.Board;
        int links = board.Links.Count();
        string text = $"{board.Cards.Count} cards, {board.ChainCount} chains, {links} links";
        if (_view.MarksShown) text += $", {board.CorrectLinks} right";
        if (_checks > 0) text += $", checks {_checks}";
        if (_view.Hints > 0) text += $", hints {_view.Hints}";
        if (board.Solved)
        {
            _clock.Stop();
            text += $"   Solved in {_clock.Elapsed:mm\\:ss}";
        }
        _status.Text = text;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_weeksBox.IsKeyboardFocusWithin || _startBox.IsKeyboardFocusWithin) return;
        switch (e.Key)
        {
            case Key.F:
                _view.Fit();
                e.Handled = true;
                break;
            case Key.C:
                Check();
                e.Handled = true;
                break;
            case Key.S:
                _view.Sort();
                e.Handled = true;
                break;
        }
    }
}
