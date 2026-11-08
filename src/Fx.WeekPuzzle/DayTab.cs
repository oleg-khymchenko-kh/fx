using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Fx.WeekPuzzle;

internal sealed class DayTab : DockPanel
{
    private const string Caption = "Day cards";
    private readonly PuzzleOptions _options;
    private readonly DayStore _store;
    private readonly DayView _view = new();
    private readonly TextBox _startBox = new() { Width = 80, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _daysBox = new() { Width = 40, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) };
    private bool _loaded;
    private string _api = "";

    public DayTab(PuzzleOptions options, DayStore store)
    {
        _options = options;
        _store = store;
        _startBox.Text = store.Current.Start ?? options.FirstMonday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _daysBox.Text = store.Current.Days > 0 ? store.Current.Days.ToString(CultureInfo.InvariantCulture) : "";

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
        bar.Children.Add(Toolbar.Button("Reload", ReloadOrShow));
        bar.Children.Add(Toolbar.Label("Start"));
        bar.Children.Add(_startBox);
        bar.Children.Add(Toolbar.Label("Days"));
        bar.Children.Add(_daysBox);
        bar.Children.Add(Toolbar.Button("Fit", _view.Fit));
        bar.Children.Add(Toolbar.Button("Arrange", AskArrange));
        bar.Children.Add(Toolbar.Button("Clear groups", ClearGroups));
        bar.Children.Add(_status);

        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(_view);

        _view.Changed += OnChanged;
        KeyDown += OnKeyDown;
    }

    public DayView View => _view;

    public DayBoard Board => _view.Board;

    public string Api
    {
        get => _api;
        set
        {
            _api = value;
            UpdateStatus();
        }
    }

    public void EnsureLoaded()
    {
        if (!_loaded) Reload();
    }

    public void Reload()
    {
        var start = _startBox.Text.Trim().Length > 0 ? PuzzleOptions.ParseDate(_startBox.Text.Trim()) : _options.FirstMonday;
        int days = _daysBox.Text.Trim().Length > 0 ? int.Parse(_daysBox.Text.Trim(), CultureInfo.InvariantCulture) : 0;
        var shapes = DayCards.Load(_options, start, days);
        if (_loaded) SaveCards();
        var groups = _loaded ? _view.Board.Export() : _store.Current.Groups;
        var board = new DayBoard(shapes);
        board.Import(groups);
        var mirrored = _store.Current.Mirrored.ToHashSet(StringComparer.Ordinal);
        foreach (var card in board.Cards) card.Mirrored = mirrored.Contains(card.Id);
        var missing = board.ApplyPositions(_store.Current.Positions);
        if (missing.Count == board.Cards.Count) board.Arrange();
        else if (missing.Count > 0) board.PlaceLoose(missing);
        _store.Current.Start = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _store.Current.Days = days;
        _loaded = true;
        _view.SetBoard(board);
    }

    public void Arrange()
    {
        Board.Arrange();
        _view.Refresh();
        _view.Fit();
    }

    private void SaveCards()
    {
        var mirrored = _store.Current.Mirrored.ToHashSet(StringComparer.Ordinal);
        foreach (var card in Board.Cards)
        {
            _store.Current.Positions[card.Id] = new[] { Math.Round(card.X, 1), Math.Round(card.Y, 1) };
            if (card.Mirrored) mirrored.Add(card.Id);
            else mirrored.Remove(card.Id);
        }
        _store.Current.Mirrored = mirrored.Order(StringComparer.Ordinal).ToList();
    }

    private void AskArrange()
    {
        var answer = MessageBox.Show(Window.GetWindow(this),
            "Lay out all cards again (groups as blocks, other cards below)? Your card positions and frame sizes will be lost.",
            Caption, MessageBoxButton.OKCancel);
        if (answer == MessageBoxResult.OK) Arrange();
    }

    private void ReloadOrShow()
    {
        Cursor = Cursors.Wait;
        try
        {
            Reload();
            _view.Focus();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Caption);
        }
        finally
        {
            Cursor = null;
        }
    }

    private void ClearGroups()
    {
        if (Board.Groups.Count == 0) return;
        var answer = MessageBox.Show(Window.GetWindow(this), $"Remove all {Board.Groups.Count} groups?", Caption,
            MessageBoxButton.OKCancel);
        if (answer != MessageBoxResult.OK) return;
        Board.Clear();
        _view.Refresh();
    }

    private void OnChanged()
    {
        if (_loaded)
        {
            _store.Current.Groups = Board.Export();
            SaveCards();
            _store.Save();
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var board = _view.Board;
        int loose = board.Loose.Count();
        int mirrored = board.Cards.Count(c => c.Mirrored);
        string text = $"{board.Cards.Count} cards, {board.Groups.Count} groups, {loose} not grouped, {mirrored} mirrored";
        if (_api.Length > 0) text += $"   API {_api}";
        _status.Text = text;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_startBox.IsKeyboardFocusWithin || _daysBox.IsKeyboardFocusWithin) return;
        if (e.Key == Key.F)
        {
            _view.Fit();
            e.Handled = true;
        }
    }
}
