using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Shared;

namespace TrayServer;

public partial class RuleManagerWindow : Window
{
    private readonly List<RuleInfo> _rules;
    private RuleInfo? _currentRule;
    private readonly ObservableCollection<RuleEntry> _entries = new();

    public Action? OnRulesChanged { get; set; }

    public RuleManagerWindow(List<RuleInfo> rules)
    {
        _rules = rules;
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshRuleList();
        EntriesGrid.ItemsSource = _entries;
    }

    private void RefreshRuleList()
    {
        RuleList.Items.Clear();
        foreach (var r in _rules)
            RuleList.Items.Add(r.Name);
        if (_rules.Count > 0 && RuleList.SelectedIndex < 0)
            RuleList.SelectedIndex = 0;
    }

    private void RuleList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RuleList.SelectedIndex >= 0 && RuleList.SelectedIndex < _rules.Count)
        {
            _currentRule = _rules[RuleList.SelectedIndex];
            RuleNameBox.Text = _currentRule.Name;
            _entries.Clear();
            foreach (var entry in _currentRule.Entries)
                _entries.Add(entry);
        }
    }

    private void NewRule_Click(object sender, RoutedEventArgs e)
    {
        var rule = new RuleInfo { Name = $"规则{_rules.Count + 1}" };
        _rules.Add(rule);
        RefreshRuleList();
        RuleList.SelectedIndex = _rules.Count - 1;
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRule == null) return;
        _rules.Remove(_currentRule);
        _currentRule = null;
        _entries.Clear();
        RuleNameBox.Text = "";
        RefreshRuleList();
        OnRulesChanged?.Invoke();
    }

    private void AddEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRule == null) { return; }
        string proc = EntryProcessBox.Text.Trim();
        if (string.IsNullOrEmpty(proc)) return;

        _currentRule.Entries.Add(new RuleEntry
        {
            ProcessName = proc,
            Tooltip = EntryTooltipBox.Text.Trim()
        });
        _entries.Add(_currentRule.Entries.Last());
        EntryProcessBox.Text = "";
        EntryTooltipBox.Text = "";
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRule == null || EntriesGrid.SelectedIndex < 0) return;
        int idx = EntriesGrid.SelectedIndex;
        _currentRule.Entries.RemoveAt(idx);
        _entries.RemoveAt(idx);
    }

    private void SaveRule_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRule != null)
        {
            _currentRule.Name = RuleNameBox.Text.Trim();
            if (string.IsNullOrEmpty(_currentRule.Name))
                _currentRule.Name = "未命名规则";
            RefreshRuleList();
        }
        OnRulesChanged?.Invoke();
        System.Windows.MessageBox.Show("规则已保存", "提示",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
}
