using System.Windows;

namespace TrayIconManager;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance check
        _mutex = new Mutex(true, "TrayIconManager_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("TrayIconManager 已在运行中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
}
