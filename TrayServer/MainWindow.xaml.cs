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
    private readonly ObservableCollection<ClientItem> _clients = new();
    private readonly ObservableCollection<TrayIconItem> _trayIcons = new();
    private List<string> _rules = [];
    private List<string> _filter = [];
    private string? _selectedClient;

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (s, e) => ClientGrid.Items.Refresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ClientGrid.ItemsSource = _clients;
        TrayIconGrid.ItemsSource = _trayIcons;

        _rules = _persistence.LoadRules();
        _filter = _persistence.LoadFilter();

        // Wire up server events
        _server.OnLog += (level, msg) => Dispatcher.Invoke(() => Log(level, msg));
        _server.OnClientConnected += id => Dispatcher.Invoke(() =>
        {
            _clients.Add(new ClientItem { HostName = id, Status = "在线", ConnectedAt = DateTime.Now, LastSeen = DateTime.Now });
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
            // Update icon count on client
            var client = _clients.FirstOrDefault(c => c.HostName == clientId);
            if (client != null) client.IconCount = icons.Count;

            // If this client is selected, update the tray icon view
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
            }
        });

        _server.Start();
        _refreshTimer.Start();
        UpdateServerStatus();

        // Load persisted clients
        var savedClients = _persistence.LoadClients();
        foreach (var c in savedClients)
        {
            _clients.Add(new ClientItem
            {
                HostName = c.HostName,
                IpAddress = c.IpAddress,
                Status = "离线",
                ConnectedAt = c.ConnectedAt
            });
        }

        Log("INFO", $"Server started | 规则: {_rules.Count} | 过滤: {_filter.Count} | 已保存客户端: {savedClients.Count}");
    }

    // ========== Client management ==========

    private void RefreshClients_Click(object sender, RoutedEventArgs e)
    {
        _clients.Clear();
        foreach (var info in _server.GetOnlineClients())
        {
            _clients.Add(new ClientItem
            {
                HostName = info.HostName,
                IpAddress = info.IpAddress,
                Status = info.IsOnline ? "在线" : "离线",
                ConnectedAt = info.ConnectedAt,
                LastSeen = info.LastSeen
            });
        }
        UpdateServerStatus();
    }

    private void ClientGrid_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var depObj = e.OriginalSource as DependencyObject;
        while (depObj != null && depObj is not DataGridRow)
            depObj = System.Windows.Media.VisualTreeHelper.GetParent(depObj);

        if (depObj is DataGridRow row && row.DataContext is ClientItem item)
        {
            ClientGrid.SelectedItem = item;
            var menu = (ContextMenu)FindResource("ClientMenu");
            menu.DataContext = item;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void ViewTrayIcons_Click(object sender, RoutedEventArgs e)
    {
        if (ClientGrid.SelectedItem is not ClientItem client) return;
        _selectedClient = client.HostName;
        _trayIcons.Clear();
        Log("INFO", $"Requesting tray icons from {client.HostName}");
        _server.SendToClient(client.HostName, new ProtocolMessage { Type = MsgType.GetTrayIcons });
    }

    private void RequestTrayIcons_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClient == null) return;
        _server.SendToClient(_selectedClient, new ProtocolMessage { Type = MsgType.GetTrayIcons });
    }

    private void HideSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ClientGrid.SelectedItem is not ClientItem client) return;
        var selectedIcons = _trayIcons.Where(i => i.IsSelected).ToList();
        if (selectedIcons.Count == 0) { Log("WARN", "请先勾选要隐藏的图标"); return; }

        var ids = selectedIcons.Select(i => i.ProcessName).Distinct().ToList();
        _server.SendToClient(client.HostName, ProtocolMessage.Create(MsgType.Hide, new HideRequest { Identifiers = ids }));
        Log("INFO", $"Sent hide to {client.HostName}: {string.Join(", ", ids)}");
    }

    private void RestartClient_Click(object sender, RoutedEventArgs e)
    {
        if (ClientGrid.SelectedItem is not ClientItem client) return;
        _server.SendToClient(client.HostName, new ProtocolMessage { Type = MsgType.Restart });
        Log("INFO", $"Sent restart to {client.HostName}");
    }

    private void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (ClientGrid.SelectedItem is not ClientItem client) return;
        _server.SendToClient(client.HostName, new ProtocolMessage { Type = MsgType.RestartExplorer });
        Log("INFO", $"Sent restart_explorer to {client.HostName}");
    }

    private void RemoveClient_Click(object sender, RoutedEventArgs e)
    {
        if (ClientGrid.SelectedItem is not ClientItem client) return;
        _clients.Remove(client);
        SaveClients();
    }

    // ========== Rules ==========

    private void RulesSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("规则设置", "每行一条，格式: 进程名 或 进程名|提示文本", string.Join("\n", _rules))
        { Owner = this };
        dlg.ShowDialog();
        if (dlg.Saved)
        {
            _rules = dlg.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            _persistence.SaveRules(_rules);
            Log("INFO", $"Rules saved: {_rules.Count}");
        }
    }

    private void FilterSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("过滤设置", "每行一条进程名，不显示在客户端列表中", string.Join("\n", _filter))
        { Owner = this };
        dlg.ShowDialog();
        if (dlg.Saved)
        {
            _filter = dlg.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            _persistence.SaveFilter(_filter);
            Log("INFO", $"Filter saved: {_filter.Count}");
        }
    }

    private void ApplyAllRules_Click(object sender, RoutedEventArgs e)
    {
        if (_rules.Count == 0) { Log("WARN", "规则为空"); return; }
        _server.SendToAll(ProtocolMessage.Create(MsgType.UpdateRules, new RulesUpdate { Rules = _rules }));
        Log("INFO", $"Applied rules to all clients: {_rules.Count} rules");
    }

    private void BatchHide_Click(object sender, RoutedEventArgs e)
    {
        var selectedClients = _clients.Where(c => c.IsSelected).ToList();
        if (selectedClients.Count == 0) { Log("WARN", "请先在客户端列表中勾选"); return; }

        var selectedIcons = _trayIcons.Where(i => i.IsSelected).ToList();
        if (selectedIcons.Count == 0) { Log("WARN", "请先在图标列表中勾选"); return; }

        var ids = selectedIcons.Select(i => i.ProcessName).Distinct().ToList();
        var msg = ProtocolMessage.Create(MsgType.Hide, new HideRequest { Identifiers = ids });
        _server.SendToMany(selectedClients.Select(c => c.HostName), msg);
        Log("INFO", $"Batch hide to {selectedClients.Count} clients: {string.Join(", ", ids)}");
    }

    // ========== Helpers ==========

    private void UpdateServerStatus()
    {
        int online = _clients.Count(c => c.Status == "在线");
        int total = _clients.Count;
        ServerStatus.Text = $"客户端: {online} 在线 / {total} 总计 | 端口: 9527";
    }

    private void SaveClients()
    {
        var infos = _clients.Select(c => new ClientInfo
        {
            HostName = c.HostName,
            IpAddress = c.IpAddress,
            ConnectedAt = c.ConnectedAt,
            LastSeen = c.LastSeen
        }).ToList();
        _persistence.SaveClients(infos);
    }

    private void Log(string level, string msg)
    {
        try
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            if (LogBox != null)
            {
                LogBox.AppendText($"[{ts}] [{level}] {msg}\n");
                LogBox.ScrollToEnd();
            }
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, $"server_{DateTime.Now:yyyyMMdd}.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {msg}\n");
        }
        catch { }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveClients();
        _server.Dispose();
    }
}

public class ClientItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status = "离线";
    private int _iconCount;
    private DateTime _lastSeen;

    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
    public string HostName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Status { get => _status; set { _status = value; OnPropertyChanged(nameof(Status)); } }
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
