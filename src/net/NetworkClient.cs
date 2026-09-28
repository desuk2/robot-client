using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RobotClient.Net;

/// <summary>
/// Protocol v1 message envelope. Wire format is fixed by robot-srv
/// docs/ARCHITECTURE.md §6.2:
/// <code>{ "v": 1, "type": "input", "seq": 42, "ts": 1720000000000, "payload": {} }</code>
/// </summary>
public sealed class Envelope
{
    public long V { get; set; }
    public string Type { get; set; } = "";
    public long Seq { get; set; }
    public long Ts { get; set; }
    public JsonElement Payload { get; set; }
}

/// <summary>Payload of the server <c>welcome</c> message (ARCHITECTURE.md §6.4).</summary>
public sealed class WelcomeMessage
{
    public string SessionId { get; set; } = "";
    public long ProtocolVersion { get; set; }
    public long TickRate { get; set; }
}

/// <summary>Payload of the server <c>error</c> message (ARCHITECTURE.md §6.4).</summary>
public sealed class ErrorMessage
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>
/// Payload of the client <c>input</c> message: a movement vector in
/// robot-local space (ARCHITECTURE.md §6.4).
/// </summary>
public sealed class InputPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("move")]
    public MoveInput Move { get; set; } = new();
}

/// <summary>Movement vector components; each in <c>-1..=1</c>.</summary>
public sealed class MoveInput
{
    /// <summary>Lateral input (strafe), positive = right.</summary>
    public double X { get; set; }
    /// <summary>Forward input, positive = forward.</summary>
    public double Z { get; set; }
}

/// <summary>Payload of the server <c>snapshot</c> message (ARCHITECTURE.md §6.5).</summary>
public sealed class SnapshotMessage
{
    /// <summary>Server tick this snapshot was produced on.</summary>
    public long Tick { get; set; }
    /// <summary>All visible entities.</summary>
    public List<EntityState> Entities { get; set; } = new();
}

/// <summary>A single entity inside a snapshot.</summary>
public sealed class EntityState
{
    public string Id { get; set; } = "";
    public Vec3 Pos { get; set; } = new();
    /// <summary>Heading in radians around Y (0 = +Z, π/2 = +X).</summary>
    public double Yaw { get; set; }
    /// <summary>Motion state: <c>idle</c> | <c>moving</c>.</summary>
    public string State { get; set; } = "";
    /// <summary>Ground speed in world units per second.</summary>
    public double Speed { get; set; }
}

/// <summary>World-space point; <c>Y</c> stays <c>0</c> in this slice.</summary>
public sealed class Vec3
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

/// <summary>
/// Pure C# WebSocket client for the protocol v1 handshake. Deliberately has
/// no dependency on Godot nodes (see robot-client docs/ARCHITECTURE.md §5);
/// results are delivered via events.
/// </summary>
public sealed class NetworkClient : IDisposable
{
    /// <summary>Client → server: authentication stub.</summary>
    public const string TypeAuth = "auth";
    /// <summary>Server → client: session established.</summary>
    public const string TypeWelcome = "welcome";
    /// <summary>Server → client: protocol error (code + message).</summary>
    public const string TypeError = "error";
    /// <summary>Client → server: movement intent (ARCHITECTURE.md §6.4).</summary>
    public const string TypeInput = "input";
    /// <summary>Server → client: world snapshot (ARCHITECTURE.md §6.5).</summary>
    public const string TypeSnapshot = "snapshot";

    private const long ProtocolVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // The wire format is snake_case (ARCHITECTURE.md §6.2).
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly Uri _uri;
    private readonly string _authToken;
    private readonly string _clientVersion;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private long _seq;

    public NetworkClient(string url, string authToken = "", string clientVersion = "0.1.0")
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("url must not be empty", nameof(url));
        }
        _uri = new Uri(url);
        _authToken = authToken;
        _clientVersion = clientVersion;
    }

    /// <summary>Raised when the server confirms the session with <c>welcome</c>.</summary>
    public event Action<WelcomeMessage>? WelcomeReceived;

    /// <summary>Raised when the server rejects something with an <c>error</c> message.</summary>
    public event Action<ErrorMessage>? ErrorReceived;

    /// <summary>Raised when a world <c>snapshot</c> arrives (ARCHITECTURE.md §6.5).</summary>
    public event Action<SnapshotMessage>? SnapshotReceived;

    /// <summary>Raised when the connection is closed (reason for logging).</summary>
    public event Action<string>? Disconnected;

    public WebSocketState State => _socket?.State ?? WebSocketState.None;

    /// <summary>Open the WebSocket connection.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _socket?.Dispose();
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(_uri, ct);
    }

    /// <summary>Send the <c>auth</c> envelope stub (token + client version).</summary>
    public async Task SendAuthAsync(CancellationToken ct = default)
    {
        var envelope = new Envelope
        {
            V = ProtocolVersion,
            Type = TypeAuth,
            Seq = NextSeq(),
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = JsonSerializer.SerializeToElement(
                new { token = _authToken, client_version = _clientVersion },
                JsonOptions),
        };
        await SendAsync(envelope, ct);
    }

    /// <summary>
    /// Send an <c>input</c> movement intent. Callers should throttle this to
    /// the server tick rate (see <see cref="WelcomeMessage.TickRate"/>).
    /// </summary>
    /// <param name="x">Lateral input in <c>-1..=1</c>, positive = right.</param>
    /// <param name="z">Forward input in <c>-1..=1</c>, positive = forward.</param>
    public async Task SendInputAsync(double x, double z, CancellationToken ct = default)
    {
        var envelope = new Envelope
        {
            V = ProtocolVersion,
            Type = TypeInput,
            Seq = NextSeq(),
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = JsonSerializer.SerializeToElement(
                new InputPayload { Move = new MoveInput { X = x, Z = z } },
                JsonOptions),
        };
        await SendAsync(envelope, ct);
    }

    /// <summary>
    /// Read server messages until the connection closes or the token is
    /// cancelled. Raises <see cref="WelcomeReceived"/>,
    /// <see cref="ErrorReceived"/> and <see cref="SnapshotReceived"/>.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (_socket is null)
        {
            throw new InvalidOperationException("Not connected; call ConnectAsync first.");
        }

        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            var json = await ReadTextMessageAsync(buffer, ct);
            if (json is null)
            {
                // The server closed the connection (close frame received).
                Disconnected?.Invoke("server closed the connection");
                return;
            }

            Envelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<Envelope>(json, JsonOptions)
                    ?? throw new JsonException("empty envelope");
            }
            catch (JsonException)
            {
                ErrorReceived?.Invoke(new ErrorMessage
                {
                    Code = "bad_json",
                    Message = "unreadable envelope from server",
                });
                continue;
            }

            switch (envelope.Type)
            {
                case TypeWelcome:
                    var welcome = envelope.Payload.Deserialize<WelcomeMessage>(JsonOptions);
                    if (welcome is not null)
                    {
                        WelcomeReceived?.Invoke(welcome);
                    }
                    break;
                case TypeError:
                    var error = envelope.Payload.Deserialize<ErrorMessage>(JsonOptions);
                    if (error is not null)
                    {
                        ErrorReceived?.Invoke(error);
                    }
                    break;
                case TypeSnapshot:
                    var snapshot = envelope.Payload.Deserialize<SnapshotMessage>(JsonOptions);
                    if (snapshot is not null)
                    {
                        SnapshotReceived?.Invoke(snapshot);
                    }
                    break;
                default:
                    // Unknown types are ignored for forward compatibility.
                    break;
            }
        }
    }

    /// <summary>Politely close the connection (close frame + dispose).</summary>
    public async Task CloseAsync()
    {
        if (_socket is null)
        {
            return;
        }
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "client closing",
                    CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // Socket already gone; nothing to do.
        }
        finally
        {
            _socket.Dispose();
            _socket = null;
        }
    }

    public void Dispose()
    {
        try
        {
            CloseAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
        {
            // Best-effort dispose on shutdown; the socket may already be gone.
        }
        _sendLock.Dispose();
    }

    private async Task<string?> ReadTextMessageAsync(byte[] buffer, CancellationToken ct)
    {
        if (_socket is null)
        {
            return null;
        }
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            switch (result.MessageType)
            {
                case WebSocketMessageType.Close:
                    // Acknowledge and report to the caller.
                    await _socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "server closed",
                        CancellationToken.None);
                    return null;
                case WebSocketMessageType.Text:
                    stream.Write(buffer, 0, result.Count);
                    break;
                case WebSocketMessageType.Binary:
                    // The server only speaks JSON text frames in this slice.
                    return null;
                default:
                    // Ping/pong are handled by the runtime transport.
                    break;
            }
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task SendAsync(Envelope envelope, CancellationToken ct)
    {
        if (_socket is null)
        {
            throw new InvalidOperationException("Not connected; call ConnectAsync first.");
        }
        // ClientWebSocket allows only one outstanding send at a time.
        await _sendLock.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private long NextSeq() => Interlocked.Increment(ref _seq);
}