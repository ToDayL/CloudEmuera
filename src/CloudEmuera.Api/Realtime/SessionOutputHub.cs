using CloudEmuera.Contracts.Realtime;
using W = CloudEmuera.Ipc.V8;
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
    long FaultCount,
    long SnapshotEncodingCount);

public sealed record RealtimeHubDiagnostics(
    SessionOutputHubState State,
    long SnapshotSequence,
    long? SnapshotBytes,
    string SnapshotSizeStatus,
    int SubscriptionCount,
    long ResyncCount,
    long SoftOverflowCount,
    long HardOverflowCount,
    long FaultCount);

public enum RealtimeFrameKind
{
    Snapshot,
    TransactionBatch,
    DisplayFrame,
    Completed
}

public sealed record RealtimeFrame(
    RealtimeFrameKind Kind,
    ulong WorkerEpoch,
    long FirstSequence,
    long LastSequence,
    ReadOnlyMemory<byte> Payload,
    bool ReplacesState,
    string? Reason = null,
    long FrameId = 0)
{
    public static RealtimeFrame Snapshot(RealtimeEncodedPayload payload, bool replacesState, string? reason = null) =>
        new(RealtimeFrameKind.Snapshot, payload.WorkerEpoch, payload.FirstSequence, payload.LastSequence, payload.Bytes, replacesState, reason);

    public static RealtimeFrame Transactions(RealtimeEncodedPayload payload) =>
        new(RealtimeFrameKind.TransactionBatch, payload.WorkerEpoch, payload.FirstSequence, payload.LastSequence, payload.Bytes, false);

    public static RealtimeFrame Display(RealtimeEncodedPayload payload) =>
        new(RealtimeFrameKind.DisplayFrame, payload.WorkerEpoch, payload.FirstSequence, payload.LastSequence, payload.Bytes, false, payload.CommitReason, payload.FrameId);

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
    // The Worker sends only committed frames. Keep the working reference
    // private to this publish transaction so no subscription can observe it
    // before the frame metadata and reducer result are complete.
    private R.ConsoleSnapshot? workingSnapshot;
    private R.ConsoleSnapshot? latestSnapshot;
    private R.DisplayCommit? committedFrame;
    private RealtimeEncodedPayload? latestSnapshotPayload;
    private SnapshotEncodingOperation? snapshotEncoding;
    private SessionOutputHubState state = SessionOutputHubState.AwaitingInitialSnapshot;
    private string? terminalReason;
    private long publishedBatchCount;
    private long ignoredBatchCount;
    private long resyncCount;
    private long faultCount;
    private long snapshotEncodingCount;
    private int disposed;

    private sealed class SnapshotEncodingOperation(
        R.ConsoleSnapshot snapshot,
        R.DisplayCommit? commit,
        bool cacheIfCurrent)
    {
        public R.ConsoleSnapshot Snapshot { get; } = snapshot;

        public R.DisplayCommit? Commit { get; } = commit;

        public bool CacheIfCurrent { get; } = cacheIfCurrent;

        public TaskCompletionSource<RealtimeEncodedPayload> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

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
                    faultCount,
                    Volatile.Read(ref snapshotEncodingCount));
            }
        }
    }

    /// <summary>
    /// Raised once when a reader-driven fault (for example a snapshot that
    /// cannot be encoded within its byte budget) transitions the hub into
    /// <see cref="SessionOutputHubState.Faulted"/>. Publish-driven faults are
    /// not raised here; the Worker Manager handles those on the receive path.
    /// The callback runs outside the hub lock and must not block.
    /// </summary>
    internal event Action<string>? FaultReported;

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
                R.DisplayCommit? snapshotCommit = committedFrame is { } commit && ReferenceEquals(commit.Snapshot, snapshot)
                    ? commit
                    : null;
                subscription.SetInitialSnapshot(snapshot, snapshotCommit);
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
                // Invalidate the cache while publishing the new mirror. Do
                // not clear it again after AddTransactions: a reader may
                // legitimately finish lazy encoding while that work runs.
                latestSnapshotPayload = null;
                state = SessionOutputHubState.Live;
                publishedBatchCount++;
            }

            if (incomingSnapshot is not null)
                ResetBatcherBaseline(candidate.SnapshotSequence);

            IReadOnlyList<RealtimeEncodedPayload> transactionPayloads = incomingSnapshot is null
                ? AddTransactions(transactions)
                : Array.Empty<RealtimeEncodedPayload>();

            lock (sync)
            {
                if (state is SessionOutputHubState.Faulted or SessionOutputHubState.Disposed)
                    return Rejected(terminalReason ?? "hub-not-live");
                foreach (RealtimeSubscription subscription in subscriptions.Values.ToArray())
                {
                    if (incomingSnapshot is not null)
                    {
                        if (!subscription.TrySetInitialSnapshot(candidate, snapshotCommit: null))
                            subscription.SetAuthoritativeSnapshot(candidate, snapshotCommit: null);
                    }
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

    /// <summary>
    /// Publishes the only Worker payload that is allowed to become browser
    /// visible. A frame is reduced and promoted as one unit; timer, count and
    /// byte thresholds are used only to decide whether its wire delta is safe,
    /// never to create a visible boundary.
    /// </summary>
    public RealtimePublishResult PublishDisplayFrame(W.DisplayFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Volatile.Read(ref disposed) != 0)
            return Rejected("hub-disposed");

        bool gateEntered = false;
        try
        {
            publishGate.Wait();
            gateEntered = true;
            if (Volatile.Read(ref disposed) != 0)
                return Rejected("hub-disposed");

            IReadOnlyList<R.SequencedConsoleTransaction> transactions;
            R.ConsoleSnapshot? incomingSnapshot = null;
            R.DisplayCommitReason reason;
            long frameId;
            try
            {
                if (frame.FrameId == 0 || frame.FrameId > long.MaxValue || frame.CommitSequence < 0)
                    throw new InvalidDataException("The display frame metadata is invalid.");
                reason = RealtimePayloadMapper.FromProto(frame.Reason);
                transactions = RealtimePayloadMapper.FromProto(frame);
                if (frame.RequiresSnapshot)
                {
                    if (frame.Snapshot is null || transactions.Count != 0)
                        throw new InvalidDataException("A snapshot display frame must contain exactly one snapshot representation.");
                    incomingSnapshot = RealtimePayloadMapper.FromProto(frame.Snapshot);
                    if (incomingSnapshot.SnapshotSequence != frame.CommitSequence)
                        throw new InvalidDataException("The display frame snapshot sequence does not match its commit sequence.");
                }
                else if (frame.Snapshot is not null || transactions.Count == 0)
                {
                    throw new InvalidDataException("A delta display frame must contain transactions and no snapshot.");
                }
                frameId = checked((long)frame.FrameId);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
            {
                return Fault("invalid-display-frame", exception);
            }

            R.ConsoleSnapshot candidate;
            RealtimePublishDisposition disposition;
            R.DisplayCommit commit;
            lock (sync)
            {
                if (state is SessionOutputHubState.Faulted or SessionOutputHubState.Disposed)
                    return Rejected(terminalReason ?? "hub-not-live");

                try
                {
                    if (latestSnapshot is null)
                    {
                        if (!frame.RequiresSnapshot || incomingSnapshot is null)
                            return FaultLocked("initial-display-snapshot-required");
                        if (incomingSnapshot.SnapshotSequence < minimumInitialSequence)
                            return FaultLocked("initial-display-snapshot-sequence-regressed");
                        candidate = incomingSnapshot;
                    }
                    else
                    {
                        long currentFrameId = committedFrame?.FrameId ?? 0;
                        if (frameId <= currentFrameId)
                            return IgnoredLocked();

                        if (frame.RequiresSnapshot)
                        {
                            if (incomingSnapshot is null || incomingSnapshot.SnapshotSequence < latestSnapshot.SnapshotSequence)
                                return IgnoredLocked();
                            if (incomingSnapshot.SnapshotSequence == latestSnapshot.SnapshotSequence)
                                return IgnoredLocked();
                            candidate = incomingSnapshot;
                        }
                        else
                        {
                            if (committedFrame is null || frameId != currentFrameId + 1)
                                return FaultLocked("display-frame-gap");
                            if (transactions[0].Sequence != latestSnapshot.SnapshotSequence + 1)
                                return FaultLocked("display-frame-sequence-gap");
                            candidate = R.ConsoleSnapshotReducer.ApplyBatch(latestSnapshot, transactions, reducerOptions);
                        }
                    }

                    if (candidate.SnapshotSequence != frame.CommitSequence)
                        return FaultLocked("display-frame-sequence-mismatch");

                    workingSnapshot = candidate;
                    commit = new R.DisplayCommit(
                        frameId,
                        frame.CommitSequence,
                        reason,
                        frame.RequiresSnapshot,
                        candidate,
                        frame.RequiresSnapshot ? Array.Empty<R.SequencedConsoleTransaction>() : transactions);
                    committedFrame = commit;
                    latestSnapshot = candidate;
                    latestSnapshotPayload = null;
                    workingSnapshot = null;
                    state = SessionOutputHubState.Live;
                    publishedBatchCount++;
                    disposition = RealtimePublishDisposition.Applied;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
                {
                    return FaultLocked("invalid-display-frame", exception);
                }
            }

            // BatchTargetBytes is a transport optimization target for the
            // legacy batcher. A committed DisplayFrame is already an atomic
            // browser-visible unit; changing its representation here because
            // its JSON is larger than that target turns a valid large table
            // into a resync. Keep the committed frame representation intact.
            bool requiresSnapshotDelivery = commit.RequiresSnapshot;
            RealtimeEncodedPayload? displayPayload = null;
            if (!requiresSnapshotDelivery)
                displayPayload = serializer.SerializeDisplayFrame(WorkerEpoch, commit);

            lock (sync)
            {
                if (state is SessionOutputHubState.Faulted or SessionOutputHubState.Disposed)
                    return Rejected(terminalReason ?? "hub-not-live");
                foreach (RealtimeSubscription subscription in subscriptions.Values.ToArray())
                {
                    if (requiresSnapshotDelivery)
                    {
                        if (!subscription.TrySetInitialSnapshot(candidate, commit))
                            subscription.SetAuthoritativeSnapshot(candidate, commit);
                    }
                    else
                        subscription.EnqueueLocked(displayPayload!);
                }
                return new RealtimePublishResult(disposition, state, candidate.SnapshotSequence);
            }
        }
        catch (RealtimePayloadSizeException exception)
        {
            return Fault("display-frame-too-large", exception);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        {
            return Fault("display-frame-encoding-failed", exception);
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
            FaultAndReport("display-encoding-failed", exception);
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

    /// <summary>
    /// Reads bounded operational counters and, when possible, the encoded
    /// snapshot byte length. Encoding failure is reported as a diagnostic
    /// value only; this method never transitions the Hub to Faulted and never
    /// returns snapshot content.
    /// </summary>
    public async Task<RealtimeHubDiagnostics> ReadDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        R.ConsoleSnapshot? snapshot;
        RealtimeEncodedPayload? payload;
        int subscriptionCount;
        long softOverflow;
        long hardOverflow;
        lock (sync)
        {
            snapshot = latestSnapshot;
            payload = latestSnapshotPayload;
            subscriptionCount = subscriptions.Count;
            softOverflow = subscriptions.Values.Sum(value => value.QueueStatistics.SoftOverflowCount);
            hardOverflow = subscriptions.Values.Sum(value => value.QueueStatistics.HardOverflowCount);
        }

        long? bytes = payload?.ByteLength;
        string sizeStatus;
        if (snapshot is null)
        {
            sizeStatus = "NOT_READY";
        }
        else if (bytes is not null)
        {
            sizeStatus = "KNOWN";
        }
        else
        {
            try
            {
                RealtimeEncodedPayload? encoded = await GetOrCreateSnapshotPayloadAsync(snapshot).ConfigureAwait(false);
                bytes = encoded?.ByteLength;
                sizeStatus = bytes is null ? "FAILED" : "KNOWN";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                sizeStatus = "FAILED";
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            return new RealtimeHubDiagnostics(
                state,
                latestSnapshot?.SnapshotSequence ?? 0,
                bytes,
                sizeStatus,
                subscriptionCount,
                Volatile.Read(ref resyncCount),
                softOverflow,
                hardOverflow,
                Volatile.Read(ref faultCount));
        }
    }

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
                    committedFrame = null;
                    workingSnapshot = null;
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
            // Do not dispose the gate here. A timer callback can have passed
            // Wait() and still be between its body and finally/Release().
            // Disposing SemaphoreSlim at that boundary makes the callback's
            // Release() throw ObjectDisposedException and turns an otherwise
            // harmless shutdown race into an unhandled timer failure. The
            // gate is managed state with no external handle; it is reclaimed
            // together with the hub after all callbacks have stopped.
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
        {
            IReadOnlyList<RealtimeEncodedPayload> flushed = batcher.Add(WorkerEpoch, transaction);
            result.AddRange(flushed);
        }
        if (batcher.PendingCount != 0)
            ArmBatchTimer();
        else
            StopBatchTimer();
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
            FaultAndReport("display-encoding-failed", exception);
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

    private void FaultAndReport(string reason, Exception? exception = null)
    {
        bool transitioned;
        lock (sync)
        {
            transitioned = state is not (SessionOutputHubState.Faulted or SessionOutputHubState.Disposed);
            if (transitioned)
                _ = FaultLocked(reason, exception);
        }

        if (transitioned)
            FaultReported?.Invoke(reason);
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

    /// <summary>
    /// Faults the hub from a subscription reader path (for example a snapshot
    /// whose JSON encoding exceeds the configured budget). Publish paths must
    /// use <see cref="FaultLocked"/> directly so the Worker Manager cancels
    /// the Worker on its receive loop; this entry additionally reports the
    /// fault so the owning session can cancel the Worker from the reader
    /// thread. Encoding failures are protocol/config errors and must fail
    /// closed rather than degrade the snapshot.
    /// </summary>
    internal void ReportReaderFault(string reason)
    {
        FaultAndReport(reason);
    }

    internal sealed record RealtimeSnapshotRead(
        R.ConsoleSnapshot? Snapshot,
        RealtimeEncodedPayload? Payload,
        SessionOutputHubState State,
        long Sequence);

    /// <summary>
    /// Returns the cached encoded snapshot when one matches the current
    /// mirror, otherwise encodes <paramref name="snapshot"/> lazily and caches
    /// it only if the mirror has not moved. A newer cached payload may be
    /// returned when the supplied snapshot is already stale; callers must
    /// re-check the hub sequence after obtaining the payload.
    /// </summary>
    internal ValueTask<RealtimeEncodedPayload?> GetOrCreateSnapshotPayloadAsync(R.ConsoleSnapshot snapshot) =>
        GetOrCreateSnapshotPayloadAsync(snapshot, snapshotCommit: null, requireExact: false);

    /// <summary>
    /// Encodes a snapshot for a subscription. Resync readers may use the
    /// current mirror even when the requested reference is stale, while an
    /// initial snapshot must remain exact so queued deltas after it retain a
    /// continuous sequence.
    /// </summary>
    internal async ValueTask<RealtimeEncodedPayload?> GetOrCreateSnapshotPayloadAsync(
        R.ConsoleSnapshot snapshot,
        R.DisplayCommit? snapshotCommit,
        bool requireExact)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SnapshotEncodingOperation operation;
        bool ownsEncoding;
        bool encodeExactStaleSnapshot;
        lock (sync)
        {
            if (!requireExact && latestSnapshotPayload is { } cached)
                return cached;
            if (latestSnapshot is null)
                return null;

            if (requireExact && !ReferenceEquals(snapshot, latestSnapshot))
            {
                operation = new SnapshotEncodingOperation(snapshot, snapshotCommit, cacheIfCurrent: false);
                ownsEncoding = true;
                encodeExactStaleSnapshot = true;
            }
            else
            {
                R.ConsoleSnapshot currentSnapshot = requireExact ? snapshot : latestSnapshot;
                if (requireExact && latestSnapshotPayload is { } exactCached && ReferenceEquals(currentSnapshot, latestSnapshot))
                    return exactCached;
                R.DisplayCommit? currentCommit = requireExact
                    ? snapshotCommit
                    : committedFrame is { } commit && ReferenceEquals(commit.Snapshot, currentSnapshot)
                        ? commit
                        : null;
                if (snapshotEncoding is { } existing && ReferenceEquals(existing.Snapshot, currentSnapshot))
                {
                    operation = existing;
                    ownsEncoding = false;
                }
                else
                {
                    operation = new SnapshotEncodingOperation(currentSnapshot, currentCommit, cacheIfCurrent: true);
                    snapshotEncoding = operation;
                    ownsEncoding = true;
                }
                encodeExactStaleSnapshot = false;
            }
        }

        if (encodeExactStaleSnapshot)
        {
            RealtimeEncodedPayload payload = operation.Commit is { } commit
                ? serializer.SerializeSnapshot(WorkerEpoch, commit)
                : serializer.SerializeSnapshot(WorkerEpoch, operation.Snapshot);
            Interlocked.Increment(ref snapshotEncodingCount);
            return payload;
        }

        if (ownsEncoding)
        {
            try
            {
                RealtimeEncodedPayload payload = operation.Commit is { } commit
                    ? serializer.SerializeSnapshot(WorkerEpoch, commit)
                    : serializer.SerializeSnapshot(WorkerEpoch, operation.Snapshot);
                Interlocked.Increment(ref snapshotEncodingCount);
                lock (sync)
                {
                    if (operation.CacheIfCurrent && ReferenceEquals(latestSnapshot, operation.Snapshot))
                        latestSnapshotPayload = payload;
                    if (ReferenceEquals(snapshotEncoding, operation))
                        snapshotEncoding = null;
                }
                operation.Completion.TrySetResult(payload);
            }
            catch (Exception exception)
            {
                lock (sync)
                {
                    if (ReferenceEquals(snapshotEncoding, operation))
                        snapshotEncoding = null;
                }
                operation.Completion.TrySetException(exception);
            }
        }

        return await operation.Completion.Task.ConfigureAwait(false);
    }

    internal DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    internal TimeSpan SnapshotResyncWindow => options.SnapshotResyncWindow;

    internal int MaxSnapshotResyncAttempts => options.MaxSnapshotResyncAttempts;
}

public sealed class RealtimeSubscription : IAsyncDisposable
{
    private sealed record InitialSnapshotSource(
        R.ConsoleSnapshot Snapshot,
        R.DisplayCommit? Commit,
        string Reason);

    private readonly SessionOutputHub hub;
    private readonly BoundedRealtimeQueue queue;
    private readonly RealtimeResyncFailureTracker resyncFailures;
    private readonly object sync = new();
    private RealtimeEncodedPayload? initialSnapshotPayload;
    private InitialSnapshotSource? initialSnapshot;
    private long expectedSequence;
    private bool hasSnapshotBaseline;
    private bool closed;
    private string? closeReason;

    internal RealtimeSubscription(SessionOutputHub hub, BoundedRealtimeQueue queue)
    {
        this.hub = hub;
        this.queue = queue;
        resyncFailures = new RealtimeResyncFailureTracker(
            hub.UtcNow,
            hub.SnapshotResyncWindow,
            hub.MaxSnapshotResyncAttempts);
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
            initialSnapshotPayload = payload;
            initialSnapshot = null;
            expectedSequence = payload.LastSequence;
        }
    }

    internal void SetInitialSnapshot(R.ConsoleSnapshot snapshot, R.DisplayCommit? snapshotCommit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (sync)
        {
            initialSnapshotPayload = null;
            initialSnapshot = new InitialSnapshotSource(snapshot, snapshotCommit, "initial-snapshot");
            expectedSequence = snapshot.SnapshotSequence;
        }
    }

    /// <summary>
    /// Promotes a snapshot to the initial baseline for a subscription that
    /// has not delivered its first frame yet. A waiting reader is woken with
    /// an initial-snapshot signal, never a resync signal.
    /// </summary>
    internal bool TrySetInitialSnapshot(R.ConsoleSnapshot snapshot, R.DisplayCommit? snapshotCommit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (sync)
        {
            if (closed || hasSnapshotBaseline)
                return false;
            initialSnapshotPayload = null;
            initialSnapshot = new InitialSnapshotSource(snapshot, snapshotCommit, "initial-snapshot");
            expectedSequence = snapshot.SnapshotSequence;
        }
        queue.RequestInitialSnapshot();
        return true;
    }

    /// <summary>
    /// Publishes a complete committed frame as a state replacement for an
    /// already-live subscription. This is an authoritative display update,
    /// not recovery from lost output, so it must not enqueue a resync marker.
    /// </summary>
    internal bool SetAuthoritativeSnapshot(R.ConsoleSnapshot snapshot, R.DisplayCommit? snapshotCommit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (sync)
        {
            if (closed)
                return false;
            initialSnapshotPayload = null;
            initialSnapshot = new InitialSnapshotSource(snapshot, snapshotCommit, "committed-snapshot");
        }
        queue.RequestInitialSnapshot();
        return true;
    }

    internal void RequestResync(string reason) => queue.RequestResync(reason);

    internal void EnqueueLocked(RealtimeEncodedPayload payload) => queue.TryEnqueue(payload);

    internal void CompleteLocked(string reason, bool preservePending)
    {
        lock (sync)
        {
            closed = true;
            closeReason = reason;
            if (!preservePending)
            {
                initialSnapshotPayload = null;
                initialSnapshot = null;
            }
        }
        queue.Complete(discardPending: !preservePending);
    }

    public async ValueTask<RealtimeFrame> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            RealtimeFrame? initial = await ReadInitialSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (initial is not null)
                return initial;

            lock (sync)
            {
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
                case RealtimeQueueReadKind.InitialSnapshot:
                    continue;
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
                        return payload.Kind switch
                        {
                            RealtimePayloadKind.TransactionBatch => RealtimeFrame.Transactions(payload),
                            RealtimePayloadKind.DisplayFrame => RealtimeFrame.Display(payload),
                            _ => throw new InvalidDataException("The realtime queue returned a non-delta payload."),
                        };
                    }
                default:
                    throw new InvalidDataException("The realtime queue returned an unknown item.");
            }
        }
    }

    private async ValueTask<RealtimeFrame?> ReadInitialSnapshotAsync(CancellationToken cancellationToken)
    {
        RealtimeEncodedPayload? payload;
        InitialSnapshotSource? source;
        string reason;
        lock (sync)
        {
            if (initialSnapshotPayload is { } pendingPayload)
            {
                initialSnapshotPayload = null;
                hasSnapshotBaseline = true;
                expectedSequence = pendingPayload.LastSequence;
                payload = pendingPayload;
                source = null;
                reason = "initial-snapshot";
            }
            else if (initialSnapshot is { } pendingSource)
            {
                initialSnapshot = null;
                // Mark the baseline as claimed before encoding. If a new
                // committed snapshot arrives during encoding, it must take
                // the ordinary resync path instead of being mistaken for a
                // second initial frame.
                hasSnapshotBaseline = true;
                payload = null;
                source = pendingSource;
                reason = pendingSource.Reason;
            }
            else
            {
                return null;
            }
        }

        queue.ConsumeInitialSnapshotSignal();
        if (payload is not null)
            return RealtimeFrame.Snapshot(payload, replacesState: true, reason);

        cancellationToken.ThrowIfCancellationRequested();
        RealtimeEncodedPayload? encoded;
        try
        {
            encoded = await hub.GetOrCreateSnapshotPayloadAsync(source!.Snapshot, source.Commit, requireExact: true).ConfigureAwait(false);
        }
        catch (RealtimePayloadSizeException)
        {
            hub.ReportReaderFault("snapshot-encoding-too-large");
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        {
            hub.ReportReaderFault("snapshot-encoding-failed");
            return null;
        }

        if (encoded is null)
            return null;
        lock (sync)
            expectedSequence = encoded.LastSequence;
        return RealtimeFrame.Snapshot(encoded, replacesState: true, reason);
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

        RealtimeEncodedPayload? payload;
        try
        {
            payload = await hub.GetOrCreateSnapshotPayloadAsync(read.Snapshot).ConfigureAwait(false);
        }
        catch (RealtimePayloadSizeException)
        {
            // A snapshot that cannot be encoded within its byte budget is a
            // protocol/config error, not a slow consumer. Fail the hub closed
            // so the Worker Manager can retire the Worker and reconcile the
            // Session instead of leaving an unreadable mirror behind.
            hub.ReportReaderFault("snapshot-encoding-too-large");
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        {
            if (!RegisterResyncFailure())
                throw new RealtimeSlowConsumerException("The subscription could not receive a bounded snapshot.");
            queue.RequestResync("snapshot-encoding-failed");
            return null;
        }
        if (payload is null)
        {
            // The mirror was released by disposal; the completed queue will
            // surface the terminal frame on the next loop iteration.
            return null;
        }

        lock (sync)
        {
            expectedSequence = payload.LastSequence;
            hasSnapshotBaseline = true;
        }
        hub.RecordResync();
        RealtimeFrame result = RealtimeFrame.Snapshot(payload, replacesState: true, reason);
        SessionOutputHub.RealtimeSnapshotRead after = hub.GetLatestSnapshot();
        if (after.State != SessionOutputHubState.Live)
            return result;

        if (after.Sequence != payload.LastSequence)
        {
            // A snapshot that raced the mirror is not a slow-consumer failure:
            // the client did receive a complete, valid replacement and simply
            // needs a newer baseline. Re-requesting a resync is bounded and
            // converges once the mirror settles; it must not disconnect a
            // fast client on a busy hub.
            RequestResyncIfNoPendingSnapshot("snapshot-raced");
        }
        else
        {
            resyncFailures.Reset();
        }
        return result;
    }

    private void RequestResyncIfNoPendingSnapshot(string reason)
    {
        lock (sync)
        {
            // A committed snapshot replacement is already queued as an
            // authoritative state update. Do not let the completion of an
            // older resync encoding overwrite it with a recovery marker.
            if (closed || initialSnapshotPayload is not null || initialSnapshot is not null)
                return;
            queue.RequestResync(reason);
        }
    }

    private bool RegisterResyncFailure()
    {
        if (resyncFailures.RegisterFailure(hub.UtcNow))
            return true;

        lock (sync)
        {
            closed = true;
            closeReason = "slow-consumer";
            queue.Complete(discardPending: true);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            closed = true;
            closeReason ??= "subscription-disposed";
            initialSnapshotPayload = null;
            initialSnapshot = null;
        }
        queue.Complete(discardPending: true);
        hub.RemoveSubscription(this);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

public sealed class RealtimeSlowConsumerException(string message) : InvalidOperationException(message);

/// <summary>
/// Tracks consecutive snapshot replacement failures within a rolling window.
/// Only genuine failures to obtain or encode a replacement count; a snapshot
/// that raced the mirror is not a failure and never reaches this tracker.
/// </summary>
internal sealed class RealtimeResyncFailureTracker
{
    private readonly TimeSpan window;
    private readonly int maxAttempts;
    private DateTimeOffset firstFailureAt;
    private int failures;

    internal RealtimeResyncFailureTracker(
        DateTimeOffset initialNow,
        TimeSpan window,
        int maxAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAttempts, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        this.window = window;
        this.maxAttempts = maxAttempts;
        firstFailureAt = initialNow;
    }

    internal bool RegisterFailure(DateTimeOffset now)
    {
        if (firstFailureAt == default || now - firstFailureAt > window)
        {
            firstFailureAt = now;
            failures = 0;
        }

        failures++;
        return failures < maxAttempts;
    }

    internal void Reset() => failures = 0;
}
