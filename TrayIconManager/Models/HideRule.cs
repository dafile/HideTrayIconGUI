namespace TrayIconManager.Models;

public class HideRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public string MatchText { get; set; } = string.Empty;
    public MatchType MatchType { get; set; } = MatchType.ProcessName;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public enum MatchType
{
    ProcessName,
    TooltipText,
    Both
}
