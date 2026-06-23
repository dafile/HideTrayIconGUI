using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Shared;

namespace TrayClient;

/// <summary>
/// Enumerates system tray icons via Windows API.
/// Reads TRAYDATA to get real process names.
/// </summary>
public static class TrayIconEnumerator
{
    const uint TB_BUTTONCOUNT = 0x0418;
    const uint TB_GETBUTTON = 0x0417;
    const byte TBSTATE_HIDDEN = 0x08;
    const uint PROCESS_VM_OPERATION = 0x0008;
    const uint PROCESS_VM_READ = 0x0010;
    const uint PROCESS_VM_WRITE = 0x0020;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint MEM_COMMIT = 0x1000;
    const uint MEM_RELEASE = 0x8000;
    const uint PAGE_READWRITE = 0x04;

    [DllImport("user32.dll")] static extern IntPtr FindWindow(string? cls, string? win);
    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? win);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, int size, uint type, uint protect);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, int size, uint type);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int read);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public static List<TrayIconData> Enumerate()
    {
        var result = new List<TrayIconData>();
        try
        {
            IntPtr mainToolbar = FindMainToolbar();
            Program.Log($"MainToolbar: {mainToolbar}");
            if (mainToolbar != IntPtr.Zero)
                result.AddRange(ReadToolbar(mainToolbar, "已显示"));

            IntPtr overflowToolbar = FindOverflowToolbar();
            Program.Log($"OverflowToolbar: {overflowToolbar}");
            if (overflowToolbar != IntPtr.Zero)
                result.AddRange(ReadToolbar(overflowToolbar, "已隐藏"));

            Program.Log($"Enumerate done: {result.Count} icons");
        }
        catch (Exception ex)
        {
            Program.Log($"Enumerate error: {ex.Message}");
        }
        return result;
    }

    static IntPtr FindMainToolbar()
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return IntPtr.Zero;
        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero) return IntPtr.Zero;
        IntPtr pager = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
        if (pager == IntPtr.Zero) return IntPtr.Zero;
        return FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null);
    }

    static IntPtr FindOverflowToolbar()
    {
        IntPtr wnd = FindWindow("NotifyIconOverflowWindow", null);
        if (wnd == IntPtr.Zero) return IntPtr.Zero;
        return FindWindowEx(wnd, IntPtr.Zero, "ToolbarWindow32", null);
    }

    static List<TrayIconData> ReadToolbar(IntPtr toolbar, string area)
    {
        var icons = new List<TrayIconData>();
        GetWindowThreadProcessId(toolbar, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return icons;

        try
        {
            int count = (int)SendMessage(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
            if (count <= 0) return icons;

            int btnSize = 32; // TBBUTTON on 64-bit
            IntPtr remoteMem = VirtualAllocEx(hProc, IntPtr.Zero, btnSize + 256, MEM_COMMIT, PAGE_READWRITE);
            if (remoteMem == IntPtr.Zero) return icons;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    var icon = ReadButton(toolbar, hProc, remoteMem, i, area, btnSize);
                    if (icon != null) icons.Add(icon);
                }
            }
            finally { VirtualFreeEx(hProc, remoteMem, 0, MEM_RELEASE); }
        }
        finally { CloseHandle(hProc); }
        return icons;
    }

    static TrayIconData? ReadButton(IntPtr toolbar, IntPtr hProc, IntPtr remoteMem, int index, string area, int btnSize)
    {
        if (SendMessage(toolbar, TB_GETBUTTON, (IntPtr)index, remoteMem) == IntPtr.Zero) return null;

        byte[] buf = new byte[btnSize];
        if (!ReadProcessMemory(hProc, remoteMem, buf, btnSize, out int read) || read < btnSize) return null;

        // Parse TBBUTTON
        int offset = 4 + 4; // skip iBitmap, idCommand
        byte fsState = buf[offset]; offset += 1 + 1 + 6; // fsState, fsStyle, bReserved

        IntPtr dwData = IntPtr.Size == 8
            ? (IntPtr)BitConverter.ToInt64(buf, offset)
            : (IntPtr)BitConverter.ToInt32(buf, offset);
        offset += IntPtr.Size;

        IntPtr iString = IntPtr.Size == 8
            ? (IntPtr)BitConverter.ToInt64(buf, offset)
            : (IntPtr)BitConverter.ToInt32(buf, offset);

        // Get process name from TRAYDATA
        string processName = "unknown";
        if (dwData != IntPtr.Zero)
        {
            int tdSize = IntPtr.Size == 8 ? 32 : 20;
            byte[] tdBuf = new byte[tdSize];
            if (ReadProcessMemory(hProc, dwData, tdBuf, tdSize, out int tdRead) && tdRead >= tdSize)
            {
                IntPtr hWnd = IntPtr.Size == 8
                    ? (IntPtr)BitConverter.ToInt64(tdBuf, 0)
                    : (IntPtr)BitConverter.ToInt32(tdBuf, 0);
                if (hWnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hWnd, out uint ownerPid);
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
                if (ReadProcessMemory(hProc, iString, strBuf, strBuf.Length, out int strRead) && strRead > 0)
                {
                    tooltip = Encoding.Unicode.GetString(strBuf, 0, strRead);
                    int nullIdx = tooltip.IndexOf('\0');
                    if (nullIdx >= 0) tooltip = tooltip[..nullIdx];
                    tooltip = tooltip.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
                }
            }
        }

        bool isHidden = (fsState & TBSTATE_HIDDEN) != 0;

        return new TrayIconData
        {
            ProcessName = processName,
            Tooltip = tooltip,
            Area = area,
            Status = isHidden ? "已隐藏" : "可见"
        };
    }
}
