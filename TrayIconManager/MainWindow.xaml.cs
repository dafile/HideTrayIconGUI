using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TrayIconManager.Models;
using TrayIconManager.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfApp = System.Windows.Application;

namespace TrayIconManager;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly TrayIconService _trayService;
    private readonly RuleManagerService _ruleService;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _refreshTimer;
    private Forms.NotifyIcon? _notifyIcon;
    private bool _isRefreshing;

    public ObservableCollection<TrayIconInfo> TrayIcons { get; } = new();
    public ObservableCollection<HideRule> Rules { get; } = new();

    private int _iconCount;
    public int IconCount
    {
        get => _iconCount;
        set { _iconCount = value; OnPropertyChanged(nameof(IconCount)); }
    }

    private AppSettings _settings = new();
    public AppSettings Settings
    {
        get => _settings;
        set { _settings = value; OnPropertyChanged(nameof(Settings)); }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _trayService = new TrayIconService();
        _settingsService = new SettingsService();
        _ruleService = new RuleManagerService(_trayService);

        Settings = _settingsService.Settings;

        _ruleService.LogMessage += msg => Dispatcher.Invoke(() => Log(msg));
        _ruleService.RulesApplied += count => Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"已自动应用 {count} 条规则";
            // Delay refresh to let hideTrayIcon.exe take effect
            Dispatcher.BeginInvoke(() => RefreshTrayIcons(), DispatcherPriority.Background,
                TimeSpan.FromMilliseconds(1500));
        });

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _refreshTimer.Tick += (s, e) => RefreshTrayIcons();

        InitNotifyIcon();
    }

    private void InitNotifyIcon()
    {
        _notifyIcon = new Forms.NotifyIcon();
        _notifyIcon.Icon = CreateDefaultIcon();
        _notifyIcon.Text = "TrayIconManager";
        _notifyIcon.Visible = false;

        var menu = new Forms.ContextMenuStrip();
        var showItem = new Forms.ToolStripMenuItem("显示主窗口");
        showItem.Click += (s, e) => ShowMainWindow();
        var refreshItem = new Forms.ToolStripMenuItem("刷新托盘图标");
        refreshItem.Click += (s, e) => RefreshTrayIcons();
        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (s, e) => ExitApplication();

        menu.Items.Add(showItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
    }

    private static Drawing.Icon CreateDefaultIcon()
    {
        using var bmp = new Drawing.Bitmap(16, 16);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Drawing.Color.Transparent);
        using var bgBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x19, 0x76, 0xD2));
        g.FillEllipse(bgBrush, 1, 1, 14, 14);
        using var textBrush = new Drawing.SolidBrush(Drawing.Color.White);
        using var font = new Drawing.Font("Arial", 9, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        var sf = new System.Drawing.StringFormat
        {
            Alignment = System.Drawing.StringAlignment.Center,
            LineAlignment = System.Drawing.StringAlignment.Center
        };
        g.DrawString("T", font, textBrush, 8, 8, sf);
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_notifyIcon != null) _notifyIcon.Visible = false;
    }

    private void ExitApplication()
    {
        Cleanup();
        _notifyIcon?.Dispose();
        WpfApp.Current.Shutdown();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (var rule in _ruleService.Rules)
            Rules.Add(rule);

        RefreshTrayIcons();

        if (Settings.AutoStartPolling)
            StartPolling();

        if (Settings.StartMinimized)
        {
            WindowState = WindowState.Minimized;
            Hide();
            if (_notifyIcon != null) _notifyIcon.Visible = true;
        }

        _refreshTimer.Start();
        StatusText.Text = "就绪";
    }

    private void RefreshTrayIcons()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;

        try
        {
            var icons = _trayService.EnumerateTrayIcons();

            TrayIcons.Clear();
            foreach (var icon in icons)
                TrayIcons.Add(icon);

            // Mark icons matching active rules
            _ruleService.GetMatchingIcons([.. TrayIcons]);

            IconCount = TrayIcons.Count;
            StatusText.Text = $"已刷新，共 {IconCount} 个托盘图标";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"刷新失败: {ex.Message}";
            Log($"Refresh error: {ex}");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshTrayIcons();

    private void HideSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = TrayIcons.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "请先勾选要隐藏的图标";
            return;
        }

        StatusText.Text = $"正在隐藏 {selected.Count} 个图标...";
        Log($"Hiding {selected.Count} icons...");

        Task.Run(() =>
        {
            int success = 0;
            int fail = 0;
            foreach (var icon in selected)
            {
                string identifier = GetBestIdentifier(icon);
                var (ok, stdout, stderr) = _trayService.HideByProcessName(identifier);
                if (ok)
                {
                    success++;
                    Dispatcher.Invoke(() => Log($"OK hide: {identifier}"));
                }
                else
                {
                    fail++;
                    Dispatcher.Invoke(() => Log($"FAIL hide: {identifier} | {stderr}"));
                }
            }

            Dispatcher.Invoke(() =>
            {
                StatusText.Text = $"隐藏完成: 成功{success} 失败{fail} | 日志: {Logger.GetLogFilePath()}";
                Dispatcher.BeginInvoke(() => RefreshTrayIcons(), DispatcherPriority.Background,
                    TimeSpan.FromMilliseconds(2000));
            });
        });
    }

    private void ShowSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = TrayIcons.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "请先勾选要显示的图标";
            return;
        }

        StatusText.Text = $"正在显示 {selected.Count} 个图标...";
        Log($"Showing {selected.Count} icons...");

        Task.Run(() =>
        {
            int success = 0;
            int fail = 0;
            foreach (var icon in selected)
            {
                string identifier = GetBestIdentifier(icon);
                var (ok, stdout, stderr) = _trayService.ShowByProcessName(identifier);
                if (ok)
                {
                    success++;
                    Dispatcher.Invoke(() => Log($"OK show: {identifier}"));
                }
                else
                {
                    fail++;
                    Dispatcher.Invoke(() => Log($"FAIL show: {identifier} | {stderr}"));
                }
            }

            Dispatcher.Invoke(() =>
            {
                StatusText.Text = $"显示完成: 成功{success} 失败{fail}";
                Dispatcher.BeginInvoke(() => RefreshTrayIcons(), DispatcherPriority.Background,
                    TimeSpan.FromMilliseconds(2000));
            });
        });
    }

    /// <summary>
    /// Get the best identifier for hideTrayIcon.exe.
    /// Prefer tooltip text (matches VBS script behavior), fallback to process name.
    /// hideTrayIcon.exe accepts both process name and tooltip text.
    /// </summary>
    private static string GetBestIdentifier(TrayIconInfo icon)
    {
        // The VBS script uses process name with .exe extension
        // hideTrayIcon.exe can match by tooltip or process name
        // Use tooltip if available (more specific), otherwise process name
        if (!string.IsNullOrEmpty(icon.TooltipText))
            return icon.TooltipText;
        return icon.ProcessName;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool isChecked = SelectAllCheckBox.IsChecked == true;
        // Don't call Items.Refresh() - the ObservableCollection binding handles it
        foreach (var icon in TrayIcons)
            icon.IsSelected = isChecked;
    }

    // Quick rule add
    private void AddQuickRule_Click(object sender, RoutedEventArgs e)
    {
        string matchText = QuickRuleText.Text.Trim();
        if (string.IsNullOrEmpty(matchText))
        {
            StatusText.Text = "请输入匹配文本";
            return;
        }

        var typeCombo = QuickRuleType.SelectedItem as ComboBoxItem;
        Models.MatchType matchType = typeCombo?.Content?.ToString() switch
        {
            "进程名" => Models.MatchType.ProcessName,
            "提示文本" => Models.MatchType.TooltipText,
            "两者皆可" => Models.MatchType.Both,
            _ => Models.MatchType.ProcessName
        };

        var rule = new HideRule
        {
            Name = $"规则-{matchText}",
            MatchText = matchText,
            MatchType = matchType,
            IsEnabled = true
        };

        _ruleService.AddRule(rule);
        Rules.Add(rule);
        QuickRuleText.Text = "";

        StatusText.Text = $"已添加规则: {rule.Name}";
        Log($"Added rule: {rule.Name} ({rule.MatchText})");
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        RuleNameBox.Text = "";
        RuleMatchBox.Text = "";
        RuleTypeBox.SelectedIndex = 0;
        RuleNameBox.Focus();
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not HideRule rule) return;
        _ruleService.RemoveRule(rule.Id);
        Rules.Remove(rule);
        StatusText.Text = $"已删除规则: {rule.Name}";
    }

    private void SaveRule_Click(object sender, RoutedEventArgs e)
    {
        string name = RuleNameBox.Text.Trim();
        string matchText = RuleMatchBox.Text.Trim();

        if (string.IsNullOrEmpty(matchText))
        {
            StatusText.Text = "匹配文本不能为空";
            return;
        }

        if (string.IsNullOrEmpty(name))
            name = $"规则-{matchText}";

        var typeCombo = RuleTypeBox.SelectedItem as ComboBoxItem;
        Models.MatchType matchType = typeCombo?.Content?.ToString() switch
        {
            "进程名" => Models.MatchType.ProcessName,
            "提示文本" => Models.MatchType.TooltipText,
            "两者皆可" => Models.MatchType.Both,
            _ => Models.MatchType.ProcessName
        };

        var existing = RulesGrid.SelectedItem as HideRule;
        if (existing != null)
        {
            existing.Name = name;
            existing.MatchText = matchText;
            existing.MatchType = matchType;
            _ruleService.UpdateRule(existing);
            RulesGrid.Items.Refresh();
            StatusText.Text = $"已更新规则: {name}";
        }
        else
        {
            var rule = new HideRule
            {
                Name = name,
                MatchText = matchText,
                MatchType = matchType,
                IsEnabled = true
            };
            _ruleService.AddRule(rule);
            Rules.Add(rule);
            StatusText.Text = $"已添加规则: {name}";
        }

        RuleNameBox.Text = "";
        RuleMatchBox.Text = "";
    }

    private void ApplyRules_Click(object sender, RoutedEventArgs e)
    {
        var activeRules = Rules.Where(r => r.IsEnabled).ToList();
        if (activeRules.Count == 0)
        {
            StatusText.Text = "没有启用的规则";
            return;
        }

        var icons = TrayIcons.ToList();
        var identifiersToHide = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in activeRules)
        {
            foreach (var icon in icons)
            {
                if (MatchesRule(icon, rule))
                {
                    string identifier = GetBestIdentifier(icon);
                    identifiersToHide.Add(identifier);
                    Log($"Rule '{rule.Name}' matched: {identifier}");
                }
            }
        }

        if (identifiersToHide.Count > 0)
        {
            // Batch all identifiers into a single call (like the VBS script)
            string allIdentifiers = string.Join(" ", identifiersToHide);
            Log($"Batch hiding: {allIdentifiers}");
            var (ok, stdout, stderr) = _trayService.HideByProcessName(allIdentifiers);
            if (ok)
                StatusText.Text = $"已批量隐藏 {identifiersToHide.Count} 个图标";
            else
                StatusText.Text = $"隐藏失败: {stderr}";
            Log($"Result: ok={ok}, stdout=[{stdout}], stderr=[{stderr}]");
        }
        else
        {
            StatusText.Text = "没有匹配到任何图标";
        }

        Dispatcher.BeginInvoke(() => RefreshTrayIcons(), DispatcherPriority.Background,
            TimeSpan.FromMilliseconds(1500));
    }

    private static bool MatchesRule(TrayIconInfo icon, HideRule rule)
    {
        string matchText = rule.MatchText;
        if (string.IsNullOrWhiteSpace(matchText)) return false;

        bool processMatch = icon.ProcessName.Contains(matchText, StringComparison.OrdinalIgnoreCase);
        bool tooltipMatch = icon.TooltipText.Contains(matchText, StringComparison.OrdinalIgnoreCase);

        return rule.MatchType switch
        {
            Models.MatchType.ProcessName => processMatch,
            Models.MatchType.TooltipText => tooltipMatch,
            Models.MatchType.Both => processMatch || tooltipMatch,
            _ => false
        };
    }

    private void TogglePolling_Click(object sender, RoutedEventArgs e)
    {
        if (_ruleService.IsPolling)
            StopPolling();
        else
            StartPolling();
    }

    private void StartPolling()
    {
        _ruleService.StartPolling(Settings.PollingIntervalSeconds, Settings);
        TogglePollingButton.Content = "停止轮询";
        PollingStatus.Text = $"轮询中 (每{Settings.PollingIntervalSeconds}秒)";
    }

    private void StopPolling()
    {
        _ruleService.StopPolling();
        TogglePollingButton.Content = "启动轮询";
        PollingStatus.Text = "轮询已停止";
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(PollingIntervalBox.Text, out int interval) && interval > 0)
            Settings.PollingIntervalSeconds = interval;

        _settingsService.Update(s =>
        {
            s.MinimizeToTrayOnClose = Settings.MinimizeToTrayOnClose;
            s.AutoStartPolling = Settings.AutoStartPolling;
            s.StartMinimized = Settings.StartMinimized;
            s.PollingIntervalSeconds = Settings.PollingIntervalSeconds;
        });

        if (_ruleService.IsPolling)
        {
            StopPolling();
            StartPolling();
        }

        StatusText.Text = "设置已保存";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (Settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            Hide();
            if (_notifyIcon != null) _notifyIcon.Visible = true;
        }
        else
        {
            Cleanup();
            _notifyIcon?.Dispose();
        }
    }

    private void Cleanup()
    {
        _refreshTimer.Stop();
        _ruleService.StopPolling();
        _trayService.Dispose();
    }

    private void TrayIconGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        e.Row.Header = (e.Row.GetIndex() + 1).ToString();
    }

    private void TrayIconGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TrayIconGrid.SelectedItem is TrayIconInfo icon)
        {
            // Toggle selection without calling Items.Refresh()
            icon.IsSelected = !icon.IsSelected;
        }
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        string logPath = Logger.GetLogFilePath();
        if (System.IO.File.Exists(logPath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = $"无法打开日志: {ex.Message}";
            }
        }
        else
        {
            StatusText.Text = $"日志文件不存在: {logPath}";
        }
    }

    private void Log(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogText.Text = $"[{timestamp}] {message}";
        Logger.Info("UI", message);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
