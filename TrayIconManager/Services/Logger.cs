using System.IO;

namespace TrayIconManager.Services;

public static class Logger
{
    private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
    private static readonly object _lock = new();

    static Logger()
    {
        try { Directory.CreateDirectory(LogDir); } catch { }
    }

    public static void Info(string tag, string message) => Write("INFO", tag, message);
    public static void Error(string tag, string message) => Write("ERROR", tag, message);
    public static void Debug(string tag, string message) => Write("DEBUG", tag, message);

    private static void Write(string level, string tag, string message)
    {
        try
        {
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string line = $"[{ts}] [{level}] [{tag}] {message}";
            System.Diagnostics.Debug.WriteLine(line);

            lock (_lock)
            {
                string file = Path.Combine(LogDir, $"tray_{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch { }
    }

    public static string GetLogFilePath()
    {
        return Path.Combine(LogDir, $"tray_{DateTime.Now:yyyyMMdd}.log");
    }
}
