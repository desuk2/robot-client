#nullable enable

using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using RobotClient.Net;
using UnityEngine;
using UnityEngine.Rendering;

namespace RobotClient.Bootstrap
{
    /// <summary>
    /// Unity bootstrap for the vertical slice. Attached to the Bootstrap
    /// object in <c>Assets/Scenes/Main.unity</c>. Responsibilities:
    ///
    /// - connect <see cref="NetworkClient"/> to the server (default
    ///   <c>ws://127.0.0.1:8080/ws</c>), send <c>auth</c>, run the receive loop;
    /// - drain the latest <c>snapshot</c> on the main thread and position the
    ///   robot cube strictly from server state (no client-side prediction);
    /// - read WASD/arrows and send <c>input</c> no more often than the server
    ///   tick rate announced in <c>welcome</c>;
    /// - drive the order → delivery → reward loop: markers for the order
    ///   origin/destination, a cargo indicator, edge-triggered
    ///   <c>E</c> (accept) / <c>F</c> (deliver), and a wallet in the HUD;
    /// - render the world procedurally (plane + cube, sun, ambient light,
    ///   third-person follow camera) and draw a HUD with <c>OnGUI</c>.
    ///
    /// No external assets, no InputSystem, no Newtonsoft — only the built-in
    /// <c>JsonUtility</c> for the protocol DTOs.
    /// </summary>
    public sealed class GameSession : MonoBehaviour
    {
        [SerializeField]
        private string serverUrl = "ws://127.0.0.1:8080/ws";

        [SerializeField]
        private string authToken = "";

        private const float DefaultTickRate = 20f;

        /// <summary>Visual Y offset for the robot cube: half of its 1.5 height.</summary>
        private const float RobotY = 0.75f;

        /// <summary>Interaction radius advertised by the server (orders, pickup/delivery).</summary>
        private const float InteractRadius = 2f;

        private NetworkClient? _client;
        private CancellationTokenSource? _cts;
        private Task? _sessionTask;

        private Transform? _robot;
        private Camera? _camera;

        /// <summary>Shared state between the network thread and the main thread.</summary>
        private readonly object _stateLock = new object();
        private SnapshotMessage? _pendingSnapshot;
        private double _tickRate = DefaultTickRate;
        private string _connectionLabel = "connecting...";

        // Input send throttle: at most one send per tick interval.
        private double _sendCooldown;
        private double _lastSentX;
        private double _lastSentZ;
        private bool _hasSentInput;
        private bool _wasWelcomed;

        // Order + economy state shared between the network and main threads.
        private readonly object _orderLock = new object();
        private bool _hasOrder;
        private string _orderId = "";
        private string _orderStatus = "-";
        private bool _orderPickedUp;
        private Vector3 _orderOrigin;
        private Vector3 _orderDestination;
        private long _walletCredits;
        private long _walletXp;
        private bool _hasWallet;
        private string _lastRewardLabel = "";

        // Visual markers (main thread only).
        private Transform? _originMarker;
        private Transform? _destinationMarker;
        private Transform? _cargoCube;

        // Last rendered state for the HUD (written on the main thread only).
        private bool _hudHasSnapshot;
        private long _hudTick;
        private string _hudId = "-";
        private Vector3 _hudPos;
        private float _hudYawDeg;
        private string _hudState = "-";
        private float _hudSpeed;

        private void Start()
        {
            BuildWorld();
            Debug.Log($"[GameSession] starting NetworkClient -> {serverUrl}");

            _client = new NetworkClient(serverUrl, authToken);
            _client.WelcomeReceived += OnWelcome;
            _client.ErrorReceived += OnError;
            _client.SnapshotReceived += OnSnapshot;
            _client.OrderUpdatedReceived += OnOrderUpdated;
            _client.RewardGrantedReceived += OnRewardGranted;
            _client.Disconnected += OnDisconnected;

            _cts = new CancellationTokenSource();
            // Run the session off the main thread: continuations stay on the
            // thread pool, so events arrive on a background thread and are
            // drained on the main thread in Update().
            _sessionTask = Task.Run(() => RunSessionAsync(_cts.Token), _cts.Token);
        }

        private void Update()
        {
            DrainSnapshot();
            DrainOrder();
            SendInput();
            SendOrderActions();
            UpdateCamera();
        }

        private void OnDestroy()
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
            _cts = null;
        }

        /// <summary>Create the whole visible world procedurally — no assets needed.</summary>
        private void BuildWorld()
        {
            // Ambient light (flat, neutral blue-grey).
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.35f, 0.37f, 0.42f);
            RenderSettings.ambientIntensity = 0.8f;

            // Directional "sun" light.
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sunGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // Ground: the Plane primitive is 10x10 units; scale to 100x100
            // to cover the server world bounds (-50..50 on x and z).
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(10f, 1f, 10f);
            SetMaterialColor(ground, new Color(0.22f, 0.24f, 0.28f));

            // Robot: a simple cube, positioned/rotated from server snapshots.
            var robotGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            robotGo.name = "Robot";
            robotGo.transform.localScale = new Vector3(1.5f, 1.5f, 1.5f);
            SetMaterialColor(robotGo, new Color(0.85f, 0.42f, 0.16f));
            _robot = robotGo.transform;

            // Order markers: pickup point (green) and delivery point (gold).
            // Both are hidden until the first `order.updated` arrives.
            _originMarker = BuildCylinderMarker("OrderOrigin", new Color(0.25f, 0.8f, 0.35f), 0.35f);
            _destinationMarker = BuildCylinderMarker("OrderDestination", new Color(1f, 0.72f, 0.15f), 0.7f);

            // Cargo crate shown above the robot while the cargo is picked up.
            var cargoGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cargoGo.name = "Cargo";
            cargoGo.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
            SetMaterialColor(cargoGo, new Color(0.75f, 0.55f, 0.2f));
            _cargoCube = cargoGo.transform;
            _cargoCube.gameObject.SetActive(false);

            // Third-person follow camera (created in code; no Main Camera in the scene).
            var cameraGo = new GameObject("FollowCamera");
            _camera = cameraGo.AddComponent<Camera>();
            _camera.fieldOfView = 60f;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 500f;
            cameraGo.transform.position = new Vector3(0f, 8f, -12f);
            cameraGo.transform.LookAt(new Vector3(0f, 1f, 0f));
        }

        private static void SetMaterialColor(GameObject go, Color color)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
            {
                return;
            }

            // Built-in render pipeline shaders only; no URP assets.
            Shader? shader = Shader.Find("Standard");
            if (shader == null)
            {
                shader = Shader.Find("Legacy Shaders/Diffuse");
            }
            if (shader == null)
            {
                return;
            }

            var material = new Material(shader);
            material.color = color;
            renderer.sharedMaterial = material;
        }

        /// <summary>
        /// Build a flat cylinder marker (the Cylinder primitive is 1 unit
        /// wide and 2 units tall; <paramref name="scaleY"/> scales the height
        /// so the top is readable from the third-person camera).
        /// </summary>
        private static Transform BuildCylinderMarker(string name, Color color, float scaleY)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.localScale = new Vector3(1.4f, scaleY, 1.4f);
            SetMaterialColor(go, color);
            go.SetActive(false);
            return go.transform;
        }

        /// <summary>
        /// Apply the latest server snapshot on the main thread. The server's
        /// <c>pos.y</c> is always 0 (flat world); the cube is raised by
        /// <see cref="RobotY"/> so it rests on the plane. Server yaw comes in
        /// radians (0 = +Z, π/2 = +X) and is converted to Unity degrees.
        /// </summary>
        private void DrainSnapshot()
        {
            SnapshotMessage? snapshot;
            lock (_stateLock)
            {
                snapshot = _pendingSnapshot;
                _pendingSnapshot = null;
            }

            if (snapshot is null || snapshot.entities is null || snapshot.entities.Length == 0)
            {
                return;
            }

            EntityState entity = snapshot.entities[0];
            var pos = new Vector3((float)entity.pos.x, RobotY, (float)entity.pos.z);
            if (_robot != null)
            {
                _robot.position = pos;
                _robot.rotation = Quaternion.Euler(0f, (float)(entity.yaw * Mathf.Rad2Deg), 0f);
            }

            _hudTick = snapshot.tick;
            _hudId = entity.id;
            _hudPos = pos;
            _hudYawDeg = (float)(entity.yaw * Mathf.Rad2Deg);
            _hudState = entity.state;
            _hudSpeed = (float)entity.speed;
            _hudHasSnapshot = true;
        }

        /// <summary>
        /// Apply the latest order state on the main thread: position the
        /// origin/destination markers and show/hide the cargo crate above the
        /// robot while the cargo is picked up.
        /// </summary>
        private void DrainOrder()
        {
            bool hasOrder;
            bool pickedUp;
            Vector3 origin;
            Vector3 destination;
            lock (_orderLock)
            {
                hasOrder = _hasOrder;
                pickedUp = _orderPickedUp;
                origin = _orderOrigin;
                destination = _orderDestination;
            }

            if (_originMarker != null)
            {
                // Cylinder base sits on the ground; y = half of its scaled height.
                _originMarker.position = origin + Vector3.up * 0.35f;
                _originMarker.gameObject.SetActive(hasOrder);
            }
            if (_destinationMarker != null)
            {
                _destinationMarker.position = destination + Vector3.up * 0.7f;
                _destinationMarker.gameObject.SetActive(hasOrder);
            }
            if (_cargoCube != null)
            {
                bool showCargo = hasOrder && pickedUp && _robot != null;
                if (_cargoCube.gameObject.activeSelf != showCargo)
                {
                    _cargoCube.gameObject.SetActive(showCargo);
                }
                if (showCargo && _robot != null)
                {
                    // The crate floats just above the robot's body.
                    _cargoCube.position = _robot.position + Vector3.up * 1.3f;
                }
            }
        }

        /// <summary>
        /// Edge-triggered order actions: <c>E</c> accepts the current order,
        /// <c>F</c> attempts delivery. The server validates the action and
        /// replies with <c>order.updated</c> or an <c>error</c>; the
        /// connection is never torn down.
        /// </summary>
        private void SendOrderActions()
        {
            if (_client is null || !_wasWelcomed)
            {
                return;
            }

            string orderId;
            lock (_orderLock)
            {
                orderId = _orderId;
            }
            if (string.IsNullOrEmpty(orderId))
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.E))
            {
                SendOrderActionFireAndForget(Protocol.TypeOrderAccept, orderId);
            }
            if (Input.GetKeyDown(KeyCode.F))
            {
                SendOrderActionFireAndForget(Protocol.TypeOrderDeliver, orderId);
            }
        }

        private async void SendOrderActionFireAndForget(string type, string orderId)
        {
            try
            {
                if (_client is null)
                {
                    return;
                }
                // NetworkClient itself gates on welcome and open socket.
                bool sent = type == Protocol.TypeOrderAccept
                    ? await _client.SendOrderAcceptAsync(orderId)
                    : await _client.SendOrderDeliverAsync(orderId);
                if (sent)
                {
                    Debug.Log($"[GameSession] {type} sent for {orderId}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameSession] {type} send failed: {ex.Message}");
            }
        }

        /// <summary>Read WASD/arrows and send <c>input</c> at most once per tick interval.</summary>
        private void SendInput()
        {
            double x = ReadMoveX();
            double z = ReadMoveZ();

            // A key held before `welcome` was dropped by the no-input-before-
            // welcome gate; force one send right after the session starts.
            bool welcomed = _client != null && _client.IsWelcomed;
            if (welcomed && !_wasWelcomed)
            {
                _hasSentInput = false;
                _sendCooldown = 0;
            }
            _wasWelcomed = welcomed;

            double tickRate;
            lock (_stateLock)
            {
                tickRate = _tickRate;
            }
            if (tickRate <= 0)
            {
                tickRate = DefaultTickRate;
            }

            _sendCooldown -= Time.deltaTime;
            if (_sendCooldown > 0)
            {
                return;
            }
            _sendCooldown = 1.0 / tickRate;

            // The server persists the last received input between ticks, so
            // only a change needs to be sent (idle stays idle without spam).
            if (_hasSentInput &&
                Math.Abs(x - _lastSentX) < 1e-6 &&
                Math.Abs(z - _lastSentZ) < 1e-6)
            {
                return;
            }

            _lastSentX = x;
            _lastSentZ = z;
            _hasSentInput = true;
            SendInputFireAndForget(x, z);
        }

        private static double ReadMoveX()
        {
            double x = 0;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) x -= 1;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) x += 1;
            return x;
        }

        private static double ReadMoveZ()
        {
            double z = 0;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) z += 1;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) z -= 1;
            return z;
        }

        private async void SendInputFireAndForget(double x, double z)
        {
            try
            {
                if (_client is null)
                {
                    return;
                }
                // NetworkClient itself gates on welcome and open socket.
                await _client.SendInputAsync(x, z);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameSession] input send failed: {ex.Message}");
            }
        }

        /// <summary>Third-person follow: keep the camera behind and above the robot.</summary>
        private void UpdateCamera()
        {
            if (_camera is null || _robot is null)
            {
                return;
            }

            Vector3 targetPos = _robot.position + new Vector3(0f, 7f, -9f);
            _camera.transform.position = Vector3.Lerp(_camera.transform.position, targetPos, 10f * Time.deltaTime);
            _camera.transform.LookAt(_robot.position + Vector3.up * 1.2f);
        }

        private void OnGUI()
        {
            string connection;
            lock (_stateLock)
            {
                connection = _connectionLabel;
            }

            string orderId, orderStatus, lastReward;
            bool pickedUp, hasWallet;
            long credits, xp;
            Vector3 origin, destination;
            lock (_orderLock)
            {
                orderId = _orderId;
                orderStatus = _orderStatus;
                pickedUp = _orderPickedUp;
                origin = _orderOrigin;
                destination = _orderDestination;
                credits = _walletCredits;
                xp = _walletXp;
                hasWallet = _hasWallet;
                lastReward = _lastRewardLabel;
            }

            GUI.Box(new Rect(8, 8, 470, 240), GUIContent.none);
            GUILayout.BeginArea(new Rect(16, 14, 450, 230));
            if (_hudHasSnapshot)
            {
                GUILayout.Label($"tick={_hudTick}  id={_hudId}");
                GUILayout.Label($"pos=({_hudPos.x:F1}, {_hudPos.z:F1})  yaw={_hudYawDeg:F1} deg");
                GUILayout.Label($"state={_hudState}  speed={_hudSpeed:F1}");
            }
            else
            {
                GUILayout.Label("no snapshot yet");
            }
            GUILayout.Label(connection);

            GUILayout.Space(4);
            if (!string.IsNullOrEmpty(orderId))
            {
                GUILayout.Label($"order={orderId}  status={orderStatus}");
                GUILayout.Label($"cargo={(pickedUp ? "picked up" : "not picked up")}");
                GUILayout.Label(
                    $"dist origin={Vector3.Distance(_hudPos, origin):F1}  " +
                    $"dist destination={Vector3.Distance(_hudPos, destination):F1}  " +
                    $"(interact < {InteractRadius:F0})");
                GUILayout.Label("E = accept order   F = deliver order");
            }
            else
            {
                GUILayout.Label("no order yet (waiting for order.updated)");
            }
            GUILayout.Label($"wallet: {(hasWallet ? $"{credits} cr / {xp} xp" : "—")}");
            if (!string.IsNullOrEmpty(lastReward))
            {
                GUILayout.Label($"last reward: {lastReward}");
            }
            GUILayout.EndArea();
        }

        private async Task RunSessionAsync(CancellationToken ct)
        {
            NetworkClient? client = _client;
            if (client is null)
            {
                return;
            }

            try
            {
                await client.ConnectAsync(ct);
                Debug.Log($"[GameSession] connected ({client.State}); sending auth");
                await client.SendAuthAsync(ct);
                await client.RunAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Debug.Log("[GameSession] session cancelled");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameSession] network session failed: {ex.Message}");
                lock (_stateLock)
                {
                    _connectionLabel = $"connection failed: {ex.Message}";
                }
            }
            finally
            {
                try
                {
                    await client.CloseAsync();
                }
                catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
                {
                    // Socket already gone; nothing to do.
                }
                lock (_stateLock)
                {
                    _connectionLabel = "disconnected";
                }
                Debug.Log($"[GameSession] closed ({client.State})");
            }
        }

        // Network-thread callbacks: they only record state under a lock and
        // never touch Unity objects; the main thread drains everything.

        private void OnWelcome(WelcomeMessage welcome)
        {
            lock (_stateLock)
            {
                if (welcome.tick_rate > 0)
                {
                    _tickRate = welcome.tick_rate;
                }
                _connectionLabel = $"session={welcome.session_id}  v{welcome.protocol_version}  tick={welcome.tick_rate}Hz";
            }
            Debug.Log($"[GameSession] welcome: session={welcome.session_id}, " +
                      $"protocol_version={welcome.protocol_version}, tick_rate={welcome.tick_rate}");
        }

        private void OnError(ErrorMessage error)
        {
            lock (_stateLock)
            {
                _connectionLabel = $"server error: {error.code}";
            }
            Debug.LogError($"[GameSession] server error: {error.code}: {error.message}");
        }

        private void OnSnapshot(SnapshotMessage snapshot)
        {
            lock (_stateLock)
            {
                _pendingSnapshot = snapshot;
            }
        }

        private void OnOrderUpdated(OrderUpdatedMessage order)
        {
            lock (_orderLock)
            {
                _hasOrder = true;
                _orderId = order.id;
                _orderStatus = order.status;
                _orderPickedUp = order.picked_up;
                _orderOrigin = new Vector3((float)order.origin.x, 0f, (float)order.origin.z);
                _orderDestination = new Vector3((float)order.destination.x, 0f, (float)order.destination.z);
            }
            Debug.Log($"[GameSession] order.updated: id={order.id}, status={order.status}, " +
                      $"picked_up={order.picked_up}");
        }

        private void OnRewardGranted(RewardGrantedMessage reward)
        {
            lock (_orderLock)
            {
                _walletCredits = reward.wallet.credits;
                _walletXp = reward.wallet.xp;
                _hasWallet = true;
                _lastRewardLabel = $"+{reward.reward.credits} cr  +{reward.reward.xp} xp ({reward.order_id})";
            }
            Debug.Log($"[GameSession] reward.granted: +{reward.reward.credits} cr, " +
                      $"+{reward.reward.xp} xp -> wallet {reward.wallet.credits} cr / {reward.wallet.xp} xp");
        }

        private void OnDisconnected(string reason)
        {
            lock (_stateLock)
            {
                _connectionLabel = $"disconnected: {reason}";
            }
            Debug.Log($"[GameSession] disconnected: {reason}");
        }
    }
}