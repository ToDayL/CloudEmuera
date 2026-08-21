using System.Text;
using CloudEmuera.Api.Realtime;
using CloudEmuera.Contracts.Realtime;
using Xunit;

namespace CloudEmuera.Realtime.Tests;

[Trait("Category", "WebSocketProtocol")]
[Trait("Category", "InputDeduplication")]
[Trait("Category", "Backpressure")]
[Trait("Category", "Reconnect")]
[Trait("Category", "Authorization")]
public sealed class RealtimeProtocolTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "GoldenJson");

    [Fact]
    public void GoldenHelloIgnoresUnknownOptionalEnvelopeFields()
    {
        RealtimeParsedMessage message = ParseFixture("client-hello.v2.json");

        ClientHelloMessage hello = Assert.IsType<ClientHelloMessage>(message.Payload);
        Assert.Equal(RealtimeProtocol.Version, message.Envelope.ProtocolVersion);
        Assert.Equal("msg_01KHELLO", message.Envelope.MessageId);
        Assert.Equal([3], hello.Value.SupportedProtocolVersions);
        Assert.Equal("sha256:runtime-v5", hello.Value.CapabilityDigest);
    }

    [Fact]
    public void GoldenInputPreservesTheClosedBrowserInputUnion()
    {
        RealtimeParsedMessage message = ParseFixture("session-input.v2.json");

        SessionInputMessage input = Assert.IsType<SessionInputMessage>(message.Payload);
        Assert.Equal("sess_01KSESSION", message.Envelope.SessionId);
        Assert.Equal((ulong)4, message.Envelope.WorkerEpoch);
        Assert.Equal("BUTTON", input.Value.Source);
        Assert.Null(input.Value.PointerData);
        Assert.Null(input.Value.Key);
    }

    [Fact]
    public void DuplicateEnvelopePropertyIsRejectedBeforeTypedDeserialization()
    {
        const string json = "{\"protocolVersion\":3,\"type\":\"client.hello\",\"type\":\"client.hello\",\"messageId\":\"msg_1\",\"payload\":{\"supportedProtocolVersions\":[3],\"capabilityDigest\":\"digest\",\"supportedCapabilities\":[]}}";

        RealtimeProtocolException exception = Assert.Throws<RealtimeProtocolException>(() => Parse(json));
        Assert.Equal("duplicate_property", exception.ReasonCode);
        Assert.Equal(1002, exception.CloseCode);
    }

    [Fact]
    public void DuplicateNestedPropertyAndSystemSourceAreRejected()
    {
        const string duplicateNested = "{\"protocolVersion\":3,\"type\":\"session.input\",\"messageId\":\"msg_1\",\"sessionId\":\"sess_1\",\"workerEpoch\":1,\"payload\":{\"clientMessageId\":\"client_1\",\"source\":\"BUTTON\",\"value\":\"0\",\"value\":\"1\"}}";
        const string systemSource = "{\"protocolVersion\":3,\"type\":\"session.input\",\"messageId\":\"msg_2\",\"sessionId\":\"sess_1\",\"workerEpoch\":1,\"payload\":{\"clientMessageId\":\"client_2\",\"source\":\"SYSTEM\",\"value\":\"0\"}}";

        Assert.Equal("duplicate_property", Assert.Throws<RealtimeProtocolException>(() => Parse(duplicateNested)).ReasonCode);
        Assert.Equal("invalid_command", Assert.Throws<RealtimeProtocolException>(() => Parse(systemSource)).ReasonCode);
    }

    [Fact]
    public void PointerAndKeyPayloadsMustBeCompleteAndMatchTheirSource()
    {
        const string incompletePointer = "{\"protocolVersion\":3,\"type\":\"session.input\",\"messageId\":\"msg_3\",\"sessionId\":\"sess_1\",\"workerEpoch\":1,\"payload\":{\"clientMessageId\":\"client_3\",\"source\":\"POINTER\",\"value\":\"\",\"pointer\":{\"x\":1,\"y\":2,\"button\":0}}}";
        const string pointerOnButton = "{\"protocolVersion\":3,\"type\":\"session.input\",\"messageId\":\"msg_4\",\"sessionId\":\"sess_1\",\"workerEpoch\":1,\"payload\":{\"clientMessageId\":\"client_4\",\"source\":\"BUTTON\",\"value\":\"\",\"pointer\":{\"x\":1,\"y\":2,\"button\":0,\"pressed\":true}}}";

        Assert.Equal("invalid_payload", Assert.Throws<RealtimeProtocolException>(() => Parse(incompletePointer)).ReasonCode);
        Assert.Equal("invalid_command", Assert.Throws<RealtimeProtocolException>(() => Parse(pointerOnButton)).ReasonCode);
    }

    [Fact]
    public void MessageDepthAndByteLimitsFailClosedWithStableCodes()
    {
        string nested = new string('[', 40) + "0" + new string(']', 40);
        string deep = "{\"protocolVersion\":3,\"type\":\"client.hello\",\"messageId\":\"msg_1\",\"payload\":{\"supportedProtocolVersions\":[3],\"capabilityDigest\":\"d\",\"supportedCapabilities\":[],\"unknown\":" + nested + "}}";
        RealtimeProtocolException depth = Assert.Throws<RealtimeProtocolException>(() => Parse(deep, new RealtimeProtocolParserOptions(64 * 1024, 8)));
        Assert.Equal("json_too_deep", depth.ReasonCode);

        RealtimeProtocolException size = Assert.Throws<RealtimeProtocolException>(() => Parse(new string('x', 100), new RealtimeProtocolParserOptions(32, 32)));
        Assert.Equal("message_too_large", size.ReasonCode);
        Assert.Equal(1009, size.CloseCode);
    }

    [Fact]
    public void V2InputRejectsTheRemovedPromptIdField()
    {
        const string json = "{\"protocolVersion\":3,\"type\":\"session.input\",\"messageId\":\"msg_1\",\"sessionId\":\"sess_1\",\"workerEpoch\":1,\"payload\":{\"promptId\":\"prompt_1\",\"clientMessageId\":\"client_1\",\"source\":\"BUTTON\",\"value\":\"0\"}}";

        Assert.Equal("invalid_payload", Assert.Throws<RealtimeProtocolException>(() => Parse(json)).ReasonCode);
    }

    [Fact]
    public void FinalEnvelopeBudgetIncludesTheOuterEnvelope()
    {
        RealtimeOutputOptions output = RealtimeOutputOptions.Default with
        {
            SnapshotMaxBytes = 256,
            BatchTargetBytes = 64,
            ConnectionQueueSoftBytes = 64,
            ConnectionQueueHardBytes = 128,
        };
        RealtimeGatewayOptions gateway = RealtimeGatewayOptions.Default;
        gateway.Validate(output);
        var codec = new RealtimeEnvelopeCodec(gateway);

        EncodedRealtimeMessage encoded = codec.Encode(
            "display.frame",
            "msg_1",
            (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("{}"),
            sessionId: "sess_1",
            workerEpoch: 1,
            sequence: 1,
            payloadAlreadyValidated: true);
        Assert.InRange(encoded.Bytes.Length, 1, gateway.ServerMessageMaxBytes);

        Assert.Throws<RealtimeEnvelopeSizeException>(() => codec.Encode(
            "display.frame",
            "msg_2",
            (ReadOnlyMemory<byte>)new byte[257],
            sessionId: "sess_1",
            workerEpoch: 1,
            sequence: 1,
            payloadAlreadyValidated: true));
    }

    [Fact]
    public void ConnectionRegistryEnforcesGlobalAndPerSessionLimitsAndReleasesCounts()
    {
        RealtimeGatewayOptions options = RealtimeGatewayOptions.Default with
        {
            MaxConnections = 2,
            MaxConnectionsPerSession = 1,
        };
        var registry = new RealtimeConnectionRegistry(options);
        using RealtimeConnectionAdmission first = registry.TryReserve("user-1")!;
        using RealtimeConnectionAdmission second = registry.TryReserve("user-2")!;
        Assert.Null(registry.TryReserve("user-3"));

        Assert.True(first.TryAddSubscription("sess-1"));
        Assert.False(second.TryAddSubscription("sess-1"));
        Assert.Equal(1, registry.SubscriptionCount);

        first.RemoveSubscription("sess-1");
        Assert.Equal(0, registry.SubscriptionCount);
        first.Dispose();
        Assert.Equal(1, registry.ConnectionCount);
        second.Dispose();
        Assert.Equal(0, registry.ConnectionCount);
    }

    [Fact]
    public void ServerEnvelopeUsesStableRealtimePayloadSchemaVersion()
    {
        RealtimeGatewayOptions gateway = RealtimeGatewayOptions.Default;
        gateway.Validate(RealtimeOutputOptions.DefaultSnapshotMaxBytes);
        var codec = new RealtimeEnvelopeCodec(gateway);
        EncodedRealtimeMessage message = codec.Encode(
            "server.hello",
            "msg_1",
            new ServerHelloPayload(RealtimeProtocol.Version, RealtimeProtocol.PayloadSchemaVersion, "conn_1", 1, 20_000, 10_000, 4, 32, gateway.ServerMessageMaxBytes, "digest"));

        string json = Encoding.UTF8.GetString(message.Bytes);
        Assert.Contains("\"payloadSchemaVersion\":\"p1-s03-display-commit\"", json, StringComparison.Ordinal);
        Assert.Contains("\"capabilityDigest\":\"digest\"", json, StringComparison.Ordinal);
    }

    private static RealtimeParsedMessage ParseFixture(string name) =>
        RealtimeProtocolParser.Parse(File.ReadAllBytes(Path.Combine(FixturePath, name)));

    private static RealtimeParsedMessage Parse(string json, RealtimeProtocolParserOptions? options = null) =>
        RealtimeProtocolParser.Parse(Encoding.UTF8.GetBytes(json), options);
}
