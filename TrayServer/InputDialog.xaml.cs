using System.Windows;

namespace TrayServer;

public partial class InputDialog : Window
{
    public string Result { get; private set; } = "";
    public bool Saved { get; private set; }

    public InputDialog(string title, string description, string initialText)
    {
        InitializeComponent();
        TitleText.Text = title;
        Title = title;
        DescText.Text = description;
        InputBox.Text = initialText;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = InputBox.Text;
        Saved = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
