using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using UploadAgent.Models;

namespace UploadAgent.Services;

/// <summary>
/// Local WebSocket server for Frontend communication.
/// Handles commands from frontend and broadcasts progress updates.
/// </summary>
public class WebSocketServer : IDisposable
{
    private readonly AppConfig _config;
    private readonly ILogger<WebSocketServer> _logger;
    private HttpListener? _listener;
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private CancellationTokenSource? _cts;

    // Event handlers for commands
    public event Func<string, string?, Task>? OnStartCommand;
    public event Func<string, Task>? OnPauseCommand;
    public event Func<string, Task>? OnResumeCommand;
    public event Func<string, Task>? OnCancelCommand;

    public WebSocketServer(AppConfig config, ILogger<WebSocketServer> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Start the WebSocket server.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_config.WsPort}/");
        
        try
        {
            _listener.Start();
            _logger.LogInformation("WebSocket server started on ws://localhost:{Port}", _config.WsPort);

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    
                    if (context.Request.IsWebSocketRequest)
                    {
                        _ = HandleClientAsync(context, _cts.Token);
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException) when (_cts.Token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket server error");
            throw;
        }
    }

    private async Task HandleClientAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var clientId = Guid.NewGuid().ToString();
        WebSocket? webSocket = null;

        try
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            webSocket = wsContext.WebSocket;
            _clients.TryAdd(clientId, webSocket);
            
            _logger.LogInformation("Client {ClientId} connected", clientId);

            // Send config on connect
            await SendToClientAsync(clientId, new ConfigMessage
            {
                ChunkSizeMB = _config.ChunkSizeMB,
                MaxThreads = _config.OptimalThreadCount,
                PresignBatchSize = _config.PresignBatchSize,
                WsPort = _config.WsPort
            });

            var buffer = new byte[4096];
            
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken
                );

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Client requested close",
                        cancellationToken
                    );
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleCommandAsync(message);
                }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error for client {ClientId}", clientId);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            webSocket?.Dispose();
            _logger.LogInformation("Client {ClientId} disconnected", clientId);
        }
    }

    private async Task HandleCommandAsync(string message)
    {
        try
        {
            var command = JsonSerializer.Deserialize<WsCommand>(message);
            if (command == null) return;

            _logger.LogInformation("Received command: {Action}", command.Action);

            switch (command.Action.ToLower())
            {
                case "start":
                    if (!string.IsNullOrEmpty(command.FilePath))
                    {
                        OnStartCommand?.Invoke(command.FilePath, command.BackendUrl);
                    }
                    break;

                case "pause":
                    if (!string.IsNullOrEmpty(command.UploadId))
                    {
                        OnPauseCommand?.Invoke(command.UploadId);
                    }
                    break;

                case "resume":
                    if (!string.IsNullOrEmpty(command.UploadId))
                    {
                        OnResumeCommand?.Invoke(command.UploadId);
                    }
                    break;

                case "cancel":
                    if (!string.IsNullOrEmpty(command.UploadId))
                    {
                        OnCancelCommand?.Invoke(command.UploadId);
                    }
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON command received");
        }
    }

    /// <summary>
    /// Broadcast a message to all connected clients.
    /// </summary>
    public async Task BroadcastAsync<T>(T message) where T : WsMessage
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        foreach (var (clientId, socket) in _clients)
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    _clients.TryRemove(clientId, out _);
                }
            }
        }
    }

    /// <summary>
    /// Send a message to a specific client.
    /// </summary>
    public async Task SendToClientAsync<T>(string clientId, T message) where T : WsMessage
    {
        if (_clients.TryGetValue(clientId, out var socket) && socket.State == WebSocketState.Open)
        {
            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    /// <summary>
    /// Broadcast progress update.
    /// </summary>
    public Task BroadcastProgressAsync(ProgressMessage progress) => BroadcastAsync(progress);

    /// <summary>
    /// Broadcast chunk status update.
    /// </summary>
    public Task BroadcastChunkUpdateAsync(ChunkMessage chunk) => BroadcastAsync(chunk);

    /// <summary>
    /// Broadcast status change.
    /// </summary>
    public Task BroadcastStatusAsync(StatusMessage status) => BroadcastAsync(status);

    /// <summary>
    /// Broadcast error.
    /// </summary>
    public Task BroadcastErrorAsync(ErrorMessage error) => BroadcastAsync(error);

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _listener?.Close();
        foreach (var socket in _clients.Values)
        {
            socket.Dispose();
        }
        _clients.Clear();
    }
}
