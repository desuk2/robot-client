using Godot;
using RobotClient.Net;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RobotClient;

/// <summary>
/// Root node of the vertical-slice client: runs <see cref="NetworkClient"/>
/// against robot-srv and logs connection state. No 3D models or assets.
/// </summary>
public partial class Main : Node
{
    /// <summary>
    /// Server WebSocket URL. Set in the inspector (export parameter);
    /// defaults to the local dev server.
    /// </summary>
    [Export]
    public string ServerUrl { get; set; } = "ws://127.0.0.1:8080/ws";

    /// <summary>Auth token stub — real authentication comes in a later stage.</summary>
    [Export]
    public string AuthToken { get; set; } = "";

    private NetworkClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _sessionTask;

    public override void _Ready()
    {
        GD.Print($"[Main] starting NetworkClient -> {ServerUrl}");

        _client = new NetworkClient(ServerUrl, AuthToken);
        _client.WelcomeReceived += OnWelcome;
        _client.ErrorReceived += OnError;
        _client.Disconnected += OnDisconnected;

        _cts = new CancellationTokenSource();
        _sessionTask = RunSessionAsync(_cts.Token);
    }

    public override void _ExitTree()
    {
        _cts?.Cancel();
        if (_sessionTask is not null)
        {
            try
            {
                _sessionTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // The task was cancelled mid-await; ignore on shutdown.
            }
        }
        _client?.Dispose();
        _client = null;
        _cts?.Dispose();
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        try
        {
            await _client!.ConnectAsync(ct);
            GD.Print($"[Main] connected ({_client.State}); sending auth envelope");
            await _client.SendAuthAsync(ct);
            await _client.RunAsync(ct);
        }
        catch (OperationCanceledException)
        {
            GD.Print("[Main] session cancelled");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Main] network session failed: {ex.Message}");
        }
        finally
        {
            await _client!.CloseAsync();
            GD.Print($"[Main] closed ({_client.State})");
        }
    }

    private void OnWelcome(WelcomeMessage welcome)
    {
        GD.Print($"[Main] welcome: session={welcome.SessionId}, " +
                 $"protocol_version={welcome.ProtocolVersion}, tick_rate={welcome.TickRate}");
    }

    private void OnError(ErrorMessage error)
    {
        GD.PrintErr($"[Main] server error: {error.Code}: {error.Message}");
    }

    private void OnDisconnected(string reason)
    {
        GD.Print($"[Main] disconnected: {reason}");
    }
}