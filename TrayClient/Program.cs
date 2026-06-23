using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Shared;

namespace TrayClient;

/// <summary>
/// Silent background client. No UI, no console, no tray icon, no taskbar.
/// Techniques: WinExe + HideConsole + SetConsoleCtrlHandler + CREATE_NO_WINDOW
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

    // ===== Layer 1: Hide console window =====
    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    const int SW_HIDE = 0;

    // ===== Layer 2: Intercept close signals =====
    delegate bool ConsoleCtrlHandler(uint ctrlType);
    [DllImport("kernel32.dll")]
    static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);

    // ===== Layer 3: CREATE_NO_WINDOW for child processes =====
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll")]
    static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    const uint CREATE_NO_WINDOW = 0x08000000;
    const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    const short SW_HIDE_FLAG = 0;
    const int STARTF_USESHOWWINDOW = 1;

    static void Main(string[] args)
    {
        // ===== Layer 1: Hide console window =====
        IntPtr cw = GetConsoleWindow();
        if (cw != IntPtr.Zero) ShowWindow(cw, SW_HIDE);

        // ===== Layer 2: Ignore all close signals =====
        SetConsoleCtrlHandler(ctrlType => true, true);

        // ===== Layer 4: Boost priority =====
        try { SetPriorityClass(Process.GetCurrentProcess().Handle, ABOVE_NORMAL_PRIORITY_CLASS); } catch { }

        // Parse args
        int port = 9527;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--server" || args[i] == "-s") serverIp = args[i + 1];
            if (args[i] == "--port" || args[i] == "-p") int.TryParse(args[i + 1], out port);
            if (args[i] == "--logdir" || args[i] == "-l") LogDir = args[i + 1];
        }

        try { Directory.CreateDirectory(LogDir); } catch { }

        TryRegisterStartup();
        LoadLocalConfig();
        Log($"Client starting, hostname={HostName}, server={serverIp}:{port}, logdir={LogDir}");
        Log($"Loaded {_currentEntries.Count} rule entries, {_currentFilter.Count} filters");
        Log($"PID={Environment.ProcessId}, no console, silent background mode");

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
                        if (_currentEntries.Count == 0) Log("Rules cleared");
                        SendAck(true, $"Config received: {_currentEntries.Count} entries");
                    }
                    break;

                case MsgType.Restart:
                    SendAck(true, "Restarting...");
                    Log("Server requested restart.");
                    Thread.Sleep(1000);
                    try { _writer?.Close(); _client?.Close(); } catch { }
                    // Launch new instance with CREATE_NO_WINDOW
                    LaunchSelfDetached();
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

    /// <summary>
    /// Launch a new instance of ourselves with CREATE_NO_WINDOW (Layer 3).
    /// </summary>
    static void LaunchSelfDetached()
    {
        try
        {
            string cmd = $"\"{Environment.ProcessPath}\" --server {serverIp} --logdir \"{LogDir}\"";

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.dwFlags = STARTF_USESHOWWINDOW;
            si.wShowWindow = SW_HIDE_FLAG;

            bool ok = CreateProcessW(
                null, cmd,
                IntPtr.Zero, IntPtr.Zero, false,
                CREATE_NO_WINDOW,
                IntPtr.Zero, null,
                ref si, out var pi);

            if (ok)
            {
                Log($"Restart launched: PID={pi.dwProcessId}");
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
            else
            {
                Log($"CreateProcess failed: {Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            Log($"LaunchSelfDetached error: {ex.Message}");
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
            // Use start /b + >nul for fully silent startup
            File.WriteAllText(batPath,
                $"@echo off\r\n" +
                $"cd /d \"{Path.GetDirectoryName(exePath)}\"\r\n" +
                $"start /b \"\" \"{exePath}\" --server {serverIp} --logdir \"{LogDir}\" >nul 2>&1\r\n");
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
