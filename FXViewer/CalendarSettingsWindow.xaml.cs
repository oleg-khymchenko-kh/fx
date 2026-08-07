using System.Windows;
using FXViewer.Calendar;

namespace FXViewer;

public partial class CalendarSettingsWindow : Window
{
    public CalendarSettings Settings { get; private set; }

    public CalendarSettingsWindow(CalendarSettings settings)
    {
        InitializeComponent();
        Settings = settings.Clone();
        RateBox.IsChecked = Settings.ShowRate;
        HighBox.IsChecked = Settings.ShowHigh;
        MediumBox.IsChecked = Settings.ShowMedium;
        LowBox.IsChecked = Settings.ShowLow;
        HolidayBox.IsChecked = Settings.ShowHoliday;
        AnyZoomBox.IsChecked = Settings.ShowAtAnyZoom;
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        Settings = new CalendarSettings
        {
            ShowRate = RateBox.IsChecked == true,
            ShowHigh = HighBox.IsChecked == true,
            ShowMedium = MediumBox.IsChecked == true,
            ShowLow = LowBox.IsChecked == true,
            ShowHoliday = HolidayBox.IsChecked == true,
            ShowAtAnyZoom = AnyZoomBox.IsChecked == true,
        };
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
