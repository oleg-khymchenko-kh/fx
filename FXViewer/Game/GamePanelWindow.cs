using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FXViewer.Game;

public sealed class GamePanelWindow : Window
{
    private const double OwnerGap = 24;
    private const double OwnerTop = 90;

    public GamePanelView Panel { get; } = new();

    public event Action<double, double>? Moved;
    public event Action<int>? StepRequested;
    public event Action? FinishDayRequested;

    public GamePanelWindow(Window owner)
    {
        Owner = owner;
        Title = "Play";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.White;
        Content = Panel;
        Panel.DragStarted += BeginDrag;
        KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.P or Key.O)) return;
            if (Keyboard.FocusedElement is TextBoxBase) return;
            if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
            {
                FinishDayRequested?.Invoke();
                e.Handled = true;
                return;
            }
            StepRequested?.Invoke(e.Key == Key.P ? 1 : -1);
            e.Handled = true;
        };
    }

    public void Place(double? left, double? top)
    {
        if (left is { } l && top is { } t && OnScreen(l, t))
        {
            Left = l;
            Top = t;
            return;
        }
        var owner = Owner;
        Left = owner.Left + Math.Max(0, owner.ActualWidth - Panel.Width - OwnerGap);
        Top = owner.Top + OwnerTop;
    }

    private static bool OnScreen(double left, double top)
    {
        double x0 = SystemParameters.VirtualScreenLeft;
        double y0 = SystemParameters.VirtualScreenTop;
        return left >= x0 - 200
            && top >= y0 - 20
            && left <= x0 + SystemParameters.VirtualScreenWidth - 100
            && top <= y0 + SystemParameters.VirtualScreenHeight - 40;
    }

    private void BeginDrag()
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        Moved?.Invoke(Left, Top);
    }
}
