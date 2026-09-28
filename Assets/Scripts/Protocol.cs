#nullable enable

using System;
using System.Text;
using UnityEngine;

namespace RobotClient.Net
{
    /// <summary>
    /// Protocol v1 wire contract, fixed byte-for-byte by robot-srv
    /// (docs/ARCHITECTURE.md §6.2): every message is an envelope
    /// <c>{v,type,seq,ts,payload}</c>. Field layout below is the same on
    /// the server: <c>input.move.x</c>/<c>input.move.z</c> for movement and
    /// <c>snapshot.entities[i].pos/yaw/state/speed</c> for world state.
    /// </summary>
    public static class Protocol
    {
        /// <summary>Protocol version advertised on every envelope.</summary>
        public const long Version = 1;

        /// <summary>Client → server: authentication.</summary>
        public const string TypeAuth = "auth";

        /// <summary>Server → client: session established.</summary>
        public const string TypeWelcome = "welcome";

        /// <summary>Server → client: protocol error (code + message).</summary>
        public const string TypeError = "error";

        /// <summary>Client → server: movement intent (input.move).</summary>
        public const string TypeInput = "input";

        /// <summary>Server → client: world snapshot.</summary>
        public const string TypeSnapshot = "snapshot";

        /// <summary>Client → server: accept the order (id = payload.order_id).</summary>
        public const string TypeOrderAccept = "order.accept";

        /// <summary>Client → server: deliver the order (id = payload.order_id).</summary>
        public const string TypeOrderDeliver = "order.deliver";

        /// <summary>Server → client: order state changed (accepted / picked up / new order).</summary>
        public const string TypeOrderUpdated = "order.updated";

        /// <summary>Server → client: reward granted after a delivery + wallet balance.</summary>
        public const string TypeRewardGranted = "reward.granted";
    }

    /// <summary>
    /// Message envelope header. JsonUtility has no polymorphism, so the
    /// <c>payload</c> member is not declared here: it is ignored during
    /// header deserialization and parsed separately by type.
    /// </summary>
    [Serializable]
    public sealed class Envelope
    {
        public int v;
        public string type = "";
        public long seq;
        public long ts;
    }

    /// <summary>Client → server <c>auth</c> payload.</summary>
    [Serializable]
    public sealed class AuthPayload
    {
        public string token = "";
        public string client_version = "";
    }

    /// <summary>Server → client <c>welcome</c> payload.</summary>
    [Serializable]
    public sealed class WelcomeMessage
    {
        public string session_id = "";
        public long protocol_version;
        public long tick_rate;
    }

    /// <summary>Server → client <c>error</c> payload.</summary>
    [Serializable]
    public sealed class ErrorMessage
    {
        public string code = "";
        public string message = "";
    }

    /// <summary>Client → server <c>input</c> payload.</summary>
    [Serializable]
    public sealed class InputPayload
    {
        /// <summary>Movement vector in robot-local space.</summary>
        public MoveInput move = new MoveInput();
    }

    /// <summary>Movement vector components, each in <c>-1..=1</c>.</summary>
    [Serializable]
    public sealed class MoveInput
    {
        /// <summary>Lateral input (strafe), positive = right.</summary>
        public double x;

        /// <summary>Forward input, positive = forward.</summary>
        public double z;
    }

    /// <summary>Server → client <c>snapshot</c> payload.</summary>
    [Serializable]
    public sealed class SnapshotMessage
    {
        /// <summary>Server tick this snapshot was produced on.</summary>
        public long tick;

        /// <summary>All visible entities.</summary>
        public EntityState[] entities = Array.Empty<EntityState>();
    }

    /// <summary>A single entity inside a snapshot.</summary>
    [Serializable]
    public sealed class EntityState
    {
        public string id = "";

        /// <summary>World position; the server keeps <c>y = 0</c> (flat world).</summary>
        public Vec3 pos = new Vec3();

        /// <summary>Heading in radians around Y (0 = +Z, π/2 = +X).</summary>
        public double yaw;

        /// <summary>Motion state: <c>idle</c> | <c>moving</c>.</summary>
        public string state = "";

        /// <summary>Ground speed in world units per second.</summary>
        public double speed;
    }

    /// <summary>World-space point.</summary>
    [Serializable]
    public sealed class Vec3
    {
        public double x;
        public double y;
        public double z;
    }

    /// <summary>Client → server <c>order.accept</c> / <c>order.deliver</c> payload.</summary>
    [Serializable]
    public sealed class OrderActionPayload
    {
        /// <summary>Id of the order to accept or deliver.</summary>
        public string order_id = "";
    }

    /// <summary>
    /// Server → client <c>order.updated</c> payload. Mirrors the flat wire
    /// shape of robot-srv <c>OrderUpdatedPayload</c> one-to-one.
    /// </summary>
    [Serializable]
    public sealed class OrderUpdatedMessage
    {
        /// <summary>Order identifier.</summary>
        public string id = "";

        /// <summary>Cargo pickup point (server keeps <c>y = 0</c>).</summary>
        public Vec3 origin = new Vec3();

        /// <summary>Delivery point (server keeps <c>y = 0</c>).</summary>
        public Vec3 destination = new Vec3();

        /// <summary>Lifecycle status: <c>available</c> | <c>in_progress</c> | <c>completed</c>.</summary>
        public string status = "";

        /// <summary>Whether the cargo was picked up (auto-pickup near the origin).</summary>
        public bool picked_up;
    }

    /// <summary>A single granted reward (robot-srv <c>economy::Reward</c>).</summary>
    [Serializable]
    public sealed class Reward
    {
        public long credits;
        public long xp;
    }

    /// <summary>Player balance after a grant (robot-srv <c>economy::Wallet</c>).</summary>
    [Serializable]
    public sealed class Wallet
    {
        public long credits;
        public long xp;
    }

    /// <summary>Server → client <c>reward.granted</c> payload.</summary>
    [Serializable]
    public sealed class RewardGrantedMessage
    {
        /// <summary>Id of the order that was completed.</summary>
        public string order_id = "";

        /// <summary>The reward just granted.</summary>
        public Reward reward = new Reward();

        /// <summary>The player's balance after granting.</summary>
        public Wallet wallet = new Wallet();
    }

    /// <summary>
    /// Minimal JSON helpers for the fixed v1 envelope. The wire shape is
    /// <c>{"v":1,"type":"...","seq":..,"ts":..,"payload":{...}}</c> — field
    /// order below matches serde's struct order on robot-srv, so the
    /// produced bytes are byte-for-byte equivalent to the server's own
    /// serializer for the same values.
    /// </summary>
    public static class Json
    {
        /// <summary>
        /// Compose an envelope string exactly as robot-srv expects.
        /// <paramref name="payloadJson"/> is the already-serialized payload.
        /// </summary>
        public static string ComposeEnvelope(long v, string type, long seq, long ts, string payloadJson)
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"v\":").Append(v).Append(',');
            sb.Append("\"type\":\"").Append(Escape(type)).Append('"').Append(',');
            sb.Append("\"seq\":").Append(seq).Append(',');
            sb.Append("\"ts\":").Append(ts).Append(',');
            sb.Append("\"payload\":").Append(payloadJson);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Extract the raw JSON of the <c>payload</c> member from a compact
        /// envelope string (the format produced by robot-srv). Handles
        /// nested objects and string values that contain braces. Returns
        /// <c>null</c> when the member is missing or malformed.
        /// </summary>
        public static string? ExtractPayload(string envelopeJson)
        {
            if (string.IsNullOrEmpty(envelopeJson))
            {
                return null;
            }

            const string key = "\"payload\"";
            int keyIndex = envelopeJson.IndexOf(key, StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                return null;
            }

            int i = keyIndex + key.Length;
            while (i < envelopeJson.Length && char.IsWhiteSpace(envelopeJson[i])) i++;
            if (i >= envelopeJson.Length || envelopeJson[i] != ':') return null;
            i++;
            while (i < envelopeJson.Length && char.IsWhiteSpace(envelopeJson[i])) i++;
            if (i >= envelopeJson.Length || envelopeJson[i] != '{') return null;

            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int j = i; j < envelopeJson.Length; j++)
            {
                char c = envelopeJson[j];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0)
                        {
                            return envelopeJson.Substring(i, j - i + 1);
                        }
                        break;
                }
            }
            return null;
        }

        /// <summary>Escape a string for a JSON double-quoted value.</summary>
        public static string Escape(string value)
        {
            if (value == null)
            {
                return "";
            }

            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Serialize a DTO with Unity's <c>JsonUtility</c>. All protocol DTOs
        /// are public-field snake_case classes, so the output matches the
        /// wire contract.
        /// </summary>
        public static string ToJson(object value) => JsonUtility.ToJson(value);

        /// <summary>Deserialize a JSON string with Unity's <c>JsonUtility</c>.</summary>
        public static T? FromJson<T>(string json) where T : class =>
            JsonUtility.FromJson<T>(json);
    }
}