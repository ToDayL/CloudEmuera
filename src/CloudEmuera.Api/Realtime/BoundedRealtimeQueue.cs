using System.Diagnostics.CodeAnalysis;

namespace CloudEmuera.Api.Realtime;

public enum RealtimeQueueReadKind
{
    Payload,
    ResyncRequired,
    Completed
}

public readonly record struct RealtimeQueueRead(
    RealtimeQueueReadKind Kind,
    RealtimeEncodedPayload? Payload,
    string? Reason)
{
    public static RealtimeQueueRead FromPayload(RealtimeEncodedPayload payload) =>
        new(RealtimeQueueReadKind.Payload, payload, null);

    public static RealtimeQueueRead Resync(string reason) =>
        new(RealtimeQueueReadKind.ResyncRequired, null, reason);

    public static RealtimeQueueRead Completed() =>
        new(RealtimeQueueReadKind.Completed, null, null);
}

public sealed record RealtimeQueueStatistics(
    int QueuedMessages,
    long QueuedBytes,
    bool NeedsResync,
    bool IsCompleted,
    long SoftOverflowCount,
    long HardOverflowCount);

/// <summary>
/// A single-reader, lock-protected queue with both message and encoded-byte
/// budgets. Overflow drops only pending deltas and wakes the reader through
/// state; it never relies on inserting a marker into a full channel.
/// </summary>
[SuppressMessage("Design", "CA1711", Justification = "The queue is an intentionally bounded realtime transport primitive.")]
public sealed class BoundedRealtimeQueue
{
    private readonly object sync = new();
    private readonly LinkedList<RealtimeEncodedPayload> payloads = [];
    private readonly int softMessages;
    private readonly int hardMessages;
    private readonly long softBytes;
    private readonly long hardBytes;
    private TaskCompletionSource<RealtimeQueueRead>? waiter;
    private long queuedBytes;
    private bool needsResync;
    private string resyncReason = "queue-overflow";
    private bool completed;
    private long softOverflowCount;
    private long hardOverflowCount;

    public BoundedRealtimeQueue(RealtimeOutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        softMessages = options.ConnectionQueueSoftMessages;
        hardMessages = options.ConnectionQueueHardMessages;
        softBytes = options.ConnectionQueueSoftBytes;
        hardBytes = options.ConnectionQueueHardBytes;
    }

    public BoundedRealtimeQueue(
        int softMessages,
        int hardMessages,
        long softBytes,
        long hardBytes)
    {
        if (softMessages <= 0 || hardMessages <= softMessages || hardMessages > RealtimeOutputOptions.AbsoluteMaxQueueMessages ||
            softBytes <= 0 || hardBytes <= softBytes)
            throw new ArgumentOutOfRangeException(nameof(softMessages), "Realtime queue limits are invalid.");
        this.softMessages = softMessages;
        this.hardMessages = hardMessages;
        this.softBytes = softBytes;
        this.hardBytes = hardBytes;
    }

    public RealtimeQueueStatistics Statistics
    {
        get
        {
            lock (sync)
            {
                return new RealtimeQueueStatistics(
                    payloads.Count,
                    queuedBytes,
                    needsResync,
                    completed,
                    softOverflowCount,
                    hardOverflowCount);
            }
        }
    }

    public bool TryEnqueue(RealtimeEncodedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        lock (sync)
        {
            if (completed || needsResync)
                return false;

            bool hardOverflow = payload.ByteLength > hardBytes ||
                payloads.Count >= hardMessages ||
                ExceedsByteLimit(hardBytes, payload.ByteLength);
            bool softOverflow = hardOverflow ||
                payloads.Count >= softMessages ||
                ExceedsByteLimit(softBytes, payload.ByteLength);
            if (softOverflow)
            {
                if (hardOverflow)
                    hardOverflowCount++;
                else
                    softOverflowCount++;
                ClearPayloadsLocked();
                needsResync = true;
                resyncReason = hardOverflow ? "hard-overflow" : "soft-overflow";
                SignalLocked();
                return false;
            }

            payloads.AddLast(payload);
            queuedBytes += payload.ByteLength;
            SignalLocked();
            return true;
        }
    }

    public void RequestResync(string reason = "sequence-gap")
    {
        if (string.IsNullOrWhiteSpace(reason))
            reason = "sequence-gap";
        lock (sync)
        {
            if (completed)
                return;
            ClearPayloadsLocked();
            needsResync = true;
            resyncReason = reason;
            SignalLocked();
        }
    }

    public async ValueTask<RealtimeQueueRead> ReadAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<RealtimeQueueRead> pendingWaiter;
        lock (sync)
        {
            if (TryReadLocked(out RealtimeQueueRead immediate))
                return immediate;
            if (waiter is not null)
                throw new InvalidOperationException("A realtime queue permits only one reader.");
            pendingWaiter = waiter = new TaskCompletionSource<RealtimeQueueRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            return await pendingWaiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (sync)
            {
                if (ReferenceEquals(waiter, pendingWaiter))
                    waiter = null;
            }
            throw;
        }
    }

    public void Complete(bool discardPending = true)
    {
        lock (sync)
        {
            if (completed)
            {
                if (discardPending)
                    ClearPayloadsLocked();
                needsResync = false;
                SignalLocked();
                return;
            }
            completed = true;
            if (discardPending)
                ClearPayloadsLocked();
            // Terminal completion supersedes any pending resync marker: a
            // completed queue must drain its payloads and then report
            // Completed, never a resync that can no longer be satisfied.
            needsResync = false;
            SignalLocked();
        }
    }

    private bool TryReadLocked(out RealtimeQueueRead result)
    {
        if (needsResync)
        {
            needsResync = false;
            result = RealtimeQueueRead.Resync(resyncReason);
            return true;
        }

        if (payloads.First is { } first)
        {
            payloads.RemoveFirst();
            queuedBytes -= first.Value.ByteLength;
            result = RealtimeQueueRead.FromPayload(first.Value);
            return true;
        }

        if (completed)
        {
            result = RealtimeQueueRead.Completed();
            return true;
        }

        result = default;
        return false;
    }

    private void SignalLocked()
    {
        if (waiter is null || !TryReadLocked(out RealtimeQueueRead result))
            return;
        TaskCompletionSource<RealtimeQueueRead> current = waiter;
        waiter = null;
        current.TrySetResult(result);
    }

    private void ClearPayloadsLocked()
    {
        payloads.Clear();
        queuedBytes = 0;
    }

    private bool ExceedsByteLimit(long limit, int payloadBytes) =>
        payloadBytes > limit || queuedBytes > limit - payloadBytes;
}
