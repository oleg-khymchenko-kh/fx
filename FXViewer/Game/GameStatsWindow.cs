using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FXViewer.Game;

public sealed class GameStatsWindow : Window
{
    private const double BarWidth = 70;

    private static readonly Brush MutedBrush = Frozen(0x70, 0x70, 0x70);
    private static readonly Brush LineBrush = Frozen(0xC8, 0xC8, 0xC8);
    private static readonly Brush WinBrush = Frozen(0x2E, 0x7D, 0x32);
    private static readonly Brush LossBrush = Frozen(0xD3, 0x2F, 0x2F);
    private static readonly Brush BarBrush = Frozen(0x15, 0x65, 0xC0);
    private static readonly Brush TrackBrush = Frozen(0xE6, 0xE6, 0xE6);
    private static readonly FontFamily Mono = new("Consolas");

    private readonly StackPanel _body = new() { Margin = new Thickness(18, 4, 18, 14) };

    public GameStatsWindow(string title)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        Grid.SetIsSharedSizeScope(_body, true);
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _body,
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    public void Update(GameStats stats)
    {
        _body.Children.Clear();
        AddDays(stats);
        AddTime(stats);
        AddOwnTime(stats);
        AddTrades(stats);
        _body.Children.Add(Note("Only finished days are counted. A game closed with ✕ before the end of its " +
            "day is not in the log, and neither is its time."));
    }

    private void AddDays(GameStats s)
    {
        AddHeader("Days");
        var facts = Facts();
        AddFact(facts, "Period", $"{DayText(s.From)} .. {DayText(s.To)}");
        AddFact(facts, "Trading days", Count(s.TradingDays));
        AddFact(facts, "Played", $"{s.PlayedDays} of {s.TradingDays} ({Percent(s.PlayedDays, s.TradingDays)})");
        foreach (var c in s.PlayCounts)
            AddFact(facts, c.Plays == 0 ? "Never played" : $"Played {c.Plays} {Plural(c.Plays, "time", "times")}",
                $"{c.Days} {Plural(c.Days, "day", "days")}");
        _body.Children.Add(facts);
        if (s.Months.Count == 0) return;
        var table = Table("Month", "Days", "Played", "Games", "");
        foreach (var m in s.Months)
            AddRow(table,
                Cell(new DateTime(m.Year, m.Month, 1).ToString("MMM-yy", CultureInfo.InvariantCulture), false),
                Cell(Count(m.Days)),
                Cell(Count(m.Played)),
                Cell(Count(m.Games)),
                Bar(m.Days == 0 ? 0 : (double)m.Played / m.Days));
        _body.Children.Add(table);
    }

    private void AddTime(GameStats s)
    {
        AddHeader("Real time");
        if (s.Games == 0)
        {
            _body.Children.Add(Note("No finished games yet.", 2));
            return;
        }
        var facts = Facts();
        AddFact(facts, "Finished games", Count(s.Games));
        AddFact(facts, "Total", $"{Minutes(s.TotalSeconds)} min  ({Clock(s.TotalSeconds)})");
        AddFact(facts, "Average game", $"{Minutes(s.AverageSeconds)} min");
        AddFact(facts, "Median game", $"{Minutes(s.MedianSeconds)} min");
        AddFact(facts, "Fastest / slowest", $"{Minutes(s.FastestSeconds)} / {Minutes(s.SlowestSeconds)} min");
        if (s.NeverPlayed > 0)
            AddFact(facts, "Rest of the days",
                $"{s.NeverPlayed} never played x {Minutes(s.AverageSeconds)} min = {Hours(s.LeftSeconds)} h");
        _body.Children.Add(facts);
        var table = Table("Played on", "Games", "Minutes", "Your min", "Pips");
        foreach (var d in s.Dates)
            AddRow(table, Cell(DayText(d.Date), false), Cell(Count(d.Games)), Cell(Minutes(d.Seconds)),
                Cell(d.OwnSeconds > 0 ? Minutes(d.OwnSeconds) : "-"), PipsCell(d.Pips));
        _body.Children.Add(table);
    }

    private void AddOwnTime(GameStats s)
    {
        AddHeader("Your time");
        if (s.OwnSessions == 0)
        {
            _body.Children.Add(Note("No game panel session is recorded yet.", 2));
            return;
        }
        var facts = Facts();
        AddFact(facts, "Sessions", Count(s.OwnSessions));
        AddFact(facts, "Your time total", $"{Minutes(s.OwnSeconds)} min  ({Clock(s.OwnSeconds)})");
        AddFact(facts, "Average session", $"{Minutes(s.AverageSessionOwn)} min");
        AddFact(facts, "Panel open", $"{Minutes(s.PanelSeconds)} min, yours is " +
            Share(s.OwnSeconds, s.PanelSeconds));
        AddFact(facts, "Away pauses", Count(s.Pauses));
        if (s.OwnGames > 0)
            AddFact(facts, "Per finished game",
                $"{Minutes(s.AverageGameOwn)} min average, {Minutes(s.MedianGameOwn)} min median" +
                (s.OwnGames < s.Games ? $", of {s.OwnGames} {Plural(s.OwnGames, "game", "games")}" : ""));
        _body.Children.Add(facts);
        _body.Children.Add(Note("Your time runs while the game panel is open, you are at the keyboard and " +
            "FXViewer is in front. After a minute without input, or a minute in the background, it stops and " +
            "the \u201cStill here?\u201d popup waits for you. Replays count here too.", 6));
    }

    private void AddTrades(GameStats s)
    {
        AddHeader("Trades");
        if (s.Trades == 0)
        {
            _body.Children.Add(Note("No trades yet.", 2));
            return;
        }
        var facts = Facts();
        AddFact(facts, "Trades", $"{s.Trades}, {Ratio(s.Trades, s.Games)} per game");
        AddFact(facts, "Won", $"{s.Wins} of {s.Trades} ({Percent(s.Wins, s.Trades)})");
        AddFact(facts, "Pips total", PipsText(s.Pips), PipsBrush(s.Pips));
        AddFact(facts, "Pips per game", PipsText(s.Pips / s.Games), PipsBrush(s.Pips));
        AddFact(facts, "Pips per trade", PipsText(s.Pips / s.Trades), PipsBrush(s.Pips));
        AddFact(facts, "Games in profit / loss",
            $"{s.ProfitGames} / {s.LossGames}" + (s.FlatGames > 0 ? $", {s.FlatGames} at zero" : ""));
        if (s.Best != null) AddFact(facts, "Best game", DayResultText(s.Best), PipsBrush(s.Best.Pips));
        if (s.Worst != null) AddFact(facts, "Worst game", DayResultText(s.Worst), PipsBrush(s.Worst.Pips));
        AddFact(facts, "Hold time",
            $"{HoursMinutes(s.AverageHoldMinutes)} average, {HoursMinutes(s.MedianHoldMinutes)} median (game time)");
        _body.Children.Add(facts);
        var table = Table("", "Trades", "Win %", "Pips", "Per trade");
        AddGroups(table, "Closed by", s.Reasons);
        AddGroups(table, "Side", s.Sides);
        AddGroups(table, "Pair", s.Pairs);
        AddGroups(table, "Entry session", s.Sessions);
        AddGroups(table, "Play of the day", s.Plays);
        _body.Children.Add(table);
    }

    private static void AddGroups(Grid table, string title, IReadOnlyList<GameTradeGroup> groups)
    {
        if (groups.Count == 0) return;
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new TextBlock
        {
            Text = title,
            FontSize = 11,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 6, 0, 1),
        };
        Grid.SetRow(header, table.RowDefinitions.Count - 1);
        Grid.SetColumnSpan(header, table.ColumnDefinitions.Count);
        table.Children.Add(header);
        foreach (var g in groups)
            AddRow(table,
                Cell("  " + g.Name, false),
                Cell(Count(g.Trades)),
                Cell(Percent(g.Wins, g.Trades)),
                PipsCell(g.Pips),
                PipsCell(g.Trades == 0 ? 0 : g.Pips / g.Trades));
    }

    private void AddHeader(string text) => _body.Children.Add(new TextBlock
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 12, 0, 4),
    });

    private static TextBlock Note(string text, double top = 12) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = MutedBrush,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 420,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, top, 0, 0),
    };

    private static Grid Facts()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "FactLabel" });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    private static void AddFact(Grid grid, string label, string value, Brush? brush = null)
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
            Margin = new Thickness(0, 2, 0, 2),
        };
        if (brush != null) text.Foreground = brush;
        Grid.SetRow(name, row);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        grid.Children.Add(name);
        grid.Children.Add(text);
    }

    private static Grid Table(params string[] headers)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var _ in headers)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < headers.Length; i++)
        {
            var header = new TextBlock
            {
                Text = headers[i],
                FontSize = 11,
                Foreground = MutedBrush,
                TextAlignment = i == 0 ? TextAlignment.Left : TextAlignment.Right,
                Margin = new Thickness(i == 0 ? 0 : 14, 0, 0, 2),
            };
            Grid.SetColumn(header, i);
            grid.Children.Add(header);
        }
        var line = new Border
        {
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        Grid.SetColumnSpan(line, headers.Length);
        grid.Children.Add(line);
        return grid;
    }

    private static void AddRow(Grid table, params FrameworkElement[] cells)
    {
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int row = table.RowDefinitions.Count - 1;
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            if (i > 0) cell.Margin = new Thickness(14, 1, 0, 1);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, i);
            table.Children.Add(cell);
        }
    }

    private static TextBlock Cell(string text, bool number = true, Brush? brush = null)
    {
        var cell = new TextBlock
        {
            Text = text,
            TextAlignment = number ? TextAlignment.Right : TextAlignment.Left,
            Margin = new Thickness(0, 1, 0, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (number) cell.FontFamily = Mono;
        if (brush != null) cell.Foreground = brush;
        return cell;
    }

    private static TextBlock PipsCell(double pips) => Cell(PipsText(pips), true, PipsBrush(pips));

    private static FrameworkElement Bar(double share)
    {
        var track = new Border
        {
            Width = BarWidth,
            Height = 8,
            Background = TrackBrush,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"{Math.Round(share * 100)}% of the days played",
        };
        track.Child = new Border
        {
            Width = BarWidth * Math.Clamp(share, 0, 1),
            Background = BarBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        return track;
    }

    private static string DayText(DateOnly day) =>
        day.ToString("dd-MMM-yy ddd", CultureInfo.InvariantCulture);

    private static string DayResultText(GameDayResult result) =>
        (GameDayPicker.Parse(result.Day) is { } day ? DayText(day) : result.Day) + "  " + PipsText(result.Pips);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    private static string Share(long part, long whole) =>
        whole == 0 ? "-" : (100.0 * part / whole).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Percent(int part, int whole) =>
        whole == 0 ? "-" : (100.0 * part / whole).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Ratio(int part, int whole) =>
        whole == 0 ? "-" : ((double)part / whole).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Minutes(double seconds) =>
        (seconds / 60).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Hours(double seconds) =>
        (seconds / 3600).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Clock(long seconds) =>
        $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";

    private static string HoursMinutes(double minutes)
    {
        long total = (long)Math.Round(minutes);
        return $"{total / 60}:{total % 60:00}";
    }

    private static string PipsText(double pips) =>
        (pips >= 0 ? "+" : "") + pips.ToString("0.0", CultureInfo.InvariantCulture);

    private static Brush PipsBrush(double pips) => pips < 0 ? LossBrush : WinBrush;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
