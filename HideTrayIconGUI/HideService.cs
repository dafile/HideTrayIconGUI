using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HideTrayIconGUI;

/// <summary>
/// Wraps hideTrayIcon.exe - the actual hide/show logic.
/// Also enumerates tray icons via Windows API for display.
/// </summary>
public static class HideService
{
    private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

    // ========== hideTrayIcon.exe wrapper ==========

    /// <summary>
    /// Get the path to hideTrayIcon.exe. It MUST be next to our exe.
    /// </summary>
    private static string GetExePath()
    {
        // Always look next to our own exe - this is the only reliable location
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hideTrayIcon.exe");
        return File.Exists(path) ? path : string.Empty;
    }

    /// <summary>
    /// Hide tray icons matching the given identifiers.
    /// Identifiers are space-separated process names or tooltip substrings.
    /// Calls: hideTrayIcon.exe -a hide -d 1 -i "id1 id2 id3"
    /// </summary>
    public static (bool ok, string msg) Hide(string identifiers)
    {
        return Run("-a hide -d 1", identifiers);
    }

    /// <summary>
    /// Show (restore) tray icons matching the given identifiers.
    /// Calls: hideTrayIcon.exe -a show -r -i "id1 id2 id3"
    /// </summary>
    public static (bool ok, string msg) Show(string identifiers)
    {
        return Run("-a show -r", identifiers);
    }

    private static (bool ok, string msg) Run(string actionArgs, string identifiers)
    {
        string exePath = GetExePath();
        if (string.IsNullOrEmpty(exePath))
        {
            string msg = $"hideTrayIcon.exe 未找到! 请确认它和程序在同一目录: {AppDomain.CurrentDomain.BaseDirectory}";
            Log("ERROR", msg);
            return (false, msg);
        }

        // Build command: hideTrayIcon.exe -a hide -d 1 -i "identifier1 identifier2"
        string args = $"{actionArgs} -i \"{identifiers}\"";
        Log("INFO", $"执行: \"{exePath}\" {args}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Log("ERROR", "Process.Start 返回 null");
                return (false, "启动进程失败");
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Log("INFO", $"退出码={proc.ExitCode}");
            if (!string.IsNullOrEmpty(stdout)) Log("INFO", $"stdout=[{stdout.Trim()}]");
            if (!string.IsNullOrEmpty(stderr)) Log("ERROR", $"stderr=[{stderr.Trim()}]");

            return (proc.ExitCode == 0, string.IsNullOrEmpty(stderr) ? "OK" : stderr.Trim());
        }
        catch (Exception ex)
        {
            Log("ERROR", $"异常: {ex.Message}");
            return (false, ex.Message);
        }
    }

    // ========== Tray icon enumeration (for display) ==========

    public record TrayIcon(string ProcessName, string Tooltip, string Area, string Status);

    public static List<TrayIcon> EnumerateTrayIcons()
    {
        var result = new List<TrayIcon>();
        try
        {
            // Main tray area
            IntPtr mainToolbar = FindMainTrayToolbar();
            if (mainToolbar != IntPtr.Zero)
                result.AddRange(ReadToolbar(mainToolbar, "已显示"));

            // Overflow area
            IntPtr overflowToolbar = FindOverflowTrayToolbar();
            if (overflowToolbar != IntPtr.Zero)
                result.AddRange(ReadToolbar(overflowToolbar, "已隐藏"));
        }
        catch (Exception ex)
        {
            Log("ERROR", $"EnumerateTrayIcons 异常: {ex.Message}");
        }
        return result;
    }

    private static IntPtr FindMainTrayToolbar()
    {
        IntPtr trayWnd = Win32.FindWindow("Shell_TrayWnd", null);
        if (trayWnd == IntPtr.Zero) return IntPtr.Zero;
        IntPtr notify = Win32.FindWindowEx(trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero) return IntPtr.Zero;
        IntPtr pager = Win32.FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
        if (pager == IntPtr.Zero) return IntPtr.Zero;
        return Win32.FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null);
    }

    private static IntPtr FindOverflowTrayToolbar()
    {
        IntPtr wnd = Win32.FindWindow("NotifyIconOverflowWindow", null);
        if (wnd == IntPtr.Zero) return IntPtr.Zero;
        return Win32.FindWindowEx(wnd, IntPtr.Zero, "ToolbarWindow32", null);
    }

    private static List<TrayIcon> ReadToolbar(IntPtr toolbar, string area)
    {
        var icons = new List<TrayIcon>();

        Win32.GetWindowThreadProcessId(toolbar, out uint pid);
        IntPtr hProc = Win32.OpenProcess(
            Win32.PROCESS_VM_OPERATION | Win32.PROCESS_VM_READ | Win32.PROCESS_VM_WRITE | Win32.PROCESS_QUERY_INFORMATION,
            false, pid);
        if (hProc == IntPtr.Zero) return icons;

        try
        {
            int count = (int)Win32.SendMessage(toolbar, Win32.TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
            if (count <= 0) return icons;

            int btnSize = Marshal.SizeOf<Win32.TBBUTTON>();
            IntPtr remoteMem = Win32.VirtualAllocEx(hProc, IntPtr.Zero, btnSize + 256, Win32.MEM_COMMIT, Win32.PAGE_READWRITE);
            if (remoteMem == IntPtr.Zero) return icons;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    var icon = ReadButton(toolbar, hProc, remoteMem, i, area, btnSize);
                    if (icon != null) icons.Add(icon);
                }
            }
            finally
            {
                Win32.VirtualFreeEx(hProc, remoteMem, 0, Win32.MEM_RELEASE);
            }
        }
        finally
        {
            Win32.CloseHandle(hProc);
        }

        return icons;
    }

    private static TrayIcon? ReadButton(IntPtr toolbar, IntPtr hProc, IntPtr remoteMem, int index, string area, int btnSize)
    {
        if (Win32.SendMessage(toolbar, Win32.TB_GETBUTTON, (IntPtr)index, remoteMem) == IntPtr.Zero)
            return null;

        byte[] buf = new byte[btnSize];
        if (!Win32.ReadProcessMemory(hProc, remoteMem, buf, btnSize, out int read) || read < btnSize)
            return null;

        // Parse TBBUTTON
        int offset = 0;
        /*iBitmap*/  offset += 4;
        /*idCommand*/ offset += 4;
        byte fsState = buf[offset]; offset += 1;
        /*fsStyle*/  offset += 1;
        /*bReserved*/ offset += 6;

        IntPtr dwData = IntPtr.Size == 8
            ? (IntPtr)BitConverter.ToInt64(buf, offset)
            : (IntPtr)BitConverter.ToInt32(buf, offset);
        offset += IntPtr.Size;

        IntPtr iString = IntPtr.Size == 8
            ? (IntPtr)BitConverter.ToInt64(buf, offset)
            : (IntPtr)BitConverter.ToInt32(buf, offset);

        // Get real process name from TRAYDATA
        string processName = "unknown";
        if (dwData != IntPtr.Zero)
        {
            int tdSize = IntPtr.Size == 8 ? 32 : 20; // TRAYDATA size
            byte[] tdBuf = new byte[tdSize];
            if (Win32.ReadProcessMemory(hProc, dwData, tdBuf, tdSize, out int tdRead) && tdRead >= tdSize)
            {
                IntPtr hWnd = IntPtr.Size == 8
                    ? (IntPtr)BitConverter.ToInt64(tdBuf, 0)
                    : (IntPtr)BitConverter.ToInt32(tdBuf, 0);
                if (hWnd != IntPtr.Zero)
                {
                    Win32.GetWindowThreadProcessId(hWnd, out uint ownerPid);
                    try { processName = Process.GetProcessById((int)ownerPid).ProcessName; }
                    catch { processName = $"PID:{ownerPid}"; }
                }
            }
        }

        // Get tooltip
        string tooltip = "";
        if (iString != IntPtr.Zero && iString != (IntPtr)(-1))
        {
            bool isPointer = IntPtr.Size == 8
                ? (long)iString > 0 && (long)iString < 0x00007FFFFFFFFFFF
                : (int)iString > 0;
            if (isPointer)
            {
                byte[] strBuf = new byte[1024];
                if (Win32.ReadProcessMemory(hProc, iString, strBuf, strBuf.Length, out int strRead) && strRead > 0)
                {
                    tooltip = Encoding.Unicode.GetString(strBuf, 0, strRead);
                    int nullIdx = tooltip.IndexOf('\0');
                    if (nullIdx >= 0) tooltip = tooltip[..nullIdx];
                    tooltip = tooltip.Trim();
                }
            }
        }

        bool isHidden = (fsState & Win32.TBSTATE_HIDDEN) != 0;

        return new TrayIcon(processName, tooltip, area, isHidden ? "已隐藏" : "可见");
    }

    // ========== Logging ==========

    public static void Log(string level, string msg)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string line = $"[{ts}] [{level}] {msg}";
            Debug.WriteLine(line);
            File.AppendAllText(
                Path.Combine(LogDir, $"hide_{DateTime.Now:yyyyMMdd}.log"),
                line + Environment.NewLine);
        }
        catch { }
    }

    public static string GetLogPath() => Path.Combine(LogDir, $"hide_{DateTime.Now:yyyyMMdd}.log");

    // ========== Win32 interop ==========

    private static class Win32
    {
        public const uint TB_BUTTONCOUNT = 0x0418;
        public const uint TB_GETBUTTON = 0x0417;
        public const byte TBSTATE_HIDDEN = 0x08;
        public const uint PROCESS_VM_OPERATION = 0x0008;
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_VM_WRITE = 0x0020;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint MEM_COMMIT = 0x1000;
        public const uint MEM_RELEASE = 0x8000;
        public const uint PAGE_READWRITE = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        public struct TBBUTTON
        {
            public int iBitmap;
            public int idCommand;
            public byte fsState;
            public byte fsStyle;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] bReserved;
            public IntPtr dwData;
            public IntPtr iString;
        }

        [DllImport("user32.dll")] public static extern IntPtr FindWindow(string? cls, string? win);
        [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? win);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, int size, uint type, uint protect);
        [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, int size, uint type);
        [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int read);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    }
}
