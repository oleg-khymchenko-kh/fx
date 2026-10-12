using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FXViewer;

public partial class ShiftPresetsWindow : Window
{
    public sealed record Row(int Index, string Name, string Target, string SourceTime, string ChartTime, string Flip);

    private readonly string _indicatorName;
    private readonly List<ShiftPreset> _presets;
    private readonly Func<ShiftPreset?> _capture;
    private readonly Action<ShiftPreset> _apply;
    private readonly Action _save;
    private Point _dragStart;
    private Row? _pressedRow;
    private bool _dragging;

    public ShiftPresetsWindow(string indicatorName, List<ShiftPreset> presets,
        Func<ShiftPreset?> capture, Action<ShiftPreset> apply, Action save)
    {
        InitializeComponent();
        _indicatorName = indicatorName;
        _presets = presets;
        _capture = capture;
        _apply = apply;
        _save = save;
        Title = "Shift presets - " + indicatorName;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                return;
            }
            if (e.OriginalSource is TextBox) return;
            if (e.Key == Key.Enter && Selected is { } row)
            {
                ApplyRow(row);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && Selected != null)
            {
                DeleteBtn_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
        Activated += (_, _) => Refresh();
        Refresh();
    }

    public void Refresh()
    {
        if (!IsInitialized) return;
        var current = _capture();
        var rows = new List<Row>(_presets.Count);
        for (int i = 0; i < _presets.Count; i++) rows.Add(ToRow(i, _presets[i]));
        var selectedName = Selected?.Name;
        PresetsList.ItemsSource = rows;
        PresetsList.SelectedItem =
            rows.FirstOrDefault(r => current != null && _presets[r.Index].SameParams(current))
            ?? rows.FirstOrDefault(r => r.Name == selectedName);
        HintText.Text = rows.Count == 0
            ? $"No presets yet. \"Save as new\" keeps the current settings of {_indicatorName} under a name."
            : $"Click a preset to apply it to {_indicatorName}. Drag rows to reorder.";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool hasSelection = Selected != null;
        OverwriteBtn.IsEnabled = hasSelection;
        RenameBtn.IsEnabled = hasSelection;
        DeleteBtn.IsEnabled = hasSelection;
    }

    private static Row ToRow(int index, ShiftPreset preset) => new(
        index, preset.Name, preset.Target(),
        TimeText(preset.SourceTimeUnix), TimeText(preset.ChartTimeUnix), preset.Flip ? "M" : "");

    private static string TimeText(long unix) => unix <= 0
        ? ""
        : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private Row? Selected => PresetsList.SelectedItem as Row;

    private ShiftPreset? PresetOf(Row? row) =>
        row != null && row.Index < _presets.Count ? _presets[row.Index] : null;

    private void ApplyRow(Row row)
    {
        if (PresetOf(row) is not { } preset) return;
        _apply(preset);
        Refresh();
    }

    private void PresetsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void PresetRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem { DataContext: Row row }) return;
        _pressedRow = row;
        _dragStart = e.GetPosition(PresetsList);
    }

    private void PresetRow_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressedRow;
        _pressedRow = null;
        if (_dragging || pressed == null) return;
        if (sender is ListViewItem { DataContext: Row row } && row.Index == pressed.Index) ApplyRow(row);
    }

    private void PresetsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedRow == null || _dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(PresetsList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _pressedRow;
        _pressedRow = null;
        _dragging = true;
        try
        {
            DragDrop.DoDragDrop(PresetsList, row, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
        }
    }

    private void PresetsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(Row)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void PresetsList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(Row)) is not Row moved || moved.Index >= _presets.Count) return;
        var target = RowAt(e.GetPosition(PresetsList));
        int to = target?.Index ?? _presets.Count - 1;
        if (to == moved.Index) return;
        var preset = _presets[moved.Index];
        _presets.RemoveAt(moved.Index);
        _presets.Insert(to, preset);
        _save();
        Refresh();
        PresetsList.SelectedItem = (PresetsList.ItemsSource as List<Row>)?.FirstOrDefault(r => r.Index == to);
    }

    private Row? RowAt(Point point)
    {
        DependencyObject? hit = PresetsList.InputHitTest(point) as DependencyObject;
        while (hit != null && hit is not ListViewItem)
            hit = hit is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(hit)
                : LogicalTreeHelper.GetParent(hit);
        return (hit as ListViewItem)?.DataContext as Row;
    }

    private void SaveNewBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_capture() is not { } current) return;
        var dlg = new TabNameWindow(current.DefaultName(), "Preset name") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        string name = dlg.TabName.Trim();
        if (name.Length == 0) return;
        var existing = _presets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (MessageBox.Show(this, $"Preset {existing.Name} exists. Overwrite it?", "Save preset",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            existing.CopyParamsFrom(current);
        }
        else
        {
            current.Name = name;
            _presets.Add(current);
        }
        _save();
        Refresh();
    }

    private void OverwriteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PresetOf(Selected) is not { } preset || _capture() is not { } current) return;
        if (MessageBox.Show(this, $"Overwrite preset {preset.Name} with the current settings of {_indicatorName}?",
                "Save preset", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        preset.CopyParamsFrom(current);
        _save();
        Refresh();
    }

    private void RenameBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PresetOf(Selected) is not { } preset) return;
        var dlg = new TabNameWindow(preset.Name, "Preset name") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        string name = dlg.TabName.Trim();
        if (name.Length == 0) return;
        preset.Name = name;
        _save();
        Refresh();
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PresetOf(Selected) is not { } preset) return;
        if (MessageBox.Show(this, $"Delete preset {preset.Name}?", "Delete preset",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _presets.Remove(preset);
        _save();
        Refresh();
    }
}
