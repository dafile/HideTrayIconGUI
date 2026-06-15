using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace HideTrayIconGUI;

public partial class HiddenListWindow : Window
{
    private readonly string _rulesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rules.txt");
    private readonly ObservableCollection<HiddenItem> _items = new();

    public Action? OnRulesChanged { get; set; }

    public HiddenListWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HiddenGrid.ItemsSource = _items;
        LoadAndRefresh();
    }

    private void LoadAndRefresh()
    {
        _items.Clear();

        // Read rules (format: "ProcessName|Tooltip" or just "ProcessName")
        var rules = new List<string>();
        try
        {
            if (File.Exists(_rulesPath))
                rules = File.ReadAllText(_rulesPath)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
        }
        catch { }

        // Get current tray icons
        var currentIcons = HideService.EnumerateTrayIcons();
        var iconDict = currentIcons
            .GroupBy(i => i.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            string procName = rule;
            string tooltip = "";
            int sep = rule.IndexOf('|');
            if (sep > 0)
            {
                procName = rule[..sep];
                tooltip = rule[(sep + 1)..];
            }

            string area = "规则";
            string status = "已隐藏";

            if (iconDict.TryGetValue(procName, out var icon))
            {
                // Still visible in tray - might be a duplicate or reappeared
                if (string.IsNullOrEmpty(tooltip)) tooltip = icon.Tooltip;
                area = icon.Area;
                status = icon.Status;
            }

            _items.Add(new HiddenItem
            {
                IsSelected = false,
                ProcessName = procName,
                Tooltip = tooltip,
                Area = area,
                Status = status
            });
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        int total = _items.Count;
        int selected = _items.Count(i => i.IsSelected);
        CountText.Text = $"共 {total} 条规则, 已选 {selected}";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadAndRefresh();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.IsSelected = true;
        UpdateCount();
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.IsSelected = false;
        UpdateCount();
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) { CountText.Text = "请先勾选"; return; }

        // Remove from rules file (match by process name before '|')
        var toRemove = selected.Select(i => i.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_rulesPath))
            {
                var rules = File.ReadAllText(_rulesPath)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(r =>
                    {
                        string name = r.Contains('|') ? r[..r.IndexOf('|')] : r;
                        return !toRemove.Contains(name.Trim());
                    })
                    .ToList();
                File.WriteAllText(_rulesPath, string.Join("\n", rules));
            }
        }
        catch { }

        // Try to restore
        string ids = string.Join(" ", toRemove);
        Task.Run(() => HideService.Show(ids));

        foreach (var item in selected) _items.Remove(item);
        UpdateCount();
        OnRulesChanged?.Invoke();
    }

    private void HiddenGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCount();
}

public class HiddenItem : IconItem { }
