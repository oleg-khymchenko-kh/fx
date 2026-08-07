using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FXViewer.Chart;

public sealed class LoadIndicatorView : Border
{
    private static readonly string[] SpinnerFrames = { "◐", "◓", "◑", "◒" };

    private readonly TextBlock _spinner = new()
    {
        Foreground = Brushes.White,
        FontSize = 12,
        Margin = new Thickness(0, 0, 6, 0),
    };

    private readonly TextBlock _label = new()
    {
        Foreground = Brushes.White,
        FontSize = 12,
    };

    private readonly StackPanel _lines = new();
    private readonly Popup _popup;
    private readonly DispatcherTimer _spinTimer;
    private int _frame;
    private int _jobCount;

    public LoadIndicatorView()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x30, 0x30, 0x30));
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(8, 4, 8, 4);
        Cursor = Cursors.Hand;
        Visibility = Visibility.Collapsed;
        ToolTip = "Click for details";
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_spinner);
        panel.Children.Add(_label);
        Child = panel;
        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = true,
            AllowsTransparency = true,
            VerticalOffset = 4,
            Child = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6, 10, 8),
                MaxWidth = 460,
                Child = _lines,
            },
        };
        _spinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _spinTimer.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % SpinnerFrames.Length;
            _spinner.Text = SpinnerFrames[_frame];
        };
        MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _popup.IsOpen = !_popup.IsOpen && _jobCount > 0;
        };
        Unloaded += (_, _) =>
        {
            _spinTimer.Stop();
            _popup.IsOpen = false;
        };
        Loaded += (_, _) =>
        {
            if (_jobCount > 0 && !_spinTimer.IsEnabled) _spinTimer.Start();
        };
    }

    public void SetJobs(IReadOnlyList<string> jobs)
    {
        _jobCount = jobs.Count;
        if (jobs.Count == 0)
        {
            Visibility = Visibility.Collapsed;
            _popup.IsOpen = false;
            _spinTimer.Stop();
            return;
        }
        Visibility = Visibility.Visible;
        if (!_spinTimer.IsEnabled)
        {
            _spinner.Text = SpinnerFrames[_frame];
            _spinTimer.Start();
        }
        _label.Text = jobs.Count == 1 ? "Loading" : $"Loading ({jobs.Count})";
        _lines.Children.Clear();
        foreach (var line in jobs)
            _lines.Children.Add(new TextBlock
            {
                Text = line,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 1, 0, 1),
            });
    }
}
