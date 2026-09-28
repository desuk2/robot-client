#nullable enable

using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RobotClient.Net
{
    /// <summary>
    /// WebSocket client for the protocol v1 handshake and game session
    /// (robot-srv, /ws). Talks JSON text envelopes over
    /// <see cref="System.Net.WebSockets.ClientWebSocket"/>.
    ///
    /// Threading rules:
    /// - <see cref="RunAsync"/> is the single receive loop; it parses
    ///   envelopes and raises events from the thread it runs on.
    /// - Event callbacks never touch Unity objects — they only receive
    ///   plain DTOs. The game layer drains them on the main thread.
    /// - Sends are serialized through a lock (ClientWebSocket allows only
    ///   one outstanding send).
    /// - Input is rejected until a <c>welcome</c> has been received
    ///   (<see cref="SendInputAsync"/> returns <c>false</c> before that).
    /// </summary>
    public sealed class NetworkClient : IDisposable
    {
        private readonly Uri _uri;
        private readonly string _authToken;
        private readonly string _clientVersion;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly object _stateLock = new object();

        private ClientWebSocket? _socket;
        private long _seq;
        private bool _welcomed;

        /// <param name="url">WebSocket server URL, e.g. <c>ws://127.0.0.1:8080/ws</c>.</param>
        /// <param name="authToken">Auth token stub; real authentication comes in a later stage.</param>
        /// <param name="clientVersion">Client version reported to the server in <c>auth</c>.</param>
        public NetworkClient(string url, string authToken = "", string clientVersion = "0.1.0")
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentException("url must not be empty", nameof(url));
            }
            _uri = new Uri(url);
            _authToken = authToken ?? "";
            _clientVersion = clientVersion ?? "0.1.0";
        }

        /// <summary>Raised when the server confirms the session with <c>welcome</c>.</summary>
        public event Action<WelcomeMessage>? WelcomeReceived;

        /// <summary>Raised when the server rejects something with an <c>error</c> message.</summary>
        public event Action<ErrorMessage>? ErrorReceived;

        /// <summary>Raised when a world <c>snapshot</c> arrives.</summary>
        public event Action<SnapshotMessage>? SnapshotReceived;

        /// <summary>Raised when the order state changes (<c>order.updated</c>).</summary>
        public event Action<OrderUpdatedMessage>? OrderUpdatedReceived;

        /// <summary>Raised when a delivery reward is granted (<c>reward.granted</c>).</summary>
        public event Action<RewardGrantedMessage>? RewardGrantedReceived;

        /// <summary>Raised when the connection is closed (reason for logging).</summary>
        public event Action<string>? Disconnected;

        /// <summary>Current WebSocket state.</summary>
        public WebSocketState State => _socket?.State ?? WebSocketState.None;

        /// <summary>True while the WebSocket is open.</summary>
        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open;

        /// <summary>
        /// True once the server accepted our <c>auth</c>. Input sent before
        /// this flag is dropped (<see cref="SendInputAsync"/> returns false).
        /// </summary>
        public bool IsWelcomed
        {
            get
            {
                lock (_stateLock)
                {
                    return _welcomed;
                }
            }
        }

        /// <summary>Open the WebSocket connection.</summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            lock (_stateLock)
            {
                _welcomed = false;
            }
            _socket?.Dispose();
            _socket = new ClientWebSocket();
            await _socket.ConnectAsync(_uri, ct);
        }

        /// <summary>Send the <c>auth</c> envelope stub (token + client version).</summary>
        public async Task SendAuthAsync(CancellationToken ct = default)
        {
            var payload = new AuthPayload
            {
                token = _authToken,
                client_version = _clientVersion,
            };
            await SendAsync(Protocol.TypeAuth, Json.ToJson(payload), ct);
        }

        /// <summary>
        /// Send an <c>input</c> movement intent. Callers should throttle
        /// sends to the server tick rate (see <see cref="WelcomeMessage.tick_rate"/>).
        /// </summary>
        /// <param name="x">Lateral input in <c>-1..=1</c>, positive = right.</param>
        /// <param name="z">Forward input in <c>-1..=1</c>, positive = forward.</param>
        /// <returns>
        /// <c>true</c> when the envelope was sent; <c>false</c> when the
        /// socket is not open or no <c>welcome</c> was received yet.
        /// </returns>
        public async Task<bool> SendInputAsync(double x, double z, CancellationToken ct = default)
        {
            if (!IsWelcomed || !IsConnected)
            {
                return false;
            }

            var payload = new InputPayload
            {
                move = new MoveInput { x = x, z = z },
            };
            await SendAsync(Protocol.TypeInput, Json.ToJson(payload), ct);
            return true;
        }

        /// <summary>
        /// Send an <c>order.accept</c> message for <paramref name="orderId"/>.
        /// The server rejects the action with an <c>error</c> envelope when the
        /// order is not <c>available</c>; the connection stays open.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the envelope was sent; <c>false</c> when the
        /// socket is not open or no <c>welcome</c> was received yet.
        /// </returns>
        public async Task<bool> SendOrderAcceptAsync(string orderId, CancellationToken ct = default)
        {
            return await SendOrderActionAsync(Protocol.TypeOrderAccept, orderId, ct);
        }

        /// <summary>
        /// Send an <c>order.deliver</c> message for <paramref name="orderId"/>.
        /// The server validates proximity to the destination and grants the
        /// reward on success (<c>reward.granted</c>), otherwise replies with
        /// an <c>error</c> envelope; the connection stays open.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the envelope was sent; <c>false</c> when the
        /// socket is not open or no <c>welcome</c> was received yet.
        /// </returns>
        public async Task<bool> SendOrderDeliverAsync(string orderId, CancellationToken ct = default)
        {
            return await SendOrderActionAsync(Protocol.TypeOrderDeliver, orderId, ct);
        }

        private async Task<bool> SendOrderActionAsync(string type, string orderId, CancellationToken ct)
        {
            if (!IsWelcomed || !IsConnected || string.IsNullOrWhiteSpace(orderId))
            {
                return false;
            }

            var payload = new OrderActionPayload { order_id = orderId };
            await SendAsync(type, Json.ToJson(payload), ct);
            return true;
        }

        /// <summary>
        /// Read server messages until the connection closes or the token is
        /// cancelled. Raises <see cref="WelcomeReceived"/>,
        /// <see cref="ErrorReceived"/>, <see cref="SnapshotReceived"/>,
        /// <see cref="OrderUpdatedReceived"/> and
        /// <see cref="RewardGrantedReceived"/>.
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
                string? json = await ReadTextMessageAsync(buffer, ct);
                if (json is null)
                {
                    // The server closed the connection (close frame received).
                    Disconnected?.Invoke("server closed the connection");
                    return;
                }

                HandleMessage(json);
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

        /// <summary>
        /// Parse one envelope and dispatch it by <c>type</c>. Runs on the
        /// receive-loop thread; no Unity objects are touched here.
        /// </summary>
        private void HandleMessage(string json)
        {
            Envelope? envelope;
            try
            {
                envelope = Json.FromJson<Envelope>(json);
            }
            catch (Exception)
            {
                envelope = null;
            }

            if (envelope is null || string.IsNullOrEmpty(envelope.type))
            {
                ErrorReceived?.Invoke(new ErrorMessage
                {
                    code = "bad_json",
                    message = "unreadable envelope from server",
                });
                return;
            }

            string? payloadJson = Json.ExtractPayload(json);
            switch (envelope.type)
            {
                case Protocol.TypeWelcome:
                    var welcome = FromPayload<WelcomeMessage>(payloadJson);
                    if (welcome is not null)
                    {
                        lock (_stateLock)
                        {
                            _welcomed = true;
                        }
                        WelcomeReceived?.Invoke(welcome);
                    }
                    break;

                case Protocol.TypeError:
                    var error = FromPayload<ErrorMessage>(payloadJson);
                    if (error is not null)
                    {
                        ErrorReceived?.Invoke(error);
                    }
                    break;

                case Protocol.TypeSnapshot:
                    var snapshot = FromPayload<SnapshotMessage>(payloadJson);
                    if (snapshot is not null)
                    {
                        SnapshotReceived?.Invoke(snapshot);
                    }
                    break;

                case Protocol.TypeOrderUpdated:
                    var order = FromPayload<OrderUpdatedMessage>(payloadJson);
                    if (order is not null)
                    {
                        OrderUpdatedReceived?.Invoke(order);
                    }
                    break;

                case Protocol.TypeRewardGranted:
                    var reward = FromPayload<RewardGrantedMessage>(payloadJson);
                    if (reward is not null)
                    {
                        RewardGrantedReceived?.Invoke(reward);
                    }
                    break;

                default:
                    // Unknown types are ignored for forward compatibility.
                    break;
            }
        }

        private static T? FromPayload<T>(string? payloadJson) where T : class
        {
            if (payloadJson is null)
            {
                return null;
            }

            try
            {
                return Json.FromJson<T>(payloadJson);
            }
            catch (Exception)
            {
                return null;
            }
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
                        // The server speaks JSON text frames in this slice.
                        return null;
                    default:
                        // Ping/pong are handled by the runtime transport.
                        break;
                }
            } while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private async Task SendAsync(string type, string payloadJson, CancellationToken ct)
        {
            if (_socket is null || _socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WebSocket is not open; call ConnectAsync first.");
            }

            long seq = NextSeq();
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string json = Json.ComposeEnvelope(Protocol.Version, type, seq, ts, payloadJson);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            // ClientWebSocket allows only one outstanding send at a time.
            await _sendLock.WaitAsync(ct);
            try
            {
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private long NextSeq() => Interlocked.Increment(ref _seq);
    }
}