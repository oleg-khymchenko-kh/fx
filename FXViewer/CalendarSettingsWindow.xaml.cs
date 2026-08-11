using System.Windows;
using FXViewer.Calendar;

namespace FXViewer;

public partial class CalendarSettingsWindow : Window
{
    public CalendarSettings Settings { get; private set; }

    public event Action<CalendarSettings>? SettingsChanged;

    public CalendarSettingsWindow(CalendarSettings settings)
    {
        InitializeComponent();
        Settings = settings.Clone();
        HighestBox.IsChecked = Settings.ShowHighest;
        HighBox.IsChecked = Settings.ShowHigh;
        MediumBox.IsChecked = Settings.ShowMedium;
        LowBox.IsChecked = Settings.ShowLow;
        HolidayBox.IsChecked = Settings.ShowHoliday;
        AnyZoomBox.IsChecked = Settings.ShowAtAnyZoom;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        Settings = new CalendarSettings
        {
            ShowHighest = HighestBox.IsChecked == true,
            ShowHigh = HighBox.IsChecked == true,
            ShowMedium = MediumBox.IsChecked == true,
            ShowLow = LowBox.IsChecked == true,
            ShowHoliday = HolidayBox.IsChecked == true,
            ShowAtAnyZoom = AnyZoomBox.IsChecked == true,
        };
        SettingsChanged?.Invoke(Settings);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
