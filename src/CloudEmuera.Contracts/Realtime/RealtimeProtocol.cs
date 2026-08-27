using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CloudEmuera.Contracts.Realtime;

/// <summary>
/// Constants and closed client message contracts for the browser realtime
/// protocol. Display payloads use the atomic committed-frame contract from
/// P1-S09; this file freezes the v5 envelope and browser commands around it.
/// </summary>
public static class RealtimeProtocol
{
    public const int Version = 5;
    public const string Subprotocol = "cloudemuera.realtime.v5";
    public const string PayloadSchemaVersion = "p1-s09-tooltip";
    public const int DefaultClientJsonMaxDepth = 32;
    public const int DefaultClientMessageMaxBytes = 64 * 1024;
    public const int MaxIdentifierLength = 128;
    public const int MaxCapabilityDigestLength = 256;
    public const int MaxInputValueLength = 16_384;

    public static bool IsClientType(string type) => type switch
    {
        "client.hello" or "connection.pong" or "session.resume" or
        "session.unsubscribe" or "session.input" => true,
        _ => false,
    };
}

public sealed record RealtimeClientHelloPayload(
    int[] SupportedProtocolVersions,
    string CapabilityDigest,
    string[] SupportedCapabilities);

public sealed record RealtimeResumePayload(
    string CapabilityDigest,
    ulong? LastEpoch = null);

public sealed record RealtimeUnsubscribePayload;

public sealed record RealtimePongPayload(string Nonce);

public sealed record RealtimePointerPayload(
    [property: JsonRequired] int X,
    [property: JsonRequired] int Y,
    [property: JsonRequired] int Button,
    [property: JsonRequired] bool Pressed);

public sealed record RealtimeKeyPayload(
    [property: JsonRequired] int KeyCode,
    [property: JsonRequired] bool Control,
    [property: JsonRequired] bool Alt,
    [property: JsonRequired] bool Shift);

public sealed record RealtimeInputPayload(
    string ClientMessageId,
    string Source,
    string Value,
    [property: JsonPropertyName("pointer")] RealtimePointerPayload? PointerData = null,
    RealtimeKeyPayload? Key = null);

public abstract record RealtimeClientPayload;

public sealed record ClientHelloMessage(RealtimeClientHelloPayload Value) : RealtimeClientPayload;

public sealed record ConnectionPongMessage(RealtimePongPayload Value) : RealtimeClientPayload;

public sealed record SessionResumeMessage(RealtimeResumePayload Value) : RealtimeClientPayload;

public sealed record SessionUnsubscribeMessage(RealtimeUnsubscribePayload Value) : RealtimeClientPayload;

public sealed record SessionInputMessage(RealtimeInputPayload Value) : RealtimeClientPayload;

public sealed record RealtimeEnvelopeMetadata(
    int ProtocolVersion,
    string Type,
    string MessageId,
    string? CorrelationId,
    string? SessionId,
    ulong? WorkerEpoch,
    long? Sequence);

/// <summary>
/// Parsed input message. PayloadJson is intentionally not exposed: parsing
/// produces a closed DTO and does not retain an unbounded JsonElement tree.
/// </summary>
public sealed record RealtimeParsedMessage(
    RealtimeEnvelopeMetadata Envelope,
    RealtimeClientPayload Payload);

public sealed class RealtimeProtocolException : JsonException
{
    public RealtimeProtocolException(string reasonCode, string message, int closeCode = 1002)
        : base(message)
    {
        ReasonCode = reasonCode;
        CloseCode = closeCode;
    }

    public string ReasonCode { get; }

    public int CloseCode { get; }
}

public sealed record RealtimeProtocolParserOptions(
    int MaxMessageBytes = RealtimeProtocol.DefaultClientMessageMaxBytes,
    int MaxDepth = RealtimeProtocol.DefaultClientJsonMaxDepth);

/// <summary>
/// Strict, allocation-bounded envelope parser.  Utf8JsonReader is used for
/// duplicate-property, depth, UTF-8 and finite-number validation.  Typed
/// payloads are then materialized through the source-generated context.
/// </summary>
public static class RealtimeProtocolParser
{
    public static RealtimeParsedMessage Parse(
        ReadOnlyMemory<byte> json,
        RealtimeProtocolParserOptions? parserOptions = null)
    {
        try
        {
            return ParseCore(json, parserOptions);
        }
        catch (RealtimeProtocolException)
        {
            throw;
        }
        catch (JsonException exception) when (exception.Message.Contains("depth", StringComparison.OrdinalIgnoreCase))
        {
            throw new RealtimeProtocolException("json_too_deep", "The realtime JSON exceeds its depth limit.", 1002);
        }
        catch (JsonException)
        {
            throw new RealtimeProtocolException("invalid_json", "The realtime message is not valid UTF-8 JSON.", 1002);
        }
    }

    private static RealtimeParsedMessage ParseCore(
        ReadOnlyMemory<byte> json,
        RealtimeProtocolParserOptions? parserOptions)
    {
        RealtimeProtocolParserOptions options = parserOptions ?? new();
        if (options.MaxMessageBytes <= 0 || options.MaxDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(parserOptions));
        if (json.Length > options.MaxMessageBytes)
            throw new RealtimeProtocolException("message_too_large", "The realtime message exceeds its byte limit.", 1009);

        var reader = new Utf8JsonReader(
            json.Span,
            new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = options.MaxDepth,
                AllowTrailingCommas = false,
            });

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw Invalid("invalid_envelope", "The realtime message must be a JSON object.");

        var properties = new HashSet<string>(StringComparer.Ordinal);
        int protocolVersion = 0;
        string? type = null;
        string? messageId = null;
        string? correlationId = null;
        string? sessionId = null;
        ulong? workerEpoch = null;
        long? sequence = null;
        byte[]? payloadJson = null;
        int propertyCount = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw Invalid("invalid_envelope", "The realtime envelope contains an invalid property.");
            if (++propertyCount > 64)
                throw Invalid("invalid_envelope", "The realtime envelope has too many properties.");

            string property = reader.GetString() ?? string.Empty;
            if (!properties.Add(property))
                throw Invalid("duplicate_property", "The realtime envelope contains a duplicate property.");
            if (!reader.Read())
                throw Invalid("invalid_envelope", "The realtime envelope has an incomplete property.");

            switch (property)
            {
                case "protocolVersion":
                    protocolVersion = ReadInt32(ref reader, "protocolVersion");
                    break;
                case "type":
                    type = ReadString(ref reader, "type");
                    break;
                case "messageId":
                    messageId = ReadString(ref reader, "messageId");
                    break;
                case "correlationId":
                    correlationId = ReadNullableString(ref reader, "correlationId");
                    break;
                case "sessionId":
                    sessionId = ReadNullableString(ref reader, "sessionId");
                    break;
                case "workerEpoch":
                    workerEpoch = ReadUInt64(ref reader, "workerEpoch");
                    break;
                case "sequence":
                    sequence = ReadInt64(ref reader, "sequence");
                    break;
                case "payload":
                    int start = checked((int)reader.TokenStartIndex);
                    ValidateValue(ref reader, 0, options.MaxDepth);
                    int end = checked((int)reader.BytesConsumed);
                    payloadJson = json.Slice(start, end - start).ToArray();
                    break;
                default:
                    ValidateValue(ref reader, 0, options.MaxDepth);
                    break;
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            throw Invalid("invalid_envelope", "The realtime envelope contains trailing JSON.");
        if (protocolVersion != RealtimeProtocol.Version)
            throw Invalid("unsupported_protocol_version", "The realtime protocol version is not supported.");
        ValidateIdentifier(type, "type");
        ValidateIdentifier(messageId, "messageId");
        if (correlationId is not null)
            ValidateIdentifier(correlationId, "correlationId");
        if (sessionId is not null)
            ValidateIdentifier(sessionId, "sessionId");
        if (workerEpoch is 0)
            throw Invalid("invalid_envelope", "workerEpoch must be positive.");
        if (sequence is < 0)
            throw Invalid("invalid_envelope", "sequence cannot be negative.");
        if (payloadJson is null)
            throw Invalid("missing_payload", "The realtime envelope must contain payload.");
        if (!RealtimeProtocol.IsClientType(type!))
            throw Invalid("unknown_type", "The realtime message type is not supported.");

        var envelope = new RealtimeEnvelopeMetadata(
            protocolVersion,
            type!,
            messageId!,
            correlationId,
            sessionId,
            workerEpoch,
            sequence);
        RealtimeClientPayload payload = DeserializePayload(type!, payloadJson);
        ValidateMessage(envelope, payload);
        return new RealtimeParsedMessage(envelope, payload);
    }

    private static RealtimeClientPayload DeserializePayload(string type, byte[] payloadJson) => type switch
    {
        "client.hello" => new ClientHelloMessage(Deserialize<RealtimeClientHelloPayload>(payloadJson, RealtimeJsonContext.Default.RealtimeClientHelloPayload)),
        "connection.pong" => new ConnectionPongMessage(Deserialize<RealtimePongPayload>(payloadJson, RealtimeJsonContext.Default.RealtimePongPayload)),
        "session.resume" => new SessionResumeMessage(Deserialize<RealtimeResumePayload>(payloadJson, RealtimeJsonContext.Default.RealtimeResumePayload)),
        "session.unsubscribe" => new SessionUnsubscribeMessage(Deserialize<RealtimeUnsubscribePayload>(payloadJson, RealtimeJsonContext.Default.RealtimeUnsubscribePayload)),
        "session.input" => new SessionInputMessage(Deserialize<RealtimeInputPayload>(payloadJson, RealtimeJsonContext.Default.RealtimeInputPayload)),
        _ => throw Invalid("unknown_type", "The realtime message type is not supported."),
    };

    private static T Deserialize<T>(byte[] bytes, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, typeInfo)
                ?? throw Invalid("invalid_payload", "The realtime payload is null.");
        }
        catch (RealtimeProtocolException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new RealtimeProtocolException("invalid_payload", "The realtime payload is invalid.", 1002);
        }
    }

    private static void ValidateMessage(RealtimeEnvelopeMetadata envelope, RealtimeClientPayload payload)
    {
        switch (payload)
        {
            case ClientHelloMessage hello:
                RequireConnectionEnvelope(envelope, "client.hello");
                if (hello.Value.SupportedProtocolVersions is not { Length: > 0 and <= 16 } versions ||
                    versions.Any(value => value is < 1 or > 32) ||
                    string.IsNullOrWhiteSpace(hello.Value.CapabilityDigest) ||
                    hello.Value.CapabilityDigest.Length > RealtimeProtocol.MaxCapabilityDigestLength ||
                    hello.Value.SupportedCapabilities is null or { Length: > 64 })
                    throw Invalid("invalid_payload", "The client hello payload is invalid.");
                break;
            case ConnectionPongMessage pong:
                RequireConnectionEnvelope(envelope, "connection.pong");
                ValidateIdentifier(pong.Value.Nonce, "payload.nonce");
                break;
            case SessionResumeMessage resume:
                RequireSessionEnvelope(envelope, "session.resume");
                ValidateCapabilityDigest(resume.Value.CapabilityDigest);
                if (resume.Value.LastEpoch is 0)
                    throw Invalid("invalid_payload", "lastEpoch must be positive when present.");
                break;
            case SessionUnsubscribeMessage:
                RequireSessionEnvelope(envelope, "session.unsubscribe");
                break;
            case SessionInputMessage input:
                RequireSessionEnvelope(envelope, "session.input");
                if (envelope.WorkerEpoch is null)
                    throw Invalid("missing_worker_epoch", "session.input requires workerEpoch.");
                ValidateIdentifier(input.Value.ClientMessageId, "payload.clientMessageId");
                if (input.Value.Value is null || input.Value.Value.Length > RealtimeProtocol.MaxInputValueLength)
                    throw Invalid("invalid_payload", "The input value is invalid.");
                if (input.Value.Source is not ("KEYBOARD" or "BUTTON" or "POINTER"))
                    throw Invalid("invalid_command", "Browser input source is invalid.");
                if (input.Value.Source == "POINTER" && input.Value.PointerData is null || input.Value.Source != "POINTER" && input.Value.PointerData is not null)
                    throw Invalid("invalid_command", "Pointer payload does not match source.");
                if (input.Value.Source == "KEYBOARD" && input.Value.Key is null || input.Value.Source != "KEYBOARD" && input.Value.Key is not null)
                    throw Invalid("invalid_command", "Key payload does not match source.");
                if (input.Value.PointerData is { Button: < 0 or > 16 })
                    throw Invalid("invalid_command", "Pointer button is outside its limit.");
                if (input.Value.Key is { KeyCode: < 0 or > 255 })
                    throw Invalid("invalid_command", "Key code is outside its limit.");
                break;
            default:
                throw Invalid("invalid_payload", "The realtime payload discriminator is invalid.");
        }
    }

    private static void RequireConnectionEnvelope(RealtimeEnvelopeMetadata envelope, string type)
    {
        if (envelope.Type != type || envelope.SessionId is not null || envelope.WorkerEpoch is not null || envelope.Sequence is not null)
            throw Invalid("invalid_envelope", $"{type} is a connection-level message.");
    }

    private static void RequireSessionEnvelope(RealtimeEnvelopeMetadata envelope, string type)
    {
        if (envelope.Type != type || envelope.SessionId is null)
            throw Invalid("missing_session_id", $"{type} requires sessionId.");
    }

    private static void ValidateCapabilityDigest(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > RealtimeProtocol.MaxCapabilityDigestLength || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not ':'))
            throw Invalid("invalid_payload", "The capability digest is invalid.");
    }

    private static string ReadString(ref Utf8JsonReader reader, string name)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw Invalid("invalid_envelope", $"{name} must be a string.");
        return reader.GetString() ?? throw Invalid("invalid_envelope", $"{name} must not be null.");
    }

    private static string? ReadNullableString(ref Utf8JsonReader reader, string name) =>
        reader.TokenType == JsonTokenType.Null ? null : ReadString(ref reader, name);

    private static int ReadInt32(ref Utf8JsonReader reader, string name)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value))
            throw Invalid("invalid_envelope", $"{name} must be a 32-bit integer.");
        return value;
    }

    private static ulong ReadUInt64(ref Utf8JsonReader reader, string name)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt64(out ulong value))
            throw Invalid("invalid_envelope", $"{name} must be an unsigned integer.");
        return value;
    }

    private static long ReadInt64(ref Utf8JsonReader reader, string name)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out long value))
            throw Invalid("invalid_envelope", $"{name} must be a 64-bit integer.");
        return value;
    }

    private static void ValidateValue(ref Utf8JsonReader reader, int depth, int maxDepth)
    {
        if (depth > maxDepth)
            throw new RealtimeProtocolException("json_too_deep", "The realtime JSON exceeds its depth limit.", 1002);
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                int properties = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName || ++properties > 256)
                        throw Invalid("invalid_json", "The realtime JSON object is invalid.");
                    string name = reader.GetString() ?? string.Empty;
                    if (!names.Add(name))
                        throw Invalid("duplicate_property", "The realtime JSON contains a duplicate property.");
                    if (!reader.Read())
                        throw Invalid("invalid_json", "The realtime JSON object is incomplete.");
                    ValidateValue(ref reader, depth + 1, maxDepth);
                }
                if (reader.TokenType != JsonTokenType.EndObject)
                    throw Invalid("invalid_json", "The realtime JSON object is incomplete.");
                break;
            }
            case JsonTokenType.StartArray:
            {
                int items = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (++items > 256)
                        throw Invalid("invalid_json", "The realtime JSON array is too large.");
                    ValidateValue(ref reader, depth + 1, maxDepth);
                }
                if (reader.TokenType != JsonTokenType.EndArray)
                    throw Invalid("invalid_json", "The realtime JSON array is incomplete.");
                break;
            }
            case JsonTokenType.Number:
                if (reader.TryGetDouble(out double number) && !double.IsFinite(number))
                    throw Invalid("invalid_number", "The realtime JSON number is not finite.");
                break;
            case JsonTokenType.String:
            case JsonTokenType.True:
            case JsonTokenType.False:
            case JsonTokenType.Null:
                break;
            default:
                throw Invalid("invalid_json", "The realtime JSON value is invalid.");
        }
    }

    private static void ValidateIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > RealtimeProtocol.MaxIdentifierLength || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not '~'))
            throw Invalid("invalid_identifier", $"{name} is not a valid identifier.");
    }

    private static RealtimeProtocolException Invalid(string reason, string message, int closeCode = 1002) =>
        new(reason, message, closeCode);
}

/// <summary>Server-side payloads. These are closed DTOs and are serialized with the source-generated context.</summary>
public sealed record ServerHelloPayload(
    int ProtocolVersion,
    string PayloadSchemaVersion,
    string ConnectionId,
    long ServerNowUnixMilliseconds,
    int HeartbeatIntervalMilliseconds,
    int HeartbeatTimeoutMilliseconds,
    int MaxSubscriptionsPerConnection,
    int MaxPendingInputsPerConnection,
    long ServerMessageMaxBytes,
    string CapabilityDigest = "");

public sealed record ResumeResultPayload(
    string Status,
    ulong? WorkerEpoch = null,
    string? ReasonCode = null);

public sealed record PingPayload(string Nonce, long ServerNowUnixMilliseconds);

public sealed record StreamEndedPayload(string ReasonCode);

public sealed record InputResultPayload(
    string ClientMessageId,
    string Status,
    string ReasonCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ResolvedPromptId = null,
    string? NormalizedValue = null);

public sealed record ProtocolErrorPayload(string Code, string Message);

public sealed record RealtimeEmptyPayload;

public sealed record RealtimeServerEnvelope(
    string Type,
    string MessageId,
    object Payload,
    string? CorrelationId = null,
    string? SessionId = null,
    ulong? WorkerEpoch = null,
    long? Sequence = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    GenerationMode = JsonSourceGenerationMode.Default,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(RealtimeClientHelloPayload))]
[JsonSerializable(typeof(RealtimeResumePayload))]
[JsonSerializable(typeof(RealtimeUnsubscribePayload))]
[JsonSerializable(typeof(RealtimePongPayload))]
[JsonSerializable(typeof(RealtimePointerPayload))]
[JsonSerializable(typeof(RealtimeKeyPayload))]
[JsonSerializable(typeof(RealtimeInputPayload))]
[JsonSerializable(typeof(ServerHelloPayload))]
[JsonSerializable(typeof(ResumeResultPayload))]
[JsonSerializable(typeof(PingPayload))]
[JsonSerializable(typeof(StreamEndedPayload))]
[JsonSerializable(typeof(InputResultPayload))]
[JsonSerializable(typeof(ProtocolErrorPayload))]
[JsonSerializable(typeof(RealtimeEmptyPayload))]
[JsonSerializable(typeof(RealtimeSnapshot))]
[JsonSerializable(typeof(RealtimeDisplayFrame))]
[JsonSerializable(typeof(RealtimeTransactionBatch))]
[JsonSerializable(typeof(RealtimeResyncRequired))]
[JsonSerializable(typeof(RealtimeTransaction))]
public partial class RealtimeJsonContext : JsonSerializerContext;
