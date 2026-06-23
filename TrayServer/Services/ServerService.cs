using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Shared;

namespace TrayServer.Services;

/// <summary>
/// Manages TCP connections from clients.
/// </summary>
public class ServerService : IDisposable
{
    private TcpListener? _listener;
    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private CancellationTokenSource? _cts;

    public event Action<string, string>? OnLog; // (level, msg)
    public event Action<string>? OnClientConnected;
    public event Action<string>? OnClientDisconnected;
    public event Action<string, List<TrayIconData>>? OnTrayIconsReceived;

    public int Port { get; set; } = 9527;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        Log("INFO", $"Server started on port {Port}");

        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
        Log("INFO", "Server stopped.");
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener!.AcceptTcpClientAsync(ct);
                var endpoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
                Log("INFO", $"Incoming connection from {endpoint}");

                _ = Task.Run(() => HandleClient(tcpClient, ct));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log("ERROR", $"Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClient(TcpClient tcpClient, CancellationToken ct)
    {
        string clientId = "";
        var conn = new ClientConnection(tcpClient);

        try
        {
            var reader = new StreamReader(tcpClient.GetStream(), Encoding.UTF8);

            while (!ct.IsCancellationRequested && tcpClient.Connected)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                var msg = ProtocolMessage.Deserialize(line);
                if (msg == null) continue;

                switch (msg.Type)
                {
                    case MsgType.Register:
                        var info = msg.DeserializePayload<ClientInfo>();
                        if (info != null)
                        {
                            clientId = info.HostName;
                            conn.Info = info;
                            conn.Info.LastSeen = DateTime.Now;
                            conn.IpAddress = info.IpAddress;
                            if (string.IsNullOrEmpty(conn.IpAddress))
                                conn.IpAddress = tcpClient.Client.RemoteEndPoint?.ToString()?.Split(':')[0] ?? "";
                            conn.Info.IpAddress = conn.IpAddress;
                            _clients[clientId] = conn;
                            OnClientConnected?.Invoke(clientId);
                            Log("INFO", $"Client registered: {clientId} ({conn.IpAddress})");
                        }
                        break;

                    case MsgType.TrayIcons:
                        var icons = msg.DeserializePayload<List<TrayIconData>>();
                        if (icons != null && !string.IsNullOrEmpty(clientId))
                        {
                            OnTrayIconsReceived?.Invoke(clientId, icons);
                            Log("INFO", $"Received {icons.Count} tray icons from {clientId}");
                        }
                        break;

                    case MsgType.Pong:
                        if (!string.IsNullOrEmpty(clientId) && _clients.TryGetValue(clientId, out var c))
                            c.Info.LastSeen = DateTime.Now;
                        break;

                    case MsgType.Ack:
                        Log("INFO", $"[{clientId}] ACK: {msg.Payload}");
                        break;

                    case MsgType.Error:
                        Log("ERROR", $"[{clientId}] {msg.Payload}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"Client {clientId} error: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(clientId))
            {
                // Don't remove - just mark offline. Entry stays for reconnect.
                if (_clients.TryGetValue(clientId, out var existingConn) && existingConn.Info != null)
                    existingConn.Info.LastSeen = DateTime.MinValue;
                OnClientDisconnected?.Invoke(clientId);
                Log("INFO", $"Client disconnected: {clientId}");
            }
            conn.Dispose();
        }
    }

    // ========== Send commands to clients ==========

    public bool SendToClient(string clientId, ProtocolMessage msg)
    {
        if (_clients.TryGetValue(clientId, out var conn))
        {
            try
            {
                conn.Send(msg);
                return true;
            }
            catch (Exception ex)
            {
                Log("ERROR", $"Send to {clientId} failed: {ex.Message}");
                return false;
            }
        }
        return false;
    }

    public void SendToAll(ProtocolMessage msg)
    {
        foreach (var (id, conn) in _clients)
        {
            try { conn.Send(msg); }
            catch (Exception ex) { Log("ERROR", $"Send to {id} failed: {ex.Message}"); }
        }
    }

    public void SendToMany(IEnumerable<string> clientIds, ProtocolMessage msg)
    {
        foreach (var id in clientIds)
            SendToClient(id, msg);
    }

    public List<ClientInfo> GetOnlineClients()
    {
        return _clients.Values
            .Where(c => c.Info != null)
            .Select(c => c.Info!)
            .ToList();
    }

    public List<string> GetOnlineClientIds()
    {
        return _clients.Keys.ToList();
    }

    private void Log(string level, string msg)
    {
        OnLog?.Invoke(level, msg);
    }

    public void Dispose()
    {
        Stop();
    }
}

public class ClientConnection : IDisposable
{
    public TcpClient TcpClient { get; }
    public ClientInfo? Info { get; set; }
    public string IpAddress { get; set; } = "";
    private StreamWriter? _writer;

    public ClientConnection(TcpClient client)
    {
        TcpClient = client;
        _writer = new StreamWriter(client.GetStream(), Encoding.UTF8) { AutoFlush = true };
    }

    public void Send(ProtocolMessage msg)
    {
        _writer?.Write(msg.Serialize());
        _writer?.Flush();
    }

    public void Dispose()
    {
        try { TcpClient.Close(); } catch { }
    }
}
