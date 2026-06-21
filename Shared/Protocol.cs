using System.Text.Json;

namespace Shared;

/// <summary>
/// TCP message protocol between server and client.
/// All messages are JSON + newline delimited.
/// </summary>
public class ProtocolMessage
{
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";

    public T? DeserializePayload<T>()
    {
        try { return JsonSerializer.Deserialize<T>(Payload); }
        catch { return default; }
    }

    public static ProtocolMessage Create<T>(string type, T data)
    {
        return new ProtocolMessage
        {
            Type = type,
            Payload = JsonSerializer.Serialize(data)
        };
    }

    public string Serialize() => JsonSerializer.Serialize(this) + "\n";

    public static ProtocolMessage? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<ProtocolMessage>(json); }
        catch { return null; }
    }
}

// Message types
public static class MsgType
{
    // Client -> Server
    public const string Register = "register";          // Client registers with hostname
    public const string TrayIcons = "tray_icons";      // Client reports tray icon list
    public const string Ack = "ack";                    // Acknowledgement
    public const string Error = "error";                // Error response
    public const string Pong = "pong";                  // Heartbeat response

    // Server -> Client
    public const string Hide = "hide";                  // Hide tray icons
    public const string Show = "show";                  // Show tray icons
    public const string Restart = "restart";            // Restart client
    public const string UpdateRules = "update_rules";   // Update rules
    public const string UpdateFilter = "update_filter"; // Update filter list
    public const string GetTrayIcons = "get_tray_icons";// Request tray icon list
    public const string Ping = "ping";                  // Heartbeat
    public const string RestartExplorer = "restart_explorer"; // Restart explorer.exe
}

// Data models
public class ClientInfo
{
    public string HostName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public DateTime ConnectedAt { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsOnline => (DateTime.Now - LastSeen).TotalSeconds < 30;
}

public class TrayIconData
{
    public string ProcessName { get; set; } = "";
    public string Tooltip { get; set; } = "";
    public string Area { get; set; } = "";
    public string Status { get; set; } = "";
}

public class HideRequest
{
    public List<string> Identifiers { get; set; } = [];
}

public class RulesUpdate
{
    public List<string> Rules { get; set; } = [];  // format: "ProcessName|Tooltip" or "ProcessName"
}

public class FilterUpdate
{
    public List<string> FilteredProcesses { get; set; } = [];
}
