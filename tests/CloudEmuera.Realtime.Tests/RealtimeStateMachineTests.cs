using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using CloudEmuera.Api.Realtime;
using CloudEmuera.Api.Workers;
using CloudEmuera.Api.Bootstrap;
using CloudEmuera.Application.Authorization;
using CloudEmuera.Application.Identity;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Contracts.Realtime;
using CloudEmuera.Ipc;
using CloudEmuera.RuntimeAdapter;
using W = CloudEmuera.Ipc.V7;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CloudEmuera.Realtime.Tests;

[Trait("Category", "Realtime")]
[Trait("Category", "Reconnect")]
[Trait("Category", "Input")]
[Trait("Category", "InputDeduplication")]
[Trait("Category", "Authorization")]
[Trait("Category", "Backpressure")]
[Trait("Category", "SessionLifecycle")]
public sealed class RealtimeStateMachineTests
{
    private static readonly int[] SupportedProtocolVersions = [5];

    [Fact]
    public void KestrelPortResolutionHonorsConfiguredUrlsAndExplicitPort()
    {
        IConfiguration urls = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = "http://+:39123" })
            .Build();
        Assert.Equal(39123, KestrelHttpPortResolver.Resolve(urls));

        IConfiguration explicitPort = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["urls"] = "http://+:39123",
                ["CloudEmuera:HttpPort"] = "39124",
            })
            .Build();
        Assert.Equal(39124, KestrelHttpPortResolver.Resolve(explicitPort));
    }

    [Fact]
    public async Task SharedSessionCommandGateSerializesInputAndCloseEntries()
    {
        var executor = new SessionLifecycleExecutor(null!, null!, null!);
        await using SessionCommandLease first = await executor.EnterAsync("sess_gate");

        Task<SessionCommandLease> waiting = executor.EnterAsync("sess_gate");
        Assert.False(waiting.IsCompleted);

        first.Dispose();
        await using SessionCommandLease second = await waiting;
    }

    [Fact]
    public async Task InputAfterCloseLinearizationReturnsSessionNotAcceptingInput()
    {
        RealtimeGatewayOptions options = RealtimeGatewayOptions.Default with
        {
            ClientHelloTimeout = TimeSpan.FromSeconds(1),
            ConnectionShutdownTimeout = TimeSpan.FromMilliseconds(250),
            HeartbeatInterval = TimeSpan.FromSeconds(2),
            HeartbeatTimeout = TimeSpan.FromSeconds(1),
        };
        options.Validate(RealtimeOutputOptions.DefaultSnapshotMaxBytes);
        var codec = new RealtimeEnvelopeCodec(options);
        var registry = new GateAwareRealtimeSessionRegistry();
        var connectionRegistry = new RealtimeConnectionRegistry(options);
        using RealtimeConnectionAdmission admission = connectionRegistry.TryReserve("usr_1")!;
        using ServiceProvider services = TestAuthorizationServices();
        var gate = new BlockingCommandGate();
        var socket = new ScriptedWebSocket();
        var connection = new RealtimeConnection(
            socket,
            admission,
            connectionRegistry,
            registry,
            gate,
            new RealtimeAuthorizationGate(services.GetRequiredService<IServiceScopeFactory>()),
            new RealtimeConnectionIdentity("usr_1", "auth_1", "stamp_1", "PLAYER"),
            options,
            codec,
            () => false);

        socket.EnqueueText(ClientHello("hello_close_gate"));
        socket.EnqueueText(Input("input_after_close", "sess_1", 1));
        Task run = connection.RunAsync();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Model the durable BeginStopping linearization while the realtime
        // input is waiting to enter the shared command gate.
        registry.BeginStopping();
        gate.Release();

        string resultText = await WaitForSentTextAsync(socket, "session.input.result");
        socket.EnqueueClose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        using JsonDocument result = JsonDocument.Parse(resultText);
        Assert.Equal(
            "SESSION_NOT_ACCEPTING_INPUT",
            result.RootElement.GetProperty("payload").GetProperty("status").GetString());
    }

    [Fact]
    public async Task AuthorizationGateRechecksLiveSessionAndCentralResourceDecision()
    {
        var identities = new TestIdentityService();
        var authorizer = new TestResourceAuthorizer();
        using ServiceProvider services = new ServiceCollection()
            .AddScoped<ILocalIdentityService>(_ => identities)
            .AddScoped<IResourceAuthorizer>(_ => authorizer)
            .BuildServiceProvider();
        var gate = new RealtimeAuthorizationGate(services.GetRequiredService<IServiceScopeFactory>());
        var identity = new RealtimeConnectionIdentity("usr_1", "auth_1", "stamp_1", "PLAYER");

        RealtimeAuthorizationResult allowed = await gate.AuthorizeResumeAsync(identity, "sess_1");
        Assert.True(allowed.Allowed);
        Assert.Equal(1, identities.ValidationCalls);
        Assert.Equal(1, authorizer.Calls);

        authorizer.Decision = ResourceAccessDecision.NotFoundOrHidden;
        RealtimeAuthorizationResult hidden = await gate.AuthorizeResumeAsync(identity, "sess_1");
        Assert.Equal(RealtimeAuthorizationStatus.NotFoundOrHidden, hidden.Status);

        identities.Live = false;
        RealtimeAuthorizationResult expired = await gate.AuthorizeInputAsync(identity, "sess_1");
        Assert.Equal(RealtimeAuthorizationStatus.AuthenticationExpired, expired.Status);
    }

    [Fact]
    public async Task ResumeAdmitsBeforeTheFirstSnapshotAndDeliversTheWorkerSnapshot()
    {
        RealtimeGatewayOptions options = RealtimeGatewayOptions.Default with
        {
            ClientHelloTimeout = TimeSpan.FromSeconds(1),
            ConnectionShutdownTimeout = TimeSpan.FromMilliseconds(250),
            HeartbeatInterval = TimeSpan.FromSeconds(2),
            HeartbeatTimeout = TimeSpan.FromSeconds(1),
        };
        options.Validate(RealtimeOutputOptions.DefaultSnapshotMaxBytes);
        var codec = new RealtimeEnvelopeCodec(options);
        var registry = new RealtimeConnectionRegistry(options);
        using RealtimeConnectionAdmission admission = registry.TryReserve("usr_1")!;
        await using var hub = new SessionOutputHub("sess_1", "worker_1", 1);
        RealtimeSubscription subscription = hub.Subscribe();
        var sessionRegistry = new TestRealtimeSessionRegistry(new RealtimeSubscriptionRoute(
            "sess_1",
            "worker_1",
            1,
            StructuredIpcProtocol.CapabilitySetDigest,
            subscription));
        using ServiceProvider services = TestAuthorizationServices();
        var socket = new ScriptedWebSocket();
        var connection = new RealtimeConnection(
            socket,
            admission,
            registry,
            sessionRegistry,
            new AllowingCommandGate(),
            new RealtimeAuthorizationGate(services.GetRequiredService<IServiceScopeFactory>()),
            new RealtimeConnectionIdentity("usr_1", "auth_1", "stamp_1", "PLAYER"),
            options,
            codec,
            () => false);

        socket.EnqueueText(ClientHello("hello_1"));
        socket.EnqueueText(Resume("resume_1", "sess_1"));

        Task run = connection.RunAsync();

        string acceptedText = await WaitForSentTextAsync(socket, "session.resume.result");
        using (JsonDocument accepted = JsonDocument.Parse(acceptedText))
        {
            Assert.Equal("ACCEPTED", accepted.RootElement.GetProperty("payload").GetProperty("status").GetString());
        }
        Assert.Equal(1, registry.SubscriptionCount);

        hub.PublishDisplayFrame(new W.DisplayFrame
        {
            FrameId = 1,
            CommitSequence = 0,
            Reason = W.DisplayCommitReason.ExplicitRefresh,
            RequiresSnapshot = true,
            Snapshot = StructuredConsoleWireMapper.ToProto(ConsoleSnapshot.Empty),
        });
        string snapshotText = await WaitForSentTextAsync(socket, "session.snapshot");
        using (JsonDocument snapshot = JsonDocument.Parse(snapshotText))
        {
            Assert.Equal("session.snapshot", snapshot.RootElement.GetProperty("type").GetString());
            Assert.Equal(0, snapshot.RootElement.GetProperty("payload").GetProperty("snapshotSequence").GetInt64());
            Assert.Equal(1, snapshot.RootElement.GetProperty("payload").GetProperty("committedFrameId").GetInt64());
        }
        Assert.DoesNotContain(socket.SentTexts, text => text.Contains("\"type\":\"resync.required\"", StringComparison.Ordinal));

        int replacementStart = socket.SentTexts.Length;
        hub.PublishDisplayFrame(new W.DisplayFrame
        {
            FrameId = 2,
            CommitSequence = 1,
            Reason = W.DisplayCommitReason.ExplicitRefresh,
            RequiresSnapshot = true,
            Snapshot = StructuredConsoleWireMapper.ToProto(new ConsoleSnapshot(
                1,
                [new ConsoleLine("line-1", [new TextNode("replacement")])])),
        });
        string replacementText = await WaitForSentTextAsync(socket, "session.snapshot", replacementStart);
        using (JsonDocument replacement = JsonDocument.Parse(replacementText))
        {
            Assert.Equal(2, replacement.RootElement.GetProperty("payload").GetProperty("committedFrameId").GetInt64());
        }
        Assert.DoesNotContain(
            socket.SentTexts.Skip(replacementStart),
            text => text.Contains("\"type\":\"resync.required\"", StringComparison.Ordinal));

        socket.EnqueueClose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, registry.SubscriptionCount);
    }

    [Fact]
    public async Task WriterSendDeadlineIsReportedAsSlowConsumer()
    {
        RealtimeGatewayOptions options = RealtimeGatewayOptions.Default with
        {
            WebSocketSendTimeout = TimeSpan.FromMilliseconds(20),
        };
        options.Validate(RealtimeOutputOptions.DefaultSnapshotMaxBytes);
        var socket = new ScriptedWebSocket(blockSends: true);
        var codec = new RealtimeEnvelopeCodec(options);
        Exception? fault = null;
        await using var writer = new RealtimeConnectionWriter(socket, codec, options, exception => fault = exception);
        Assert.True(writer.TryEnqueueControl("protocol.error", "msg_1", new ProtocolErrorPayload("test", "test")));

        await Assert.ThrowsAsync<RealtimeSlowConsumerException>(() => writer.RunAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsType<RealtimeSlowConsumerException>(fault);
    }

    [Fact]
    public async Task HeartbeatTimeoutClosesOnlyTheRealtimeConnection()
    {
        RealtimeGatewayOptions options = RealtimeGatewayOptions.Default with
        {
            ClientHelloTimeout = TimeSpan.FromSeconds(1),
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(100),
            ConnectionShutdownTimeout = TimeSpan.FromMilliseconds(250),
        };
        options.Validate(RealtimeOutputOptions.DefaultSnapshotMaxBytes);
        var connectionRegistry = new RealtimeConnectionRegistry(options);
        using RealtimeConnectionAdmission admission = connectionRegistry.TryReserve("usr_1")!;
        using ServiceProvider services = TestAuthorizationServices();
        var socket = new ScriptedWebSocket();
        var connection = new RealtimeConnection(
            socket,
            admission,
            connectionRegistry,
            new TestRealtimeSessionRegistry(null!),
            new AllowingCommandGate(),
            new RealtimeAuthorizationGate(services.GetRequiredService<IServiceScopeFactory>()),
            new RealtimeConnectionIdentity("usr_1", "auth_1", "stamp_1", "PLAYER"),
            options,
            new RealtimeEnvelopeCodec(options),
            () => false);

        socket.EnqueueText(ClientHello("hello_heartbeat"));
        Task run = connection.RunAsync();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (socket.CloseStatus is null && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal((WebSocketCloseStatus)1008, socket.CloseStatus);
        Assert.Equal("heartbeat_timeout", socket.CloseStatusDescription);
        Assert.Equal(1, connectionRegistry.ConnectionCount);
        socket.EnqueueClose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, connectionRegistry.ConnectionCount);
    }

    [Fact]
    public async Task WorkerPendingInputBudgetRejectsOverflowAndCountsForgedReceipts()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"ce-r-{Guid.NewGuid():N}"[..10]);
        var options = new WorkerManagerOptions(dataRoot, typeof(RealtimeStateMachineTests).Assembly.Location)
        {
            PendingInputMaxMessages = 1,
            PendingInputMaxBytes = 2 * 1024,
        };
        options.Validate();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        var request = new WorkerLaunchRequest(
            new WorkerBinding("sess_input", "worker_input", 1),
            Path.Combine(dataRoot, "session-root"),
            "v18-compatible",
            RuntimeSaveLayout.Root);
        await using var session = new ApiWorkerSession(request, options, loggerFactory.CreateLogger<ApiWorkerSession>());
        session.SetBootstrapPath(Path.Combine(options.BootstrapDirectory, "realtime-test.json"));

        Task<SessionInputResult> first = await session.QueueInputAsync(
            new SessionInputCommand("sess_input", 1, "client_1", "one", SessionInputSource.Keyboard),
            TimeSpan.FromSeconds(5));
        SessionInputResult overflow = await (await session.QueueInputAsync(
            new SessionInputCommand("sess_input", 1, "client_2", "two", SessionInputSource.Keyboard),
            TimeSpan.FromSeconds(5)));

        Assert.Equal(SessionInputResultCodes.InputBackpressure, overflow.Status);
        Assert.False(session.TryCompleteInput(new W.WorkerEnvelope
        {
            CorrelationId = "input_forged",
            SessionId = "sess_input",
            WorkerId = "worker_input",
            WorkerEpoch = 1,
            InputResult = new W.InputResult
            {
                ClientMessageId = "client_1",
                Kind = W.InputResultKind.Accepted,
                ReasonCode = "accepted",
            },
        }));
        Assert.Equal(1, session.UnknownInputResultCount);

        await session.DisposeAsync();
        Assert.Equal(SessionInputResultCodes.WorkerUnavailable, (await first).Status);
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
    }

    private static string ClientHello(string messageId) => JsonSerializer.Serialize(new
    {
        protocolVersion = 5,
        type = "client.hello",
        messageId,
        payload = new
        {
            supportedProtocolVersions = SupportedProtocolVersions,
            capabilityDigest = StructuredIpcProtocol.CapabilitySetDigest,
            supportedCapabilities = Array.Empty<string>(),
        },
    });

    private static string Resume(string messageId, string sessionId) => JsonSerializer.Serialize(new
    {
        protocolVersion = 5,
        type = "session.resume",
        messageId,
        sessionId,
        payload = new { capabilityDigest = StructuredIpcProtocol.CapabilitySetDigest },
    });

    private static string Input(string messageId, string sessionId, ulong workerEpoch) => JsonSerializer.Serialize(new
    {
        protocolVersion = 5,
        type = "session.input",
        messageId,
        sessionId,
        workerEpoch,
        payload = new
        {
            clientMessageId = "client_1",
            source = "KEYBOARD",
            value = "7",
            key = new { keyCode = 55, control = false, alt = false, shift = false },
        },
    });

    private static async Task<string> WaitForSentTextAsync(ScriptedWebSocket socket, string marker, int startingIndex = 0)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            string? value = socket.SentTexts.Skip(startingIndex).FirstOrDefault(text => text.Contains(marker, StringComparison.Ordinal));
            if (value is not null)
                return value;
            await Task.Delay(10);
        }

        throw new TimeoutException($"The scripted realtime socket did not send {marker}.");
    }

    private static ServiceProvider TestAuthorizationServices() => new ServiceCollection()
        .AddScoped<ILocalIdentityService>(_ => new TestIdentityService())
        .AddScoped<IResourceAuthorizer>(_ => new TestResourceAuthorizer())
        .BuildServiceProvider();

    private sealed class AllowingCommandGate : ISessionCommandGate
    {
        public Task<SessionCommandLease> EnterAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionCommandLease(static () => { }));
    }

    private sealed class BlockingCommandGate : ISessionCommandGate
    {
        private readonly TaskCompletionSource<SessionCommandLease> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SessionCommandLease> EnterAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            return release.Task;
        }

        public void Release() => release.TrySetResult(new SessionCommandLease(static () => { }));
    }

    private sealed class GateAwareRealtimeSessionRegistry : IRealtimeSessionRegistry
    {
        private int accepting = 1;

        public void BeginStopping() => Interlocked.Exchange(ref accepting, 0);

        public Task<RealtimeSubscriptionRoute?> TrySubscribeAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<RealtimeSubscriptionRoute?>(null);

        public Task<RealtimeInputDispatch> BeginInputAsync(SessionInputCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            string status = Volatile.Read(ref accepting) != 0
                ? SessionInputResultCodes.Accepted
                : SessionInputResultCodes.SessionNotAcceptingInput;
            return Task.FromResult(new RealtimeInputDispatch(Task.FromResult(new SessionInputResult(
                null,
                command.ClientMessageId,
                status,
                status,
                status == SessionInputResultCodes.Accepted ? command.Value : null))));
        }

        public async Task<SessionInputResult> DispatchInputAsync(SessionInputCommand command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            await (await BeginInputAsync(command, timeout, cancellationToken)).Completion;
    }

    private sealed class TestRealtimeSessionRegistry(RealtimeSubscriptionRoute route) : IRealtimeSessionRegistry
    {
        public Task<RealtimeSubscriptionRoute?> TrySubscribeAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<RealtimeSubscriptionRoute?>(route);

        public Task<RealtimeInputDispatch> BeginInputAsync(SessionInputCommand command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RealtimeInputDispatch(Task.FromResult(new SessionInputResult(
                null,
                command.ClientMessageId,
                SessionInputResultCodes.Accepted,
                SessionInputResultCodes.Accepted,
                command.Value))));

        public async Task<SessionInputResult> DispatchInputAsync(SessionInputCommand command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            await (await BeginInputAsync(command, timeout, cancellationToken)).Completion;
    }

    private sealed class TestIdentityService : ILocalIdentityService
    {
        public bool Live { get; set; } = true;
        public int ValidationCalls { get; private set; }

        public Task<bool> ValidateSessionAsync(string userId, string sessionId, string securityStamp, CancellationToken cancellationToken = default)
        {
            ValidationCalls++;
            return Task.FromResult(Live);
        }

        public Task<CurrentUser?> GetCurrentUserAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CurrentUser?>(new CurrentUser(userId, "test", "test@example.test", "PLAYER", "ACTIVE", false, 1));

        public Task<SessionStartupDefaults> GetSessionStartupDefaultsAsync(CurrentActor actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionStartupDefaults> UpdateSessionStartupDefaultsAsync(CurrentActor actor, SessionStartupDefaultsCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LoginResult?> LoginAsync(LoginCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task LogoutAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LoginResult?> ChangePasswordAsync(CurrentActor actor, string currentPassword, string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CurrentUser>> ListUsersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CurrentUser> CreateUserAsync(CreateUserCommand command, CurrentActor actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CurrentUser> UpdateUserAsync(string id, UpdateUserCommand command, CurrentActor actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResetPasswordAsync(string id, string temporaryPassword, int expectedStateVersion, CurrentActor actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestResourceAuthorizer : IResourceAuthorizer
    {
        public ResourceAccessDecision Decision { get; set; } = ResourceAccessDecision.Allowed;
        public int Calls { get; private set; }

        public Task<ResourceAccessDecision> AuthorizeAsync(CurrentActor actor, ResourceKind kind, string resourceId, ResourceAction action, bool mustChangePassword = false, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Decision);
        }
    }

    private sealed class ScriptedWebSocket(bool blockSends = false) : WebSocket
    {
        private readonly Channel<(WebSocketMessageType Type, byte[] Bytes)> incoming = Channel.CreateUnbounded<(WebSocketMessageType, byte[])>();
        private readonly object sync = new();
        private readonly List<string> sentTexts = [];
        private WebSocketState state = WebSocketState.Open;

        public string[] SentTexts
        {
            get { lock (sync) return sentTexts.ToArray(); }
        }

        public void EnqueueText(string text) => incoming.Writer.TryWrite((WebSocketMessageType.Text, Encoding.UTF8.GetBytes(text)));

        public void EnqueueClose() => incoming.Writer.TryWrite((WebSocketMessageType.Close, []));

        private WebSocketCloseStatus? closeStatus;
        private string? closeStatusDescription;

        public override WebSocketCloseStatus? CloseStatus => closeStatus;
        public override string? CloseStatusDescription => closeStatusDescription;
        public override WebSocketState State => state;
        public override string? SubProtocol => RealtimeProtocol.Subprotocol;

        public override void Abort() => state = WebSocketState.Aborted;

        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            this.closeStatus = closeStatus;
            closeStatusDescription = statusDescription;
            state = WebSocketState.Closed;
            await Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            this.closeStatus = closeStatus;
            closeStatusDescription = statusDescription;
            state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            (WebSocketMessageType type, byte[] bytes) item = await incoming.Reader.ReadAsync(cancellationToken);
            if (item.type == WebSocketMessageType.Close)
            {
                state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "client_closed");
            }

            item.bytes.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(item.bytes.Length, item.type, true);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (blockSends)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            lock (sync)
                sentTexts.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
        }

        public override void Dispose()
        {
            state = WebSocketState.Closed;
            incoming.Writer.TryComplete();
        }
    }
}
