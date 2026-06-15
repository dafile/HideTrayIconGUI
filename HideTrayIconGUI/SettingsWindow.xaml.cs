using System.Windows;

namespace HideTrayIconGUI;

public partial class SettingsWindow : Window
{
    public string FilterText { get; set; } = "";
    public bool Saved { get; private set; }

    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (s, e) => FilterList.Text = FilterText;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        FilterText = FilterList.Text;
        Saved = true;
        Close();
    }

    private void CancelSettings_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
