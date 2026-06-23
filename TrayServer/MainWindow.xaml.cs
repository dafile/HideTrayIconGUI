using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Shared;
using TrayServer.Services;

namespace TrayServer;

public partial class MainWindow : Window
{
    private readonly ServerService _server = new();
    private readonly PersistenceService _persistence = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _cycleTimer;
    private readonly ObservableCollection<ClientItem> _clients = new();
    private readonly ObservableCollection<TrayIconItem> _trayIcons = new();
    private List<RuleInfo> _rules = [];
    private List<string> _filter = [];
    private Dictionary<string, string> _assignments = new();
    private Dictionary<string, string> _remarks = new();
    private string? _selectedClient;

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (s, e) => ClientGrid.Items.Refresh();
        _cycleTimer = new DispatcherTimer();
        _cycleTimer.Tick += (s, e) => CycleApply();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ClientGrid.ItemsSource = _clients;
        TrayIconGrid.ItemsSource = _trayIcons;

        _rules = _persistence.LoadRules();
        _filter = _persistence.LoadFilter();
        _assignments = _persistence.LoadAssignments();
        _remarks = _persistence.LoadRemarks();

        _server.OnLog += (level, msg) => Dispatcher.Invoke(() => Log(level, msg));
        _server.OnClientConnected += id => Dispatcher.Invoke(() =>
        {
            var info = _server.GetOnlineClients().FirstOrDefault(c => c.HostName == id);
            var existing = _clients.FirstOrDefault(c => c.HostName == id);
            if (existing != null)
            {
                existing.Status = "在线";
                existing.LastSeen = DateTime.Now;
                if (info != null && !string.IsNullOrEmpty(info.IpAddress))
                    existing.IpAddress = info.IpAddress;
            }
            else
            {
                _clients.Add(new ClientItem
                {
                    HostName = id,
                    IpAddress = info?.IpAddress ?? "",
                    Status = "在线",
                    ConnectedAt = DateTime.Now,
                    LastSeen = DateTime.Now,
                    RuleName = _assignments.GetValueOrDefault(id, ""),
                    Remark = _remarks.GetValueOrDefault(id, ""),
                });
            }
            UpdateServerStatus();
        });
        _server.OnClientDisconnected += id => Dispatcher.Invoke(() =>
        {
            var item = _clients.FirstOrDefault(c => c.HostName == id);
            if (item != null) item.Status = "离线";
            UpdateServerStatus();
        });
        _server.OnTrayIconsReceived += (clientId, icons) => Dispatcher.Invoke(() =>
        {
            var client = _clients.FirstOrDefault(c => c.HostName == clientId);
            if (client != null)
            {
                client.IconCount = icons.Count;
                if (string.IsNullOrEmpty(client.IpAddress))
                {
                    var info = _server.GetOnlineClients().FirstOrDefault(c => c.HostName == clientId);
                    if (info != null) client.IpAddress = info.IpAddress;
                }
            }
            if (_selectedClient == clientId)
            {
                _trayIcons.Clear();
                foreach (var icon in icons)
                    _trayIcons.Add(new TrayIconItem
                    {
                        ProcessName = icon.ProcessName,
                        Tooltip = icon.Tooltip,
                        Area = icon.Area,
                        Status = icon.Status
                    });
                TrayIconTitle.Text = $"客户端托盘图标 ({icons.Count} 个)";
            }
        });

        _server.Start();
        _refreshTimer.Start();
        UpdateServerStatus();

        foreach (var c in _persistence.LoadClients())
        {
            if (_clients.Any(x => x.HostName == c.HostName)) continue;
            _clients.Add(new ClientItem
            {
                HostName = c.HostName, IpAddress = c.IpAddress, Status = "离线",
                ConnectedAt = c.ConnectedAt,
                RuleName = _assignments.GetValueOrDefault(c.HostName, ""),
                Remark = _remarks.GetValueOrDefault(c.HostName, ""),
            });
        }

        Log("INFO", $"启动完成 | 规则: {_rules.Count} | 过滤: {_filter.Count} | 客户端: {_clients.Count}");
    }

    // ========== Client actions ==========

    private void RefreshClients_Click(object sender, RoutedEventArgs e)
    {
        foreach (var info in _server.GetOnlineClients())
        {
            var c = _clients.FirstOrDefault(x => x.HostName == info.HostName);
            if (c != null) { c.Status = info.IsOnline ? "在线" : "离线"; c.IpAddress = info.IpAddress; c.LastSeen = info.LastSeen; }
        }
        UpdateServerStatus();
    }

    private void ViewTrayIcons_Click(object sender, RoutedEventArgs e)
    {
        var mi = (MenuItem)sender;
        var cm = (ContextMenu)mi.Parent;
        if (cm.DataContext is not ClientItem client) return;
        _selectedClient = client.HostName;
        _trayIcons.Clear();
        TrayIconTitle.Text = $"客户端托盘图标 (请求中...)";
        Log("INFO", $"请求 {client.HostName} 的托盘图标");
        _server.SendToClient(client.HostName, new ProtocolMessage { Type = MsgType.GetTrayIcons });
    }

    private void RequestTrayIcons_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClient == null) return;
        _server.SendToClient(_selectedClient, new ProtocolMessage { Type = MsgType.GetTrayIcons });
    }

    private void RestartClient_Click(object sender, RoutedEventArgs e)
    {
        var mi = (MenuItem)sender;
        var cm = (ContextMenu)mi.Parent;
        if (cm.DataContext is not ClientItem client) return;
        _server.SendToClient(client.HostName, new ProtocolMessage { Type = MsgType.Restart });
        Log("INFO", $"发送重启命令到 {client.HostName}");
    }

    private void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        var mi = (MenuItem)sender;
        var cm = (ContextMenu)mi.Parent;
        if (cm.DataContext is not ClientItem client) return;
        _server.SendToClient(client.HostName, new ProtocolMessage { Type = MsgType.RestartExplorer });
        Log("INFO", $"发送重启资源管理器到 {client.HostName}");
    }

    private void RemoveClient_Click(object sender, RoutedEventArgs e)
    {
        var mi = (MenuItem)sender;
        var cm = (ContextMenu)mi.Parent;
        if (cm.DataContext is not ClientItem client) return;
        _clients.Remove(client);
        SaveClients();
    }

    private void ClearRule_Click(object sender, RoutedEventArgs e)
    {
        var mi = (MenuItem)sender;
        var cm = (ContextMenu)mi.Parent;
        if (cm.DataContext is not ClientItem client) return;
        client.RuleName = "";
        SaveAssignmentsAndRemarks();
        ClientGrid.Items.Refresh();
        Log("INFO", $"清除 {client.HostName} 的规则");
    }

    // ========== Context menu builders (via Row Loaded) ==========

    private void ClientRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGridRow row || row.DataContext is not ClientItem item) return;
        row.ContextMenu.DataContext = item;
        var assignMenu = row.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header.ToString() == "分配规则");
        if (assignMenu == null) return;
        assignMenu.Items.Clear();
        foreach (var rule in _rules)
        {
            var mi = new MenuItem { Header = rule.Name };
            var capturedRule = rule;
            mi.Click += (s2, e2) => AssignRuleToClient(item, capturedRule);
            assignMenu.Items.Add(mi);
        }
        if (_rules.Count == 0)
            assignMenu.Items.Add(new MenuItem { Header = "(无规则，请先创建)", IsEnabled = false });
    }

    private void TrayIconRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGridRow row || row.DataContext is not TrayIconItem item) return;
        row.ContextMenu.DataContext = item;
        var addMenu = row.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header.ToString() == "添加到规则");
        if (addMenu == null) return;
        addMenu.Items.Clear();
        foreach (var rule in _rules)
        {
            var mi = new MenuItem { Header = rule.Name };
            var capturedRule = rule;
            mi.Click += (s2, e2) => AddIconToRule(item, capturedRule);
            addMenu.Items.Add(mi);
        }
        if (_rules.Count == 0)
            addMenu.Items.Add(new MenuItem { Header = "(无规则)", IsEnabled = false });
    }

    private void AssignRuleToClient(ClientItem client, RuleInfo rule)
    {
        client.RuleName = rule.Name;
        SaveAssignmentsAndRemarks();
        ClientGrid.Items.Refresh();
        var config = new ClientConfig { Rules = _rules, Filter = _filter };
        _server.SendToClient(client.HostName, ProtocolMessage.Create(MsgType.UpdateRules, config));
        Log("INFO", $"分配规则 [{rule.Name}] 到 {client.HostName}");
    }

    private void AddIconToRule(TrayIconItem icon, RuleInfo rule)
    {
        if (rule.Entries.Any(x => x.ProcessName.Equals(icon.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            Log("INFO", $"{icon.ProcessName} 已在规则 [{rule.Name}] 中");
            return;
        }
        rule.Entries.Add(new RuleEntry { ProcessName = icon.ProcessName, Tooltip = icon.Tooltip });
        _persistence.SaveRules(_rules);
        Log("INFO", $"已添加 {icon.ProcessName} 到规则 [{rule.Name}]");
        PushRulesToAllClients();
    }

    // ========== Toolbar actions ==========

    private void RestartExplorerSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _clients.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) { Log("WARN", "请先勾选客户端"); return; }
        foreach (var c in selected)
            _server.SendToClient(c.HostName, new ProtocolMessage { Type = MsgType.RestartExplorer });
        Log("INFO", $"发送重启资源管理器到 {selected.Count} 台客户端");
    }

    private void BatchHide_Click(object sender, RoutedEventArgs e)
    {
        var selectedClients = _clients.Where(c => c.IsSelected).ToList();
        if (selectedClients.Count == 0) { Log("WARN", "请先勾选客户端"); return; }
        var selectedIcons = _trayIcons.Where(i => i.IsSelected).ToList();
        if (selectedIcons.Count == 0) { Log("WARN", "请先勾选托盘图标"); return; }
        var ids = selectedIcons.Select(i => i.ProcessName).Distinct().ToList();
        var msg = ProtocolMessage.Create(MsgType.Hide, new HideRequest { Identifiers = ids });
        _server.SendToMany(selectedClients.Select(c => c.HostName), msg);
        Log("INFO", $"批量隐藏 {selectedClients.Count} 台客户端: {string.Join(", ", ids)}");
    }

    private void BatchApplyRules_Click(object sender, RoutedEventArgs e)
    {
        var selectedClients = _clients.Where(c => c.IsSelected).ToList();
        if (selectedClients.Count == 0) { Log("WARN", "请先勾选客户端"); return; }
        if (_rules.Count == 0) { Log("WARN", "没有可用规则"); return; }

        var dlg = new RuleSelectDialog(_rules.Select(r => r.Name).ToList()) { Owner = this };
        dlg.ShowDialog();
        if (!dlg.Saved || dlg.SelectedRuleName == null) return;

        var rule = _rules.FirstOrDefault(r => r.Name == dlg.SelectedRuleName);
        if (rule == null) return;

        foreach (var c in selectedClients) c.RuleName = rule.Name;
        SaveAssignmentsAndRemarks();

        var config = new ClientConfig { Rules = _rules, Filter = _filter };
        _server.SendToMany(selectedClients.Select(c => c.HostName), ProtocolMessage.Create(MsgType.UpdateRules, config));
        Log("INFO", $"批量分配规则 [{rule.Name}] 到 {selectedClients.Count} 台客户端");
        ClientGrid.Items.Refresh();
    }

    private void HideSingleIcon_Click(object sender, RoutedEventArgs e)
    {
        var mi = (MenuItem)sender;
        var cm = (ContextMenu)mi.Parent;
        if (cm.DataContext is not TrayIconItem icon || _selectedClient == null) return;
        var msg = ProtocolMessage.Create(MsgType.Hide, new HideRequest { Identifiers = [icon.ProcessName] });
        _server.SendToClient(_selectedClient, msg);
        Log("INFO", $"隐藏 {_selectedClient}: {icon.ProcessName}");
    }

    private void CopyProcessName_Click(object sender, RoutedEventArgs e)
    {
        var mi = (MenuItem)sender;
        var cm = (ContextMenu)mi.Parent;
        if (cm.DataContext is TrayIconItem icon) System.Windows.Clipboard.SetText(icon.ProcessName);
    }

    private void CopyTooltip_Click(object sender, RoutedEventArgs e)
    {
        var mi = (MenuItem)sender;
        var cm = (ContextMenu)mi.Parent;
        if (cm.DataContext is TrayIconItem icon) System.Windows.Clipboard.SetText(icon.Tooltip);
    }

    // ========== Rules & Settings ==========

    private void RulesSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RuleManagerWindow(_rules)
        {
            Owner = this,
            OnRulesChanged = () => { _persistence.SaveRules(_rules); PushRulesToAllClients(); ClientGrid.Items.Refresh(); }
        };
        dlg.ShowDialog();
    }

    private void FilterSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("过滤设置", "每行一条进程名", string.Join("\n", _filter)) { Owner = this };
        dlg.ShowDialog();
        if (dlg.Saved)
        {
            _filter = dlg.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            _persistence.SaveFilter(_filter);
            Log("INFO", $"过滤已保存: {_filter.Count} 条");
        }
    }

    private void PushRulesToAllClients()
    {
        var config = new ClientConfig { Rules = _rules, Filter = _filter };
        _server.SendToAll(ProtocolMessage.Create(MsgType.UpdateRules, config));
        Log("INFO", $"推送规则到所有客户端: {_rules.Count} 条规则");
    }

    // ========== Cycle timer ==========

    private void CycleEnabled_Changed(object sender, RoutedEventArgs e) => UpdateCycleInterval();

    private void CycleInterval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CustomCycleBox == null || CycleIntervalCombo == null) return;
        string selected = (CycleIntervalCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        CustomCycleBox.Visibility = selected == "自定义" ? Visibility.Visible : Visibility.Collapsed;
        UpdateCycleInterval();
    }

    private void UpdateCycleInterval()
    {
        _cycleTimer.Stop();
        if (CycleEnabledCheck.IsChecked != true) return;
        int seconds = GetCycleSeconds();
        _cycleTimer.Interval = TimeSpan.FromSeconds(seconds);
        _cycleTimer.Start();
        Log("INFO", $"循环应用已启动 (每{seconds}秒)");
    }

    private int GetCycleSeconds()
    {
        string selected = (CycleIntervalCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "10秒";
        return selected switch
        {
            "1秒" => 1, "2秒" => 2, "3秒" => 3, "5秒" => 5, "10秒" => 10,
            "自定义" => int.TryParse(CustomCycleBox.Text, out int v) && v > 0 ? v : 10,
            _ => 10
        };
    }

    private void CycleApply()
    {
        foreach (var client in _clients.Where(c => c.Status == "在线"))
        {
            if (string.IsNullOrEmpty(client.RuleName)) continue;
            var rule = _rules.FirstOrDefault(r => r.Name == client.RuleName);
            if (rule == null || rule.Entries.Count == 0) continue;
            var ids = rule.Entries.Select(e => e.ProcessName).Distinct().ToList();
            _server.SendToClient(client.HostName, ProtocolMessage.Create(MsgType.Hide, new HideRequest { Identifiers = ids }));
        }
    }

    private void ClientGrid_RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(() => { SaveAssignmentsAndRemarks(); Log("INFO", "备注已自动保存"); });
    }

    // ========== Helpers ==========

    private void UpdateServerStatus()
    {
        int online = _clients.Count(c => c.Status == "在线");
        ServerStatus.Text = $"客户端: {online} 在线 / {_clients.Count} 总计 | 端口: 9527";
    }

    private void SaveClients()
    {
        _persistence.SaveClients(_clients.Select(c => new ClientInfo
        { HostName = c.HostName, IpAddress = c.IpAddress, ConnectedAt = c.ConnectedAt, LastSeen = c.LastSeen }).ToList());
    }

    private void SaveAssignmentsAndRemarks()
    {
        _assignments = _clients.Where(c => !string.IsNullOrEmpty(c.RuleName)).ToDictionary(c => c.HostName, c => c.RuleName);
        _remarks = _clients.Where(c => !string.IsNullOrEmpty(c.Remark)).ToDictionary(c => c.HostName, c => c.Remark);
        _persistence.SaveAssignments(_assignments);
        _persistence.SaveRemarks(_remarks);
    }

    private void Log(string level, string msg)
    {
        try
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            if (LogBox != null) { LogBox.AppendText($"[{ts}] [{level}] {msg}\n"); LogBox.ScrollToEnd(); }
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, $"server_{DateTime.Now:yyyyMMdd}.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {msg}\n");
        }
        catch { }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveClients(); SaveAssignmentsAndRemarks();
        _server.Dispose(); _cycleTimer.Stop(); _refreshTimer.Stop();
    }
}

// ========== View Models ==========

public class ClientItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status = "离线";
    private int _iconCount;
    private DateTime _lastSeen;
    private string _ruleName = "";
    private string _remark = "";
    private string _ipAddress = "";

    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
    public string HostName { get; set; } = "";
    public string IpAddress { get => _ipAddress; set { _ipAddress = value; OnPropertyChanged(nameof(IpAddress)); } }
    public string Status { get => _status; set { _status = value; OnPropertyChanged(nameof(Status)); } }
    public string RuleName { get => _ruleName; set { _ruleName = value; OnPropertyChanged(nameof(RuleName)); } }
    public string Remark { get => _remark; set { _remark = value; OnPropertyChanged(nameof(Remark)); } }
    public DateTime ConnectedAt { get; set; }
    public DateTime LastSeen { get => _lastSeen; set { _lastSeen = value; OnPropertyChanged(nameof(LastSeen)); } }
    public int IconCount { get => _iconCount; set { _iconCount = value; OnPropertyChanged(nameof(IconCount)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class TrayIconItem : INotifyPropertyChanged
{
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
    public string ProcessName { get; set; } = "";
    public string Tooltip { get; set; } = "";
    public string Area { get; set; } = "";
    public string Status { get; set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
