using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FXViewer.Chart;

public sealed class ZoomLevelView : Border
{
    private const int ReopenGuardMs = 250;
    private const double NumberColumnWidth = 30;
    private const double ValueColumnWidth = 76;

    private static readonly Brush RowHoverBrush = Frozen(0xF0, 0xF0, 0xF0);
    private static readonly Brush RowActiveBrush = Frozen(0xEA, 0xF2, 0xFB);
    private static readonly Brush LineBrush = Frozen(0xC8, 0xC8, 0xC8);
    private static readonly Brush MutedBrush = Frozen(0x70, 0x70, 0x70);
    private static readonly Brush CornerBrush = FrozenAlpha(0x40, 0xFF, 0xFF, 0xFF);
    private static readonly Brush CornerHoverBrush = FrozenAlpha(0x90, 0xFF, 0xFF, 0xFF);

    private readonly TextBlock _label = new()
    {
        Foreground = Brushes.White,
        FontSize = 10,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Button _saveButton;
    private readonly Button _revertButton;
    private readonly StackPanel _rows = new();

    private readonly TextBlock _currentText = new()
    {
        FontSize = 11,
        Foreground = MutedBrush,
        Margin = new Thickness(4, 8, 4, 0),
    };

    private readonly TextBlock _fitText = new()
    {
        FontSize = 11,
        Foreground = MutedBrush,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 260,
        Margin = new Thickness(4, 2, 4, 0),
    };

    private readonly Popup _popup;
    private readonly List<Border> _rowBorders = new();
    private IReadOnlyList<ZoomLevel> _levels = Array.Empty<ZoomLevel>();
    private int _index = -1;
    private long _closedTicks;

    public event Action<int>? LevelSelected;
    public event Action? SaveRequested;
    public event Action? RevertRequested;
    public event Action<int>? InsertRequested;
    public event Action<int>? DeleteRequested;
    public event Action<int, double, double>? LevelEdited;

    public ZoomLevelView()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x30, 0x30, 0x30));
        CornerRadius = new CornerRadius(3);
        Padding = new Thickness(5, 1, 4, 1);
        Cursor = Cursors.Hand;
        ToolTip = "Zoom level - wheel over the chart switches, click opens the list";
        _saveButton = CornerButton(SaveIcon(), "Save the current zoom into this level",
            () => SaveRequested?.Invoke());
        _revertButton = CornerButton(RevertIcon(), "Go back to the zoom saved in this level",
            () => RevertRequested?.Invoke());
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_label);
        panel.Children.Add(_saveButton);
        panel.Children.Add(_revertButton);
        Child = panel;
        var body = new StackPanel();
        body.Children.Add(BuildHeader());
        body.Children.Add(_rows);
        body.Children.Add(_currentText);
        body.Children.Add(_fitText);
        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            VerticalOffset = 4,
            Child = new Border
            {
                Background = Brushes.White,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6, 10, 8),
                Child = body,
            },
        };
        _popup.Closed += (_, _) => _closedTicks = Environment.TickCount64;
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            TogglePopup();
        };
        MouseWheel += (_, e) => e.Handled = true;
        Unloaded += (_, _) => _popup.IsOpen = false;
    }

    public void SetLevels(IReadOnlyList<ZoomLevel> levels, int index, bool dirty, ZoomLevel current,
        double fitPerDay)
    {
        _levels = levels;
        BuildRows();
        SetCurrent(index, dirty, current, fitPerDay);
    }

    public void SetCurrent(int index, bool dirty, ZoomLevel current, double fitPerDay)
    {
        _index = index;
        _label.Text = index >= 0 && index < _levels.Count
            ? $"Zoom {index + 1}/{_levels.Count}"
            : "Zoom -";
        var buttons = dirty ? Visibility.Visible : Visibility.Collapsed;
        _saveButton.Visibility = buttons;
        _revertButton.Visibility = buttons;
        _currentText.Text = current.IsValid
            ? $"Current: {Format(current.PixelsPerDay)} px/day, "
                + $"{Format(current.PixelsPer100Pips)} px/100 pips"
            : "Current: -";
        _fitText.Text = fitPerDay > 0
            ? $"All history fills the width at {Format(fitPerDay)} px/day - "
                + "below that the chart leaves empty space"
            : "";
        _fitText.Visibility = fitPerDay > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _rowBorders.Count; i++)
            _rowBorders[i].Background = i == _index ? RowActiveBrush : Brushes.Transparent;
    }

    private void TogglePopup()
    {
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
            return;
        }
        if (Environment.TickCount64 - _closedTicks < ReopenGuardMs) return;
        _popup.IsOpen = _levels.Count > 0;
    }

    private void BuildRows()
    {
        _rows.Children.Clear();
        _rowBorders.Clear();
        for (int i = 0; i < _levels.Count; i++) _rows.Children.Add(BuildRow(i));
        _rows.Children.Add(BuildAppendRow());
    }

    private static UIElement BuildHeader()
    {
        var grid = NewRowGrid();
        AddHeaderCell(grid, 0, "#");
        AddHeaderCell(grid, 1, "px/day");
        AddHeaderCell(grid, 2, "px/100 pips");
        return new Border
        {
            Padding = new Thickness(0, 0, 0, 4),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Margin = new Thickness(0, 0, 0, 3),
            Child = grid,
        };
    }

    private UIElement BuildRow(int index)
    {
        var level = _levels[index];
        var grid = NewRowGrid();
        var number = FlatButton(
            new TextBlock
            {
                Text = (index + 1).ToString(CultureInfo.InvariantCulture),
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
            },
            "Apply this zoom level",
            () =>
            {
                _popup.IsOpen = false;
                LevelSelected?.Invoke(index);
            });
        number.BorderThickness = new Thickness(0);
        number.Margin = new Thickness(0, 0, 6, 0);
        number.Padding = new Thickness(0, 3, 0, 3);
        Grid.SetColumn(number, 0);
        grid.Children.Add(number);
        var perDay = ValueBox(index, level.PixelsPerDay, true);
        Grid.SetColumn(perDay, 1);
        grid.Children.Add(perDay);
        var per100Pips = ValueBox(index, level.PixelsPer100Pips, false);
        Grid.SetColumn(per100Pips, 2);
        grid.Children.Add(per100Pips);
        var insert = SmallButton("+", "Insert a new level here with the current zoom",
            () => InsertRequested?.Invoke(index));
        Grid.SetColumn(insert, 3);
        grid.Children.Add(insert);
        var delete = SmallButton("×", "Delete this level", () => DeleteRequested?.Invoke(index));
        delete.IsEnabled = _levels.Count > 1;
        delete.Opacity = delete.IsEnabled ? 1 : 0.4;
        Grid.SetColumn(delete, 4);
        grid.Children.Add(delete);
        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Child = grid,
        };
        _rowBorders.Add(row);
        return row;
    }

    private TextBox ValueBox(int index, double value, bool horizontal)
    {
        var box = new TextBox
        {
            Text = Format(value),
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            TextAlignment = TextAlignment.Right,
            Padding = new Thickness(2, 1, 2, 1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.IBeam,
            ToolTip = horizontal
                ? "Pixels per day - snapped to a step the chart can render"
                : "Pixels per 100 pips",
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitEdit(index, box, horizontal);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                box.Text = Format(StoredValue(index, horizontal));
                box.SelectAll();
            }
        };
        box.LostFocus += (_, _) => CommitEdit(index, box, horizontal);
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        return box;
    }

    private double StoredValue(int index, bool horizontal)
    {
        if (index < 0 || index >= _levels.Count) return 0;
        var level = _levels[index];
        return horizontal ? level.PixelsPerDay : level.PixelsPer100Pips;
    }

    private void CommitEdit(int index, TextBox box, bool horizontal)
    {
        if (index < 0 || index >= _levels.Count) return;
        double stored = StoredValue(index, horizontal);
        if (!TryParse(box.Text, out double value) || value <= 0)
        {
            box.Text = Format(stored);
            return;
        }
        if (horizontal) value = ZoomLevel.PixelsPerDayOf(ZoomLevel.ToColumnSeconds(value));
        box.Text = Format(value);
        if (Math.Abs(value - stored) <= Math.Abs(stored) * 1e-9) return;
        var level = _levels[index];
        LevelEdited?.Invoke(index,
            horizontal ? value : level.PixelsPerDay,
            horizontal ? level.PixelsPer100Pips : value);
    }

    private UIElement BuildAppendRow()
    {
        var button = FlatButton(
            new TextBlock { Text = "+  Add level at the end", FontSize = 12 },
            "Add a level at the end with the current zoom",
            () => InsertRequested?.Invoke(_levels.Count));
        button.Padding = new Thickness(6, 3, 6, 3);
        button.Margin = new Thickness(4, 6, 4, 0);
        return button;
    }

    private static Grid NewRowGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NumberColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ValueColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ValueColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    private static void AddHeaderCell(Grid grid, int column, string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = MutedBrush,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(4, 0, column == 0 ? 10 : 12, 0),
        };
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private static Button SmallButton(string glyph, string tip, Action click)
    {
        var button = FlatButton(
            new TextBlock { Text = glyph, FontSize = 13 }, tip, click);
        button.Width = 20;
        button.Height = 20;
        button.Margin = new Thickness(2, 0, 0, 0);
        button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }

    private static Button FlatButton(UIElement content, string tip, Action click)
    {
        var button = new Button
        {
            Content = content,
            Template = FlatTemplate(3),
            Background = Brushes.Transparent,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = tip,
        };
        button.Click += (_, _) => click();
        button.MouseEnter += (_, _) =>
        {
            if (button.IsEnabled) button.Background = RowHoverBrush;
        };
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    private static Button CornerButton(UIElement icon, string tip, Action click)
    {
        var button = new Button
        {
            Content = icon,
            Template = FlatTemplate(2),
            Background = CornerBrush,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Width = 15,
            Height = 13,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = false,
            Visibility = Visibility.Collapsed,
            ToolTip = tip,
        };
        button.Click += (_, _) => click();
        button.MouseEnter += (_, _) => button.Background = CornerHoverBrush;
        button.MouseLeave += (_, _) => button.Background = CornerBrush;
        return button;
    }

    private static ControlTemplate FlatTemplate(double cornerRadius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty,
            new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty,
            new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty,
            new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        template.Seal();
        return template;
    }

    private static UIElement SaveIcon() => new Path
    {
        Data = Geometry.Parse("M1,1 H9 L11,3 V11 H1 Z M3.5,1 V4 H8 V1 M3,11 V7 H9 V11"),
        Stroke = Brushes.White,
        StrokeThickness = 1,
        Width = 12,
        Height = 12,
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new ScaleTransform(0.8, 0.8),
    };

    private static UIElement RevertIcon() => new TextBlock
    {
        Text = "↺",
        FontFamily = new FontFamily("Segoe UI Symbol"),
        FontSize = 11,
        Foreground = Brushes.White,
        Margin = new Thickness(0, -1, 0, 0),
    };

    private static bool TryParse(string text, out double value) => double.TryParse(
        text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static Brush Frozen(byte r, byte g, byte b) => FrozenAlpha(0xFF, r, g, b);

    private static Brush FrozenAlpha(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
