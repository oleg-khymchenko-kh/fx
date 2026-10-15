using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace FXViewer;

public partial class LevelParamsWindow : Window
{
    private const int MaxPixels = 500;
    private const int MaxRows = 50;

    public List<LevelStep> LeftSteps { get; private set; }

    public List<LevelStep> RightSteps { get; private set; }

    public LevelParamsWindow(IEnumerable<LevelStep> left, IEnumerable<LevelStep> right)
    {
        InitializeComponent();
        LeftSteps = LevelStep.CopyOf(left);
        RightSteps = LevelStep.CopyOf(right);
        FillRows(LeftRows, LeftSteps);
        FillRows(RightRows, RightSteps);
    }

    public static string Summary(IEnumerable<LevelStep> left, IEnumerable<LevelStep> right) =>
        "Left " + Summary(left) + " | Right " + Summary(right);

    private static string Summary(IEnumerable<LevelStep> steps) =>
        string.Join(" ", steps.OrderBy(x => x.Pips).Select(x =>
            x.Pips.ToString("0.##", CultureInfo.InvariantCulture) + "/"
            + x.Pixels.ToString("0.##", CultureInfo.InvariantCulture)));

    private void FillRows(StackPanel panel, IEnumerable<LevelStep> steps)
    {
        panel.Children.Clear();
        foreach (var step in steps.OrderBy(x => x.Pips)) AddRow(panel, step.Pips, step.Pixels);
    }

    private static TextBox NumberBox(double? value) => new()
    {
        Width = 56,
        Height = 24,
        Margin = new Thickness(0, 0, 8, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
        Text = value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
    };

    private void AddRow(StackPanel panel, double? pips, double? pixels)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(NumberBox(pips));
        row.Children.Add(NumberBox(pixels));
        var remove = new Button
        {
            Content = "✕",
            Width = 24,
            Height = 24,
            ToolTip = "Delete this row",
        };
        remove.Click += (_, _) => panel.Children.Remove(row);
        row.Children.Add(remove);
        panel.Children.Add(row);
    }

    private void AddNewRow(StackPanel panel)
    {
        if (panel.Children.Count >= MaxRows) return;
        AddRow(panel, null, null);
        if (panel.Children[^1] is StackPanel row && row.Children[0] is TextBox pips) pips.Focus();
    }

    private void LeftAddBtn_Click(object sender, RoutedEventArgs e) => AddNewRow(LeftRows);

    private void RightAddBtn_Click(object sender, RoutedEventArgs e) => AddNewRow(RightRows);

    private void DefaultsBtn_Click(object sender, RoutedEventArgs e)
    {
        FillRows(LeftRows, IndicatorSymbol.DefaultLevel2LeftSteps());
        FillRows(RightRows, IndicatorSymbol.DefaultLevel2RightSteps());
    }

    private static bool TryReadNumber(TextBox box, out double value) =>
        double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private List<LevelStep>? ReadRows(StackPanel panel, string side)
    {
        var steps = new List<LevelStep>();
        foreach (var child in panel.Children)
        {
            if (child is not StackPanel row || row.Children[0] is not TextBox pipsBox
                || row.Children[1] is not TextBox pixelsBox) continue;
            if (pipsBox.Text.Trim().Length == 0 && pixelsBox.Text.Trim().Length == 0) continue;
            if (!TryReadNumber(pipsBox, out double pips) || pips <= 0)
            {
                Warn($"Pips in the {side} table must be positive numbers.");
                pipsBox.Focus();
                return null;
            }
            if (!TryReadNumber(pixelsBox, out double pixels) || pixels < 0 || pixels > MaxPixels)
            {
                Warn($"Pixels in the {side} table must be numbers from 0 to {MaxPixels}.");
                pixelsBox.Focus();
                return null;
            }
            if (steps.Any(x => x.Pips == pips))
            {
                Warn($"The {side} table has {pips.ToString("0.##", CultureInfo.InvariantCulture)} pips twice.");
                pipsBox.Focus();
                return null;
            }
            steps.Add(new LevelStep { Pips = pips, Pixels = pixels });
        }
        if (steps.Count == 0)
        {
            Warn($"The {side} table needs at least one row.");
            return null;
        }
        return steps.OrderBy(x => x.Pips).ToList();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ReadRows(LeftRows, "left") is not { } left) return;
        if (ReadRows(RightRows, "right") is not { } right) return;
        LeftSteps = left;
        RightSteps = right;
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
}
