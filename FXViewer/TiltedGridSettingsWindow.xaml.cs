using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FXViewer.Chart;

namespace FXViewer;

public partial class TiltedGridSettingsWindow : Window
{
    private const string PipsFormat = "0.###";

    private readonly List<TiltedGridState> _grids;
    private readonly ToggleButton[] _gridButtons;
    private int _selected;

    public IReadOnlyList<TiltedGridState> Grids => _grids;

    public TiltedGridSettingsWindow(IReadOnlyList<TiltedGridState> grids, int selected)
    {
        InitializeComponent();
        _grids = grids.Select(g => g.Clone()).ToList();
        while (_grids.Count < ChartViewState.TiltedGridCount)
            _grids.Add(TiltedGridState.CreateDefault());
        _gridButtons = new[] { Grid1Btn, Grid2Btn, Grid3Btn, Grid4Btn, Grid5Btn, Grid6Btn, Grid7Btn };
        _selected = Math.Clamp(selected - 1, 0, _grids.Count - 1);
        ShowGrid(_selected);
    }

    private void ShowGrid(int index)
    {
        var grid = _grids[index];
        UpPipsBox.Text = Text(grid.UpPipsPerDay);
        DownPipsBox.Text = Text(grid.DownPipsPerDay);
        UpLockBox.IsChecked = grid.UpLocked;
        DownLockBox.IsChecked = grid.DownLocked;
        for (int i = 0; i < _gridButtons.Length; i++) _gridButtons[i].IsChecked = i == index;
    }

    private void GridBtn_Click(object sender, RoutedEventArgs e)
    {
        int index = Array.IndexOf(_gridButtons, sender);
        if (index < 0) return;
        if (index == _selected)
        {
            _gridButtons[index].IsChecked = true;
            return;
        }
        if (!ReadGrid(_grids[_selected]))
        {
            ShowGrid(_selected);
            return;
        }
        _selected = index;
        ShowGrid(index);
    }

    private static string Text(double pipsPerDay) =>
        pipsPerDay.ToString(PipsFormat, CultureInfo.InvariantCulture);

    private bool TryReadPips(string label, TextBox box, bool up, out double pips)
    {
        pips = 0;
        string text = box.Text.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
        {
            Warn($"{label}: enter a number of pips per day.");
            return false;
        }
        double magnitude = Math.Abs(value);
        if (magnitude < TiltedGridState.MinPipsPerDay || magnitude > TiltedGridState.MaxPipsPerDay)
        {
            Warn($"{label}: the slope must be between {TiltedGridState.MinPipsPerDay} and " +
                $"{TiltedGridState.MaxPipsPerDay} pips per day.");
            return false;
        }
        pips = TiltedGridState.SignedPips(magnitude, up);
        return true;
    }

    private bool ReadGrid(TiltedGridState grid)
    {
        string prefix = $"Grid {_selected + 1}";
        if (!TryReadPips($"{prefix} rising", UpPipsBox, true, out double up)) return false;
        if (!TryReadPips($"{prefix} falling", DownPipsBox, false, out double down)) return false;
        grid.UpPipsPerDay = up;
        grid.DownPipsPerDay = down;
        grid.UpLocked = UpLockBox.IsChecked == true;
        grid.DownLocked = DownLockBox.IsChecked == true;
        return true;
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadGrid(_grids[_selected])) return;
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
}
