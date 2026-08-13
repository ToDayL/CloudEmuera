using CloudEmuera.Contracts.Realtime;
using W = CloudEmuera.Ipc.V3;
using R = CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.Api.Realtime;

public enum SessionOutputHubState
{
    AwaitingInitialSnapshot,
    Live,
    Faulted,
    Disposed
}

public enum RealtimePublishDisposition
{
    Applied,
    IgnoredDuplicate,
    Faulted,
    Rejected
}

public sealed record RealtimePublishResult(
    RealtimePublishDisposition Disposition,
    SessionOutputHubState State,
    long SnapshotSequence,
    string? ReasonCode = null)
{
    public bool Accepted => Disposition == RealtimePublishDisposition.Applied;
}

public sealed record RealtimeHubStatistics(
    ulong WorkerEpoch,
    SessionOutputHubState State,
    long SnapshotSequence,
    int SubscriptionCount,
    long PublishedBatchCount,
    long IgnoredBatchCount,
    long ResyncCount,
    long FaultCount);

public enum RealtimeFrameKind
{
    Snapshot,
    TransactionBatch,
    Completed
}

public sealed record RealtimeFrame(
    RealtimeFrameKind Kind,
    ulong WorkerEpoch,
    long FirstSequence,
    long LastSequence,
    ReadOnlyMemory<byte> Payload,
    bool ReplacesState,
    string? Reason = null)
{
    public static RealtimeFrame Snapshot(RealtimeEncodedPayload payload, bool replacesState, string? reason = null) =>
        new(RealtimeFrameKind.Snapshot, payload.WorkerEpoch, payload.FirstSequence, payload.LastSequence, payload.Bytes, replacesState, reason);

    public static RealtimeFrame Transactions(RealtimeEncodedPayload payload) =>
        new(RealtimeFrameKind.TransactionBatch, payload.WorkerEpoch, payload.FirstSequence, payload.LastSequence, payload.Bytes, false);

    public static RealtimeFrame Completed(ulong workerEpoch, string reason) =>
        new(RealtimeFrameKind.Completed, workerEpoch, 0, 0, ReadOnlyMemory<byte>.Empty, false, reason);
}

public sealed class SessionOutputHub : IAsyncDisposable
{
    private readonly string sessionId;
    private readonly string workerId;
    private readonly RealtimeOutputOptions options;
    private readonly R.ConsoleHistoryOptions reducerOptions;
    private readonly TimeProvider timeProvider;
    private readonly RealtimePayloadSerializer serializer;
    private readonly RealtimeBatcher batcher;
    private readonly ITimer batchTimer;
    private readonly long minimumInitialSequence;
    private readonly object sync = new();
    private readonly SemaphoreSlim publishGate = new(1, 1);
    private readonly Dictionary<Guid, RealtimeSubscription> subscriptions = [];
    private R.ConsoleSnapshot? latestSnapshot;
    private RealtimeEncodedPayload? latestSnapshotPayload;
    private SessionOutputHubState state = SessionOutputHubState.AwaitingInitialSnapshot;
    private string? terminalReason;
    private long publishedBatchCount;
    private long ignoredBatchCount;
    private long resyncCount;
    private long faultCount;
    private int disposed;

    public SessionOutputHub(
        string sessionId,
        string workerId,
        ulong workerEpoch,
        RealtimeOutputOptions? options = null,
        R.ConsoleHistoryOptions? reducerOptions = null,
        TimeProvider? timeProvider = null,
        long minimumInitialSequence = 0)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("A session id is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("A worker id is required.", nameof(workerId));
        ArgumentOutOfRangeException.ThrowIfZero(workerEpoch);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumInitialSequence);
        this.sessionId = sessionId;
        this.workerId = workerId;
        WorkerEpoch = workerEpoch;
        this.minimumInitialSequence = minimumInitialSequence;
        this.options = options ?? RealtimeOutputOptions.Default;
        this.options.Validate();
        this.reducerOptions = reducerOptions ?? R.ConsoleHistoryOptions.Default;
        this.reducerOptions.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        serializer = new RealtimePayloadSerializer(this.options);
        batcher = new RealtimeBatcher(this.options, this.timeProvider, serializer);
        batchTimer = this.timeProvider.CreateTimer(
            static state => ((SessionOutputHub)state!).FlushDueFromTimer(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public string SessionId => sessionId;

    public string WorkerId => workerId;

    public ulong WorkerEpoch { get; }

    public SessionOutputHubState State
    {
        get
        {
            lock (sync)
                return state;
        }
    }

    public long SnapshotSequence
    {
        get
        {
            lock (sync)
                return latestSnapshot?.SnapshotSequence ?? 0;
        }
    }

    public R.ConsoleSnapshot? CurrentSnapshot
    {
        get
        {
            lock (sync)
                return latestSnapshot;
        }
    }

    public RealtimeHubStatistics Statistics
    {
        get
        {
            lock (sync)
            {
                return new RealtimeHubStatistics(
                    WorkerEpoch,
                    state,
                    latestSnapshot?.SnapshotSequence ?? 0,
                    subscriptions.Count,
                    publishedBatchCount,
                    ignoredBatchCount,
                    resyncCount,
                    faultCount);
            }
        }
    }

    public RealtimeSubscription Subscribe()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (state is SessionOutputHubState.Faulted or SessionOutputHubState.Disposed)
                throw new InvalidOperationException("The realtime output hub is no longer accepting subscriptions.");

            var subscription = new RealtimeSubscription(this, new BoundedRealtimeQueue(options));
            subscriptions.Add(subscription.Id, subscription);
            RealtimeEncodedPayload? snapshotPayload = latestSnapshotPayload;
            R.ConsoleSnapshot? snapshot = latestSnapshot;
            if (snapshot is not null && snapshotPayload is not null)
            {
                subscription.SetInitialSnapshot(snapshotPayload);
            }
            else if (snapshot is not null)
            {
                subscription.RequestResync("snapshot-encoding-pending");
            }

            // This second read is intentionally kept even though the lock
            // makes the common path atomic. It documents and enforces the
            // subscribe contract if registration is later split for transport
            // integration: a changed immutable reference always resyncs.
            if (!ReferenceEquals(snapshot, latestSnapshot))
                subscription.RequestResync("subscribe-race");
            return subscription;
        }
    }

    public Task<RealtimeSubscription> SubscribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Subscribe());
    }

    public RealtimePublishResult PublishDisplayBatch(W.DisplayBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (Volatile.Read(ref disposed) != 0)
            return Rejected("hub-disposed");

        bool gateEntered = false;
        try
        {
            publishGate.Wait();
            gateEntered = true;
            if (Volatile.Read(ref disposed) != 0)
                return Rejected("hub-disposed");

            R.ConsoleSnapshot? incomingSnapshot = null;
            IReadOnlyList<R.SequencedConsoleTransaction> transactions;
            try
            {
                incomingSnapshot = batch.IsSnapshot
                    ? batch.Snapshot is null
                        ? throw new InvalidDataException("A snapshot batch has no snapshot payload.")
                        : RealtimePayloadMapper.FromProto(batch.Snapshot)
                    : null;
                transactions = RealtimePayloadMapper.FromProto(batch);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
            {
                return Fault("invalid-display-batch", exception);
            }

            // Flush a due batch before applying the next IPC message. Snapshot
            // messages are a hard boundary and flush any pending deltas even
            // when the 16 ms window has not elapsed yet.
            FlushPendingTransactions(force: batch.IsSnapshot);

            R.ConsoleSnapshot candidate;
            RealtimePublishDisposition disposition;
            lock (sync)
            {
                if (state is SessionOutputHubState.Faulted or SessionOutputHubState.Disposed)
                    return Rejected(terminalReason ?? "hub-not-live");

                try
                {
                    if (latestSnapshot is null)
                    {
                        if (incomingSnapshot is null)
                            return FaultLocked("initial-snapshot-required");
                        if (incomingSnapshot.SnapshotSequence < minimumInitialSequence)
                            return FaultLocked("initial-snapshot-sequence-regressed");
                        candidate = ReduceSnapshot(incomingSnapshot, transactions);
                        disposition = RealtimePublishDisposition.Applied;
                    }
                    else if (incomingSnapshot is not null)
                    {
                        if (incomingSnapshot.SnapshotSequence < latestSnapshot.SnapshotSequence)
                            return IgnoredLocked();
                        if (incomingSnapshot.SnapshotSequence == latestSnapshot.SnapshotSequence && transactions.Count == 0)
                            return IgnoredLocked();
                        candidate = ReduceSnapshot(incomingSnapshot, transactions);
                        if (candidate.SnapshotSequence <= latestSnapshot.SnapshotSequence)
                            return IgnoredLocked();
                        disposition = RealtimePublishDisposition.Applied;
                    }
                    else
                    {
                        if (transactions.Count == 0)
                            return FaultLocked("empty-display-batch");
                        long firstSequence = transactions[0].Sequence;
                        if (firstSequence <= latestSnapshot.SnapshotSequence)
                        {
                            if (transactions[^1].Sequence <= latestSnapshot.SnapshotSequence)
                                return IgnoredLocked();
                            return FaultLocked("overlapping-display-batch");
                        }
                        if (latestSnapshot.SnapshotSequence == long.MaxValue)
                            return FaultLocked("output-sequence-exhausted");
                        if (firstSequence != latestSnapshot.SnapshotSequence + 1)
                            return FaultLocked("output-sequence-gap");
                        candidate = R.ConsoleSnapshotReducer.ApplyBatch(latestSnapshot, transactions, reducerOptions);
                        disposition = RealtimePublishDisposition.Applied;
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
                {
                    return FaultLocked("invalid-display-batch", exception);
                }

                latestSnapshot = candidate;
                latestSnapshotPayload = null;
                state = SessionOutputHubState.Live;
                publishedBatchCount++;
            }

            if (incomingSnapshot is not null)
                ResetBatcherBaseline(candidate.SnapshotSequence);

            RealtimeEncodedPayload currentSnapshotPayload = serializer.SerializeSnapshot(WorkerEpoch, candidate);
            RealtimeEncodedPayload? replacementPayload = incomingSnapshot is not null ? currentSnapshotPayload : null;
            IReadOnlyList<RealtimeEncodedPayload> transactionPayloads = incomingSnapshot is null
                ? AddTransactions(transactions)
                : Array.Empty<RealtimeEncodedPayload>();

            lock (sync)
            {
                if (state is SessionOutputHubState.Faulted or SessionOutputHubState.Disposed)
                    return Rejected(terminalReason ?? "hub-not-live");
                latestSnapshotPayload = currentSnapshotPayload;
                foreach (RealtimeSubscription subscription in subscriptions.Values.ToArray())
                {
                    if (replacementPayload is not null)
                        subscription.RequestResyncLocked("snapshot-replaced");
                }
                EnqueueTransactionPayloadsLocked(transactionPayloads);
                return new RealtimePublishResult(disposition, state, candidate.SnapshotSequence);
            }
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
        {
            return Rejected("hub-disposed");
        }
        catch (RealtimePayloadSizeException exception)
        {
            return Fault("payload-too-large", exception);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        {
            return Fault("display-encoding-failed", exception);
        }
        finally
        {
            if (gateEntered)
                publishGate.Release();
        }
    }

    public void Complete(string reason = "runtime-completed", bool preservePending = true)
    {
        if (string.IsNullOrWhiteSpace(reason))
            reason = "runtime-completed";

        bool gateEntered = false;
        try
        {
            publishGate.Wait();
            gateEntered = true;
            if (preservePending)
                FlushPendingTransactions(force: true);
            CompleteCore(reason, preservePending);
        }
        catch (ObjectDisposedException)
        {
            // A concurrent DisposeAsync has already completed the hub.
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        {
            lock (sync)
                FaultLocked("display-encoding-failed", exception);
        }
        finally
        {
            if (gateEntered)
                publishGate.Release();
        }
    }

    private void CompleteCore(string reason, bool preservePending)
    {
        lock (sync)
        {
            if (state == SessionOutputHubState.Disposed)
                return;
            terminalReason = reason;
            state = SessionOutputHubState.Disposed;
            StopBatchTimer();
            if (!preservePending)
                batcher.Clear();
            foreach (RealtimeSubscription subscription in subscriptions.Values.ToArray())
                subscription.CompleteLocked(reason, preservePending);
            subscriptions.Clear();
        }
    }

    internal bool RemoveSubscription(RealtimeSubscription subscription)
    {
        lock (sync)
            return subscriptions.Remove(subscription.Id);
    }

    internal RealtimeSnapshotRead GetLatestSnapshot()
    {
        lock (sync)
        {
            return new RealtimeSnapshotRead(
                latestSnapshot,
                latestSnapshotPayload,
                state,
                latestSnapshot?.SnapshotSequence ?? 0);
        }
    }

    internal void RecordResync() => Interlocked.Increment(ref resyncCount);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        try
        {
            await publishGate.WaitAsync().ConfigureAwait(false);
            try
            {
                CompleteCore("hub-disposed", preservePending: false);
                lock (sync)
                {
                    latestSnapshot = null;
                    latestSnapshotPayload = null;
                }
            }
            finally
            {
                publishGate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // A concurrent disposal has already released the gate and
            // cleared the hub; disposal is intentionally idempotent.
        }
        finally
        {
            batchTimer.Dispose();
            publishGate.Dispose();
        }
    }

    private R.ConsoleSnapshot ReduceSnapshot(R.ConsoleSnapshot snapshot, IReadOnlyList<R.SequencedConsoleTransaction> transactions)
    {
        snapshot.Validate(reducerOptions);
        if (transactions.Count == 0)
            return snapshot;
        return R.ConsoleSnapshotReducer.ApplyBatch(snapshot, transactions, reducerOptions);
    }

    private IReadOnlyList<RealtimeEncodedPayload> AddTransactions(IReadOnlyList<R.SequencedConsoleTransaction> transactions)
    {
        if (transactions.Count == 0)
            return Array.Empty<RealtimeEncodedPayload>();
        var result = new List<RealtimeEncodedPayload>();
        foreach (R.SequencedConsoleTransaction transaction in transactions)
            result.AddRange(batcher.Add(WorkerEpoch, transaction));
        if (batcher.PendingCount != 0)
            ArmBatchTimer();
        return result;
    }

    private void FlushPendingTransactions(bool force)
    {
        IReadOnlyList<RealtimeEncodedPayload> payloads = force
            ? batcher.Flush()
            : batcher.FlushIfDue();
        if (payloads.Count == 0)
        {
            if (batcher.PendingCount == 0)
                StopBatchTimer();
            else
                ArmBatchTimer();
            return;
        }

        lock (sync)
        {
            if (state is SessionOutputHubState.Faulted or SessionOutputHubState.Disposed)
            {
                batcher.Clear();
                StopBatchTimer();
                return;
            }
            EnqueueTransactionPayloadsLocked(payloads);
        }

        if (batcher.PendingCount == 0)
            StopBatchTimer();
        else
            ArmBatchTimer();
    }

    private void EnqueueTransactionPayloadsLocked(IReadOnlyList<RealtimeEncodedPayload> payloads)
    {
        foreach (RealtimeSubscription subscription in subscriptions.Values.ToArray())
        {
            foreach (RealtimeEncodedPayload payload in payloads)
                subscription.EnqueueLocked(payload);
        }
    }

    private void ResetBatcherBaseline(long snapshotSequence)
    {
        batcher.ResetBaseline(WorkerEpoch, snapshotSequence);
        StopBatchTimer();
    }

    private void ArmBatchTimer()
    {
        if (batcher.PendingCount == 0)
            return;
        try
        {
            batchTimer.Change(options.BatchMaxDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposal owns the final lifecycle boundary.
        }
    }

    private void StopBatchTimer()
    {
        try
        {
            batchTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposal owns the final lifecycle boundary.
        }
    }

    private void FlushDueFromTimer()
    {
        bool gateEntered = false;
        try
        {
            publishGate.Wait();
            gateEntered = true;
            if (Volatile.Read(ref disposed) == 0)
                FlushPendingTransactions(force: false);
        }
        catch (ObjectDisposedException)
        {
            // The timer may race with DisposeAsync.
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        {
            lock (sync)
                FaultLocked("display-encoding-failed", exception);
        }
        finally
        {
            if (gateEntered)
                publishGate.Release();
        }
    }

    private RealtimePublishResult IgnoredLocked()
    {
        ignoredBatchCount++;
        return new RealtimePublishResult(RealtimePublishDisposition.IgnoredDuplicate, state, latestSnapshot?.SnapshotSequence ?? 0, "duplicate-or-older");
    }

    private RealtimePublishResult Rejected(string reason) =>
        new(RealtimePublishDisposition.Rejected, State, SnapshotSequence, reason);

    private RealtimePublishResult Fault(string reason, Exception? exception = null)
    {
        lock (sync)
            return FaultLocked(reason, exception);
    }

    private RealtimePublishResult FaultLocked(string reason, Exception? exception = null)
    {
        if (state is SessionOutputHubState.Faulted or SessionOutputHubState.Disposed)
            return Rejected(terminalReason ?? reason);
        state = SessionOutputHubState.Faulted;
        terminalReason = reason;
        faultCount++;
        batcher.Clear();
        StopBatchTimer();
        foreach (RealtimeSubscription subscription in subscriptions.Values.ToArray())
            subscription.CompleteLocked(reason, preservePending: true);
        subscriptions.Clear();
        return new RealtimePublishResult(RealtimePublishDisposition.Faulted, state, latestSnapshot?.SnapshotSequence ?? 0, reason);
    }

    internal sealed record RealtimeSnapshotRead(
        R.ConsoleSnapshot? Snapshot,
        RealtimeEncodedPayload? Payload,
        SessionOutputHubState State,
        long Sequence);

    internal RealtimeEncodedPayload CreateSnapshotPayload(R.ConsoleSnapshot snapshot) =>
        serializer.SerializeSnapshot(WorkerEpoch, snapshot);

    internal DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    internal TimeSpan SnapshotResyncWindow => options.SnapshotResyncWindow;

    internal int MaxSnapshotResyncAttempts => options.MaxSnapshotResyncAttempts;
}

public sealed class RealtimeSubscription : IAsyncDisposable
{
    private readonly SessionOutputHub hub;
    private readonly BoundedRealtimeQueue queue;
    private readonly object sync = new();
    private RealtimeEncodedPayload? initialSnapshot;
    private long expectedSequence;
    private bool closed;
    private string? closeReason;
    private DateTimeOffset firstResyncAt;
    private int resyncAttempts;

    internal RealtimeSubscription(SessionOutputHub hub, BoundedRealtimeQueue queue)
    {
        this.hub = hub;
        this.queue = queue;
        Id = Guid.CreateVersion7();
    }

    public Guid Id { get; }

    public RealtimeQueueStatistics QueueStatistics => queue.Statistics;

    public long ExpectedSequence
    {
        get
        {
            lock (sync)
                return expectedSequence;
        }
    }

    public string? CloseReason
    {
        get
        {
            lock (sync)
                return closeReason;
        }
    }

    internal void SetInitialSnapshot(RealtimeEncodedPayload payload)
    {
        lock (sync)
        {
            initialSnapshot = payload;
            expectedSequence = payload.LastSequence;
        }
    }

    internal void RequestResyncLocked(string reason) => queue.RequestResync(reason);

    internal void RequestResync(string reason) => queue.RequestResync(reason);

    internal void EnqueueLocked(RealtimeEncodedPayload payload) => queue.TryEnqueue(payload);

    internal void CompleteLocked(string reason, bool preservePending)
    {
        lock (sync)
        {
            closed = true;
            closeReason = reason;
            if (!preservePending)
                initialSnapshot = null;
        }
        queue.Complete(discardPending: !preservePending);
    }

    public async ValueTask<RealtimeFrame> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            RealtimeEncodedPayload? initial;
            lock (sync)
            {
                if (initialSnapshot is not null)
                {
                    initial = initialSnapshot;
                    initialSnapshot = null;
                    expectedSequence = initial.LastSequence;
                    return RealtimeFrame.Snapshot(initial, replacesState: true, "initial-snapshot");
                }
                initial = null;
                RealtimeQueueStatistics statistics = queue.Statistics;
                if (closed && statistics.IsCompleted && statistics.QueuedMessages == 0 && !statistics.NeedsResync)
                    return RealtimeFrame.Completed(hub.WorkerEpoch, closeReason ?? "closed");
            }

            RealtimeQueueRead read = await queue.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (read.Kind)
            {
                case RealtimeQueueReadKind.Completed:
                    SessionOutputHubState hubState = hub.State;
                    lock (sync)
                    {
                        closed = true;
                        closeReason ??= hubState == SessionOutputHubState.Faulted ? "hub-faulted" : "completed";
                        return RealtimeFrame.Completed(hub.WorkerEpoch, closeReason);
                    }
                case RealtimeQueueReadKind.ResyncRequired:
                    RealtimeFrame? snapshot = await ReadResyncSnapshotAsync(read.Reason ?? "resync-required", cancellationToken).ConfigureAwait(false);
                    if (snapshot is not null)
                        return snapshot;
                    continue;
                case RealtimeQueueReadKind.Payload when read.Payload is { } payload:
                    lock (sync)
                    {
                        if (payload.WorkerEpoch != hub.WorkerEpoch ||
                            expectedSequence == long.MaxValue || payload.FirstSequence != expectedSequence + 1)
                        {
                            queue.RequestResync("sequence-gap");
                            continue;
                        }
                        expectedSequence = payload.LastSequence;
                        return RealtimeFrame.Transactions(payload);
                    }
                default:
                    throw new InvalidDataException("The realtime queue returned an unknown item.");
            }
        }
    }

    private async ValueTask<RealtimeFrame?> ReadResyncSnapshotAsync(string reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionOutputHub.RealtimeSnapshotRead read = hub.GetLatestSnapshot();
        if (read.Snapshot is null)
        {
            if (!RegisterResyncFailure())
                throw new RealtimeSlowConsumerException("The subscription could not obtain a current snapshot.");
            queue.RequestResync("snapshot-unavailable");
            return null;
        }

        RealtimeEncodedPayload payload;
        try
        {
            payload = read.Payload ?? hub.CreateSnapshotPayload(read.Snapshot);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        {
            if (!RegisterResyncFailure())
                throw new RealtimeSlowConsumerException("The subscription could not receive a bounded snapshot.");
            queue.RequestResync("snapshot-encoding-failed");
            return null;
        }

        lock (sync)
        {
            expectedSequence = payload.LastSequence;
        }
        hub.RecordResync();
        RealtimeFrame result = RealtimeFrame.Snapshot(payload, replacesState: true, reason);
        SessionOutputHub.RealtimeSnapshotRead after = hub.GetLatestSnapshot();
        if (after.State != SessionOutputHubState.Live)
            return result;

        if (after.Sequence != payload.LastSequence)
        {
            if (!RegisterResyncFailure())
                throw new RealtimeSlowConsumerException("The subscription could not catch up to a moving snapshot.");
            queue.RequestResync("snapshot-raced");
        }
        else
        {
            lock (sync)
                ResetResyncFailures();
        }
        return result;
    }

    private bool RegisterResyncFailure()
    {
        lock (sync)
        {
            DateTimeOffset now = hub.UtcNow;
            if (firstResyncAt == default || now - firstResyncAt > hub.SnapshotResyncWindow)
            {
                firstResyncAt = now;
                resyncAttempts = 0;
            }
            resyncAttempts++;
            if (resyncAttempts >= hub.MaxSnapshotResyncAttempts)
            {
                closed = true;
                closeReason = "slow-consumer";
                queue.Complete(discardPending: true);
                return false;
            }
            return true;
        }
    }

    private void ResetResyncFailures()
    {
        firstResyncAt = default;
        resyncAttempts = 0;
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            closed = true;
            closeReason ??= "subscription-disposed";
            initialSnapshot = null;
        }
        queue.Complete(discardPending: true);
        hub.RemoveSubscription(this);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

public sealed class RealtimeSlowConsumerException(string message) : InvalidOperationException(message);
