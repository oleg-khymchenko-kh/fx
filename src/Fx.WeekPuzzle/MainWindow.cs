using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Fx.WeekPuzzle;

internal sealed class MainWindow : Window
{
    private readonly PuzzleOptions _options;
    private readonly PuzzleView _view = new();
    private readonly TextBox _weeksBox = new() { Width = 40, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _startBox = new() { Width = 80, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) };
    private readonly Stopwatch _clock = new();
    private readonly Random _random = new();
    private int _checks;

    public MainWindow(PuzzleOptions options)
    {
        _options = options;
        Title = $"Week puzzle {options.Symbol}, {options.LineName}";
        Width = 1400;
        Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _weeksBox.Text = options.Weeks > 0 ? options.Weeks.ToString(CultureInfo.InvariantCulture) : "";
        _startBox.Text = options.FirstMonday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
        bar.Children.Add(Button("New game", NewGame));
        bar.Children.Add(Label("Start"));
        bar.Children.Add(_startBox);
        bar.Children.Add(Label("Weeks"));
        bar.Children.Add(_weeksBox);
        bar.Children.Add(Button("Sort", _view.Sort));
        bar.Children.Add(Button("Check", Check));
        bar.Children.Add(Button("Fit", _view.Fit));
        bar.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_view);
        Content = root;

        _view.Changed += UpdateStatus;
        Loaded += (_, _) => NewGame();
        KeyDown += OnKeyDown;
    }

    public PuzzleView View => _view;

    public void NewGame()
    {
        DateTime start = _options.FirstMonday;
        int weeks = _options.Weeks;
        try
        {
            if (_startBox.Text.Trim().Length > 0) start = PuzzleOptions.ParseDate(_startBox.Text.Trim());
            weeks = _weeksBox.Text.Trim().Length > 0 ? int.Parse(_weeksBox.Text.Trim(), CultureInfo.InvariantCulture) : 0;
        }
        catch (FormatException)
        {
            MessageBox.Show(this, "Start must be a date yyyy-MM-dd and Weeks a number", Title);
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
            MessageBox.Show(this, ex.Message, Title);
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

    private static Button Button(string text, Action click)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => click();
        return button;
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
}
