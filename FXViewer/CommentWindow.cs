using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FXViewer;

public sealed record CommentPairLevel(string Symbol, int ColorArgb, string Level);

public sealed class CommentWindow : Window
{
    public const string MinuteFormat = "yyyy-MM-dd HH:mm";

    private static readonly FontFamily Mono = new("Consolas");
    private static readonly Brush MutedBrush = Brushes.Gray;

    private readonly ComboBox _pairBox = new()
    {
        Width = 130,
        Height = 24,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private readonly TextBox _minuteBox = TextBoxOf(130);
    private readonly TextBox _levelBox = TextBoxOf(130);

    private readonly TextBox _titleBox = new()
    {
        Width = 320,
        Height = 24,
        FontSize = 13,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private readonly TextBox _descriptionBox = new()
    {
        Width = 320,
        Height = 110,
        FontSize = 13,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private readonly Dictionary<string, string> _cursorLevels = new(StringComparer.Ordinal);
    private bool _ready;

    public string Pair { get; private set; } = "";
    public long MinuteUnix { get; private set; }
    public double LevelPips { get; private set; }
    public string TitleText { get; private set; } = "";
    public string Description { get; private set; } = "";
    public bool Deleted { get; private set; }

    public CommentWindow(bool isNew, IReadOnlyList<CommentPairLevel> pairs, string pair,
        long minuteUnix, string level, string title, string description)
    {
        Title = isNew ? "Add comment" : "Comment";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        foreach (var p in pairs)
        {
            _cursorLevels[p.Symbol] = p.Level;
            _pairBox.Items.Add(new ComboBoxItem
            {
                Content = new TextBlock
                {
                    Text = p.Symbol,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushOf(p.ColorArgb),
                },
                Tag = p.Symbol,
            });
        }
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddRow(grid, Muted("Pair"), _pairBox);
        AddRow(grid, Muted("Minute"), WithHint(_minuteBox, "UTC, " + MinuteFormat));
        AddRow(grid, Muted("Level"), WithHint(_levelBox, "pips"));
        AddRow(grid, Muted("Title"), _titleBox);
        AddRow(grid, Muted("Description", true), _descriptionBox);
        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button
        {
            Content = "Cancel",
            Width = 90,
            Height = 28,
            IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0),
        };
        cancel.Click += (_, _) => Close();
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(cancel);
        var ok = new Button { Content = "OK", Width = 90, Height = 28, IsDefault = true };
        ok.Click += (_, _) => Submit();
        DockPanel.SetDock(ok, Dock.Right);
        buttons.Children.Add(ok);
        if (!isNew)
        {
            var delete = new Button { Content = "Delete", Width = 90, Height = 28 };
            delete.Click += (_, _) => Remove();
            DockPanel.SetDock(delete, Dock.Left);
            buttons.Children.Add(delete);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, grid.RowDefinitions.Count - 1);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
        SelectPair(pair);
        _minuteBox.Text = DateTimeOffset.FromUnixTimeSeconds(minuteUnix).UtcDateTime
            .ToString(MinuteFormat, CultureInfo.InvariantCulture);
        _levelBox.Text = level;
        _titleBox.Text = title;
        _descriptionBox.Text = description;
        _ready = true;
        _pairBox.SelectionChanged += (_, _) => PairChanged();
        Loaded += (_, _) =>
        {
            _titleBox.Focus();
            _titleBox.SelectAll();
        };
    }

    private void SelectPair(string pair)
    {
        for (int i = 0; i < _pairBox.Items.Count; i++)
            if (((ComboBoxItem)_pairBox.Items[i]!).Tag as string == pair)
            {
                _pairBox.SelectedIndex = i;
                return;
            }
        if (_pairBox.Items.Count > 0) _pairBox.SelectedIndex = 0;
    }

    private string SelectedPair() => _pairBox.SelectedItem is ComboBoxItem item
        ? item.Tag as string ?? ""
        : "";

    private void PairChanged()
    {
        if (!_ready) return;
        if (_cursorLevels.TryGetValue(SelectedPair(), out var level)) _levelBox.Text = level;
    }

    private void Submit()
    {
        string pair = SelectedPair();
        if (pair.Length == 0)
        {
            Warn("Pick a pair for the comment.");
            return;
        }
        if (!DateTime.TryParseExact(_minuteBox.Text.Trim(), MinuteFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var minute))
        {
            Warn($"The minute must be UTC as {MinuteFormat}, for example 2026-09-11 14:30.");
            _minuteBox.Focus();
            _minuteBox.SelectAll();
            return;
        }
        if (!double.TryParse(_levelBox.Text.Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double pips) || !double.IsFinite(pips))
        {
            Warn("The level must be a number in pips, for example 11712.3.");
            _levelBox.Focus();
            _levelBox.SelectAll();
            return;
        }
        Pair = pair;
        MinuteUnix = new DateTimeOffset(minute, TimeSpan.Zero).ToUnixTimeSeconds();
        MinuteUnix -= MinuteUnix % 60;
        LevelPips = pips;
        TitleText = _titleBox.Text.Trim();
        Description = _descriptionBox.Text.TrimEnd();
        DialogResult = true;
    }

    private void Remove()
    {
        if (MessageBox.Show(this, "Delete this comment?", "Comment",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Deleted = true;
        DialogResult = true;
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static void AddRow(Grid grid, FrameworkElement first, FrameworkElement second)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int row = grid.RowDefinitions.Count - 1;
        foreach (var (cell, column) in new[] { (first, 0), (second, 1) })
        {
            cell.Margin = new Thickness(cell.Margin.Left, 3, cell.Margin.Right, 3);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
    }

    private static StackPanel WithHint(TextBox box, string hint)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = hint,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        });
        return panel;
    }

    private static TextBlock Muted(string text, bool top = false) => new()
    {
        Text = text,
        Foreground = MutedBrush,
        VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center,
        Margin = new Thickness(0, top ? 4 : 0, 12, 0),
    };

    private static TextBox TextBoxOf(double width) => new()
    {
        Width = width,
        Height = 24,
        FontFamily = Mono,
        FontSize = 13,
        VerticalContentAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static Brush BrushOf(int argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }
}
