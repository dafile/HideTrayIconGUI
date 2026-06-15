namespace TrayIconManager.Models;

public class AppSettings
{
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public int PollingIntervalSeconds { get; set; } = 5;
    public bool AutoStartPolling { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
}
