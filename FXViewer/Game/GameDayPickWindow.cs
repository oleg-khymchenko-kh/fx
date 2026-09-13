using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FXViewer.Game;

public sealed class GameDayPickWindow : Window
{
    private static readonly Brush MutedBrush = Brushes.Gray;

    public GameDayPickWindow(DateOnly from, DateOnly to, int days, GameDayPick pick)
    {
        Title = "Play random day";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        var grid = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddRow(grid, "Period from", DayText(from), false);
        AddRow(grid, "Period to", DayText(to), false);
        AddRow(grid, "Trading days", pick.Plays == 0
            ? $"{days} in the period, {pick.Pool} never played"
            : $"{days} in the period, {pick.Pool} played {pick.Plays} time(s) (the fewest)", false);
        AddRow(grid, "Picked day", DayText(pick.Day), true);
        var ok = new Button
        {
            Content = "OK",
            Width = 90,
            Height = 28,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        ok.Click += (_, _) => Close();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(ok, grid.RowDefinitions.Count - 1);
        Grid.SetColumnSpan(ok, 2);
        grid.Children.Add(ok);
        Content = grid;
        Loaded += (_, _) => ok.Focus();
    }

    private static string DayText(DateOnly day) =>
        day.ToString("dd-MMM-yy ddd", CultureInfo.InvariantCulture);

    private static void AddRow(Grid grid, string label, string value, bool strong)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int row = grid.RowDefinitions.Count - 1;
        var name = new TextBlock
        {
            Text = label,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 3, 16, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = new TextBlock
        {
            Text = value,
            FontSize = strong ? 16 : 13,
            FontWeight = strong ? FontWeights.Bold : FontWeights.Normal,
            Margin = new Thickness(0, 3, 0, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(name, row);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        grid.Children.Add(name);
        grid.Children.Add(text);
    }
}
