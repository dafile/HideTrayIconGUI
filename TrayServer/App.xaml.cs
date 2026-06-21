using System.IO;
using System.Windows;

namespace TrayServer;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Log startup immediately
        try
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "server_startup.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] App starting...\n");
        }
        catch { }

        DispatcherUnhandledException += (s, ev) =>
        {
            LogCrash(ev.Exception);
            ev.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            if (ev.ExceptionObject is Exception ex) LogCrash(ex);
        };

        TaskScheduler.UnobservedTaskException += (s, ev) =>
        {
            LogCrash(ev.Exception);
            ev.SetObserved();
        };

        base.OnStartup(e);
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "server_crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CRASH\n{ex}\n\n");
        }
        catch { }
    }
}
