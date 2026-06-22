using System.IO;
using System.Text.Json;
using Shared;

namespace TrayServer.Services;

/// <summary>
/// Persists client list, rules, filter, assignments, and remarks.
/// </summary>
public class PersistenceService
{
    private readonly string _dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

    public List<ClientInfo> LoadClients() => Load<List<ClientInfo>>("clients.json") ?? [];
    public void SaveClients(List<ClientInfo> clients) => Save("clients.json", clients);

    public List<RuleInfo> LoadRules() => Load<List<RuleInfo>>("rules.json") ?? [];
    public void SaveRules(List<RuleInfo> rules) => Save("rules.json", rules);

    public List<string> LoadFilter() => Load<List<string>>("filter.json") ?? ["Taskmgr", "Idle"];
    public void SaveFilter(List<string> filter) => Save("filter.json", filter);

    /// <summary>Client hostname -> rule name assignment</summary>
    public Dictionary<string, string> LoadAssignments() =>
        Load<Dictionary<string, string>>("assignments.json") ?? new();
    public void SaveAssignments(Dictionary<string, string> map) => Save("assignments.json", map);

    /// <summary>Client hostname -> remark text</summary>
    public Dictionary<string, string> LoadRemarks() =>
        Load<Dictionary<string, string>>("remarks.json") ?? new();
    public void SaveRemarks(Dictionary<string, string> map) => Save("remarks.json", map);

    private T? Load<T>(string file)
    {
        try
        {
            string path = Path.Combine(_dataDir, file);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch { }
        return default;
    }

    private void Save<T>(string file, T data)
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            string path = Path.Combine(_dataDir, file);
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
