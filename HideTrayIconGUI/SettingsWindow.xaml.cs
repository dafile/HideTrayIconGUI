using System.Windows;

namespace HideTrayIconGUI;

public partial class SettingsWindow : Window
{
    public string FilterText { get; set; } = "";
    public bool MinimizeToTray { get; set; } = true;
    public bool Saved { get; private set; }

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            FilterList.Text = FilterText;
            MinimizeToTrayCheck.IsChecked = MinimizeToTray;
        };
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        FilterText = FilterList.Text;
        MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
        Saved = true;
        Close();
    }

    private void CancelSettings_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
