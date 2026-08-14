using System.Buffers;
using System.Text.Json;
using CloudEmuera.Contracts.Realtime;

namespace CloudEmuera.Api.Realtime;

public sealed class RealtimeEnvelopeSizeException(string message) : InvalidOperationException(message);

public sealed record EncodedRealtimeMessage(
    string Type,
    string MessageId,
    byte[] Bytes,
    string? SessionId = null,
    ulong? WorkerEpoch = null,
    long? Sequence = null);

/// <summary>
/// Encodes one final WebSocket message.  P1-08 snapshot/batch bytes are
/// already validated and are embedded as raw JSON exactly once; all other
/// payloads are serialized by the source-generated contract first.
/// </summary>
public sealed class RealtimeEnvelopeCodec(RealtimeGatewayOptions options)
{
    private readonly RealtimeGatewayOptions options = options ?? throw new ArgumentNullException(nameof(options));

    public EncodedRealtimeMessage Encode(
        string type,
        string messageId,
        object payload,
        string? correlationId = null,
        string? sessionId = null,
        ulong? workerEpoch = null,
        long? sequence = null,
        bool payloadAlreadyValidated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(payload);
        ValidateIdentifier(type, nameof(type));
        ValidateIdentifier(messageId, nameof(messageId));
        if (correlationId is not null) ValidateIdentifier(correlationId, nameof(correlationId));
        if (sessionId is not null) ValidateIdentifier(sessionId, nameof(sessionId));
        if (workerEpoch is 0 || sequence is < 0)
            throw new ArgumentOutOfRangeException(nameof(workerEpoch));

        var buffer = new ArrayBufferWriter<byte>(Math.Min((int)Math.Min(options.ServerMessageMaxBytes, int.MaxValue), 16 * 1024));
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", RealtimeProtocol.Version);
            writer.WriteString("type", type);
            writer.WriteString("messageId", messageId);
            if (correlationId is not null) writer.WriteString("correlationId", correlationId);
            if (sessionId is not null) writer.WriteString("sessionId", sessionId);
            if (workerEpoch is not null) writer.WriteNumber("workerEpoch", workerEpoch.Value);
            if (sequence is not null) writer.WriteNumber("sequence", sequence.Value);
            writer.WritePropertyName("payload");
            if (payloadAlreadyValidated)
            {
                if (payload is not ReadOnlyMemory<byte> memory)
                    throw new ArgumentException("Validated payloads must be UTF-8 bytes.", nameof(payload));
                if (options.SnapshotMaxBytes <= 0 || memory.Length > options.SnapshotMaxBytes)
                    throw new RealtimeEnvelopeSizeException("The validated realtime payload exceeds its payload budget.");
                writer.WriteRawValue(memory.Span, skipInputValidation: true);
            }
            else
            {
                WritePayload(writer, payload);
            }
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > options.ServerMessageMaxBytes)
            throw new RealtimeEnvelopeSizeException("The final realtime message exceeds its byte limit.");
        return new EncodedRealtimeMessage(type, messageId, buffer.WrittenMemory.ToArray(), sessionId, workerEpoch, sequence);
    }

    private static void WritePayload(Utf8JsonWriter writer, object payload)
    {
        switch (payload)
        {
            case ServerHelloPayload value: JsonSerializer.Serialize(writer, value, RealtimeJsonContext.Default.ServerHelloPayload); break;
            case PingPayload value: JsonSerializer.Serialize(writer, value, RealtimeJsonContext.Default.PingPayload); break;
            case ResumeResultPayload value: JsonSerializer.Serialize(writer, value, RealtimeJsonContext.Default.ResumeResultPayload); break;
            case StreamEndedPayload value: JsonSerializer.Serialize(writer, value, RealtimeJsonContext.Default.StreamEndedPayload); break;
            case InputResultPayload value: JsonSerializer.Serialize(writer, value, RealtimeJsonContext.Default.InputResultPayload); break;
            case ProtocolErrorPayload value: JsonSerializer.Serialize(writer, value, RealtimeJsonContext.Default.ProtocolErrorPayload); break;
            case RealtimeEmptyPayload value: JsonSerializer.Serialize(writer, value, RealtimeJsonContext.Default.RealtimeEmptyPayload); break;
            case RealtimeResyncRequired value: JsonSerializer.Serialize(writer, value, RealtimeJsonContext.Default.RealtimeResyncRequired); break;
            case RealtimeSnapshot value: JsonSerializer.Serialize(writer, value, RealtimeJsonContext.Default.RealtimeSnapshot); break;
            case RealtimeTransactionBatch value: JsonSerializer.Serialize(writer, value, RealtimeJsonContext.Default.RealtimeTransactionBatch); break;
            default: throw new InvalidDataException("The realtime payload is outside the closed protocol.");
        }
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (value.Length is 0 or > RealtimeProtocol.MaxIdentifierLength || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not '~'))
            throw new ArgumentException($"{name} is not a valid realtime identifier.", name);
    }
}
