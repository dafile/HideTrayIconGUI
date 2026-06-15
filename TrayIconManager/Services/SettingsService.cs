using System.IO;
using System.Text.Json;
using TrayIconManager.Models;

namespace TrayIconManager.Services;

public class SettingsService
{
    private readonly string _settingsPath;
    private AppSettings _settings = new();

    public AppSettings Settings => _settings;

    public SettingsService()
    {
        _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            _settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_settings, options);
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    public void Update(Action<AppSettings> modifier)
    {
        modifier(_settings);
        Save();
    }
}
