using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FXViewer.Game;

public sealed class GameAwayWindow : Window
{
    private static readonly Brush MutedBrush = Brushes.Gray;
    private static readonly FontFamily Mono = new("Consolas");

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _paused;
    private readonly DateTimeOffset _since;

    public GameAwayWindow(string reason, DateTimeOffset since, string ownTime)
    {
        _since = since;
        Title = "Play";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ShowActivated = false;
        UseLayoutRounding = true;
        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        body.Children.Add(new TextBlock
        {
            Text = "Still here?",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
        });
        body.Children.Add(new TextBlock
        {
            Text = "Your time at the game stopped because " + reason + ".",
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            Margin = new Thickness(0, 6, 0, 0),
        });
        var facts = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddFact(facts, "Stopped at", since.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        _paused = AddFact(facts, "Paused for", PausedText());
        AddFact(facts, "Your time", ownTime);
        body.Children.Add(facts);
        var back = new Button
        {
            Content = "I am back",
            Width = 110,
            Height = 28,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        back.Click += (_, _) => Close();
        body.Children.Add(back);
        Content = body;
        _timer.Tick += (_, _) => _paused.Text = PausedText();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private string PausedText()
    {
        var span = DateTimeOffset.Now - _since;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    private static TextBlock AddFact(Grid grid, string label, string value)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int row = grid.RowDefinitions.Count - 1;
        var name = new TextBlock
        {
            Text = label,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 2, 16, 2),
        };
        var text = new TextBlock
        {
            Text = value,
            FontFamily = Mono,
            Margin = new Thickness(0, 2, 0, 2),
        };
        Grid.SetRow(name, row);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        grid.Children.Add(name);
        grid.Children.Add(text);
        return text;
    }
}
