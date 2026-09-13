using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace FXViewer.Game;

public sealed record GamePanelPair(string Symbol, int ColorArgb, bool Mirror, string Buy, string Sell,
    string Spread);

public sealed record GamePanelRow(string Symbol, int ColorArgb, int Id, bool Buy, bool Pending,
    string Price, string Stop, string Take, string Pips, double PipsValue, string Tip);

public sealed record GamePanelComment(string Time, string Symbol, int ColorArgb, string Title);

public sealed record GamePanelData(string Time, IReadOnlyList<GamePanelPair> Pairs,
    IReadOnlyList<GamePanelRow> Positions, IReadOnlyList<GamePanelRow> Orders,
    string Closed, string Open, string Total, double TotalValue, string Stats,
    double StopPips, double TakePips)
{
    public string Title { get; init; } = "Play";
    public string Elapsed { get; init; } = "";
    public string Day { get; init; } = "";
    public string Comment { get; init; } = "";
    public bool NotesOpen { get; init; }
    public bool FutureOpen { get; init; }
    public bool Replay { get; init; }
    public string OwnTime { get; init; } = "";
    public bool Locked { get; init; }
    public IReadOnlyList<GamePanelComment> Comments { get; init; } = Array.Empty<GamePanelComment>();
}

public sealed class GamePanelView : Border
{
    private const double PanelWidth = 384;
    private const double RowNameWidth = 52;
    private static readonly TimeSpan CommentSaveDelay = TimeSpan.FromMilliseconds(800);

    private static readonly Brush LineBrush = Frozen(0xC8, 0xC8, 0xC8);
    private static readonly Brush MutedBrush = Frozen(0x70, 0x70, 0x70);
    private static readonly Brush BuyBrush = Frozen(0x15, 0x65, 0xC0);
    private static readonly Brush SellBrush = Frozen(0x8E, 0x24, 0xAA);
    private static readonly Brush WinBrush = Frozen(0x2E, 0x7D, 0x32);
    private static readonly Brush LossBrush = Frozen(0xD3, 0x2F, 0x2F);
    private static readonly FontFamily Mono = new("Consolas");

    private readonly TextBlock _time = new()
    {
        FontSize = 12,
        FontFamily = Mono,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBlock _title = new()
    {
        Text = "Play",
        FontSize = 12,
        FontWeight = FontWeights.Bold,
        Foreground = MutedBrush,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBlock _elapsed = new()
    {
        FontSize = 12,
        FontFamily = Mono,
        Foreground = MutedBrush,
        Margin = new Thickness(10, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Real time since the game started",
    };

    private readonly TextBlock _replayMark = new()
    {
        Text = "replay",
        FontSize = 11,
        FontStyle = FontStyles.Italic,
        Foreground = MutedBrush,
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
        ToolTip = "This run is a replay: it is not written to the game log, does not count for the random "
            + "pick and is not in Game stats",
    };

    private readonly TextBlock _futureMark = new()
    {
        Text = "future",
        FontSize = 11,
        FontStyle = FontStyles.Italic,
        Foreground = SellBrush,
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
        ToolTip = "The history after the play minute is shown. The dashed line on the chart is the play "
            + "minute; the game itself still runs from it.",
    };

    private readonly TextBlock _ownTime = new()
    {
        FontSize = 11,
        FontFamily = Mono,
        Foreground = MutedBrush,
        Margin = new Thickness(12, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Your own time at the game: it runs while this panel is open, you are at the keyboard and "
            + "FXViewer is in front. A minute without input, or in the background, stops it.",
    };

    private readonly Button _replayButton;
    private readonly Button _futureButton;
    private readonly Button _notesButton;
    private readonly List<Button> _stepButtons = new();
    private readonly StackPanel _notes = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _notesTitle = new()
    {
        Text = "Comment of this day",
        FontSize = 11,
        Foreground = MutedBrush,
        Margin = new Thickness(0, 0, 0, 2),
    };

    private readonly TextBox _commentBox = new()
    {
        FontSize = 12,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 70,
        MaxHeight = 220,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(2),
        Cursor = Cursors.IBeam,
        ToolTip = "Comment of this day, one for all games of the day",
    };

    private readonly StackPanel _commentList = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _commentListTitle = new()
    {
        Text = "Comments of this day",
        FontSize = 11,
        Foreground = MutedBrush,
        Margin = new Thickness(0, 0, 0, 2),
    };

    private readonly DispatcherTimer _commentTimer = new() { Interval = CommentSaveDelay };
    private string _commentDay = "";
    private bool _settingComment;

    private readonly Grid _pairs = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _stopBox = PipsBox("Stop loss in pips, 0 = none");
    private readonly TextBox _takeBox = PipsBox("Take profit in pips, 0 = none");
    private readonly StackPanel _rows = new();
    private readonly TextBlock _summary = new() { FontSize = 12, FontFamily = Mono };
    private readonly TextBlock _stats = new()
    {
        FontSize = 11,
        Foreground = MutedBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    private double _stopPips = GameState.DefaultPips;
    private double _takePips = GameState.DefaultPips;

    public event Action<string, bool>? MarketRequested;
    public event Action<int>? StepRequested;
    public event Action<string, int>? CloseRequested;
    public event Action<string, int>? CancelRequested;
    public event Action<double, double>? PipsChanged;
    public event Action? StopPlayRequested;
    public event Action? DragStarted;
    public event Action? NotesToggled;
    public event Action? FutureToggled;
    public event Action? ReplayRequested;
    public event Action<string, string>? CommentChanged;

    public GamePanelView()
    {
        Background = Brushes.White;
        BorderBrush = LineBrush;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(8, 6, 8, 6);
        Width = PanelWidth;
        Focusable = false;
        Cursor = Cursors.SizeAll;
        PreviewMouseLeftButtonDown += OnDragStart;
        _pairs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pairs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pairs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pairs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_elapsed, 1);
        titleRow.Children.Add(_title);
        titleRow.Children.Add(_elapsed);
        header.Children.Add(titleRow);
        _replayButton = SmallButton("Replay",
            "Play this day again from the start. A replay is not written to the game log: it does not count "
            + "for the random pick and is not in Game stats. The draft drawing is kept.",
            () => ReplayRequested?.Invoke());
        _replayButton.Margin = new Thickness(0, 0, 6, 0);
        _replayButton.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_replayButton, 1);
        header.Children.Add(_replayButton);
        _futureButton = SmallButton("Future",
            "Show or hide the history after the play minute, the rest of the day and everything after it. "
            + "The game does not end: you keep stepping and trading from the play minute, marked on the "
            + "chart by a dashed line while the future is shown.",
            () => FutureToggled?.Invoke());
        _futureButton.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(_futureButton, 2);
        header.Children.Add(_futureButton);
        _notesButton = SmallButton("Notes",
            "Show or hide the comment and the day drawing of this day. They are hidden while you play.",
            () => NotesToggled?.Invoke());
        _notesButton.Margin = new Thickness(0, 0, 6, 0);
        _notesButton.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_notesButton, 3);
        header.Children.Add(_notesButton);
        var close = SmallButton("✕", "Stop the game (same as Play in the chart menu)",
            () => StopPlayRequested?.Invoke());
        Grid.SetColumn(close, 4);
        header.Children.Add(close);
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _stepButtons.Add(SmallButton("◀", "O - one minute back", () => StepRequested?.Invoke(-1)));
        _stepButtons.Add(SmallButton("▶", "P - one minute forward", () => StepRequested?.Invoke(1)));
        foreach (var step in _stepButtons) timeRow.Children.Add(step);
        timeRow.Children.Add(new Border { Width = 6 });
        timeRow.Children.Add(_time);
        timeRow.Children.Add(_replayMark);
        timeRow.Children.Add(_futureMark);
        timeRow.Children.Add(_ownTime);
        var pipsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        pipsRow.Children.Add(Label("SL", 0));
        pipsRow.Children.Add(_stopBox);
        pipsRow.Children.Add(Label("TP", 8));
        pipsRow.Children.Add(_takeBox);
        pipsRow.Children.Add(Label("pips", 4));
        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(timeRow);
        body.Children.Add(_pairs);
        body.Children.Add(pipsRow);
        body.Children.Add(new Border
        {
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 6, 0, 4),
        });
        body.Children.Add(new ScrollViewer
        {
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _rows,
        });
        body.Children.Add(new Border
        {
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 4, 0, 4),
        });
        body.Children.Add(_summary);
        body.Children.Add(_stats);
        body.Children.Add(new TextBlock
        {
            Text = "P - next minute, O - back. Right click the chart for orders.",
            FontSize = 11,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        _notes.Children.Add(new Border
        {
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 6, 0, 4),
        });
        _notes.Children.Add(_notesTitle);
        _notes.Children.Add(_commentBox);
        _commentList.Children.Add(_commentListTitle);
        _notes.Children.Add(_commentList);
        _notes.Children.Add(new TextBlock
        {
            Text = "Day drawing: right click the chart -> Draw line - <pair> · day.",
            FontSize = 11,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        });
        body.Children.Add(_notes);
        Child = body;
        _stopBox.LostFocus += (_, _) => CommitPips();
        _takeBox.LostFocus += (_, _) => CommitPips();
        _stopBox.KeyDown += PipsKeyDown;
        _takeBox.KeyDown += PipsKeyDown;
        _commentTimer.Tick += (_, _) => FlushComment();
        _commentBox.TextChanged += (_, _) =>
        {
            if (_settingComment) return;
            _commentTimer.Stop();
            _commentTimer.Start();
        };
        _commentBox.LostKeyboardFocus += (_, _) => FlushComment();
    }

    public void SetElapsed(string text) => _elapsed.Text = text;

    public void SetOwnTime(string text) => _ownTime.Text = text;

    public void FlushComment()
    {
        if (!_commentTimer.IsEnabled) return;
        _commentTimer.Stop();
        if (_commentDay.Length > 0) CommentChanged?.Invoke(_commentDay, _commentBox.Text);
    }

    private void ShowNotes(GamePanelData data)
    {
        if (data.Day != _commentDay)
        {
            FlushComment();
            _commentDay = data.Day;
            SetCommentText(data.Comment);
        }
        else if (!_commentTimer.IsEnabled && !_commentBox.IsKeyboardFocusWithin && _commentBox.Text != data.Comment)
        {
            SetCommentText(data.Comment);
        }
        _notesButton.Visibility = data.Day.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _notesButton.FontWeight = data.NotesOpen ? FontWeights.Bold : FontWeights.Normal;
        _notes.Visibility = data.Day.Length > 0 && data.NotesOpen ? Visibility.Visible : Visibility.Collapsed;
        BuildComments(data.Comments);
    }

    private void BuildComments(IReadOnlyList<GamePanelComment> comments)
    {
        _commentList.Children.RemoveRange(1, _commentList.Children.Count - 1);
        _commentList.Visibility = comments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var comment in comments)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = comment.Time,
                FontSize = 12,
                FontFamily = Mono,
                Foreground = MutedBrush,
                Margin = new Thickness(0, 0, 8, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = comment.Symbol,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = BrushOf(comment.ColorArgb),
                Width = RowNameWidth,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = comment.Title,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = comment.Title.Length > 0 ? comment.Title : null,
            });
            _commentList.Children.Add(row);
        }
    }

    private void SetCommentText(string text)
    {
        _settingComment = true;
        _commentBox.Text = text;
        _settingComment = false;
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (IsControl(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        DragStarted?.Invoke();
    }

    private bool IsControl(DependencyObject? source)
    {
        while (source != null && !ReferenceEquals(source, this))
        {
            if (source is ButtonBase or TextBoxBase or ScrollBar) return true;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : null;
        }
        return false;
    }

    public void Update(GamePanelData data)
    {
        _title.Text = data.Title;
        _elapsed.Text = data.Elapsed;
        foreach (var step in _stepButtons) step.IsEnabled = !data.Locked;
        _replayButton.Visibility = data.Day.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _replayMark.Visibility = data.Replay ? Visibility.Visible : Visibility.Collapsed;
        _futureButton.FontWeight = data.FutureOpen ? FontWeights.Bold : FontWeights.Normal;
        _futureMark.Visibility = data.FutureOpen ? Visibility.Visible : Visibility.Collapsed;
        _ownTime.Text = data.OwnTime;
        ShowNotes(data);
        _time.Text = data.Time;
        if (!_stopBox.IsFocused)
        {
            _stopPips = data.StopPips;
            _stopBox.Text = FormatPips(data.StopPips);
        }
        if (!_takeBox.IsFocused)
        {
            _takePips = data.TakePips;
            _takeBox.Text = FormatPips(data.TakePips);
        }
        BuildPairs(data.Pairs, data.Locked);
        _rows.Children.Clear();
        AddSection($"Positions {data.Positions.Count}", data.Positions, false);
        AddSection($"Orders {data.Orders.Count}", data.Orders, true);
        if (data.Positions.Count == 0 && data.Orders.Count == 0)
            _rows.Children.Add(new TextBlock
            {
                Text = "Nothing open",
                FontSize = 11,
                Foreground = MutedBrush,
                Margin = new Thickness(0, 2, 0, 2),
            });
        _summary.Inlines.Clear();
        _summary.Inlines.Add(new System.Windows.Documents.Run($"closed {data.Closed}   open {data.Open}   "));
        _summary.Inlines.Add(new System.Windows.Documents.Run($"total {data.Total}")
        {
            Foreground = data.TotalValue < 0 ? LossBrush : WinBrush,
            FontWeight = FontWeights.Bold,
        });
        _stats.Text = data.Stats;
    }

    private void BuildPairs(IReadOnlyList<GamePanelPair> pairs, bool locked)
    {
        _pairs.Children.Clear();
        _pairs.RowDefinitions.Clear();
        if (pairs.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No visible pairs on the chart",
                FontSize = 11,
                Foreground = MutedBrush,
            };
            Grid.SetColumnSpan(empty, 4);
            _pairs.Children.Add(empty);
            return;
        }
        for (int i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            _pairs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Place(new TextBlock
            {
                Text = pair.Symbol,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = BrushOf(pair.ColorArgb),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            }, i, 0);
            int column = 1;
            foreach (bool buy in GameTradeButton.Sides(pair.Mirror))
            {
                string price = buy ? pair.Buy : pair.Sell;
                string symbol = pair.Symbol;
                var button = GameTradeButton.Create(buy, pair.Mirror,
                    $"{(buy ? "Buy" : "Sell")}: {(price.Length > 0 ? price : "-")}",
                    buy
                        ? $"Buy {symbol} at market: fills at the worst ask of this minute"
                        : $"Sell {symbol} at market: fills at the worst bid of this minute",
                    12, new Thickness(7, 2, 7, 2),
                    () =>
                    {
                        CommitPips();
                        MarketRequested?.Invoke(symbol, buy);
                    });
                button.IsEnabled = price.Length > 0 && !locked;
                button.Margin = new Thickness(0, 2, 4, 2);
                Place(button, i, column++);
            }
            Place(new TextBlock
            {
                Text = pair.Spread.Length > 0 ? $"sp {pair.Spread}" : "",
                FontSize = 11,
                Foreground = MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Spread of this minute, pips",
            }, i, 3);
        }
    }

    private void Place(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        _pairs.Children.Add(element);
    }

    private void AddSection(string title, IReadOnlyList<GamePanelRow> rows, bool pending)
    {
        if (rows.Count == 0) return;
        _rows.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 4, 0, 2),
        });
        foreach (var row in rows) _rows.Children.Add(BuildRow(row, pending));
    }

    private Grid BuildRow(GamePanelRow row, bool pending)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = row.Symbol,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = BrushOf(row.ColorArgb),
            Width = RowNameWidth,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var side = new TextBlock
        {
            Text = row.Buy ? "B" : "S",
            FontSize = 12,
            FontFamily = Mono,
            FontWeight = FontWeights.Bold,
            Foreground = row.Buy ? BuyBrush : SellBrush,
            Width = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);
        var text = new TextBlock
        {
            Text = row.Stop.Length + row.Take.Length > 0
                ? $"{row.Price}  {row.Stop}/{row.Take}"
                : row.Price,
            FontSize = 12,
            FontFamily = Mono,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = row.Tip,
        };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        var pips = new TextBlock
        {
            Text = row.Pips,
            FontSize = 12,
            FontFamily = Mono,
            Foreground = row.PipsValue < 0 ? LossBrush : WinBrush,
            Margin = new Thickness(6, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(pips, 3);
        grid.Children.Add(pips);
        string symbol = row.Symbol;
        int id = row.Id;
        var button = SmallButton("✕", pending ? "Cancel the order" : "Close at market",
            () =>
            {
                if (pending) CancelRequested?.Invoke(symbol, id);
                else CloseRequested?.Invoke(symbol, id);
            });
        Grid.SetColumn(button, 4);
        grid.Children.Add(button);
        return grid;
    }

    private void PipsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitPips();
            Keyboard.ClearFocus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _stopBox.Text = FormatPips(_stopPips);
            _takeBox.Text = FormatPips(_takePips);
            Keyboard.ClearFocus();
        }
    }

    private void CommitPips()
    {
        double stop = ParsePips(_stopBox.Text, _stopPips);
        double take = ParsePips(_takeBox.Text, _takePips);
        _stopBox.Text = FormatPips(stop);
        _takeBox.Text = FormatPips(take);
        if (stop == _stopPips && take == _takePips) return;
        _stopPips = stop;
        _takePips = take;
        PipsChanged?.Invoke(stop, take);
    }

    public static double ParsePips(string text, double fallback) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double value)
            ? GameState.ClampPips(value)
            : fallback;

    public static string FormatPips(double pips) =>
        pips.ToString("0.##", CultureInfo.InvariantCulture);

    public static Brush BrushOf(int argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    private static TextBox PipsBox(string tip) => new()
    {
        Width = 38,
        FontSize = 12,
        FontFamily = Mono,
        TextAlignment = TextAlignment.Right,
        Padding = new Thickness(2, 1, 2, 1),
        VerticalAlignment = VerticalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = Cursors.IBeam,
        ToolTip = tip,
    };

    private static TextBlock Label(string text, double left) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = MutedBrush,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(left, 0, 3, 0),
    };

    private static Button SmallButton(string text, string tip, Action action)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 11,
            Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = tip,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
