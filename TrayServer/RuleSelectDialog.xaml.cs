using System.Windows;

namespace TrayServer;

public partial class RuleSelectDialog : Window
{
    public string? SelectedRuleName { get; private set; }
    public bool Saved { get; private set; }

    public RuleSelectDialog(List<string> ruleNames)
    {
        InitializeComponent();
        foreach (var name in ruleNames)
            RuleListBox.Items.Add(name);
        if (RuleListBox.Items.Count > 0)
            RuleListBox.SelectedIndex = 0;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (RuleListBox.SelectedItem == null) return;
        SelectedRuleName = RuleListBox.SelectedItem.ToString();
        Saved = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
