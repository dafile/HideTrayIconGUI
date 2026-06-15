using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace HideTrayIconGUI;

public partial class MainWindow : Window
{
    private readonly string _rulesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rules.txt");
    private readonly string _filterPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "filter.txt");
    private readonly DispatcherTimer _autoTimer;
    private readonly DispatcherTimer _countTimer;
    private readonly ObservableCollection<IconItem> _allIcons = new();  // unfiltered
    private readonly ObservableCollection<IconItem> _viewIcons = new(); // displayed
    private string _currentFilter = "全部";
    private List<string> _filteredProcesses = [];

    public MainWindow()
    {
        InitializeComponent();

        _autoTimer = new DispatcherTimer();
        _autoTimer.Tick += (s, e) => ApplyRules();

        _countTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _countTimer.Tick += (s, e) => { try { UpdateCount(); } catch { } };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Log("[1] 绑定数据源...");
            IconGrid.ItemsSource = _viewIcons;
            _countTimer.Start();

            Log("[2] 加载过滤配置...");
            LoadFilter();

            Log("[3] 加载规则...");
            LoadRules();

            Log("[4] 刷新图标列表...");
            RefreshList();

            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hideTrayIcon.exe");
            Log($"[5] hideTrayIcon.exe: {(File.Exists(exePath) ? "已找到" : "未找到!")}");
            Log($"[6] 过滤进程: {(_filteredProcesses == null ? "null" : string.Join(", ", _filteredProcesses))}");

            // Auto-apply on startup
            string rules = RulesBox?.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(rules))
            {
                Log("[7] 启动时自动应用规则...");
                ApplyRules();
            }
            Log("[8] 启动完成");
        }
        catch (Exception ex)
        {
            string msg = $"启动错误: {ex.GetType().Name}\n{ex.Message}\n\n{ex.StackTrace}";
            HideService.Log("CRASH", msg);
            System.Windows.MessageBox.Show(msg, "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    // ========== Icon list ==========

    private void RefreshList()
    {
        try
        {
            _allIcons.Clear();
            var icons = HideService.EnumerateTrayIcons();
            if (icons == null)
            {
                Log("EnumerateTrayIcons 返回 null");
                return;
            }
            foreach (var icon in icons)
            {
                if (icon == null) continue;
                // Apply process filter
                if (_filteredProcesses != null && _filteredProcesses.Any(f =>
                    icon.ProcessName != null && icon.ProcessName.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    continue;

                _allIcons.Add(new IconItem
                {
                    IsSelected = false,
                    ProcessName = icon.ProcessName ?? "",
                    Tooltip = icon.Tooltip ?? "",
                    Area = icon.Area ?? "",
                    Status = icon.Status ?? ""
                });
            }
            ApplyViewFilter();
            Log($"刷新列表: {_allIcons.Count} 个图标");
        }
        catch (Exception ex)
        {
            Log($"刷新列表异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ApplyViewFilter()
    {
        try
        {
            _viewIcons.Clear();
            var filtered = _currentFilter switch
            {
                "可见" => _allIcons.Where(i => i.Status == "可见"),
                "已隐藏" => _allIcons.Where(i => i.Status == "已隐藏"),
                _ => _allIcons.AsEnumerable()
            };
            foreach (var item in filtered)
                _viewIcons.Add(item);
            UpdateCount();
        }
        catch (Exception ex)
        {
            Log($"ApplyViewFilter 异常: {ex.Message}");
        }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Guard: may fire during InitializeComponent before _viewIcons is ready
        if (_viewIcons == null || _allIcons == null) return;
        if (FilterCombo?.SelectedItem is ComboBoxItem item)
        {
            _currentFilter = item.Content?.ToString() ?? "全部";
            ApplyViewFilter();
        }
    }

    private void UpdateCount()
    {
        if (StatusText == null) return; // Guard: may be called during InitializeComponent
        int total = _allIcons.Count;
        int visible = _allIcons.Count(i => i.Status == "可见");
        int hidden = _allIcons.Count(i => i.Status == "已隐藏");
        int selected = _allIcons.Count(i => i.IsSelected);
        int viewing = _viewIcons.Count;
        string view = _currentFilter == "全部" ? "" : $" (显示 {viewing})";
        StatusText.Text = $"共 {total} | 可见 {visible} | 已隐藏 {hidden} | 已选 {selected}{view}";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _viewIcons) item.IsSelected = true;
        UpdateCount();
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _allIcons) item.IsSelected = false;
        UpdateCount();
    }

    // ========== Hide / Show ==========

    private void HideSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _allIcons.Where(i => i.IsSelected).ToList();
        Log($"点击隐藏选中, 当前选中: {selected.Count} 个");

        if (selected.Count == 0)
        {
            StatusText.Text = "请先勾选要隐藏的图标";
            return;
        }

        var ids = selected.Select(i => i.ProcessName).Distinct().ToList();
        string identifierStr = string.Join(" ", ids);

        Log($"准备隐藏: [{identifierStr}]");
        StatusText.Text = "正在隐藏...";

        Task.Run(() =>
        {
            var (ok, msg) = HideService.Hide(identifierStr);
            Dispatcher.Invoke(() =>
            {
                if (ok)
                {
                    Log("隐藏命令执行成功");
                    StatusText.Text = "隐藏命令已发送";

                    // Update status in-place (don't remove from list)
                    foreach (var item in selected)
                        item.Status = "已隐藏";

                    // Auto-save rules
                    AutoSaveRules(ids);
                }
                else
                {
                    Log($"隐藏失败: {msg}");
                    StatusText.Text = $"失败: {msg}";
                }

                // Delayed refresh to pick up actual state
                SafeDelay(2000, () => RefreshList());
            });
        });
    }

    private void ShowSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _allIcons.Where(i => i.IsSelected).ToList();
        Log($"点击显示选中, 当前选中: {selected.Count} 个");

        if (selected.Count == 0)
        {
            StatusText.Text = "请先勾选要显示的图标";
            return;
        }

        var ids = selected.Select(i => i.ProcessName).Distinct().ToList();
        string identifierStr = string.Join(" ", ids);

        Log($"准备显示: [{identifierStr}]");
        StatusText.Text = "正在显示...";

        Task.Run(() =>
        {
            var (ok, msg) = HideService.Show(identifierStr);
            Dispatcher.Invoke(() =>
            {
                if (ok)
                {
                    Log("显示命令执行成功");
                    StatusText.Text = "显示命令已发送";

                    foreach (var item in selected)
                        item.Status = "可见";

                    AutoRemoveRules(ids);
                }
                else
                {
                    Log($"显示失败: {msg}");
                    StatusText.Text = $"失败: {msg}";
                }

                SafeDelay(2000, () => RefreshList());
            });
        });
    }

    // ========== Rules ==========

    private void LoadRules()
    {
        try
        {
            if (File.Exists(_rulesPath))
            {
                RulesBox.Text = File.ReadAllText(_rulesPath);
                string[] rules = RulesBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Log($"已加载规则 ({rules.Length} 条): {string.Join(", ", rules)}");
            }
        }
        catch (Exception ex) { Log($"加载规则失败: {ex.Message}"); }
    }

    private void SaveRules_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText(_rulesPath, RulesBox.Text);
            string[] rules = RulesBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            RuleStatus.Text = $"已保存 ({rules.Length} 条)";
            Log($"规则已保存: {string.Join(", ", rules)}");
        }
        catch (Exception ex) { Log($"保存规则失败: {ex.Message}"); }
    }

    private void AutoSaveRules(List<string> processNames)
    {
        try
        {
            var existing = RulesBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            bool changed = false;
            foreach (var name in processNames)
            {
                if (!existing.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    existing.Add(name);
                    changed = true;
                }
            }
            if (changed)
            {
                RulesBox.Text = string.Join("\n", existing);
                File.WriteAllText(_rulesPath, RulesBox.Text);
                Log($"自动添加规则: {string.Join(", ", processNames)}");
            }
        }
        catch (Exception ex) { Log($"自动保存规则失败: {ex.Message}"); }
    }

    private void AutoRemoveRules(List<string> processNames)
    {
        try
        {
            var existing = RulesBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            bool changed = false;
            foreach (var name in processNames)
            {
                if (existing.RemoveAll(r => r.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0)
                    changed = true;
            }
            if (changed)
            {
                RulesBox.Text = string.Join("\n", existing);
                File.WriteAllText(_rulesPath, RulesBox.Text);
                Log($"自动移除规则: {string.Join(", ", processNames)}");
            }
        }
        catch (Exception ex) { Log($"自动移除规则失败: {ex.Message}"); }
    }

    private void ApplyRules()
    {
        try
        {
            string rulesText = RulesBox.Text.Trim();
            if (string.IsNullOrEmpty(rulesText)) return;

            var rules = rulesText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (rules.Length == 0) return;

            var icons = HideService.EnumerateTrayIcons();
            var toHide = new List<string>();

            foreach (var rule in rules)
            {
                foreach (var icon in icons)
                {
                    if (icon.ProcessName.Contains(rule, StringComparison.OrdinalIgnoreCase) ||
                        icon.Tooltip.Contains(rule, StringComparison.OrdinalIgnoreCase))
                    {
                        toHide.Add(icon.ProcessName);
                    }
                }
            }

            toHide = toHide.Distinct().ToList();
            if (toHide.Count == 0) return;

            string identifierStr = string.Join(" ", toHide);
            Log($"定时隐藏: [{identifierStr}]");

            Task.Run(() =>
            {
                var (ok, msg) = HideService.Hide(identifierStr);
                Dispatcher.Invoke(() =>
                {
                    if (ok)
                        RuleStatus.Text = $"已自动隐藏 {toHide.Count} 个 ({DateTime.Now:HH:mm:ss})";
                    else
                        RuleStatus.Text = $"自动隐藏失败: {msg}";
                });
            });
        }
        catch (Exception ex) { Log($"应用规则异常: {ex.Message}"); }
    }

    private void ApplyRules_Click(object sender, RoutedEventArgs e)
    {
        ApplyRules();
        SafeDelay(2000, () => RefreshList());
    }

    private void AutoApply_Changed(object sender, RoutedEventArgs e)
    {
        if (_autoTimer == null || IntervalBox == null) return; // Guard: may fire during init
        if (AutoApplyCheck.IsChecked == true)
        {
            if (int.TryParse(IntervalBox.Text, out int sec) && sec > 0)
                _autoTimer.Interval = TimeSpan.FromSeconds(sec);
            else
                _autoTimer.Interval = TimeSpan.FromSeconds(10);

            _autoTimer.Start();
            RuleStatus.Text = $"定时隐藏已启动 (每{_autoTimer.Interval.TotalSeconds}秒)";
            Log($"定时隐藏已启动 (每{_autoTimer.Interval.TotalSeconds}秒)");
        }
        else
        {
            _autoTimer.Stop();
            RuleStatus.Text = "定时隐藏已停止";
            Log("定时隐藏已停止");
        }
    }

    // ========== Filter settings ==========

    private void LoadFilter()
    {
        try
        {
            if (File.Exists(_filterPath))
            {
                string text = File.ReadAllText(_filterPath);
                _filteredProcesses = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            else
            {
                // Default filter list
                _filteredProcesses = ["Taskmgr.exe", "Idle.exe"];
                SaveFilterFile();
            }
        }
        catch { _filteredProcesses = ["Taskmgr.exe", "Idle.exe"]; }
    }

    private void SaveFilterFile()
    {
        try { File.WriteAllText(_filterPath, string.Join("\n", _filteredProcesses)); }
        catch { }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow
        {
            Owner = this,
            FilterText = string.Join("\n", _filteredProcesses)
        };
        dlg.ShowDialog();

        if (dlg.Saved)
        {
            _filteredProcesses = dlg.FilterText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            SaveFilterFile();
            Log($"过滤进程已更新 ({_filteredProcesses.Count}): {string.Join(", ", _filteredProcesses)}");
            RefreshList();
        }
    }

    // ========== Log ==========

    private void Log(string msg)
    {
        string ts = DateTime.Now.ToString("HH:mm:ss.fff");
        if (LogBox != null)
        {
            LogBox.AppendText($"[{ts}] {msg}\n");
            LogBox.ScrollToEnd();
        }
        HideService.Log("INFO", msg);
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        string path = HideService.GetLogPath();
        if (File.Exists(path))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch (Exception ex) { Log($"打开日志失败: {ex.Message}"); }
        }
        else { Log($"日志文件不存在: {path}"); }
    }

    /// <summary>
    /// Safe delayed action using DispatcherTimer (avoids BeginInvoke TimeSpan bug).
    /// </summary>
    private void SafeDelay(int ms, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (s, e) => { timer.Stop(); action(); };
        timer.Start();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _autoTimer.Stop();
        _countTimer.Stop();
    }
}

public class IconItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status = "";

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
    }

    public string ProcessName { get; set; } = "";
    public string Tooltip { get; set; } = "";
    public string Area { get; set; } = "";

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
