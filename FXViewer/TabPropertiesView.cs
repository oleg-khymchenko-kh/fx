using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FXViewer.Chart;

namespace FXViewer;

public sealed class TabPropertiesView : Popup
{
    private static readonly Brush LineBrush = Frozen(0xC8, 0xC8, 0xC8);
    private static readonly Brush MutedBrush = Frozen(0x70, 0x70, 0x70);

    private readonly TextBlock _title = new()
    {
        FontSize = 12,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 0, 6),
    };

    private readonly TextBox _zoomBox = new()
    {
        Width = 70,
        FontSize = 12,
        FontFamily = new FontFamily("Consolas"),
        TextAlignment = TextAlignment.Right,
        Padding = new Thickness(2, 1, 2, 1),
        VerticalAlignment = VerticalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = Cursors.IBeam,
        ToolTip = "Extra vertical zoom, 1 = off",
    };

    private ChartTab? _tab;

    public event Action<ChartTab, double>? CustomZoomChanged;

    public TabPropertiesView()
    {
        Placement = PlacementMode.Bottom;
        StaysOpen = false;
        AllowsTransparency = true;
        VerticalOffset = 2;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "Custom zoom",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        row.Children.Add(_zoomBox);
        row.Children.Add(new TextBlock
        {
            Text = "×",
            FontSize = 12,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0),
        });
        var reset = new Button
        {
            Content = "Reset",
            FontSize = 12,
            Padding = new Thickness(8, 2, 8, 2),
            Cursor = Cursors.Hand,
            ToolTip = "Back to 1",
        };
        reset.Click += (_, _) =>
        {
            _zoomBox.Text = Format(1);
            Commit();
        };
        row.Children.Add(reset);
        var body = new StackPanel();
        body.Children.Add(_title);
        body.Children.Add(row);
        body.Children.Add(new TextBlock
        {
            Text = "Vertical zoom on top of every zoom level - only in this tab.",
            FontSize = 11,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 260,
            Margin = new Thickness(0, 8, 0, 0),
        });
        Child = new Border
        {
            Background = Brushes.White,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Child = body,
        };
        _zoomBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (_tab != null) _zoomBox.Text = Format(_tab.CustomZoom);
                IsOpen = false;
            }
        };
        _zoomBox.LostFocus += (_, _) => Commit();
        Opened += (_, _) =>
        {
            _zoomBox.Focus();
            _zoomBox.SelectAll();
        };
        Closed += (_, _) => Commit();
    }

    public void Show(UIElement target, ChartTab tab)
    {
        IsOpen = false;
        _tab = tab;
        _title.Text = $"Tab {tab.Name}";
        _zoomBox.Text = Format(tab.CustomZoom);
        PlacementTarget = target;
        IsOpen = true;
    }

    public void Hide(ChartTab tab)
    {
        if (ReferenceEquals(_tab, tab)) IsOpen = false;
    }

    private void Commit()
    {
        if (_tab == null) return;
        double stored = ZoomLevel.ClampCustomZoom(_tab.CustomZoom);
        if (!TryParse(_zoomBox.Text, out double value))
        {
            _zoomBox.Text = Format(stored);
            return;
        }
        value = ZoomLevel.ClampCustomZoom(value);
        _zoomBox.Text = Format(value);
        if (value == stored) return;
        CustomZoomChanged?.Invoke(_tab, value);
    }

    private static bool TryParse(string text, out double value) => double.TryParse(
        text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
