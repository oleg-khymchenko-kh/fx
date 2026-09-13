using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FXViewer.Chart;

namespace FXViewer;

public partial class AppSettingsWindow : Window
{
    private const double SwatchSize = 24;

    private static readonly Brush SelectedRowBrush = BrushOf(unchecked((int)0xFFEAF2FB));

    private sealed class PairRow
    {
        public PairRow(string symbol, int defaultColor, int color,
            Border rowBorder, Border swatch, TextBlock label)
        {
            Symbol = symbol;
            DefaultColor = defaultColor;
            Color = color;
            RowBorder = rowBorder;
            Swatch = swatch;
            Label = label;
        }

        public string Symbol { get; }
        public int DefaultColor { get; }
        public int Color { get; set; }
        public Border RowBorder { get; }
        public Border Swatch { get; }
        public TextBlock Label { get; }
    }

    private const int ShadeCount = 16;
    private const double ShadeMinLightness = 0.10;
    private const double ShadeMaxLightness = 0.85;

    private readonly List<PairRow> _pairs = new();
    private readonly List<Border> _swatches = new();
    private int _selected = -1;
    private Popup? _shadePopup;
    private UniformGrid? _shadeGrid;

    public AppSettingsWindow(IReadOnlyList<(string Symbol, int DefaultColorArgb, int ColorArgb)> pairs,
        bool hideWideSpread, bool showAsk, int spotDiameterPx, int spotColorArgb, int spotOpacityPercent)
    {
        InitializeComponent();
        foreach (var (symbol, defaultColor, color) in pairs) AddPairRow(symbol, defaultColor, color);
        BuildSwatches();
        SetColorControlsEnabled(false);
        HideWideSpreadCheck.IsChecked = hideWideSpread;
        ShowAskCheck.IsChecked = showAsk;
        SpotDiameterPx = spotDiameterPx;
        SpotColorArgb = spotColorArgb;
        SpotOpacityPercent = spotOpacityPercent;
        SpotSizeBox.Text = spotDiameterPx.ToString(CultureInfo.InvariantCulture);
        SpotColorBox.Text = HexOf(spotColorArgb);
        SpotOpacityBox.Text = spotOpacityPercent.ToString(CultureInfo.InvariantCulture);
    }

    public bool HideWideSpread => HideWideSpreadCheck.IsChecked == true;

    public bool ShowAsk => ShowAskCheck.IsChecked == true;

    public int SpotDiameterPx { get; private set; }

    public int SpotColorArgb { get; private set; }

    public int SpotOpacityPercent { get; private set; }

    private void SetColorControlsEnabled(bool enabled)
    {
        double opacity = enabled ? 1 : 0.4;
        PalettePanel.IsEnabled = enabled;
        PalettePanel.Opacity = opacity;
        CustomPanel.IsEnabled = enabled;
        CustomPanel.Opacity = opacity;
    }

    public int ColorOf(string symbol)
    {
        foreach (var pair in _pairs)
            if (pair.Symbol == symbol) return pair.Color;
        return 0;
    }

    private void AddPairRow(string symbol, int defaultColor, int color)
    {
        var brush = BrushOf(color);
        var swatch = new Border
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            Background = brush,
        };
        var label = new TextBlock
        {
            Text = symbol,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = brush,
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(swatch);
        panel.Children.Add(label);
        var row = new Border
        {
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = panel,
        };
        int index = _pairs.Count;
        row.MouseLeftButtonDown += (_, _) => SelectPair(index);
        _pairs.Add(new PairRow(symbol, defaultColor, color, row, swatch, label));
        PairsPanel.Children.Add(row);
    }

    private void BuildSwatches()
    {
        foreach (var argb in PairPalette.Colors)
        {
            var swatch = new Border
            {
                Width = SwatchSize,
                Height = SwatchSize,
                Margin = new Thickness(2),
                Background = BrushOf(argb),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = argb,
            };
            swatch.MouseLeftButtonDown += (_, _) => ApplyColor(argb);
            swatch.MouseRightButtonDown += (_, e) =>
            {
                e.Handled = true;
                ShowShades(argb, swatch);
            };
            _swatches.Add(swatch);
            PalettePanel.Children.Add(swatch);
        }
    }

    private void ShowShades(int argb, UIElement target)
    {
        if (_selected < 0) return;
        if (_shadePopup == null)
        {
            _shadeGrid = new UniformGrid { Columns = 8 };
            _shadePopup = new Popup
            {
                StaysOpen = false,
                Placement = PlacementMode.Bottom,
                Child = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(4),
                    Child = _shadeGrid,
                },
            };
        }
        _shadeGrid!.Children.Clear();
        foreach (var shade in Shades(argb))
        {
            var swatch = new Border
            {
                Width = SwatchSize,
                Height = SwatchSize,
                Margin = new Thickness(2),
                Background = BrushOf(shade),
                Cursor = Cursors.Hand,
            };
            int picked = shade;
            swatch.MouseLeftButtonDown += (_, _) =>
            {
                _shadePopup!.IsOpen = false;
                ApplyColor(picked);
            };
            _shadeGrid.Children.Add(swatch);
        }
        _shadePopup.PlacementTarget = target;
        _shadePopup.IsOpen = true;
    }

    private static IEnumerable<int> Shades(int argb)
    {
        var (h, s, _) = PairPalette.RgbToHsl(argb);
        for (int i = 0; i < ShadeCount; i++)
        {
            double l = ShadeMinLightness
                + i * (ShadeMaxLightness - ShadeMinLightness) / (ShadeCount - 1);
            yield return PairPalette.HslToRgb(h, s, l);
        }
    }

    private void SelectPair(int index)
    {
        _selected = index;
        for (int i = 0; i < _pairs.Count; i++)
            _pairs[i].RowBorder.Background = i == index ? SelectedRowBrush : Brushes.Transparent;
        var pair = _pairs[index];
        CustomBox.Text = HexOf(pair.Color);
        HighlightSwatch(pair.Color);
        SetColorControlsEnabled(true);
    }

    private void ApplyColor(int argb)
    {
        if (_selected < 0) return;
        var pair = _pairs[_selected];
        pair.Color = argb;
        var brush = BrushOf(argb);
        pair.Swatch.Background = brush;
        pair.Label.Foreground = brush;
        CustomBox.Text = HexOf(argb);
        HighlightSwatch(argb);
    }

    private void HighlightSwatch(int argb)
    {
        foreach (var swatch in _swatches)
            swatch.BorderBrush = (int)swatch.Tag! == argb ? Brushes.Black : Brushes.Transparent;
    }

    private static string HexOf(int argb) =>
        (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    private static bool TryParseHex(string text, out int argb)
    {
        argb = 0;
        text = text.Trim().TrimStart('#');
        if (text.Length != 6) return false;
        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return false;
        argb = unchecked((int)0xFF000000) | rgb;
        return true;
    }

    private void ApplyCustom()
    {
        if (_selected < 0) return;
        if (!TryParseHex(CustomBox.Text, out int argb))
        {
            MessageBox.Show(this, "Enter a color as RRGGBB hex, for example 3366DD.",
                "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ApplyColor(argb);
    }

    private void ApplyCustomBtn_Click(object sender, RoutedEventArgs e) => ApplyCustom();

    private void CustomBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplyCustom();
    }

    private void DefaultBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selected < 0) return;
        ApplyColor(_pairs[_selected].DefaultColor);
    }

    private void SpotColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SpotPreview == null) return;
        SpotPreview.Background = TryParseHex(SpotColorBox.Text, out int argb)
            ? BrushOf(argb)
            : Brushes.Transparent;
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SpotSizeBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int diameter)
            || diameter < CommentStyle.MinDiameterPx || diameter > CommentStyle.MaxDiameterPx)
        {
            MessageBox.Show(this,
                $"The comment spot diameter must be {CommentStyle.MinDiameterPx} to "
                + $"{CommentStyle.MaxDiameterPx} pixels.",
                "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
            SpotSizeBox.Focus();
            SpotSizeBox.SelectAll();
            return;
        }
        if (!TryParseHex(SpotColorBox.Text, out int spotColor))
        {
            MessageBox.Show(this, "Enter the comment spot color as RRGGBB hex, for example 808080.",
                "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
            SpotColorBox.Focus();
            SpotColorBox.SelectAll();
            return;
        }
        if (!int.TryParse(SpotOpacityBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int opacity) || opacity < 1 || opacity > 100)
        {
            MessageBox.Show(this, "The comment spot opacity must be 1 to 100 percent.",
                "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
            SpotOpacityBox.Focus();
            SpotOpacityBox.SelectAll();
            return;
        }
        SpotDiameterPx = diameter;
        SpotColorArgb = spotColor;
        SpotOpacityPercent = opacity;
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

    private static SolidColorBrush BrushOf(int argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }
}

public static class PairPalette
{
    private const int HueCount = 16;

    private static readonly double[] Lightness = { 0.25, 0.35, 0.5, 0.65 };

    public static readonly int[] Colors = Build();

    private static int[] Build()
    {
        var colors = new int[HueCount * Lightness.Length];
        int i = 0;
        for (int hue = 0; hue < HueCount; hue++)
            foreach (double l in Lightness)
                colors[i++] = HslToRgb((double)hue / HueCount, 1, l);
        return colors;
    }

    public static (double H, double S, double L) RgbToHsl(int argb)
    {
        double r = ((argb >> 16) & 0xFF) / 255.0;
        double g = ((argb >> 8) & 0xFF) / 255.0;
        double b = (argb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        double d = max - min;
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h =
            max == r ? (g - b) / d + (g < b ? 6 : 0) :
            max == g ? (b - r) / d + 2 :
            (r - g) / d + 4;
        return (h / 6, s, l);
    }

    public static int HslToRgb(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double r = HueChannel(p, q, h + 1.0 / 3);
        double g = HueChannel(p, q, h);
        double b = HueChannel(p, q, h - 1.0 / 3);
        return unchecked((int)0xFF000000)
            | ((int)Math.Round(r * 255) << 16)
            | ((int)Math.Round(g * 255) << 8)
            | (int)Math.Round(b * 255);
    }

    private static double HueChannel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
