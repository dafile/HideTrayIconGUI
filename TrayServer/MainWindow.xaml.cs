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
        _refreshTimer.Tick += (s, e) => { /* refresh display */ };
        _cycleTimer = new DispatcherTimer();
        _cycleTimer.Tick += (s, e) => CycleApply();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ClientList.ItemsSource = _clients;
        TrayIconList.ItemsSource = _trayIcons;

        // Add context menus
        ClientList.MouseRightButtonUp += ClientList_RightClick;
        TrayIconList.MouseRightButtonUp += TrayIconList_RightClick;

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
            UpdateStatus();
        });
        _server.OnClientDisconnected += id => Dispatcher.Invoke(() =>
        {
            var item = _clients.FirstOrDefault(c => c.HostName == id);
            if (item != null) item.Status = "离线";
            UpdateStatus();
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
        UpdateStatus();

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

    // ========== Client context menu ==========

    private void ClientList_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = FindListViewItem<ClientItem>(e.OriginalSource as DependencyObject);
        if (item == null) return;
        ClientList.SelectedItem = item;

        var menu = new ContextMenu();
        var viewItem = new MenuItem { Header = "查看托盘图标" };
        viewItem.Click += (s2, e2) => { _selectedClient = item.HostName; _trayIcons.Clear(); TrayIconTitle.Text = "客户端托盘图标 (请求中...)"; _server.SendToClient(item.HostName, new ProtocolMessage { Type = MsgType.GetTrayIcons }); };
        menu.Items.Add(viewItem);
        menu.Items.Add(new Separator());

        var assignItem = new MenuItem { Header = "分配规则" };
        foreach (var rule in _rules)
        {
            var mi = new MenuItem { Header = rule.Name };
            var captured = rule;
            mi.Click += (s2, e2) => AssignRule(item, captured);
            assignItem.Items.Add(mi);
        }
        if (_rules.Count == 0) assignItem.Items.Add(new MenuItem { Header = "(无规则)", IsEnabled = false });
        menu.Items.Add(assignItem);

        var clearItem = new MenuItem { Header = "清除规则" };
        clearItem.Click += (s2, e2) => { item.RuleName = ""; SaveMeta(); };
        menu.Items.Add(clearItem);
        menu.Items.Add(new Separator());

        var restartItem = new MenuItem { Header = "重启客户端" };
        restartItem.Click += (s2, e2) => { _server.SendToClient(item.HostName, new ProtocolMessage { Type = MsgType.Restart }); Log("INFO", $"重启 {item.HostName}"); };
        menu.Items.Add(restartItem);

        var explorerItem = new MenuItem { Header = "重启资源管理器" };
        explorerItem.Click += (s2, e2) => { _server.SendToClient(item.HostName, new ProtocolMessage { Type = MsgType.RestartExplorer }); Log("INFO", $"重启资源管理器 {item.HostName}"); };
        menu.Items.Add(explorerItem);
        menu.Items.Add(new Separator());

        var removeItem = new MenuItem { Header = "移除客户端" };
        removeItem.Click += (s2, e2) => { _clients.Remove(item); SaveClients(); };
        menu.Items.Add(removeItem);

        ClientList.ContextMenu = menu;
    }

    private void TrayIconList_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = FindListViewItem<TrayIconItem>(e.OriginalSource as DependencyObject);
        if (item == null) return;
        TrayIconList.SelectedItem = item;

        var menu = new ContextMenu();
        var hideItem = new MenuItem { Header = "隐藏此项" };
        hideItem.Click += (s2, e2) => { if (_selectedClient != null) _server.SendToClient(_selectedClient, ProtocolMessage.Create(MsgType.Hide, new HideRequest { Identifiers = [item.ProcessName] })); };
        menu.Items.Add(hideItem);
        menu.Items.Add(new Separator());

        var addItem = new MenuItem { Header = "添加到规则" };
        foreach (var rule in _rules)
        {
            var mi = new MenuItem { Header = rule.Name };
            var captured = rule;
            mi.Click += (s2, e2) => AddIconToRule(item, captured);
            addItem.Items.Add(mi);
        }
        if (_rules.Count == 0) addItem.Items.Add(new MenuItem { Header = "(无规则)", IsEnabled = false });
        menu.Items.Add(addItem);
        menu.Items.Add(new Separator());

        var copyName = new MenuItem { Header = "复制进程名" };
        copyName.Click += (s2, e2) => Clipboard.SetText(item.ProcessName);
        menu.Items.Add(copyName);
        var copyTip = new MenuItem { Header = "复制提示文本" };
        copyTip.Click += (s2, e2) => Clipboard.SetText(item.Tooltip);
        menu.Items.Add(copyTip);

        TrayIconList.ContextMenu = menu;
    }

    private T? FindListViewItem<T>(DependencyObject? dep) where T : class
    {
        while (dep != null)
        {
            if (dep is ListViewItem lvi && lvi.DataContext is T t) return t;
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        }
        return null;
    }

    private void ClientList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = FindListViewItem<ClientItem>(e.OriginalSource as DependencyObject);
        if (item != null)
        {
            _selectedClient = item.HostName;
            _trayIcons.Clear();
            TrayIconTitle.Text = "客户端托盘图标 (请求中...)";
            _server.SendToClient(item.HostName, new ProtocolMessage { Type = MsgType.GetTrayIcons });
        }
    }

    private void AssignRule(ClientItem client, RuleInfo rule)
    {
        client.RuleName = rule.Name;
        SaveMeta();
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

    // ========== Toolbar ==========

    private void RefreshClients_Click(object sender, RoutedEventArgs e)
    {
        foreach (var info in _server.GetOnlineClients())
        {
            var c = _clients.FirstOrDefault(x => x.HostName == info.HostName);
            if (c != null) { c.Status = info.IsOnline ? "在线" : "离线"; c.IpAddress = info.IpAddress; c.LastSeen = info.LastSeen; }
        }
        UpdateStatus();
    }

    private void RequestTrayIcons_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClient == null) return;
        _server.SendToClient(_selectedClient, new ProtocolMessage { Type = MsgType.GetTrayIcons });
    }

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
        SaveMeta();

        var config = new ClientConfig { Rules = _rules, Filter = _filter };
        _server.SendToMany(selectedClients.Select(c => c.HostName), ProtocolMessage.Create(MsgType.UpdateRules, config));
        Log("INFO", $"批量分配规则 [{rule.Name}] 到 {selectedClients.Count} 台客户端");
    }

    private void Remark_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveMeta();
    }

    // ========== Rules & Settings ==========

    private void RulesSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RuleManagerWindow(_rules)
        {
            Owner = this,
            OnRulesChanged = () => { _persistence.SaveRules(_rules); PushRulesToAllClients(); }
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

    private void CycleEnabled_Changed(object sender, RoutedEventArgs e) => UpdateCycle();

    private void CycleInterval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CustomCycleBox == null || CycleIntervalCombo == null) return;
        string sel = (CycleIntervalCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        CustomCycleBox.Visibility = sel == "自定义" ? Visibility.Visible : Visibility.Collapsed;
        UpdateCycle();
    }

    private void UpdateCycle()
    {
        _cycleTimer.Stop();
        if (CycleEnabledCheck.IsChecked != true) return;
        int sec = (CycleIntervalCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
        {
            "1秒" => 1, "2秒" => 2, "3秒" => 3, "5秒" => 5, "10秒" => 10,
            "自定义" => int.TryParse(CustomCycleBox.Text, out int v) && v > 0 ? v : 10,
            _ => 10
        };
        _cycleTimer.Interval = TimeSpan.FromSeconds(sec);
        _cycleTimer.Start();
        Log("INFO", $"循环应用已启动 (每{sec}秒)");
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

    // ========== Helpers ==========

    private void UpdateStatus()
    {
        int online = _clients.Count(c => c.Status == "在线");
        ServerStatus.Text = $"客户端: {online} 在线 / {_clients.Count} 总计 | 端口: 9527";
    }

    private void SaveClients()
    {
        _persistence.SaveClients(_clients.Select(c => new ClientInfo
        { HostName = c.HostName, IpAddress = c.IpAddress, ConnectedAt = c.ConnectedAt, LastSeen = c.LastSeen }).ToList());
    }

    private void SaveMeta()
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
        SaveClients(); SaveMeta();
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
