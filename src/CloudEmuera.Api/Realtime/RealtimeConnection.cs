using System.Buffers;
using System.Net.WebSockets;
using System.Security.Claims;
using CloudEmuera.Api.Workers;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Contracts.Realtime;
using CloudEmuera.Ipc;
using Microsoft.Extensions.Logging;

namespace CloudEmuera.Api.Realtime;

/// <summary>
/// One authenticated WebSocket connection. The receive loop is the only
/// owner of subscription-map mutations; input work is bounded and completed
/// independently so pong and close cannot be starved by Worker IPC.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The connection owns and disposes its cancellation source in RunAsync's terminal path.")]
public sealed class RealtimeConnection
{
    private readonly WebSocket socket;
    private readonly RealtimeConnectionAdmission admission;
    private readonly RealtimeConnectionRegistry registry;
    private readonly IRealtimeSessionRegistry sessionRegistry;
    private readonly ISessionCommandGate commandGate;
    private readonly RealtimeAuthorizationGate authorization;
    private readonly RealtimeConnectionIdentity identity;
    private readonly RealtimeGatewayOptions options;
    private readonly RealtimeEnvelopeCodec codec;
    private readonly Func<bool> isDraining;
    private readonly ILogger? logger;
    private readonly CancellationTokenSource stop = new();
    private readonly object inputSync = new();
    private readonly object subscriptionSync = new();
    private readonly HashSet<Task> inputTasks = [];
    private readonly Dictionary<string, RealtimeSubscriptionRoute> subscriptions = new(StringComparer.Ordinal);
    private readonly HashSet<string> receivedMessageIds = new(StringComparer.Ordinal);
    private readonly Queue<string> receivedMessageOrder = new();
    private readonly object heartbeatSync = new();
    private readonly object closeSync = new();
    private string? pendingPongNonce;
    private DateTimeOffset pingSentAt;
    private int pendingInputs;
    private int closeCode = 1000;
    private string closeReason = "normal";
    private Task? closeOutputTask;
    private bool closeOutputStarted;
    private int closeLogged;
    private RealtimeConnectionWriter? writer;

    public RealtimeConnection(
        WebSocket socket,
        RealtimeConnectionAdmission admission,
        RealtimeConnectionRegistry registry,
        IRealtimeSessionRegistry sessionRegistry,
        ISessionCommandGate commandGate,
        RealtimeAuthorizationGate authorization,
        RealtimeConnectionIdentity identity,
        RealtimeGatewayOptions options,
        RealtimeEnvelopeCodec codec,
        Func<bool> isDraining,
        ILogger? logger = null)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        this.admission = admission ?? throw new ArgumentNullException(nameof(admission));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        this.commandGate = commandGate ?? throw new ArgumentNullException(nameof(commandGate));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        this.isDraining = isDraining ?? throw new ArgumentNullException(nameof(isDraining));
        this.logger = logger;
    }

    public string ConnectionId => admission.ConnectionId;

    public async Task RunAsync(CancellationToken requestCancellationToken = default)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            requestCancellationToken,
            stop.Token);
        CancellationToken cancellationToken = lifetime.Token;

        try
        {
            // Internal shutdown cancels the writer and heartbeat, then sends a
            // close frame. Keep the receive operation alive until that frame is
            // observed so Kestrel can complete the RFC 6455 close handshake
            // instead of resetting the transport.
            RealtimeParsedMessage? hello = await ReadHelloAsync(requestCancellationToken).ConfigureAwait(false);
            if (hello is null)
                return;

            var outputWriter = new RealtimeConnectionWriter(
                socket,
                codec,
                options,
                OnWriterFault,
                OnSubscriptionCompleted);
            writer = outputWriter;
            if (!outputWriter.TryEnqueueControl(
                    "server.hello",
                    NewMessageId(),
                    new ServerHelloPayload(
                        RealtimeProtocol.Version,
                        RealtimeProtocol.PayloadSchemaVersion,
                        ConnectionId,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        checked((int)options.HeartbeatInterval.TotalMilliseconds),
                        checked((int)options.HeartbeatTimeout.TotalMilliseconds),
                        options.MaxSubscriptionsPerConnection,
                        options.MaxPendingInputsPerConnection,
                        options.ServerMessageMaxBytes,
                        StructuredIpcProtocol.CapabilitySetDigest)))
            {
                RequestClose(1008, "control_queue_full");
                return;
            }

            Task writerTask = outputWriter.RunAsync(cancellationToken);
            Task heartbeatTask = RunHeartbeatAsync(outputWriter, cancellationToken);
            try
            {
                await ReceiveLoopAsync(requestCancellationToken).ConfigureAwait(false);
            }
            finally
            {
                RequestCloseIfUnset(1000, "normal");
                stop.Cancel();
                try { await heartbeatTask.WaitAsync(options.ConnectionShutdownTimeout, CancellationToken.None).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
                await DrainInputTasksAsync().ConfigureAwait(false);
                try { await writerTask.WaitAsync(options.ConnectionShutdownTimeout, CancellationToken.None).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
                await outputWriter.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (RealtimeProtocolException exception)
        {
            if (logger is not null)
                ProtocolErrorLog(logger, ConnectionId, exception.ReasonCode, exception);
            RequestClose(exception.CloseCode, exception.ReasonCode);
            // The receive-loop cancellation is part of RequestClose.  Use a
            // bounded independent send token here so a protocol.error can be
            // emitted when the socket is still writable; close correctness
            // never depends on this best-effort message.
            await SendProtocolErrorDirectAsync(exception.ReasonCode, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (isDraining())
                RequestCloseIfUnset(1012, "api_draining");
        }
        catch (Exception exception)
        {
            if (logger is not null)
                ConnectionFaultLog(logger, ConnectionId, exception);
            RequestClose(1011, "internal_error");
        }
        finally
        {
            string[] sessionIds;
            lock (subscriptionSync)
            {
                sessionIds = subscriptions.Keys.ToArray();
                subscriptions.Clear();
            }
            foreach (string sessionId in sessionIds)
                admission.RemoveSubscription(sessionId);
            admission.Dispose();
            stop.Cancel();
            stop.Dispose();
            await CloseSocketAsync().ConfigureAwait(false);
        }
    }

    private async Task<RealtimeParsedMessage?> ReadHelloAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        helloTimeout.CancelAfter(options.ClientHelloTimeout);
        byte[]? bytes;
        try
        {
            bytes = await ReceiveMessageAsync(helloTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (helloTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            RequestClose(1008, "hello_timeout");
            return null;
        }

        if (bytes is null)
        {
            RequestCloseIfUnset(1000, "peer_closed");
            return null;
        }

        RealtimeParsedMessage hello = RealtimeProtocolParser.Parse(
            bytes,
            new RealtimeProtocolParserOptions(options.ClientMessageMaxBytes, options.ClientJsonMaxDepth));
        if (hello.Payload is not ClientHelloMessage clientHello)
            throw new RealtimeProtocolException("hello_required", "The first realtime message must be client.hello.", 1002);
        RememberMessageId(hello.Envelope.MessageId);
        if (!clientHello.Value.SupportedProtocolVersions.Contains(RealtimeProtocol.Version))
            throw new RealtimeProtocolException("unsupported_protocol_version", "The client does not support realtime protocol v2.", 1002);
        return hello;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            byte[]? bytes = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                RequestCloseIfUnset(1000, "peer_closed");
                return;
            }

            registry.Touch(ConnectionId);
            RealtimeParsedMessage parsed = RealtimeProtocolParser.Parse(
                bytes,
                new RealtimeProtocolParserOptions(options.ClientMessageMaxBytes, options.ClientJsonMaxDepth));
            if (!RememberMessageId(parsed.Envelope.MessageId))
                throw new RealtimeProtocolException("duplicate_message_id", "The realtime messageId was already used on this connection.", 1002);

            switch (parsed.Payload)
            {
                case ConnectionPongMessage pong:
                    HandlePong(pong.Value.Nonce);
                    break;
                case SessionResumeMessage resume:
                    await HandleResumeAsync(parsed, resume.Value, cancellationToken).ConfigureAwait(false);
                    break;
                case SessionUnsubscribeMessage:
                    await HandleUnsubscribeAsync(parsed.Envelope.SessionId!, cancellationToken).ConfigureAwait(false);
                    break;
                case SessionInputMessage input:
                    StartInput(parsed, input.Value, cancellationToken);
                    break;
                case ClientHelloMessage:
                    throw new RealtimeProtocolException("duplicate_hello", "client.hello is only valid as the first message.", 1002);
                default:
                    throw new RealtimeProtocolException("unsupported_message", "The realtime message type is not supported.", 1002);
            }
        }
    }

    private async Task HandleResumeAsync(
        RealtimeParsedMessage parsed,
        RealtimeResumePayload payload,
        CancellationToken cancellationToken)
    {
        string sessionId = parsed.Envelope.SessionId!;
        if (!string.Equals(payload.CapabilityDigest, StructuredIpcProtocol.CapabilitySetDigest, StringComparison.Ordinal))
        {
            QueueResumeResult(parsed.Envelope.MessageId, sessionId, "CAPABILITY_MISMATCH", null, "CLIENT_CAPABILITY_MISMATCH");
            return;
        }

        RealtimeAuthorizationResult authorizationResult = await authorization.AuthorizeResumeAsync(
            identity,
            sessionId,
            cancellationToken).ConfigureAwait(false);
        if (!authorizationResult.Allowed)
        {
            if (authorizationResult.Status is RealtimeAuthorizationStatus.AuthenticationExpired or RealtimeAuthorizationStatus.PasswordChangeRequired)
            {
                QueueProtocolError(
                    authorizationResult.Status == RealtimeAuthorizationStatus.PasswordChangeRequired
                        ? "PASSWORD_CHANGE_REQUIRED"
                        : "AUTHENTICATION_EXPIRED",
                    "The realtime authentication is no longer valid.");
                RequestClose(1008, authorizationResult.Status == RealtimeAuthorizationStatus.PasswordChangeRequired
                    ? "password_change_required"
                    : "authentication_expired");
            }
            else
            {
                QueueResumeResult(parsed.Envelope.MessageId, sessionId, "SESSION_NOT_FOUND", null, "SESSION_NOT_FOUND");
            }
            return;
        }

        bool replacing;
        lock (subscriptionSync)
        {
            replacing = subscriptions.ContainsKey(sessionId);
            if (!replacing && subscriptions.Count >= options.MaxSubscriptionsPerConnection)
            {
                QueueResumeResult(parsed.Envelope.MessageId, sessionId, "SUBSCRIPTION_LIMIT_EXCEEDED", null, "SUBSCRIPTION_LIMIT_EXCEEDED");
                return;
            }
        }

        RealtimeSubscriptionRoute? route;
        try
        {
            route = await sessionRegistry.TrySubscribeAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            route = null;
        }
        if (route is null)
        {
            QueueResumeResult(parsed.Envelope.MessageId, sessionId, "SESSION_NOT_RUNNING", null, "SESSION_NOT_RUNNING");
            return;
        }

        if (!admission.TryAddSubscription(sessionId))
        {
            await route.Subscription.DisposeAsync().ConfigureAwait(false);
            QueueResumeResult(parsed.Envelope.MessageId, sessionId, "SUBSCRIPTION_LIMIT_EXCEEDED", null, "SUBSCRIPTION_LIMIT_EXCEEDED");
            return;
        }

        if (!QueueResumeResult(parsed.Envelope.MessageId, sessionId, "ACCEPTED", route.WorkerEpoch, null))
        {
            await route.Subscription.DisposeAsync().ConfigureAwait(false);
            if (!replacing) admission.RemoveSubscription(sessionId);
            RequestClose(1008, "control_queue_full");
            return;
        }

        RealtimeSubscriptionRoute? previous;
        lock (subscriptionSync)
            previous = subscriptions.GetValueOrDefault(sessionId);
        // Register the subscription before the Worker publishes its first
        // display batch. The hub wakes this reader with a resync snapshot
        // when that batch arrives; rejecting the route here creates a race
        // between Ready and the first output and leaves clients polling.
        if (writer is null || !await writer.AddSubscriptionAsync(sessionId, route.Subscription, cancellationToken).ConfigureAwait(false))
        {
            await route.Subscription.DisposeAsync().ConfigureAwait(false);
            if (!replacing) admission.RemoveSubscription(sessionId);
            RequestClose(1011, "subscription_unavailable");
            return;
        }

        lock (subscriptionSync)
            subscriptions[sessionId] = route;
        if (previous is not null)
        {
            await writer.RemoveSubscriptionAsync(sessionId, previous.Subscription, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleUnsubscribeAsync(string sessionId, CancellationToken cancellationToken)
    {
        RealtimeSubscriptionRoute? route;
        lock (subscriptionSync)
            subscriptions.TryGetValue(sessionId, out route);
        if (route is null)
            return;
        QueueControl(
            "session.stream.ended",
            NewMessageId(),
            new StreamEndedPayload("unsubscribed"),
            sessionId: sessionId,
            workerEpoch: route.WorkerEpoch);
        if (writer is not null)
            await writer.RemoveSubscriptionAsync(sessionId, null, cancellationToken).ConfigureAwait(false);
        lock (subscriptionSync)
            subscriptions.Remove(sessionId);
        admission.RemoveSubscription(sessionId);
    }

    private void OnSubscriptionCompleted(string sessionId, RealtimeSubscription subscription)
    {
        bool removed = false;
        lock (subscriptionSync)
        {
            if (subscriptions.TryGetValue(sessionId, out RealtimeSubscriptionRoute? route) &&
                ReferenceEquals(route.Subscription, subscription))
            {
                subscriptions.Remove(sessionId);
                removed = true;
            }
        }
        if (removed)
            admission.RemoveSubscription(sessionId);
    }

    private void StartInput(
        RealtimeParsedMessage parsed,
        RealtimeInputPayload payload,
        CancellationToken cancellationToken)
    {
        Task? task = null;
        lock (inputSync)
        {
            if (pendingInputs >= options.MaxPendingInputsPerConnection)
            {
                QueueInputResult(
                    parsed.Envelope.MessageId,
                    parsed.Envelope.SessionId!,
                    parsed.Envelope.WorkerEpoch!.Value,
                    new SessionInputResult(null, payload.ClientMessageId, SessionInputResultCodes.InputBackpressure, SessionInputResultCodes.InputBackpressure));
                return;
            }
            pendingInputs++;
            admission.SetPendingInputs(pendingInputs);
            task = ProcessInputAsync(parsed, payload, cancellationToken);
            inputTasks.Add(task);
        }
        _ = FinishInputTaskAsync(task);
    }

    private async Task ProcessInputAsync(
        RealtimeParsedMessage parsed,
        RealtimeInputPayload payload,
        CancellationToken cancellationToken)
    {
        string sessionId = parsed.Envelope.SessionId!;
        ulong workerEpoch = parsed.Envelope.WorkerEpoch!.Value;
        try
        {
            RealtimeAuthorizationResult auth = await authorization.AuthorizeInputAsync(identity, sessionId, cancellationToken).ConfigureAwait(false);
            if (!auth.Allowed)
            {
                if (auth.Status is RealtimeAuthorizationStatus.AuthenticationExpired or RealtimeAuthorizationStatus.PasswordChangeRequired)
                {
                    QueueProtocolError(
                        auth.Status == RealtimeAuthorizationStatus.PasswordChangeRequired
                            ? "PASSWORD_CHANGE_REQUIRED"
                            : "AUTHENTICATION_EXPIRED",
                        "The realtime authentication is no longer valid.");
                    RequestClose(1008, auth.Status == RealtimeAuthorizationStatus.PasswordChangeRequired
                        ? "password_change_required"
                        : "authentication_expired");
                }
                else
                {
                    QueueInputResult(
                        parsed.Envelope.MessageId,
                        sessionId,
                        workerEpoch,
                        new SessionInputResult(null, payload.ClientMessageId, SessionInputResultCodes.Forbidden, SessionInputResultCodes.Forbidden));
                }
                return;
            }

            SessionInputCommand command = ToSessionCommand(sessionId, workerEpoch, payload);
            RealtimeInputDispatch dispatch;
            await using (SessionCommandLease commandLease = await commandGate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {
                dispatch = await sessionRegistry.BeginInputAsync(
                    command,
                    options.InputResultTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            SessionInputResult result = await dispatch.Completion.ConfigureAwait(false);
            QueueInputResult(parsed.Envelope.MessageId, sessionId, workerEpoch, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            QueueInputResult(
                parsed.Envelope.MessageId,
                sessionId,
                workerEpoch,
                new SessionInputResult(null, payload.ClientMessageId, SessionInputResultCodes.WorkerUnavailable, SessionInputResultCodes.WorkerUnavailable));
        }
    }

    private async Task FinishInputTaskAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
        finally
        {
            lock (inputSync)
            {
                inputTasks.Remove(task);
                if (pendingInputs > 0)
                    pendingInputs--;
                admission.SetPendingInputs(pendingInputs);
            }
        }
    }

    private async Task DrainInputTasksAsync()
    {
        Task[] tasks;
        lock (inputSync) tasks = inputTasks.ToArray();
        if (tasks.Length == 0)
            return;
        try { await Task.WhenAll(tasks).WaitAsync(options.ConnectionShutdownTimeout).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
    }

    private static SessionInputCommand ToSessionCommand(
        string sessionId,
        ulong workerEpoch,
        RealtimeInputPayload payload)
    {
        SessionInputSource source = payload.Source switch
        {
            "KEYBOARD" => SessionInputSource.Keyboard,
            "BUTTON" => SessionInputSource.Button,
            "POINTER" => SessionInputSource.PointerDevice,
            _ => SessionInputSource.None,
        };
        return new SessionInputCommand(
            sessionId,
            workerEpoch,
            payload.ClientMessageId,
            payload.Value,
            source,
            payload.PointerData is { } pointer ? new SessionPointerInput(pointer.X, pointer.Y, pointer.Button, pointer.Pressed) : null,
            payload.Key is { } key ? new SessionKeyInput(key.KeyCode, key.Control, key.Alt, key.Shift) : null);
    }

    private bool QueueResumeResult(
        string correlationId,
        string sessionId,
        string status,
        ulong? epoch,
        string? reasonCode) =>
        QueueControl(
            "session.resume.result",
            NewMessageId(),
            new ResumeResultPayload(status, epoch, reasonCode),
            correlationId,
            sessionId,
            epoch);

    private void QueueInputResult(
        string correlationId,
        string sessionId,
        ulong epoch,
        SessionInputResult result)
    {
        QueueControl(
            "session.input.result",
            NewMessageId(),
            new InputResultPayload(
                result.ClientMessageId,
                ToPublicStatus(result.Status),
                result.ReasonCode,
                result.ResolvedPromptId,
                result.NormalizedValue),
            correlationId,
            sessionId,
            epoch);
    }

    private bool QueueProtocolError(string code, string message) =>
        QueueControl("protocol.error", NewMessageId(), new ProtocolErrorPayload(code, message));

    private bool QueueControl(
        string type,
        string messageId,
        object payload,
        string? correlationId = null,
        string? sessionId = null,
        ulong? workerEpoch = null,
        long? sequence = null)
    {
        RealtimeConnectionWriter? outputWriter = writer;
        if (outputWriter is null)
            return false;
        try
        {
            bool accepted = outputWriter.TryEnqueueControl(
                type,
                messageId,
                payload,
                correlationId,
                sessionId,
                workerEpoch,
                sequence);
            admission.SetControlQueueBytes(outputWriter.ControlQueueBytes);
            if (!accepted)
                RequestClose(1008, "control_queue_full");
            return accepted;
        }
        catch (RealtimeEnvelopeSizeException)
        {
            RequestClose(1011, "envelope_size_error");
            return false;
        }
    }

    private async Task RunHeartbeatAsync(
        RealtimeConnectionWriter outputWriter,
        CancellationToken cancellationToken)
    {
        TimeSpan tick = TimeSpan.FromMilliseconds(Math.Max(50, Math.Min(
            options.HeartbeatInterval.TotalMilliseconds,
            options.HeartbeatTimeout.TotalMilliseconds / 2)));
        DateTimeOffset nextPing = DateTimeOffset.UtcNow + options.HeartbeatInterval;
        DateTimeOffset nextIdentityCheck = DateTimeOffset.UtcNow + options.IdentityRevalidationInterval;
        using var timer = new PeriodicTimer(tick);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (isDraining())
            {
                RequestClose(1012, "api_draining");
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (heartbeatSync)
            {
                if (pendingPongNonce is not null && now - pingSentAt > options.HeartbeatTimeout)
                {
                    RequestClose(1008, "heartbeat_timeout");
                    return;
                }
            }

            if (now >= nextIdentityCheck)
            {
                RealtimeAuthorizationResult auth = await authorization.AuthenticateConnectionAsync(identity, cancellationToken).ConfigureAwait(false);
                if (!auth.Allowed)
                {
                    QueueProtocolError(
                        auth.Status == RealtimeAuthorizationStatus.PasswordChangeRequired
                            ? "PASSWORD_CHANGE_REQUIRED"
                            : "AUTHENTICATION_EXPIRED",
                        "The realtime authentication is no longer valid.");
                    RequestClose(1008, auth.Status == RealtimeAuthorizationStatus.PasswordChangeRequired
                        ? "password_change_required"
                        : "authentication_expired");
                    return;
                }
                nextIdentityCheck = now + options.IdentityRevalidationInterval;
            }

            if (now < nextPing)
                continue;
            string nonce = $"ping_{Guid.CreateVersion7():N}";
            lock (heartbeatSync)
            {
                pendingPongNonce = nonce;
                pingSentAt = now;
            }
            if (!QueueControl(
                    "connection.ping",
                    NewMessageId(),
                    new PingPayload(nonce, now.ToUnixTimeMilliseconds())))
                return;
            nextPing = now + options.HeartbeatInterval;
        }
    }

    private void HandlePong(string nonce)
    {
        lock (heartbeatSync)
        {
            if (pendingPongNonce is not null && string.Equals(pendingPongNonce, nonce, StringComparison.Ordinal))
            {
                pendingPongNonce = null;
                return;
            }
        }
        QueueProtocolError("INVALID_PONG", "The pong nonce does not match an outstanding heartbeat.");
    }

    private bool RememberMessageId(string messageId)
    {
        lock (receivedMessageIds)
        {
            if (!receivedMessageIds.Add(messageId))
                return false;
            receivedMessageOrder.Enqueue(messageId);
            while (receivedMessageOrder.Count > 4096)
                receivedMessageIds.Remove(receivedMessageOrder.Dequeue());
            return true;
        }
    }

    private async ValueTask<byte[]?> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        int capacity = checked(options.ClientMessageMaxBytes + 1);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
        int count = 0;
        WebSocketMessageType? messageType = null;
        try
        {
            while (true)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer, count, capacity - count),
                    cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    RequestCloseIfUnset(
                        result.CloseStatus is WebSocketCloseStatus.NormalClosure or null ? 1000 : 1002,
                        "peer_closed");
                    return null;
                }
                if (messageType is null)
                    messageType = result.MessageType;
                else if (messageType != result.MessageType)
                    throw new RealtimeProtocolException("mixed_message_type", "A fragmented realtime message changed its WebSocket type.", 1002);
                if (messageType != WebSocketMessageType.Text)
                    throw new RealtimeProtocolException("binary_not_supported", "Realtime messages must be UTF-8 text.", 1003);
                count += result.Count;
                if (count > options.ClientMessageMaxBytes)
                    throw new RealtimeProtocolException("message_too_large", "The realtime message exceeds its byte limit.", 1009);
                if (result.EndOfMessage)
                    return buffer.AsSpan(0, count).ToArray();
                if (count == capacity)
                    throw new RealtimeProtocolException("message_too_large", "The realtime message exceeds its byte limit.", 1009);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task SendProtocolErrorDirectAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            EncodedRealtimeMessage message = codec.Encode(
                "protocol.error",
                NewMessageId(),
                new ProtocolErrorPayload(code, "The realtime message was rejected."));
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.WebSocketSendTimeout);
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(message.Bytes, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private void OnWriterFault(Exception exception)
    {
        if (logger is not null)
            WriterFaultLog(logger, ConnectionId, exception);
        RequestClose(
            exception is RealtimeSlowConsumerException ? 1008 : 1011,
            exception is RealtimeSlowConsumerException ? "slow_consumer" : "writer_failed");
    }

    private void RequestClose(int code, string reason)
    {
        Interlocked.CompareExchange(ref closeCode, code, 1000);
        if (Volatile.Read(ref closeCode) == code)
            closeReason = NormalizeCloseReason(reason);
        if (Interlocked.Exchange(ref closeLogged, 1) == 0)
        {
            if (logger is not null)
                ConnectionCloseLog(logger, ConnectionId, Volatile.Read(ref closeCode), closeReason, null);
        }
        stop.Cancel();
        lock (closeSync)
        {
            if (!closeOutputStarted)
            {
                closeOutputStarted = true;
                closeOutputTask = SendCloseOutputAsync();
            }
        }
    }

    private void RequestCloseIfUnset(int code, string reason)
    {
        if (Volatile.Read(ref closeCode) == 1000)
            RequestClose(code, reason);
    }

    private async Task CloseSocketAsync()
    {
        Task? closeOutput;
        lock (closeSync)
            closeOutput = closeOutputTask;
        if (closeOutput is not null)
        {
            try { await closeOutput.ConfigureAwait(false); }
            catch { }
        }

        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        try
        {
            using CancellationTokenSource timeout = new(options.ConnectionShutdownTimeout);
            await socket.CloseAsync((WebSocketCloseStatus)closeCode, closeReason, timeout.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task SendCloseOutputAsync()
    {
        try
        {
            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                return;
            using CancellationTokenSource timeout = new(options.ConnectionShutdownTimeout);
            await socket.CloseOutputAsync(
                (WebSocketCloseStatus)Volatile.Read(ref closeCode),
                closeReason,
                timeout.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private static string ToPublicStatus(string status) => status switch
    {
        SessionInputResultCodes.Accepted => "ACCEPTED",
        SessionInputResultCodes.Duplicate => "DUPLICATE",
        SessionInputResultCodes.Conflict => "CONFLICT",
        SessionInputResultCodes.NoActivePrompt => "NO_ACTIVE_PROMPT",
        SessionInputResultCodes.InvalidFormat => "INVALID_FORMAT",
        SessionInputResultCodes.InvalidCommand => "INVALID_COMMAND",
        SessionInputResultCodes.SessionNotAcceptingInput => "SESSION_NOT_ACCEPTING_INPUT",
        SessionInputResultCodes.StaleEpoch => "STALE_EPOCH",
        SessionInputResultCodes.SessionNotRunning => "SESSION_NOT_RUNNING",
        SessionInputResultCodes.InputBackpressure => "INPUT_BACKPRESSURE",
        SessionInputResultCodes.WorkerUnavailable => "WORKER_UNAVAILABLE",
        SessionInputResultCodes.Forbidden => "FORBIDDEN",
        _ => "INVALID_COMMAND",
    };

    private static string NewMessageId() => $"msg_{Guid.CreateVersion7():N}";

    private static readonly Action<ILogger, string, string, Exception?> ProtocolErrorLog =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2201, "RealtimeProtocolError"),
            "realtime_event=protocol_error connectionId={ConnectionId} reason={Reason}");

    private static readonly Action<ILogger, string, Exception?> ConnectionFaultLog =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2202, "RealtimeConnectionFault"),
            "realtime_event=connection_fault connectionId={ConnectionId}");

    private static readonly Action<ILogger, string, Exception?> WriterFaultLog =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2203, "RealtimeWriterFault"),
            "realtime_event=writer_fault connectionId={ConnectionId}");

    private static readonly Action<ILogger, string, int, string, Exception?> ConnectionCloseLog =
        LoggerMessage.Define<string, int, string>(
            LogLevel.Information,
            new EventId(2204, "RealtimeConnectionClose"),
            "realtime_event=connection_close connectionId={ConnectionId} closeCode={CloseCode} closeReason={CloseReason}");

    private static string NormalizeCloseReason(string value) =>
        string.IsNullOrWhiteSpace(value) ? "normal" : value.Length > 120 ? value[..120] : value;
}
