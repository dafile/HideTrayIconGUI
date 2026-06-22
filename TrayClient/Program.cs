using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Shared;

namespace TrayClient;

/// <summary>
/// Headless tray icon client. No UI, no tray icon, no windows.
/// Connects to server, receives commands, applies them locally.
/// Usage: TrayClient.exe --server 10.10.106.27
/// </summary>
class Program
{
    static readonly string LogDir = Path.Combine(Path.GetTempPath(), "TrayClient_Logs");
    static readonly string RulesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rules.txt");
    static readonly string FilterPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "filter.txt");
    static readonly string HostName = Environment.MachineName;
    static volatile bool _running = true;
    static TcpClient? _client;
    static StreamWriter? _writer;
    static List<RuleEntry> _currentEntries = [];  // flat list of process names to hide
    static List<string> _currentFilter = [];

    static void Main(string[] args)
    {
        string serverIp = "10.10.106.27";
        int port = 9527;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--server" || args[i] == "-s")
                serverIp = args[i + 1];
            if (args[i] == "--port" || args[i] == "-p")
                int.TryParse(args[i + 1], out port);
        }

        TryRegisterStartup();
        LoadLocalConfig();
        Log($"Client starting, hostname={HostName}, server={serverIp}:{port}");

        while (_running)
        {
            try { ConnectAndRun(serverIp, port); }
            catch (Exception ex) { Log($"Connection error: {ex.Message}"); }

            if (_running)
            {
                Log("Reconnecting in 3 seconds...");
                Thread.Sleep(3000);
            }
        }
    }

    static void ConnectAndRun(string serverIp, int port)
    {
        Log($"Connecting to {serverIp}:{port}...");
        _client = new TcpClient();
        _client.Connect(serverIp, port);
        Log("Connected.");

        var stream = _client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

        SendMessage(new ProtocolMessage
        {
            Type = MsgType.Register,
            Payload = JsonSerializer.Serialize(new ClientInfo
            {
                HostName = HostName,
                IpAddress = GetLocalIp(),
                ConnectedAt = DateTime.Now,
                LastSeen = DateTime.Now
            })
        });

        while (_running && _client.Connected)
        {
            string? line = reader.ReadLine();
            if (line == null) break;
            var msg = ProtocolMessage.Deserialize(line);
            if (msg == null) continue;
            HandleMessage(msg);
        }

        Log("Disconnected from server.");
        _client?.Close();
    }

    static void HandleMessage(ProtocolMessage msg)
    {
        Log($"Received: {msg.Type}");
        try
        {
            switch (msg.Type)
            {
                case MsgType.Ping:
                    SendMessage(new ProtocolMessage { Type = MsgType.Pong });
                    break;

                case MsgType.Hide:
                    var hideReq = msg.DeserializePayload<HideRequest>();
                    if (hideReq?.Identifiers != null && hideReq.Identifiers.Count > 0)
                    {
                        string ids = string.Join(" ", hideReq.Identifiers);
                        var (ok, output) = RunHideTrayIcon("-a hide -d 0", ids);
                        SendAck(ok, output);
                    }
                    break;

                case MsgType.Show:
                    var showReq = msg.DeserializePayload<HideRequest>();
                    if (showReq?.Identifiers != null && showReq.Identifiers.Count > 0)
                    {
                        string ids = string.Join(" ", showReq.Identifiers);
                        var (ok, output) = RunHideTrayIcon("-a show -r", ids);
                        SendAck(ok, output);
                    }
                    break;

                case MsgType.GetTrayIcons:
                    var icons = TrayIconEnumerator.Enumerate();
                    var filtered = icons.Where(i =>
                        !_currentFilter.Any(f =>
                            i.ProcessName.Contains(f, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    SendMessage(ProtocolMessage.Create(MsgType.TrayIcons, filtered));
                    break;

                case MsgType.UpdateRules:
                    // New format: ClientConfig with Rules + Filter
                    var config = msg.DeserializePayload<ClientConfig>();
                    if (config != null)
                    {
                        _currentEntries = config.Rules.SelectMany(r => r.Entries).ToList();
                        _currentFilter = config.Filter;
                        // Save to local files
                        var ruleLines = _currentEntries.Select(e =>
                            string.IsNullOrEmpty(e.Tooltip) ? e.ProcessName : $"{e.ProcessName}|{e.Tooltip}");
                        File.WriteAllText(RulesPath, string.Join("\n", ruleLines));
                        File.WriteAllText(FilterPath, string.Join("\n", _currentFilter));
                        Log($"Config updated: {config.Rules.Count} rules, {_currentEntries.Count} entries, {_currentFilter.Count} filters");
                        ApplyRules();
                        SendAck(true, $"Config applied: {_currentEntries.Count} entries");
                    }
                    break;

                case MsgType.Restart:
                    SendAck(true, "Restarting...");
                    Log("Server requested restart.");
                    Thread.Sleep(500);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath!,
                        Arguments = $"--server {_client?.Client.RemoteEndPoint?.ToString()?.Split(':')[0]}",
                        UseShellExecute = false
                    });
                    _running = false;
                    Environment.Exit(0);
                    break;

                case MsgType.RestartExplorer:
                    RestartExplorer();
                    SendAck(true, "Explorer restarted");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"HandleMessage error: {ex.Message}");
            SendMessage(new ProtocolMessage { Type = MsgType.Error, Payload = ex.Message });
        }
    }

    static void ApplyRules()
    {
        if (_currentEntries.Count == 0) return;

        var identifiers = _currentEntries.Select(e => e.ProcessName)
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        if (identifiers.Count == 0) return;

        string ids = string.Join(" ", identifiers);
        Log($"Auto-applying rules: {ids}");
        RunHideTrayIcon("-a hide -d 0", ids);
    }

    static (bool ok, string output) RunHideTrayIcon(string actionArgs, string identifiers)
    {
        string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hideTrayIcon.exe");
        if (!File.Exists(exePath))
        {
            Log("hideTrayIcon.exe not found!");
            return (false, "hideTrayIcon.exe not found");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"{actionArgs} -i \"{identifiers}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            string stdout = proc?.StandardOutput.ReadToEnd() ?? "";
            string stderr = proc?.StandardError.ReadToEnd() ?? "";
            return (proc?.ExitCode == 0, stdout + stderr);
        }
        catch (Exception ex)
        {
            Log($"RunHideTrayIcon error: {ex.Message}");
            return (false, ex.Message);
        }
    }

    static void RestartExplorer()
    {
        foreach (var proc in Process.GetProcessesByName("explorer"))
        {
            try { proc.Kill(); proc.WaitForExit(5000); } catch { }
        }
        Thread.Sleep(1000);
        try { Process.Start("explorer.exe"); } catch { }
    }

    static void SendMessage(ProtocolMessage msg)
    {
        try { _writer?.Write(msg.Serialize()); _writer?.Flush(); }
        catch (Exception ex) { Log($"SendMessage error: {ex.Message}"); }
    }

    static void SendAck(bool ok, string message)
    {
        SendMessage(ProtocolMessage.Create(MsgType.Ack, new { ok, message }));
    }

    static void LoadLocalConfig()
    {
        try
        {
            if (File.Exists(RulesPath))
            {
                _currentEntries = File.ReadAllText(RulesPath)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line =>
                    {
                        int sep = line.IndexOf('|');
                        return new RuleEntry
                        {
                            ProcessName = sep > 0 ? line[..sep] : line,
                            Tooltip = sep > 0 ? line[(sep + 1)..] : ""
                        };
                    }).ToList();
            }
            if (File.Exists(FilterPath))
                _currentFilter = File.ReadAllText(FilterPath)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            else
                _currentFilter = ["Taskmgr", "Idle"];
        }
        catch { }
    }

    static void TryRegisterStartup()
    {
        try
        {
            string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string batPath = Path.Combine(startupDir, "TrayClient.bat");
            if (!File.Exists(batPath))
            {
                string exePath = Environment.ProcessPath ?? "";
                File.WriteAllText(batPath, $"@echo off\r\nstart \"\" \"{exePath}\" --server 10.10.106.27\r\n");
                Log($"Registered startup: {batPath}");
            }
        }
        catch (Exception ex) { Log($"Startup registration failed: {ex.Message}"); }
    }

    static string GetLocalIp()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            return host.AddressList.FirstOrDefault(a =>
                a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string line = $"[{ts}] {msg}";
            Console.WriteLine(line);
            File.AppendAllText(Path.Combine(LogDir, $"client_{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
        }
        catch { }
    }
}
