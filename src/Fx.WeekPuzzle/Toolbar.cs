using System.Windows;
using System.Windows.Controls;

namespace Fx.WeekPuzzle;

internal static class Toolbar
{
    public static Button Button(string text, Action click)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => click();
        return button;
    }

    public static TextBlock Label(string text) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
}
