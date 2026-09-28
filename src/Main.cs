using Godot;
using RobotClient.Net;
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace RobotClient;

/// <summary>
/// Root node of the vertical-slice client: runs <see cref="NetworkClient"/>
/// against robot-srv, reads WASD/arrows input (throttled to the server tick
/// rate), renders the world procedurally (plane + box mesh, camera, HUD
/// label) and moves the robot from server snapshots. No assets, no
/// CharacterBody3D and no client-side prediction — the server is the truth.
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

    private const double DefaultTickRate = 20.0;
    private const float WorldSize = 100.0f;

    private NetworkClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _sessionTask;

    // Procedural scene nodes.
    private MeshInstance3D? _robotMesh;
    private Label? _statusLabel;

    /// <summary>Shared state between the network thread and the main thread.</summary>
    private readonly object _stateLock = new();
    private SnapshotMessage? _pendingSnapshot;
    private double _tickRate = DefaultTickRate;
    private string _connectionLabel = "connecting...";
    // Set by network callbacks, consumed on the main thread in _Process.
    private bool _statusDirty;

    // Input send throttle: at most one input per tick interval.
    private double _sendCooldown;

    public override void _Ready()
    {
        GD.Print($"[Main] starting NetworkClient -> {ServerUrl}");

        EnsureInputActions();
        BuildScene();

        _client = new NetworkClient(ServerUrl, AuthToken);
        _client.WelcomeReceived += OnWelcome;
        _client.ErrorReceived += OnError;
        _client.SnapshotReceived += OnSnapshot;
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

    public override void _Process(double delta)
    {
        DrainSnapshot();
        SendInput(delta);
        RefreshStatusLabel();
    }

    /// <summary>Register WASD/arrows input actions (physical keys, layout-independent).</summary>
    private void EnsureInputActions()
    {
        AddMoveAction("move_forward", Key.W, Key.Up);
        AddMoveAction("move_back", Key.S, Key.Down);
        AddMoveAction("move_left", Key.A, Key.Left);
        AddMoveAction("move_right", Key.D, Key.Right);
    }

    private static void AddMoveAction(string action, Key primary, Key alternate)
    {
        if (!InputMap.HasAction(action))
        {
            InputMap.AddAction(action);
        }
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = primary });
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = alternate });
    }

    /// <summary>Create the whole visible world procedurally — no assets needed.</summary>
    private void BuildScene()
    {
        // Camera: elevated behind the robot, looking at the world center.
        var camera = new Camera3D { Position = new Vector3(0, 45, -45) };
        camera.LookAt(Vector3.Zero, Vector3.Up);
        AddChild(camera);

        // Ground: a 100x100 plane covering the -50..50 world bounds.
        var ground = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(WorldSize, WorldSize) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.22f, 0.24f, 0.28f),
            },
        };
        AddChild(ground);

        // Robot: a simple box, positioned/rotated from server snapshots.
        _robotMesh = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.5f, 1.5f, 1.5f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.85f, 0.42f, 0.16f),
            },
        };
        AddChild(_robotMesh);

        // HUD label.
        var canvas = new CanvasLayer();
        AddChild(canvas);
        _statusLabel = new Label { Position = new Vector2(8, 8) };
        canvas.AddChild(_statusLabel);
        UpdateStatusLabel();
    }

    /// <summary>
    /// Read the movement vector from the input map. Godot's
    /// <c>GetVector</c> returns <c>(-1, -1)</c> for left+forward; the Y axis
    /// is inverted so that forward becomes a positive <c>z</c> intent.
    /// </summary>
    private (double X, double Z) ReadMoveInput()
    {
        var dir = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        return (dir.X, -dir.Y);
    }

    /// <summary>Send the current input at most once per tick interval.</summary>
    private void SendInput(double delta)
    {
        var (x, z) = ReadMoveInput();
        double tickRate;
        lock (_stateLock)
        {
            tickRate = _tickRate;
        }
        if (tickRate <= 0)
        {
            tickRate = DefaultTickRate;
        }

        _sendCooldown -= delta;
        if (_sendCooldown <= 0)
        {
            _sendCooldown = 1.0 / tickRate;
            SendInputFireAndForget(x, z);
        }
    }

    private async void SendInputFireAndForget(double x, double z)
    {
        try
        {
            // Gate: never send input before the WebSocket is open (or after
            // it has been closed); _client may also be null during shutdown.
            if (_client is null || _client.State != WebSocketState.Open)
            {
                return;
            }
            await _client.SendInputAsync(x, z);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Main] input send failed: {ex.Message}");
        }
    }

    /// <summary>Apply the latest server snapshot on the main thread.</summary>
    private void DrainSnapshot()
    {
        SnapshotMessage? snapshot;
        lock (_stateLock)
        {
            snapshot = _pendingSnapshot;
            _pendingSnapshot = null;
        }
        if (snapshot is null || snapshot.Entities.Count == 0)
        {
            return;
        }

        var entity = snapshot.Entities[0];
        _robotMesh!.Position = new Vector3((float)entity.Pos.X, (float)entity.Pos.Y, (float)entity.Pos.Z);
        _robotMesh.Rotation = new Vector3(0, (float)entity.Yaw, 0);

        string connection;
        lock (_stateLock)
        {
            connection = _connectionLabel;
            // A fresh snapshot already rendered the current state; drop any
            // pending status-only refresh so it can't overwrite this text.
            _statusDirty = false;
        }
        _statusLabel!.Text =
            $"tick={snapshot.Tick}  id={entity.Id}\n" +
            $"pos=({entity.Pos.X:F1}, {entity.Pos.Z:F1})  yaw={entity.Yaw:F2}\n" +
            $"state={entity.State}  speed={entity.Speed:F1}\n{connection}";
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
        lock (_stateLock)
        {
            if (welcome.TickRate > 0)
            {
                _tickRate = welcome.TickRate;
            }
            _connectionLabel = $"session={welcome.SessionId}  v{welcome.ProtocolVersion}";
        }
        GD.Print($"[Main] welcome: session={welcome.SessionId}, " +
                 $"protocol_version={welcome.ProtocolVersion}, tick_rate={welcome.TickRate}");
    }

    private void OnError(ErrorMessage error)
    {
        // Network thread: only record state; the label is updated on the
        // main thread via the dirty flag (see _Process/RefreshStatusLabel).
        lock (_stateLock)
        {
            _connectionLabel = $"server error: {error.Code}";
            _statusDirty = true;
        }
        GD.PrintErr($"[Main] server error: {error.Code}: {error.Message}");
    }

    private void OnSnapshot(SnapshotMessage snapshot)
    {
        // Called from the network thread; the main thread drains this queue.
        lock (_stateLock)
        {
            _pendingSnapshot = snapshot;
        }
    }

    private void OnDisconnected(string reason)
    {
        // Network thread: only record state; the label is updated on the
        // main thread via the dirty flag (see _Process/RefreshStatusLabel).
        lock (_stateLock)
        {
            _connectionLabel = $"disconnected: {reason}";
            _statusDirty = true;
        }
        GD.Print($"[Main] disconnected: {reason}");
    }

    /// <summary>
    /// Push pending connection-state changes to the HUD label. Runs on the
    /// main thread only (<see cref="_Process"/>); network callbacks never
    /// touch Godot nodes directly.
    /// </summary>
    private void RefreshStatusLabel()
    {
        bool dirty;
        lock (_stateLock)
        {
            dirty = _statusDirty;
            _statusDirty = false;
        }
        if (dirty)
        {
            UpdateStatusLabel();
        }
    }

    private void UpdateStatusLabel()
    {
        if (_statusLabel is null)
        {
            return;
        }
        string connection;
        lock (_stateLock)
        {
            connection = _connectionLabel;
        }
        _statusLabel.Text = $"no snapshot yet\n{connection}";
    }
}