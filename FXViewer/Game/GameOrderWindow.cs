using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FXViewer.Game;

public sealed record GameOrderPair(string Symbol, int ColorArgb, bool Mirror, string Level);

public sealed class GameOrderWindow : Window
{
    private static readonly FontFamily Mono = new("Consolas");
    private static readonly Brush MutedBrush = Brushes.Gray;

    private readonly List<(GameOrderPair Pair, RadioButton Radio, TextBox Level)> _rows = new();
    private readonly TextBox _stopBox = NumberBox(50);
    private readonly TextBox _takeBox = NumberBox(50);
    private readonly StackPanel _sides = new() { Orientation = Orientation.Horizontal };
    private readonly double _defaultStop;
    private readonly double _defaultTake;
    private int _selected;

    public string Symbol { get; private set; } = "";
    public bool Buy { get; private set; }
    public double Level { get; private set; }
    public double StopPips { get; private set; }
    public double TakePips { get; private set; }

    public GameOrderWindow(IReadOnlyList<GameOrderPair> pairs, string? selected, double stopPips,
        double takePips)
    {
        Title = "Order";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        _defaultStop = stopPips;
        _defaultTake = takePips;
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddRow(grid, Muted("Pair"), Muted("Level"));
        for (int i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            int index = i;
            var radio = new RadioButton
            {
                GroupName = "pair",
                Content = new TextBlock
                {
                    Text = pair.Symbol,
                    FontWeight = FontWeights.Bold,
                    Foreground = GamePanelView.BrushOf(pair.ColorArgb),
                },
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            var level = NumberBox(90);
            level.Text = pair.Level;
            level.ToolTip = $"{pair.Symbol} price under the cursor";
            radio.Checked += (_, _) => Select(index);
            radio.Click += (_, _) => FocusLevel(index);
            level.GotKeyboardFocus += (_, _) => radio.IsChecked = true;
            _rows.Add((pair, radio, level));
            AddRow(grid, radio, level);
        }
        _stopBox.Text = GamePanelView.FormatPips(stopPips);
        _takeBox.Text = GamePanelView.FormatPips(takePips);
        AddRow(grid, Muted("Stop loss"), WithHint(_stopBox), 10);
        AddRow(grid, Muted("Take profit"), WithHint(_takeBox));
        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button
        {
            Content = "Cancel",
            Width = 90,
            Height = 28,
            IsCancel = true,
            Margin = new Thickness(16, 0, 0, 0),
        };
        cancel.Click += (_, _) => Close();
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(_sides, Dock.Left);
        buttons.Children.Add(_sides);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, grid.RowDefinitions.Count - 1);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
        int start = Math.Max(0, _rows.FindIndex(r => r.Pair.Symbol == selected));
        if (_rows.Count > 0) _rows[start].Radio.IsChecked = true;
        Loaded += (_, _) => FocusLevel(_selected);
    }

    private void Select(int index)
    {
        _selected = index;
        var pair = _rows[index].Pair;
        _sides.Children.Clear();
        foreach (bool buy in GameTradeButton.Sides(pair.Mirror))
        {
            var button = GameTradeButton.Create(buy, pair.Mirror, buy ? "Buy" : "Sell",
                buy
                    ? $"Place a buy order for {pair.Symbol} at the level"
                    : $"Place a sell order for {pair.Symbol} at the level",
                12, new Thickness(14, 0, 14, 0), () => Submit(buy));
            button.MinWidth = 90;
            button.Height = 28;
            button.Margin = new Thickness(0, 0, 8, 0);
            _sides.Children.Add(button);
        }
    }

    private void FocusLevel(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        var box = _rows[index].Level;
        box.Focus();
        box.SelectAll();
    }

    private void Submit(bool buy)
    {
        if (_rows.Count == 0) return;
        var row = _rows[_selected];
        if (!double.TryParse(row.Level.Text.Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double level)
            || !double.IsFinite(level) || level <= 0)
        {
            MessageBox.Show(this, $"The {row.Pair.Symbol} level must be a positive number.",
                "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
            FocusLevel(_selected);
            return;
        }
        Symbol = row.Pair.Symbol;
        Buy = buy;
        Level = level;
        StopPips = GamePanelView.ParsePips(_stopBox.Text, _defaultStop);
        TakePips = GamePanelView.ParsePips(_takeBox.Text, _defaultTake);
        DialogResult = true;
    }

    private static void AddRow(Grid grid, FrameworkElement first, FrameworkElement second, double top = 0)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int row = grid.RowDefinitions.Count - 1;
        foreach (var (cell, column) in new[] { (first, 0), (second, 1) })
        {
            cell.Margin = new Thickness(cell.Margin.Left, top + 2, cell.Margin.Right, 2);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
    }

    private static StackPanel WithHint(TextBox box)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = "pips, 0 = none",
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        });
        return panel;
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        Foreground = MutedBrush,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 12, 0),
    };

    private static TextBox NumberBox(double width) => new()
    {
        Width = width,
        Height = 24,
        FontFamily = Mono,
        FontSize = 13,
        TextAlignment = TextAlignment.Right,
        VerticalContentAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
}
