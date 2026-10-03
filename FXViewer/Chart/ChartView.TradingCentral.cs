using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FXViewer.Storage;

namespace FXViewer.Chart;

public sealed partial class ChartView
{
    private const int TradingCentralHitPx = 4;
    private const double TradingCentralPopupMaxWidth = 660;
    private const double TradingCentralPopupScreenMargin = 80;
    private const double TradingCentralPopupMinHeight = 300;
    private const double TradingCentralImageMaxWidth = 600;
    private const int TradingCentralLabelMaxLength = 40;
    private const int TradingCentralMaxDecimals = 5;

    private readonly List<TradingCentralMarker> _tradingCentralMarkers = new();
    private Popup? _tradingCentralPopup;

    public string TradingCentralFolder { get; set; } = "";

    private List<TradingCentralMarker> FindTradingCentralMarkersAt(int x, int y)
    {
        var hits = new List<(double Dist, TradingCentralMarker Marker)>();
        foreach (var m in _tradingCentralMarkers)
        {
            double dx = x - m.X;
            double dy = y - m.Y;
            double r = m.HalfPx + TradingCentralHitPx;
            double d = dx * dx + dy * dy;
            if (d <= r * r) hits.Add((d, m));
        }
        return hits.OrderBy(h => h.Dist).Select(h => h.Marker).ToList();
    }

    private void ShowTradingCentralPopup(List<TradingCentralMarker> markers)
    {
        if (markers.Count == 0) return;
        var marker = markers[0];
        CloseChartPopups();
        var dpi = VisualTreeHelper.GetDpi(this);
        double cxDip = marker.X / dpi.DpiScaleX;
        double cyDip = marker.Y / dpi.DpiScaleY;
        _tradingCentralPopup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Top,
            PlacementRectangle = new Rect(
                cxDip - marker.HalfPx, cyDip - marker.HalfPx,
                2 * marker.HalfPx, 2 * marker.HalfPx),
            StaysOpen = true,
            AllowsTransparency = true,
            Child = BuildTradingCentralPopupContent(markers),
        };
        _tradingCentralPopup.IsOpen = true;
    }

    private void CloseTradingCentralPopup()
    {
        if (_tradingCentralPopup == null) return;
        _tradingCentralPopup.IsOpen = false;
        _tradingCentralPopup = null;
    }

    private FrameworkElement BuildTradingCentralPopupContent(List<TradingCentralMarker> markers)
    {
        var panel = new StackPanel();
        int blocks = 0;
        foreach (var group in markers.GroupBy(m => m.Mark.Levels))
        {
            if (blocks++ > 0)
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Background = BrushFor(unchecked((int)0xFFDDDDDD)),
                    Margin = new Thickness(0, 12, 0, 12),
                });
            AddTradingCentralBlock(panel, group.ToList());
        }
        var closeButton = new Button
        {
            Content = "✕",
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ToolTip = "Close",
        };
        closeButton.Click += (_, _) => CloseTradingCentralPopup();
        var scroll = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Max(TradingCentralPopupMinHeight,
                SystemParameters.WorkArea.Height - TradingCentralPopupScreenMargin),
            Margin = new Thickness(0, 2, 0, 0),
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(closeButton, 0);
        Grid.SetRow(scroll, 1);
        grid.Children.Add(closeButton);
        grid.Children.Add(scroll);
        return new Border
        {
            Background = Brushes.White,
            BorderBrush = BrushFor(unchecked((int)0xFF888888)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 10),
            MaxWidth = TradingCentralPopupMaxWidth,
            Child = grid,
        };
    }

    private void AddTradingCentralBlock(StackPanel panel, List<TradingCentralMarker> lines)
    {
        var first = lines[0].Mark;
        var levels = first.Levels;
        string name = levels.Name.Length > 0 ? levels.Name : levels.Pair;
        int shown = 0;
        foreach (var line in lines.DistinctBy(l => (l.Mark.Kind, l.Mark.Number)))
        {
            var mark = line.Mark;
            panel.Children.Add(new TextBlock
            {
                Text = $"{name}  ·  {TradingCentralLineName(mark)}  ·  "
                    + TradingCentralPrice(TradingCentralLinePrice(mark), TradingCentralDecimals(levels)),
                Foreground = BrushFor(line.ColorArgb),
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, shown++ > 0 ? 10 : 0, 0, 0),
            });
            panel.Children.Add(new TextBlock
            {
                Text = TradingCentralExplanation(mark),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        var from = DateTimeOffset.FromUnixTimeSeconds(first.FromUnix).UtcDateTime;
        var to = DateTimeOffset.FromUnixTimeSeconds(first.ToUnix).UtcDateTime;
        string toText = to.Date == from.Date ? $"{to:HH:mm}" : $"{to:yyyy-MM-dd HH:mm}";
        bool signal = TradingCentralSessions.IsSignal(first.Session);
        var notes = new List<string>
        {
            $"Trading Central  ·  {TradingCentralSourceName(first.Session)}  ·  "
            + $"{from:yyyy-MM-dd HH:mm} - {toText} UTC",
        };
        if (lines[0].Mirrored)
            notes.Add($"{name} is drawn mirrored on this chart: when the price goes up, the line goes down.");
        foreach (var note in notes)
            panel.Children.Add(new TextBlock
            {
                Text = note,
                Foreground = BrushFor(unchecked((int)0xFF555555)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
        string original = levels.Text.Length > 0
            ? levels.Text
            : string.Join("\n\n", new[] { levels.Title, levels.Comment }.Where(s => s.Length > 0));
        if (original.Length > 0 || levels.Images.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = signal ? "Original forecast from FxPro Direct" : "Original forecast from the mail",
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = BrushFor(unchecked((int)0xFF777777)),
                Margin = new Thickness(0, 14, 0, 2),
            });
        if (original.Length > 0) panel.Children.Add(TradingCentralOriginalText(original));
        foreach (var relative in levels.Images)
        {
            var image = LoadTradingCentralImage(relative);
            if (image == null)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Chart image is missing: " + relative,
                    Foreground = BrushFor(unchecked((int)0xFF777777)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0),
                });
                continue;
            }
            var dpi = VisualTreeHelper.GetDpi(this);
            var view = new Image
            {
                Source = image,
                Stretch = Stretch.Uniform,
                MaxWidth = Math.Min(image.PixelWidth / dpi.DpiScaleX, TradingCentralImageMaxWidth),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0),
            };
            RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.HighQuality);
            panel.Children.Add(view);
        }
        var set = first.Set;
        var made = DateTimeOffset.FromUnixTimeSeconds(set.MadeAtUnix).UtcDateTime;
        var published = DateTimeOffset.FromUnixTimeSeconds(TradingCentralTimes.StartOf(set, levels)).UtcDateTime;
        panel.Children.Add(new TextBlock
        {
            Text = (set.Subject.Length > 0 ? set.Subject + ", " : "")
                + $"{(signal ? "read" : "received")} {made:yyyy-MM-dd HH:mm} UTC"
                + (published != made ? $". Trading Central published it {published:yyyy-MM-dd HH:mm} UTC." : ""),
            Foreground = BrushFor(unchecked((int)0xFF777777)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
    }

    private static TextBlock TradingCentralOriginalText(string text)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) block.Inlines.Add(new System.Windows.Documents.LineBreak());
            string line = lines[i];
            bool bold = i == 0 || (line.EndsWith(':') && line.Length <= TradingCentralLabelMaxLength);
            block.Inlines.Add(bold ? new Bold(new Run(line)) : new Run(line));
        }
        return block;
    }

    private BitmapImage? LoadTradingCentralImage(string relative)
    {
        try
        {
            string path = Path.GetFullPath(Path.Combine(TradingCentralFolder, relative));
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string TradingCentralSourceName(string session) =>
        TradingCentralSessions.IsSignal(session) ? "FxPro Direct signal"
        : TradingCentralSessions.IsMidday(session) ? "midday set"
        : "morning set";

    private static string TradingCentralLineName(TradingCentralMark mark) => mark.Kind switch
    {
        TradingCentralLevelKind.Pivot => "Pivot",
        TradingCentralLevelKind.Target => $"Target {mark.Number}",
        _ => $"Alternative target {mark.Number}",
    };

    private static double TradingCentralLinePrice(TradingCentralMark mark)
    {
        var levels = mark.Levels;
        var list = mark.Kind switch
        {
            TradingCentralLevelKind.Target => levels.Targets,
            TradingCentralLevelKind.Alternative => levels.Alternatives,
            _ => null,
        };
        if (list == null) return levels.Pivot;
        return mark.Number >= 1 && mark.Number <= list.Count ? list[mark.Number - 1] : 0;
    }

    private static int TradingCentralDecimals(TradingCentralLevels levels)
    {
        int decimals = 0;
        foreach (double price in levels.Targets.Concat(levels.Alternatives).Append(levels.Pivot))
        {
            string text = Math.Round(price, TradingCentralMaxDecimals).ToString("0.#####", CultureInfo.InvariantCulture);
            int dot = text.IndexOf('.');
            if (dot >= 0) decimals = Math.Max(decimals, text.Length - dot - 1);
        }
        return decimals;
    }

    private static string TradingCentralPrice(double price, int decimals) =>
        price.ToString("N" + decimals, CultureInfo.InvariantCulture);

    private static string TradingCentralPrices(IEnumerable<double> prices, int decimals)
    {
        var list = prices.Where(p => p > 0).Select(p => TradingCentralPrice(p, decimals)).ToList();
        return list.Count <= 1
            ? string.Concat(list)
            : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    private static string TradingCentralExplanation(TradingCentralMark mark)
    {
        var levels = mark.Levels;
        int decimals = TradingCentralDecimals(levels);
        string pivot = TradingCentralPrice(levels.Pivot, decimals);
        string targets = TradingCentralPrices(levels.Targets, decimals);
        string alternatives = TradingCentralPrices(levels.Alternatives, decimals);
        string side = mark.Up ? "above" : "below";
        string otherSide = mark.Up ? "below" : "above";
        string move = mark.Up ? "a rise" : "a fall";
        var text = new StringBuilder();
        switch (mark.Kind)
        {
            case TradingCentralLevelKind.Pivot:
                text.Append("The border between the two scenarios. ");
                text.Append($"While the price stays {side} {pivot}, Trading Central expects {move}");
                text.Append(targets.Length > 0 ? $" to {targets} (solid lines with a disc)." : ".");
                if (alternatives.Length > 0)
                    text.Append($" If the price goes {otherSide} {pivot}, they expect the alternative "
                        + $"targets {alternatives} (dashed lines).");
                break;
            case TradingCentralLevelKind.Target:
                text.Append($"Target {mark.Number} of the main scenario. Trading Central expects {move} "
                    + $"to this level while the price stays {side} the pivot {pivot} "
                    + "(solid line with a triangle).");
                if (alternatives.Length > 0)
                    text.Append($" If the price goes {otherSide} the pivot, the alternative targets are "
                        + $"{alternatives} (dashed lines).");
                break;
            default:
                text.Append($"Alternative target {mark.Number}. Trading Central expects this level only if "
                    + $"the price goes {otherSide} the pivot {pivot}.");
                if (targets.Length > 0) text.Append($" Their main scenario is {move} to {targets}.");
                break;
        }
        return text.ToString();
    }
}
