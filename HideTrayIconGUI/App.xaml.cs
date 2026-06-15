using System.IO;
using System.Windows;

namespace HideTrayIconGUI;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Global exception handling - prevent silent crashes
        DispatcherUnhandledException += (s, ev) =>
        {
            LogCrash(ev.Exception);
            ev.Handled = true;
            System.Windows.MessageBox.Show($"发生错误:\n{ev.Exception.Message}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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

        _mutex = new Mutex(true, "HideTrayIconGUI_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("HideTrayIconGUI 已在运行中。", "提示",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            string path = Path.Combine(logDir, "crash.log");
            string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CRASH\n" +
                          $"Type: {ex.GetType().Name}\n" +
                          $"Message: {ex.Message}\n" +
                          $"Stack: {ex.StackTrace}\n" +
                          $"Inner: {ex.InnerException}\n\n";
            File.AppendAllText(path, text);
        }
        catch { }
    }
}
