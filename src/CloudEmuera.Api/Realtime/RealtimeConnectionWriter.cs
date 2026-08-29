using System.Net.WebSockets;
using CloudEmuera.Contracts.Realtime;

namespace CloudEmuera.Api.Realtime;

public sealed class RealtimeControlQueueFullException(string message) : InvalidOperationException(message);

/// <summary>
/// The only component allowed to call WebSocket.SendAsync for a connection.
/// Control messages have priority; display subscriptions publish one pending
/// frame at a time and are selected round-robin.
/// </summary>
public sealed class RealtimeConnectionWriter : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly RealtimeEnvelopeCodec codec;
    private readonly RealtimeGatewayOptions options;
    private readonly Action<Exception> fault;
    private readonly Action<string, RealtimeSubscription>? subscriptionCompleted;
    private readonly object sync = new();
    private readonly Queue<EncodedRealtimeMessage> controls = new();
    private readonly List<RealtimeSubscriptionPump> pumps = [];
    private readonly SemaphoreSlim wake = new(0);
    private int roundRobinIndex;
    private long controlBytes;
    private int disposed;

    public RealtimeConnectionWriter(
        WebSocket socket,
        RealtimeEnvelopeCodec codec,
        RealtimeGatewayOptions options,
        Action<Exception> fault,
        Action<string, RealtimeSubscription>? subscriptionCompleted = null)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.fault = fault ?? throw new ArgumentNullException(nameof(fault));
        this.subscriptionCompleted = subscriptionCompleted;
    }

    public long ControlQueueBytes
    {
        get { lock (sync) return controlBytes; }
    }

    public int ControlQueueMessages
    {
        get { lock (sync) return controls.Count; }
    }

    public bool TryEnqueueControl(EncodedRealtimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (sync)
        {
            if (Volatile.Read(ref disposed) != 0 ||
                controls.Count >= options.ControlQueueMaxMessages ||
                controlBytes > options.ControlQueueMaxBytes - message.Bytes.LongLength)
                return false;
            controls.Enqueue(message);
            controlBytes += message.Bytes.LongLength;
        }
        Signal();
        return true;
    }

    public bool TryEnqueueControl(
        string type,
        string messageId,
        object payload,
        string? correlationId = null,
        string? sessionId = null,
        ulong? workerEpoch = null,
        long? sequence = null)
    {
        EncodedRealtimeMessage message = codec.Encode(
            type,
            messageId,
            payload,
            correlationId,
            sessionId,
            workerEpoch,
            sequence);
        return TryEnqueueControl(message);
    }

    public async Task<bool> AddSubscriptionAsync(
        string sessionId,
        RealtimeSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(subscription);
        cancellationToken.ThrowIfCancellationRequested();
        var pump = new RealtimeSubscriptionPump(sessionId, subscription, Signal, fault);
        lock (sync)
        {
            if (Volatile.Read(ref disposed) != 0)
                return false;
            pumps.Add(pump);
        }
        pump.Start();
        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    public async ValueTask RemoveSubscriptionAsync(
        string sessionId,
        RealtimeSubscription? expectedSubscription = null,
        CancellationToken cancellationToken = default)
    {
        RealtimeSubscriptionPump? pump = null;
        lock (sync)
        {
            int index = pumps.FindIndex(item =>
                string.Equals(item.SessionId, sessionId, StringComparison.Ordinal) &&
                (expectedSubscription is null || ReferenceEquals(item.Subscription, expectedSubscription)));
            if (index >= 0)
            {
                pump = pumps[index];
                pumps.RemoveAt(index);
                if (roundRobinIndex >= pumps.Count)
                    roundRobinIndex = 0;
            }
        }
        if (pump is not null)
            await pump.DisposeAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                while (TryTakeNext(out EncodedRealtimeMessage[]? group, out RealtimeSubscriptionPump? pump))
                {
                    foreach (EncodedRealtimeMessage message in group!)
                        await SendAsync(message, cancellationToken).ConfigureAwait(false);
                    if (pump is not null && pump.LastFrameWasCompleted)
                    {
                        await RemovePumpAfterCompletionAsync(pump).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            fault(exception);
            throw;
        }
    }

    private async Task SendAsync(EncodedRealtimeMessage message, CancellationToken cancellationToken)
    {
        using CancellationTokenSource sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sendTimeout.CancelAfter(options.WebSocketSendTimeout);
        try
        {
            await socket.SendAsync(
                message.Bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                sendTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sendTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new RealtimeSlowConsumerException("The WebSocket send deadline expired.");
        }
    }

    private bool TryTakeNext(out EncodedRealtimeMessage[]? group, out RealtimeSubscriptionPump? pump)
    {
        lock (sync)
        {
            if (controls.Count > 0)
            {
                EncodedRealtimeMessage control = controls.Dequeue();
                controlBytes -= control.Bytes.LongLength;
                group = [control];
                pump = null;
                return true;
            }

            if (pumps.Count == 0)
            {
                group = null;
                pump = null;
                return false;
            }

            int count = pumps.Count;
            for (int offset = 0; offset < count; offset++)
            {
                int index = (roundRobinIndex + offset) % count;
                if (!pumps[index].TryTake(out RealtimeFrame? frame))
                    continue;
                roundRobinIndex = (index + 1) % count;
                pump = pumps[index];
                group = EncodeFrame(pump.SessionId, frame!);
                return true;
            }
        }

        group = null;
        pump = null;
        return false;
    }

    private EncodedRealtimeMessage[] EncodeFrame(string sessionId, RealtimeFrame frame)
    {
        return frame.Kind switch
        {
            RealtimeFrameKind.Snapshot => EncodeSnapshot(sessionId, frame),
            RealtimeFrameKind.TransactionBatch =>
                throw new InvalidDataException("The v4 realtime writer cannot emit legacy display.batch messages."),
            RealtimeFrameKind.DisplayFrame =>
            [codec.Encode(
                "display.frame",
                NewMessageId(),
                frame.Payload,
                sessionId: sessionId,
                workerEpoch: frame.WorkerEpoch,
                sequence: frame.LastSequence,
                payloadAlreadyValidated: true)],
            RealtimeFrameKind.Completed =>
            [codec.Encode(
                "session.stream.ended",
                NewMessageId(),
                new StreamEndedPayload(string.IsNullOrWhiteSpace(frame.Reason) ? "completed" : frame.Reason),
                sessionId: sessionId,
                workerEpoch: frame.WorkerEpoch)],
            _ => throw new InvalidDataException("The realtime subscription returned an unknown frame kind."),
        };
    }

    private EncodedRealtimeMessage[] EncodeSnapshot(string sessionId, RealtimeFrame frame)
    {
        var messages = new List<EncodedRealtimeMessage>(2);
        // Both the first snapshot and a complete committed-frame replacement
        // are authoritative display updates. Only snapshots requested after
        // a transport gap/overflow announce a browser resync.
        if (frame.Reason is not ("initial-snapshot" or "committed-snapshot"))
        {
            messages.Add(codec.Encode(
                "resync.required",
                NewMessageId(),
                new RealtimeResyncRequired(
                    frame.WorkerEpoch,
                    frame.LastSequence,
                    string.IsNullOrWhiteSpace(frame.Reason) ? "resync-required" : frame.Reason),
                sessionId: sessionId,
                workerEpoch: frame.WorkerEpoch,
                sequence: frame.LastSequence));
        }
        messages.Add(codec.Encode(
            "session.snapshot",
            NewMessageId(),
            frame.Payload,
            sessionId: sessionId,
            workerEpoch: frame.WorkerEpoch,
            sequence: frame.LastSequence,
            payloadAlreadyValidated: true));
        return messages.ToArray();
    }

    private async Task RemovePumpAfterCompletionAsync(RealtimeSubscriptionPump pump)
    {
        lock (sync)
        {
            if (!pumps.Remove(pump))
                return;
            if (roundRobinIndex >= pumps.Count)
                roundRobinIndex = 0;
        }
        subscriptionCompleted?.Invoke(pump.SessionId, pump.Subscription);
        await pump.DisposeAsync().ConfigureAwait(false);
    }

    private void Signal()
    {
        try { wake.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    private static string NewMessageId() => $"msg_{Guid.CreateVersion7():N}";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        RealtimeSubscriptionPump[] current;
        lock (sync)
        {
            current = pumps.ToArray();
            pumps.Clear();
            controls.Clear();
            controlBytes = 0;
        }
        foreach (RealtimeSubscriptionPump pump in current)
            await pump.DisposeAsync().ConfigureAwait(false);
        wake.Dispose();
    }
}

internal sealed class RealtimeSubscriptionPump : IAsyncDisposable
{
    private readonly RealtimeSubscription subscription;
    private readonly Action signal;
    private readonly Action<Exception> fault;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object sync = new();
    private RealtimeFrame? pending;
    private TaskCompletionSource<bool>? delivered;
    private Task? reader;
    private int disposed;

    public RealtimeSubscriptionPump(
        string sessionId,
        RealtimeSubscription subscription,
        Action signal,
        Action<Exception> fault)
    {
        SessionId = sessionId;
        this.subscription = subscription;
        this.signal = signal;
        this.fault = fault;
    }

    public string SessionId { get; }

    public RealtimeSubscription Subscription => subscription;

    public bool LastFrameWasCompleted { get; private set; }

    public void Start() => reader = ReadLoopAsync();

    public bool TryTake(out RealtimeFrame? frame)
    {
        lock (sync)
        {
            if (pending is null)
            {
                frame = null;
                return false;
            }
            frame = pending;
            pending = null;
            delivered?.TrySetResult(true);
            delivered = null;
            LastFrameWasCompleted = frame.Kind == RealtimeFrameKind.Completed;
            return true;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                RealtimeFrame frame = await subscription.ReadAsync(cancellation.Token).ConfigureAwait(false);
                TaskCompletionSource<bool> delivery = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (sync)
                {
                    if (Volatile.Read(ref disposed) != 0)
                        return;
                    pending = frame;
                    delivered = delivery;
                }
                signal();
                await delivery.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
                if (frame.Kind == RealtimeFrameKind.Completed)
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            fault(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        cancellation.Cancel();
        lock (sync)
        {
            pending = null;
            delivered?.TrySetCanceled(cancellation.Token);
            delivered = null;
        }
        try
        {
            if (reader is not null)
                await reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
    }
}
