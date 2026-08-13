using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.Api.Realtime;

/// <summary>
/// Groups already validated, continuous transactions using the final UTF-8
/// payload size. It deliberately has no background timer; the owner can call
/// <see cref="FlushIfDue"/> from its receive loop or use <see cref="Flush"/>
/// at a lifecycle boundary.
/// </summary>
public sealed class RealtimeBatcher
{
    /// <summary>
    /// Upper bound for the shared JSON batch wrapper
    /// (workerEpoch/firstSequence/lastSequence/transactions array plus
    /// separators). Each transaction is measured once as a single-transaction
    /// batch, so the cumulative estimate is an upper bound of the final batch
    /// size and the flush decision never needs to re-serialize the whole
    /// pending list on every add.
    /// </summary>
    private const int BatchWrapperOverheadBytes = 256;

    private readonly RealtimeOutputOptions options;
    private readonly RealtimePayloadSerializer serializer;
    private readonly TimeProvider timeProvider;
    private readonly List<SequencedConsoleTransaction> pending = [];
    private ulong epoch;
    private long lastSequence;
    private bool hasLastSequence;
    private long startedTimestamp;
    private long pendingEstimatedBytes;

    public RealtimeBatcher(
        RealtimeOutputOptions? options = null,
        TimeProvider? timeProvider = null,
        RealtimePayloadSerializer? serializer = null)
    {
        this.options = options ?? RealtimeOutputOptions.Default;
        this.options.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.serializer = serializer ?? new RealtimePayloadSerializer(this.options);
    }

    public int PendingCount => pending.Count;

    public long PendingFirstSequence => pending.Count == 0 ? 0 : pending[0].Sequence;

    public long PendingLastSequence => pending.Count == 0 ? 0 : pending[^1].Sequence;

    public IReadOnlyList<RealtimeEncodedPayload> Add(
        ulong workerEpoch,
        SequencedConsoleTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentOutOfRangeException.ThrowIfZero(workerEpoch);
        var flushed = new List<RealtimeEncodedPayload>();

        if (pending.Count != 0 && epoch != workerEpoch)
        {
            flushed.AddRange(Flush());
            hasLastSequence = false;
        }
        else if (pending.Count == 0 && hasLastSequence && epoch != workerEpoch)
        {
            hasLastSequence = false;
        }

        if (pending.Count != 0 && timeProvider.GetElapsedTime(startedTimestamp) >= options.BatchMaxDelay)
            flushed.AddRange(Flush());

        if (hasLastSequence &&
            (lastSequence == long.MaxValue || transaction.Sequence != lastSequence + 1))
            throw new RealtimeSequenceException("Realtime batch transactions must be continuous.");

        bool wasEmpty = pending.Count == 0;

        // Measure the candidate exactly once as a single-transaction batch and
        // use the cumulative estimate for the byte budget so adding a batch
        // stays linear in the transaction count.
        RealtimeEncodedPayload single = serializer.SerializeTransactionBatch(workerEpoch, [transaction]);
        if (!wasEmpty &&
            pendingEstimatedBytes + single.ByteLength >= options.BatchTargetBytes)
        {
            flushed.AddRange(Flush());
            wasEmpty = true;
        }

        pending.Add(transaction);
        epoch = workerEpoch;
        lastSequence = transaction.Sequence;
        hasLastSequence = true;
        if (wasEmpty)
        {
            startedTimestamp = timeProvider.GetTimestamp();
            pendingEstimatedBytes = single.ByteLength + BatchWrapperOverheadBytes;
        }
        else
        {
            pendingEstimatedBytes += single.ByteLength;
        }

        if (single.ByteLength > options.BatchTargetBytes)
        {
            // A transaction is atomic. It may exceed the batching target, but
            // it must be emitted immediately when it still fits the protocol
            // hard limit.
            flushed.AddRange(Flush());
        }
        else if (pending.Count >= options.BatchMaxTransactions)
        {
            flushed.AddRange(Flush());
        }

        return flushed;
    }

    public IReadOnlyList<RealtimeEncodedPayload> FlushIfDue()
    {
        if (pending.Count == 0 || timeProvider.GetElapsedTime(startedTimestamp) < options.BatchMaxDelay)
            return Array.Empty<RealtimeEncodedPayload>();
        return Flush();
    }

    public IReadOnlyList<RealtimeEncodedPayload> Flush()
    {
        if (pending.Count == 0)
            return Array.Empty<RealtimeEncodedPayload>();

        RealtimeEncodedPayload result = serializer.SerializeTransactionBatch(epoch, pending);
        pending.Clear();
        pendingEstimatedBytes = 0;
        startedTimestamp = 0;
        return [result];
    }

    /// <summary>
    /// Resets the continuity baseline at a complete Snapshot boundary. A
    /// flushed batch keeps its last sequence for normal delta continuity, but
    /// a replacement Snapshot establishes a new authoritative sequence.
    /// </summary>
    public void ResetBaseline(ulong workerEpoch, long snapshotSequence)
    {
        ArgumentOutOfRangeException.ThrowIfZero(workerEpoch);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotSequence);
        if (pending.Count != 0)
            throw new InvalidOperationException("A batcher baseline cannot be reset while transactions are pending.");

        epoch = workerEpoch;
        lastSequence = snapshotSequence;
        hasLastSequence = true;
        startedTimestamp = 0;
    }

    public void Clear()
    {
        pending.Clear();
        epoch = 0;
        lastSequence = 0;
        hasLastSequence = false;
        pendingEstimatedBytes = 0;
        startedTimestamp = 0;
    }
}

public sealed class RealtimeSequenceException(string message) : ArgumentException(message);
