using System.IO;
using System.Text.Json;
using Shared;

namespace TrayServer.Services;

/// <summary>
/// Persists client list, rules, and filter to disk.
/// </summary>
public class PersistenceService
{
    private readonly string _dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

    public List<ClientInfo> LoadClients()
    {
        return Load<List<ClientInfo>>("clients.json") ?? [];
    }

    public void SaveClients(List<ClientInfo> clients)
    {
        Save("clients.json", clients);
    }

    public List<string> LoadRules()
    {
        return Load<List<string>>("rules.json") ?? [];
    }

    public void SaveRules(List<string> rules)
    {
        Save("rules.json", rules);
    }

    public List<string> LoadFilter()
    {
        return Load<List<string>>("filter.json") ?? ["Taskmgr", "Idle"];
    }

    public void SaveFilter(List<string> filter)
    {
        Save("filter.json", filter);
    }

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
