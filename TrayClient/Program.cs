using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Shared;

namespace TrayClient;

/// <summary>
/// Silent background client. No UI, no console, no tray icon.
/// Usage: TrayClient.exe [--server IP] [--port PORT] [--logdir PATH]
/// </summary>
class Program
{
    static string LogDir = Path.Combine(Path.GetTempPath(), "TrayClient_Logs");
    static readonly string RulesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rules.txt");
    static readonly string FilterPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "filter.txt");
    static readonly string HostName = Environment.MachineName;
    static volatile bool _running = true;
    static TcpClient? _client;
    static StreamWriter? _writer;
    static List<RuleEntry> _currentEntries = [];
    static List<string> _currentFilter = [];
    static string serverIp = "10.10.106.27";

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    const int SW_HIDE = 0;

    static void Main(string[] args)
    {
        // Hide console window immediately (in case someone runs .exe directly)
        var hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_HIDE);

        int port = 9527;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--server" || args[i] == "-s")
                serverIp = args[i + 1];
            if (args[i] == "--port" || args[i] == "-p")
                int.TryParse(args[i + 1], out port);
            if (args[i] == "--logdir" || args[i] == "-l")
                LogDir = args[i + 1];
        }

        // Ensure log directory exists
        try { Directory.CreateDirectory(LogDir); } catch { }

        TryRegisterStartup();
        LoadLocalConfig();
        Log($"Client starting, hostname={HostName}, server={serverIp}:{port}, logdir={LogDir}");
        Log($"Loaded {_currentEntries.Count} rule entries, {_currentFilter.Count} filters");

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
                        _ = Task.Run(() =>
                        {
                            var (ok, output) = RunHideTrayIcon("-a hide -d 0", ids);
                            SendAck(ok, output);
                            Log($"Hide result: ok={ok}");
                        });
                    }
                    break;

                case MsgType.Show:
                    var showReq = msg.DeserializePayload<HideRequest>();
                    if (showReq?.Identifiers != null && showReq.Identifiers.Count > 0)
                    {
                        string ids = string.Join(" ", showReq.Identifiers);
                        _ = Task.Run(() =>
                        {
                            var (ok, output) = RunHideTrayIcon("-a show -r", ids);
                            SendAck(ok, output);
                            Log($"Show result: ok={ok}");
                        });
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
                    var config = msg.DeserializePayload<ClientConfig>();
                    if (config != null)
                    {
                        _currentEntries = config.Rules.SelectMany(r => r.Entries).ToList();
                        _currentFilter = config.Filter;
                        var ruleLines = _currentEntries.Select(e =>
                            string.IsNullOrEmpty(e.Tooltip) ? e.ProcessName : $"{e.ProcessName}|{e.Tooltip}");
                        File.WriteAllText(RulesPath, string.Join("\n", ruleLines));
                        File.WriteAllText(FilterPath, string.Join("\n", _currentFilter));
                        Log($"Config updated: {config.Rules.Count} rules, {_currentEntries.Count} entries, {_currentFilter.Count} filters");
                        if (_currentEntries.Count == 0)
                            Log("Rules cleared - no auto-hide will occur");
                        SendAck(true, $"Config received: {_currentEntries.Count} entries");
                    }
                    break;

                case MsgType.Restart:
                    SendAck(true, "Restarting...");
                    Log("Server requested restart.");
                    Thread.Sleep(1000);
                    try { _writer?.Close(); _client?.Close(); } catch { }
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c start \"\" \"{Environment.ProcessPath}\" --server {serverIp} --logdir \"{LogDir}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        Process.Start(psi);
                        Log($"Restart launched: {Environment.ProcessPath} --server {serverIp}");
                    }
                    catch (Exception ex) { Log($"Restart failed: {ex.Message}"); }
                    Thread.Sleep(500);
                    _running = false;
                    Environment.Exit(0);
                    break;

                case MsgType.RestartExplorer:
                    RestartExplorer();
                    SendAck(true, "Explorer restarted");
                    Log("Explorer restarted. Server controls re-hiding via cycle timer.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"HandleMessage error: {ex.Message}");
            SendMessage(new ProtocolMessage { Type = MsgType.Error, Payload = ex.Message });
        }
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
            if (proc == null) return (false, "Process.Start returned null");

            bool exited = proc.WaitForExit(8000);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                Log("hideTrayIcon.exe timed out after 8s, killed");
                return (false, "Timeout");
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            return (proc.ExitCode == 0, stdout + stderr);
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
            string exePath = Environment.ProcessPath ?? "";
            // Always overwrite to keep args up to date; use /b for no window
            File.WriteAllText(batPath,
                $"@echo off\r\nstart /b \"\" \"{exePath}\" --server {serverIp} --logdir \"{LogDir}\"\r\n");
            Log($"Registered startup: {batPath}");
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

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string line = $"[{ts}] {msg}";
            File.AppendAllText(Path.Combine(LogDir, $"client_{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
        }
        catch { }
    }
}
