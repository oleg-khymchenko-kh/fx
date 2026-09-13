using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FXViewer.Game;

public static class GameTradeButton
{
    private static readonly Brush BuyBrush = Frozen(0x2E, 0x7D, 0x32);
    private static readonly Brush SellBrush = Frozen(0xEF, 0x6C, 0x00);
    private static readonly ControlTemplate FlatTemplate = BuildTemplate();

    public static bool[] Sides(bool mirror) => mirror ? new[] { false, true } : new[] { true, false };

    public static string Arrow(bool buy, bool mirror) => buy != mirror ? "↑" : "↓";

    public static Button Create(bool buy, bool mirror, string text, string tip, double fontSize,
        Thickness padding, Action click)
    {
        var button = new Button
        {
            Content = $"{Arrow(buy, mirror)} {text}",
            Template = FlatTemplate,
            Background = buy ? BuyBrush : SellBrush,
            Foreground = Brushes.White,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Padding = padding,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = tip,
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static ControlTemplate BuildTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Chrome");
        border.SetValue(Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty,
            new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        template.Triggers.Add(OpacityTrigger(UIElement.IsMouseOverProperty, true, 0.85));
        template.Triggers.Add(OpacityTrigger(ButtonBase.IsPressedProperty, true, 0.7));
        template.Triggers.Add(OpacityTrigger(UIElement.IsEnabledProperty, false, 0.4));
        template.Seal();
        return template;
    }

    private static Trigger OpacityTrigger(DependencyProperty property, bool value, double opacity)
    {
        var trigger = new Trigger { Property = property, Value = value };
        trigger.Setters.Add(new Setter(UIElement.OpacityProperty, opacity, "Chrome"));
        return trigger;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
