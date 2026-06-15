using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TrayIconManager.Models;

namespace TrayIconManager.Services;

public class RuleManagerService
{
    private readonly string _rulesFilePath;
    private readonly TrayIconService _trayService;
    private List<HideRule> _rules = [];
    private System.Threading.Timer? _pollingTimer;
    private bool _isPolling;

    public event Action<string>? LogMessage;
    public event Action<int>? RulesApplied;

    public List<HideRule> Rules => _rules;
    public bool IsPolling => _isPolling;

    public RuleManagerService(TrayIconService trayService)
    {
        _trayService = trayService;
        _rulesFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hide_rules.json");
        LoadRules();
    }

    public void LoadRules()
    {
        try
        {
            if (File.Exists(_rulesFilePath))
            {
                string json = File.ReadAllText(_rulesFilePath);
                _rules = JsonSerializer.Deserialize<List<HideRule>>(json) ?? [];
                Log($"Loaded {_rules.Count} rules from {_rulesFilePath}");
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading rules: {ex.Message}");
            _rules = [];
        }
    }

    public void SaveRules()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_rules, options);
            File.WriteAllText(_rulesFilePath, json);
            Log($"Saved {_rules.Count} rules to {_rulesFilePath}");
        }
        catch (Exception ex)
        {
            Log($"Error saving rules: {ex.Message}");
        }
    }

    public void AddRule(HideRule rule)
    {
        _rules.Add(rule);
        SaveRules();
    }

    public void RemoveRule(string ruleId)
    {
        _rules.RemoveAll(r => r.Id == ruleId);
        SaveRules();
    }

    public void UpdateRule(HideRule rule)
    {
        var idx = _rules.FindIndex(r => r.Id == rule.Id);
        if (idx >= 0)
        {
            _rules[idx] = rule;
            SaveRules();
        }
    }

    public void StartPolling(int intervalSeconds, AppSettings settings)
    {
        if (_isPolling) return;

        _isPolling = true;
        _pollingTimer = new System.Threading.Timer(async _ =>
        {
            await ApplyRulesAsync();
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(intervalSeconds));

        Log($"Started polling every {intervalSeconds}s");
    }

    public void StopPolling()
    {
        _isPolling = false;
        _pollingTimer?.Dispose();
        _pollingTimer = null;
        Log("Stopped polling");
    }

    private async Task ApplyRulesAsync()
    {
        try
        {
            var activeRules = _rules.Where(r => r.IsEnabled).ToList();
            if (activeRules.Count == 0) return;

            var icons = _trayService.EnumerateTrayIcons();
            var identifiersToHide = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int matchedCount = 0;

            foreach (var rule in activeRules)
            {
                foreach (var icon in icons)
                {
                    if (MatchesRule(icon, rule))
                    {
                        // Use tooltip as primary identifier (matches VBS script behavior)
                        string identifier = !string.IsNullOrEmpty(icon.TooltipText)
                            ? icon.TooltipText
                            : icon.ProcessName;

                        if (identifiersToHide.Add(identifier))
                        {
                            matchedCount++;
                            Log($"Rule '{rule.Name}' matched: {identifier}");
                        }
                    }
                }
            }

            // Batch all identifiers into a single hideTrayIcon.exe call (like the VBS script)
            if (identifiersToHide.Count > 0)
            {
                string allIdentifiers = string.Join(" ", identifiersToHide);
                Log($"Batch hiding: {allIdentifiers}");
                var (ok, stdout, stderr) = _trayService.HideByProcessName(allIdentifiers);
                Log($"Result: ok={ok}, stderr=[{stderr}]");
                if (ok) RulesApplied?.Invoke(matchedCount);
            }
        }
        catch (Exception ex)
        {
            Log($"Error applying rules: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    public List<TrayIconInfo> GetMatchingIcons(List<TrayIconInfo> icons)
    {
        var activeRules = _rules.Where(r => r.IsEnabled).ToList();
        var matched = new List<TrayIconInfo>();

        foreach (var icon in icons)
        {
            foreach (var rule in activeRules)
            {
                if (MatchesRule(icon, rule))
                {
                    icon.IsSelected = true;
                    matched.Add(icon);
                    break;
                }
            }
        }

        return matched;
    }

    private static bool MatchesRule(TrayIconInfo icon, HideRule rule)
    {
        string matchText = rule.MatchText;
        if (string.IsNullOrWhiteSpace(matchText)) return false;

        bool processMatch = icon.ProcessName.Contains(matchText, StringComparison.OrdinalIgnoreCase);
        bool tooltipMatch = icon.TooltipText.Contains(matchText, StringComparison.OrdinalIgnoreCase);

        return rule.MatchType switch
        {
            Models.MatchType.ProcessName => processMatch,
            Models.MatchType.TooltipText => tooltipMatch,
            Models.MatchType.Both => processMatch || tooltipMatch,
            _ => false
        };
    }

    private void Log(string message)
    {
        Debug.WriteLine($"[RuleManager] {message}");
        LogMessage?.Invoke(message);
    }
}
