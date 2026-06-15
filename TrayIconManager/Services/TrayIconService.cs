using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TrayIconManager.Models;
using static TrayIconManager.Services.NativeMethods;

namespace TrayIconManager.Services;

public class TrayIconService : IDisposable
{
    private const string TAG = "TrayIconSvc";

    public List<TrayIconInfo> EnumerateTrayIcons()
    {
        var icons = new List<TrayIconInfo>();

        var mainToolbar = FindMainTrayToolbar();
        Logger.Info(TAG, $"Main tray toolbar: {mainToolbar}");
        if (mainToolbar != IntPtr.Zero)
            icons.AddRange(ReadToolbarIcons(mainToolbar, "已显示"));

        var overflowToolbar = FindOverflowTrayToolbar();
        Logger.Info(TAG, $"Overflow tray toolbar: {overflowToolbar}");
        if (overflowToolbar != IntPtr.Zero)
            icons.AddRange(ReadToolbarIcons(overflowToolbar, "已隐藏"));

        Logger.Info(TAG, $"Total icons found: {icons.Count}");
        return icons;
    }

    private IntPtr FindMainTrayToolbar()
    {
        IntPtr trayWnd = FindWindow("Shell_TrayWnd", null);
        if (trayWnd == IntPtr.Zero) return IntPtr.Zero;

        IntPtr trayNotify = FindWindowEx(trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
        if (trayNotify == IntPtr.Zero) return IntPtr.Zero;

        IntPtr sysPager = FindWindowEx(trayNotify, IntPtr.Zero, "SysPager", null);
        if (sysPager == IntPtr.Zero) return IntPtr.Zero;

        return FindWindowEx(sysPager, IntPtr.Zero, "ToolbarWindow32", null);
    }

    private IntPtr FindOverflowTrayToolbar()
    {
        IntPtr overflowWnd = FindWindow("NotifyIconOverflowWindow", null);
        if (overflowWnd == IntPtr.Zero) return IntPtr.Zero;

        return FindWindowEx(overflowWnd, IntPtr.Zero, "ToolbarWindow32", null);
    }

    private List<TrayIconInfo> ReadToolbarIcons(IntPtr toolbarHwnd, string area)
    {
        var icons = new List<TrayIconInfo>();

        GetWindowThreadProcessId(toolbarHwnd, out uint toolbarPid);
        Logger.Info(TAG, $"Reading {area} toolbar, explorer PID={toolbarPid}");

        IntPtr hProcess = OpenProcess(
            PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION,
            false, toolbarPid);

        if (hProcess == IntPtr.Zero)
        {
            Logger.Error(TAG, $"OpenProcess failed for PID {toolbarPid}, err={Marshal.GetLastWin32Error()}");
            return icons;
        }

        try
        {
            int buttonCount = (int)SendMessage(toolbarHwnd, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
            Logger.Info(TAG, $"Button count in {area}: {buttonCount}");
            if (buttonCount <= 0) return icons;

            IntPtr imageListPtr = SendMessage(toolbarHwnd, TB_GETIMAGELIST, IntPtr.Zero, IntPtr.Zero);
            Logger.Debug(TAG, $"ImageList handle: {imageListPtr}");

            int tbButtonSize = Marshal.SizeOf<TBBUTTON>();
            Logger.Debug(TAG, $"TBBUTTON size: {tbButtonSize}");

            IntPtr remoteMem = VirtualAllocEx(hProcess, IntPtr.Zero, tbButtonSize + 256, MEM_COMMIT, PAGE_READWRITE);
            if (remoteMem == IntPtr.Zero)
            {
                Logger.Error(TAG, $"VirtualAllocEx failed, err={Marshal.GetLastWin32Error()}");
                return icons;
            }

            try
            {
                for (int i = 0; i < buttonCount; i++)
                {
                    try
                    {
                        var info = ReadSingleButton(toolbarHwnd, hProcess, remoteMem, i, area, imageListPtr);
                        if (info != null)
                        {
                            Logger.Debug(TAG, $"  [{i}] proc={info.ProcessName}, tip='{info.TooltipText}', status={info.Status}");
                            icons.Add(info);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(TAG, $"Button {i} error: {ex.Message}");
                    }
                }
            }
            finally
            {
                VirtualFreeEx(hProcess, remoteMem, 0, MEM_RELEASE);
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }

        return icons;
    }

    private TrayIconInfo? ReadSingleButton(IntPtr toolbarHwnd, IntPtr hProcess, IntPtr remoteMem, int index, string area, IntPtr imageListPtr)
    {
        int tbButtonSize = Marshal.SizeOf<TBBUTTON>();

        IntPtr result = SendMessage(toolbarHwnd, TB_GETBUTTON, (IntPtr)index, remoteMem);
        if (result == IntPtr.Zero) return null;

        byte[] buttonBytes = new byte[tbButtonSize];
        if (!ReadProcessMemory(hProcess, remoteMem, buttonBytes, tbButtonSize, out int bytesRead) || bytesRead < tbButtonSize)
            return null;

        TBBUTTON button = BytesToTBBUTTON(buttonBytes);

        // Read TRAYDATA from dwData to get the REAL owner process
        string processName = string.Empty;
        uint ownerPid = 0;
        IntPtr ownerHWnd = IntPtr.Zero;

        if (button.dwData != IntPtr.Zero)
        {
            int trayDataSize = Marshal.SizeOf<TRAYDATA>();
            byte[] trayDataBytes = new byte[trayDataSize];
            if (ReadProcessMemory(hProcess, button.dwData, trayDataBytes, trayDataSize, out int tdRead) && tdRead >= trayDataSize)
            {
                TRAYDATA trayData = BytesToTRAYDATA(trayDataBytes);
                ownerHWnd = trayData.hWnd;

                if (ownerHWnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(ownerHWnd, out ownerPid);
                    processName = GetProcessName(ownerPid);
                }
            }
        }

        if (string.IsNullOrEmpty(processName))
            processName = $"PID:{ownerPid}";

        // Read tooltip text
        string tooltip = string.Empty;
        if (button.iString != IntPtr.Zero && button.iString != (IntPtr)(-1))
        {
            bool isPointer = IntPtr.Size == 8
                ? (long)button.iString > 0 && (long)button.iString < 0x00007FFFFFFFFFFF
                : (int)button.iString > 0;

            if (isPointer)
                tooltip = ReadRemoteString(hProcess, button.iString, 512);
        }

        bool isHidden = (button.fsState & TBSTATE_HIDDEN) != 0;
        string status = isHidden ? "已隐藏" : "可见";

        // Extract icon
        Icon? icon = null;
        if (imageListPtr != IntPtr.Zero && button.iBitmap >= 0)
        {
            try
            {
                IntPtr hIcon = ImageList_GetIcon(imageListPtr, button.iBitmap, 0);
                if (hIcon != IntPtr.Zero)
                {
                    IntPtr hCopy = CopyIcon(hIcon);
                    if (hCopy != IntPtr.Zero)
                        icon = Icon.FromHandle(hCopy);
                    DestroyIcon(hIcon);
                }
            }
            catch { }
        }

        return new TrayIconInfo
        {
            Icon = icon,
            ProcessName = processName,
            TooltipText = tooltip.Trim('\0').Trim(),
            Area = area,
            Status = status,
            HWnd = toolbarHwnd,
            OwnerHWnd = ownerHWnd,
            OwnerPid = ownerPid,
            ButtonIndex = index,
            IsHidden = isHidden
        };
    }

    // ============ Hide / Show ============

    public (bool success, string output, string error) HideByProcessName(string identifier)
    {
        return RunHideTrayIcon("-a hide -d 1", identifier);
    }

    public (bool success, string output, string error) ShowByProcessName(string identifier)
    {
        return RunHideTrayIcon("-a show -r", identifier);
    }

    /// <summary>
    /// Runs hideTrayIcon.exe directly (no cmd.exe wrapper).
    /// Matches VBS behavior: hideTrayIcon.exe -a hide -d 1 -i "identifier"
    /// </summary>
    private (bool success, string output, string error) RunHideTrayIcon(string actionArgs, string identifier)
    {
        string exePath = FindHideTrayIconExe();
        if (string.IsNullOrEmpty(exePath))
        {
            string msg = "hideTrayIcon.exe not found! Searched: " +
                         string.Join(", ", GetSearchPaths());
            Logger.Error(TAG, msg);
            return (false, "", msg);
        }

        // Build args: -a hide -d 1 -i "ProcessName.exe AnotherName"
        string arguments = $"{actionArgs} -i \"{identifier}\"";

        Logger.Info(TAG, $"Running: \"{exePath}\" {arguments}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Logger.Error(TAG, "Process.Start returned null!");
                return (false, "", "Process.Start returned null");
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            int exitCode = proc.ExitCode;
            Logger.Info(TAG, $"ExitCode={exitCode}");
            if (!string.IsNullOrEmpty(stdout)) Logger.Info(TAG, $"stdout=[{stdout.Trim()}]");
            if (!string.IsNullOrEmpty(stderr)) Logger.Error(TAG, $"stderr=[{stderr.Trim()}]");

            bool ok = exitCode == 0;
            return (ok, stdout.Trim(), stderr.Trim());
        }
        catch (Exception ex)
        {
            Logger.Error(TAG, $"Exception: {ex.GetType().Name}: {ex.Message}");
            return (false, "", ex.Message);
        }
    }

    private static string FindHideTrayIconExe()
    {
        foreach (var path in GetSearchPaths())
        {
            if (File.Exists(path))
            {
                Logger.Info(TAG, $"Found hideTrayIcon.exe at: {path}");
                return path;
            }
        }
        return string.Empty;
    }

    private static string[] GetSearchPaths()
    {
        return
        [
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hideTrayIcon.exe"),
            @"D:\Personal\Downloads\hideTrayIcon.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "hideTrayIcon", "hideTrayIcon.exe"),
        ];
    }

    // ============ Helpers ============

    private static TBBUTTON BytesToTBBUTTON(byte[] bytes)
    {
        var btn = new TBBUTTON();
        int offset = 0;

        btn.iBitmap = BitConverter.ToInt32(bytes, offset); offset += 4;
        btn.idCommand = BitConverter.ToInt32(bytes, offset); offset += 4;
        btn.fsState = bytes[offset]; offset += 1;
        btn.fsStyle = bytes[offset]; offset += 1;
        offset += 6;

        btn.dwData = IntPtr.Size == 8
            ? (IntPtr)BitConverter.ToInt64(bytes, offset)
            : (IntPtr)BitConverter.ToInt32(bytes, offset);
        offset += IntPtr.Size;

        btn.iString = IntPtr.Size == 8
            ? (IntPtr)BitConverter.ToInt64(bytes, offset)
            : (IntPtr)BitConverter.ToInt32(bytes, offset);

        return btn;
    }

    private static TRAYDATA BytesToTRAYDATA(byte[] bytes)
    {
        var td = new TRAYDATA();
        int offset = 0;

        td.hWnd = IntPtr.Size == 8
            ? (IntPtr)BitConverter.ToInt64(bytes, offset)
            : (IntPtr)BitConverter.ToInt32(bytes, offset);
        offset += IntPtr.Size;

        td.uID = BitConverter.ToUInt32(bytes, offset); offset += 4;
        td.uCallbackMessage = BitConverter.ToUInt32(bytes, offset); offset += 4;
        td.Reserved0 = BitConverter.ToUInt32(bytes, offset); offset += 4;
        td.Reserved1 = BitConverter.ToUInt32(bytes, offset); offset += 4;

        td.hIcon = IntPtr.Size == 8
            ? (IntPtr)BitConverter.ToInt64(bytes, offset)
            : (IntPtr)BitConverter.ToInt32(bytes, offset);

        return td;
    }

    private static string ReadRemoteString(IntPtr hProcess, IntPtr remoteAddress, int maxChars)
    {
        byte[] buffer = new byte[maxChars * 2];
        if (ReadProcessMemory(hProcess, remoteAddress, buffer, buffer.Length, out int bytesRead) && bytesRead > 0)
        {
            string str = Encoding.Unicode.GetString(buffer, 0, bytesRead);
            int nullIdx = str.IndexOf('\0');
            if (nullIdx >= 0) str = str[..nullIdx];
            if (!string.IsNullOrEmpty(str)) return str;
        }

        byte[] ansiBuffer = new byte[maxChars];
        if (ReadProcessMemory(hProcess, remoteAddress, ansiBuffer, ansiBuffer.Length, out bytesRead) && bytesRead > 0)
        {
            string str = Encoding.Default.GetString(ansiBuffer, 0, bytesRead);
            int nullIdx = str.IndexOf('\0');
            if (nullIdx >= 0) str = str[..nullIdx];
            return str;
        }

        return string.Empty;
    }

    private static string GetProcessName(uint pid)
    {
        try
        {
            var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return $"PID:{pid}";
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
