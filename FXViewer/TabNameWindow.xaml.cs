using System.Windows;

namespace FXViewer;

public partial class TabNameWindow : Window
{
    public string TabName { get; private set; } = "";

    public TabNameWindow(string name)
    {
        InitializeComponent();
        NameBox.Text = name;
        Loaded += (_, _) =>
        {
            NameBox.SelectAll();
            NameBox.Focus();
        };
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        var text = NameBox.Text.Trim();
        if (text.Length == 0)
        {
            MessageBox.Show(this, "The name cannot be empty.", "Invalid input",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TabName = text;
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();
}
