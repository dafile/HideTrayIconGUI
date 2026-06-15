using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace HideTrayIconGUI;

public partial class MainWindow : Window
{
    private readonly string _rulesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rules.txt");
    private readonly string _filterPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "filter.txt");
    private readonly string _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
    private readonly DispatcherTimer _autoTimer;
    private readonly DispatcherTimer _countTimer;
    private readonly ObservableCollection<IconItem> _allIcons = new();
    private readonly ObservableCollection<IconItem> _viewIcons = new();
    private string _currentFilter = "全部";
    private List<string> _filteredProcesses = [];
    private bool _minimizeToTray = true; // default: minimize to tray on close
    private Forms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();

        _autoTimer = new DispatcherTimer();
        _autoTimer.Tick += (s, e) => ApplyRules();

        _countTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _countTimer.Tick += (s, e) => { try { UpdateCount(); } catch { } };

        InitTrayIcon();
    }

    // ========== Tray Icon ==========

    private void InitTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon();
        _trayIcon.Icon = CreateAppIcon();
        _trayIcon.Text = "HideTrayIcon GUI";
        _trayIcon.Visible = false;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (s, e) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("立即应用规则", null, (s, e) => { ApplyRules(); SafeDelay(2000, RefreshList); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (s, e) => ExitApp());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (s, e) => ShowFromTray();
    }

    private static Drawing.Icon CreateAppIcon()
    {
        using var bmp = new Drawing.Bitmap(16, 16);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Drawing.Color.Transparent);
        // Blue circle with "H"
        using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x19, 0x76, 0xD2));
        g.FillEllipse(bg, 0, 0, 15, 15);
        using var pen = new Drawing.Pen(Drawing.Color.White, 2);
        // Draw "H" shape
        g.DrawLine(pen, 4, 4, 4, 12);  // left stroke
        g.DrawLine(pen, 11, 4, 11, 12); // right stroke
        g.DrawLine(pen, 4, 8, 11, 8);   // crossbar
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon != null) _trayIcon.Visible = false;
    }

    private void MinimizeToTray()
    {
        Hide();
        if (_trayIcon != null) _trayIcon.Visible = true;
    }

    private void ExitApp()
    {
        _trayIcon?.Dispose();
        _autoTimer.Stop();
        _countTimer.Stop();
        System.Windows.Application.Current.Shutdown();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _minimizeToTray)
        {
            MinimizeToTray();
        }
    }

    // ========== Load ==========

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            IconGrid.ItemsSource = _viewIcons;
            _countTimer.Start();

            LoadConfig();
            LoadFilter();
            LoadRules();
            RefreshList();

            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hideTrayIcon.exe");
            Log($"程序启动 | hideTrayIcon.exe: {(File.Exists(exePath) ? "已找到" : "未找到")}");
            if (_filteredProcesses.Count > 0)
                Log($"过滤: {string.Join(", ", _filteredProcesses)}");

            if (!string.IsNullOrEmpty(RulesBox?.Text?.Trim() ?? ""))
            {
                Log("启动时自动应用规则...");
                ApplyRules();
            }
        }
        catch (Exception ex)
        {
            string msg = $"启动错误: {ex.GetType().Name}\n{ex.Message}\n\n{ex.StackTrace}";
            HideService.Log("CRASH", msg);
            System.Windows.MessageBox.Show(msg, "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    // ========== Config ==========

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                foreach (var line in File.ReadAllLines(_configPath))
                {
                    if (line.StartsWith("MinimizeToTray="))
                        _minimizeToTray = line.Split('=')[1].Trim() == "true";
                }
            }
            else
            {
                SaveConfig();
            }
        }
        catch { }
    }

    private void SaveConfig()
    {
        try { File.WriteAllText(_configPath, $"MinimizeToTray={_minimizeToTray.ToString().ToLower()}\n"); }
        catch { }
    }

    public bool MinimizeToTraySetting
    {
        get => _minimizeToTray;
        set { _minimizeToTray = value; SaveConfig(); }
    }

    // ========== Icon list ==========

    private void RefreshList()
    {
        try
        {
            _allIcons.Clear();
            var icons = HideService.EnumerateTrayIcons();
            if (icons == null) { Log("EnumerateTrayIcons 返回 null"); return; }

            foreach (var icon in icons)
            {
                if (icon == null) continue;
                if (_filteredProcesses != null && _filteredProcesses.Any(f =>
                    icon.ProcessName != null && icon.ProcessName.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Collapse multi-line tooltip to single line (prevents row height issues)
                string tooltip = (icon.Tooltip ?? "").Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();

                _allIcons.Add(new IconItem
                {
                    IsSelected = false,
                    ProcessName = icon.ProcessName ?? "",
                    Tooltip = tooltip,
                    Area = icon.Area ?? "",
                    Status = icon.Status ?? ""
                });
            }

            // Merge rules (hidden items that may have been hard-deleted)
            var rules = GetCurrentRules();
            var existingNames = _allIcons.Select(i => i.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

                if (!existingNames.Contains(procName))
                {
                    _allIcons.Add(new IconItem
                    {
                        IsSelected = false,
                        ProcessName = procName,
                        Tooltip = tooltip,
                        Area = "规则",
                        Status = "已隐藏"
                    });
                }
            }

            ApplyViewFilter();
            Log($"刷新: {_allIcons.Count} 个图标");
        }
        catch (Exception ex)
        {
            Log($"刷新异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private List<string> GetCurrentRules()
    {
        try
        {
            if (File.Exists(_rulesPath))
                return File.ReadAllText(_rulesPath)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
        }
        catch { }
        return [];
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
            foreach (var item in filtered) _viewIcons.Add(item);
            UpdateCount();
        }
        catch (Exception ex) { Log($"ApplyViewFilter: {ex.Message}"); }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_viewIcons == null || _allIcons == null) return;
        if (FilterCombo?.SelectedItem is ComboBoxItem item)
        {
            _currentFilter = item.Content?.ToString() ?? "全部";
            ApplyViewFilter();
        }
    }

    private void UpdateCount()
    {
        if (StatusText == null) return;
        int total = _allIcons.Count;
        int visible = _allIcons.Count(i => i.Status == "可见");
        int hidden = _allIcons.Count(i => i.Status == "已隐藏");
        int selected = _allIcons.Count(i => i.IsSelected);
        int viewing = _viewIcons.Count;
        string view = _currentFilter == "全部" ? "" : $" (显示 {viewing})";
        StatusText.Text = $"共 {total} | 可见 {visible} | 已隐藏 {hidden} | 已选 {selected}{view}";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var i in _viewIcons) i.IsSelected = true; UpdateCount(); }
    private void DeselectAll_Click(object sender, RoutedEventArgs e) { foreach (var i in _allIcons) i.IsSelected = false; UpdateCount(); }

    // ========== Right-click context menu ==========

    private void IconGrid_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Find the row under the cursor
        var depObj = e.OriginalSource as DependencyObject;
        while (depObj != null && depObj is not DataGridRow)
            depObj = System.Windows.Media.VisualTreeHelper.GetParent(depObj);

        if (depObj is DataGridRow row && row.DataContext is IconItem item)
        {
            IconGrid.SelectedItem = item;
            var menu = (ContextMenu)FindResource("IconContextMenu");
            menu.DataContext = item;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void CtxHide_Click(object sender, RoutedEventArgs e)
    {
        if (IconGrid.SelectedItem is not IconItem item) return;
        HideSingle(item);
    }

    private void CtxShow_Click(object sender, RoutedEventArgs e)
    {
        if (IconGrid.SelectedItem is not IconItem item) return;
        ShowSingle(item);
    }

    private void CtxCopyName_Click(object sender, RoutedEventArgs e)
    {
        if (IconGrid.SelectedItem is IconItem item)
            System.Windows.Clipboard.SetText(item.ProcessName);
    }

    private void CtxCopyTooltip_Click(object sender, RoutedEventArgs e)
    {
        if (IconGrid.SelectedItem is IconItem item)
            System.Windows.Clipboard.SetText(item.Tooltip);
    }

    private void HideSingle(IconItem item)
    {
        Log($"右键隐藏: {item.ProcessName} ({item.Tooltip})");
        Task.Run(() => HideService.Hide(item.ProcessName)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                item.Status = "已隐藏";
                AutoSaveRule(item.ProcessName, item.Tooltip);
                Log($"已隐藏: {item.ProcessName}");
                SafeDelay(2000, RefreshList);
            });
        });
    }

    private void ShowSingle(IconItem item)
    {
        Log($"右键显示: {item.ProcessName}");
        Task.Run(() => HideService.Show(item.ProcessName)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                item.Status = "可见";
                AutoRemoveRule(item.ProcessName);
                Log($"已显示: {item.ProcessName}");
                SafeDelay(2000, RefreshList);
            });
        });
    }

    // ========== Hide / Show (batch) ==========

    private void HideSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _allIcons.Where(i => i.IsSelected).ToList();
        Log($"隐藏选中: {selected.Count} 个");
        if (selected.Count == 0) { StatusText.Text = "请先勾选"; return; }

        var ids = selected.Select(i => i.ProcessName).Distinct().ToList();
        string identifierStr = string.Join(" ", ids);
        StatusText.Text = "正在隐藏...";

        Task.Run(() => HideService.Hide(identifierStr)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var item in selected)
                {
                    item.Status = "已隐藏";
                    AutoSaveRule(item.ProcessName, item.Tooltip);
                }
                Log("隐藏命令已发送, 已自动保存规则");
                StatusText.Text = "隐藏完成";
                SafeDelay(2000, RefreshList);
            });
        });
    }

    private void ShowSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _allIcons.Where(i => i.IsSelected).ToList();
        Log($"显示选中: {selected.Count} 个");
        if (selected.Count == 0) { StatusText.Text = "请先勾选"; return; }

        var ids = selected.Select(i => i.ProcessName).Distinct().ToList();
        string identifierStr = string.Join(" ", ids);
        StatusText.Text = "正在显示...";

        Task.Run(() => HideService.Show(identifierStr)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var item in selected)
                {
                    item.Status = "可见";
                    AutoRemoveRule(item.ProcessName);
                }
                Log("显示命令已发送, 已自动移除规则");
                StatusText.Text = "显示完成";
                SafeDelay(2000, RefreshList);
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
                // File stores full format: "ProcessName|Tooltip"
                // Display only process names in the text box
                var lines = File.ReadAllText(_rulesPath)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var displayNames = lines.Select(line =>
                {
                    int sep = line.IndexOf('|');
                    return sep > 0 ? line[..sep] : line;
                });
                RulesBox.Text = string.Join("\n", displayNames);
                Log($"已加载规则 ({lines.Length} 条)");
            }
        }
        catch (Exception ex) { Log($"加载规则失败: {ex.Message}"); }
    }

    private void SaveRules_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // RulesBox only shows process names. Merge with existing tooltip data from file.
            var existingRules = GetCurrentRules(); // full format from file
            var tooltipMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in existingRules)
            {
                int sep = rule.IndexOf('|');
                if (sep > 0)
                    tooltipMap[rule[..sep]] = rule[(sep + 1)..];
            }

            // User-edited process names
            var names = RulesBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var merged = names.Select(name =>
            {
                string trimmed = name.Trim();
                if (tooltipMap.TryGetValue(trimmed, out string? tip))
                    return $"{trimmed}|{tip}";
                return trimmed;
            });

            File.WriteAllText(_rulesPath, string.Join("\n", merged));
            RuleStatus.Text = $"已保存 ({names.Length} 条)";
            Log($"规则已保存: {string.Join(", ", names)}");
        }
        catch (Exception ex) { Log($"保存失败: {ex.Message}"); }
    }

    /// <summary>
    /// Save rule in format: "ProcessName|TooltipText"
    /// </summary>
    private void AutoSaveRule(string processName, string tooltip)
    {
        try
        {
            var rules = GetCurrentRules();
            // Check if already exists (match by process name before '|')
            bool exists = rules.Any(r =>
            {
                string name = r.Contains('|') ? r[..r.IndexOf('|')] : r;
                return name.Equals(processName, StringComparison.OrdinalIgnoreCase);
            });

            if (!exists)
            {
                string rule = string.IsNullOrEmpty(tooltip)
                    ? processName
                    : $"{processName}|{tooltip}";
                rules.Add(rule);
                RulesBox.Text = string.Join("\n", rules);
                File.WriteAllText(_rulesPath, RulesBox.Text);
                Log($"自动添加规则: {rule}");
            }
        }
        catch (Exception ex) { Log($"自动保存规则失败: {ex.Message}"); }
    }

    private void AutoRemoveRule(string processName)
    {
        try
        {
            var rules = GetCurrentRules();
            var filtered = rules.Where(r =>
            {
                string name = r.Contains('|') ? r[..r.IndexOf('|')] : r;
                return !name.Equals(processName, StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (filtered.Count != rules.Count)
            {
                RulesBox.Text = string.Join("\n", filtered);
                File.WriteAllText(_rulesPath, RulesBox.Text);
                Log($"自动移除规则: {processName}");
            }
        }
        catch (Exception ex) { Log($"自动移除规则失败: {ex.Message}"); }
    }

    private void ApplyRules()
    {
        try
        {
            string rulesText = RulesBox?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(rulesText)) return;

            var rules = rulesText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (rules.Length == 0) return;

            // Extract process names from rules (format: "ProcessName|Tooltip" or just "ProcessName")
            var identifiers = new List<string>();
            foreach (var rule in rules)
            {
                string name = rule.Contains('|') ? rule[..rule.IndexOf('|')] : rule;
                if (!string.IsNullOrWhiteSpace(name))
                    identifiers.Add(name.Trim());
            }

            if (identifiers.Count == 0) return;

            string identifierStr = string.Join(" ", identifiers.Distinct());
            Log($"定时隐藏: [{identifierStr}]");

            Task.Run(() => HideService.Hide(identifierStr)).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    RuleStatus.Text = $"已自动隐藏 ({DateTime.Now:HH:mm:ss})";
                });
            });
        }
        catch (Exception ex) { Log($"应用规则异常: {ex.Message}"); }
    }

    private void ApplyRules_Click(object sender, RoutedEventArgs e)
    {
        ApplyRules();
        SafeDelay(2000, RefreshList);
    }

    private void AutoApply_Changed(object sender, RoutedEventArgs e)
    {
        if (_autoTimer == null || IntervalBox == null) return;
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

    // ========== Hidden list window ==========

    private void HiddenList_Click(object sender, RoutedEventArgs e)
    {
        var win = new HiddenListWindow
        {
            Owner = this,
            OnRulesChanged = () => { LoadRules(); RefreshList(); }
        };
        win.ShowDialog();
    }

    // ========== Filter settings ==========

    private void LoadFilter()
    {
        try
        {
            if (File.Exists(_filterPath))
                _filteredProcesses = File.ReadAllText(_filterPath)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            else
            {
                _filteredProcesses = ["Taskmgr", "Idle"];
                SaveFilterFile();
            }
        }
        catch { _filteredProcesses = ["Taskmgr", "Idle"]; }
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
            FilterText = string.Join("\n", _filteredProcesses),
            MinimizeToTray = _minimizeToTray
        };
        dlg.ShowDialog();

        if (dlg.Saved)
        {
            _filteredProcesses = dlg.FilterText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            SaveFilterFile();
            _minimizeToTray = dlg.MinimizeToTray;
            SaveConfig();
            Log($"设置已保存 | 过滤: {string.Join(", ", _filteredProcesses)} | 关闭时最小化到托盘: {_minimizeToTray}");
            RefreshList();
        }
    }

    // ========== Window behavior ==========

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_minimizeToTray)
        {
            e.Cancel = true;
            MinimizeToTray();
        }
        else
        {
            ExitApp();
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
        else { Log($"日志不存在: {path}"); }
    }

    private void SafeDelay(int ms, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (s, ev) => { timer.Stop(); action(); };
        timer.Start();
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
