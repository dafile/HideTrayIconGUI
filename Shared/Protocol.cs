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
    public const string Register = "register";
    public const string TrayIcons = "tray_icons";
    public const string Ack = "ack";
    public const string Error = "error";
    public const string Pong = "pong";

    // Server -> Client
    public const string Hide = "hide";
    public const string Show = "show";
    public const string Restart = "restart";
    public const string UpdateRules = "update_rules";
    public const string UpdateFilter = "update_filter";
    public const string GetTrayIcons = "get_tray_icons";
    public const string Ping = "ping";
    public const string RestartExplorer = "restart_explorer";
}

// ========== Data Models ==========

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

/// <summary>
/// A single rule containing multiple entries.
/// </summary>
public class RuleInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public List<RuleEntry> Entries { get; set; } = [];
}

/// <summary>
/// A single entry in a rule: process name + tooltip (for reference).
/// </summary>
public class RuleEntry
{
    public string ProcessName { get; set; } = "";
    public string Tooltip { get; set; } = ""; // reference only, not editable
}

/// <summary>
/// Full config push to a client: rules + filter.
/// </summary>
public class ClientConfig
{
    public List<RuleInfo> Rules { get; set; } = [];
    public List<string> Filter { get; set; } = [];
}

/// <summary>
/// Legacy: for backward compat with old clients.
/// </summary>
public class RulesUpdate
{
    public List<string> Rules { get; set; } = [];
}

public class FilterUpdate
{
    public List<string> FilteredProcesses { get; set; } = [];
}
