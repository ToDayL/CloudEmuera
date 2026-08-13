using System.Text;
using CloudEmuera.Api.Realtime;
using W = CloudEmuera.Ipc.V3;
using CloudEmuera.RuntimeAdapter;
using CloudEmuera.Worker;
using Xunit;

namespace CloudEmuera.Realtime.Tests;

[Trait("Category", "Snapshot")]
[Trait("Category", "Backpressure")]
[Trait("Category", "Concurrency")]
public sealed class RealtimeOutputTests
{
    [Fact]
    public async Task SubscriptionReceivesACompleteSnapshotAndThenContinuousTransactions()
    {
        await using var hub = new SessionOutputHub("session-1", "worker-1", 7);
        await using RealtimeSubscription subscription = hub.Subscribe();

        SequencedConsoleTransaction first = Transaction(1, "first");
        RealtimePublishResult initial = hub.PublishDisplayBatch(SnapshotBatch(ConsoleSnapshot.Empty, first));
        Assert.Equal(RealtimePublishDisposition.Applied, initial.Disposition);

        RealtimeFrame snapshot = await subscription.ReadAsync();
        Assert.Equal(RealtimeFrameKind.Snapshot, snapshot.Kind);
        Assert.Equal(1, snapshot.LastSequence);
        Assert.Contains("first", Encoding.UTF8.GetString(snapshot.Payload.Span));

        RealtimePublishResult delta = hub.PublishDisplayBatch(DeltaBatch(Transaction(2, "second")));
        Assert.Equal(RealtimePublishDisposition.Applied, delta.Disposition);

        RealtimeFrame transactionBatch = await subscription.ReadAsync();
        Assert.Equal(RealtimeFrameKind.TransactionBatch, transactionBatch.Kind);
        Assert.Equal(2, transactionBatch.FirstSequence);
        Assert.Equal(2, transactionBatch.LastSequence);
    }

    [Fact]
    public async Task NewSubscriptionStartsFromTheLatestImmutableSnapshot()
    {
        await using var hub = new SessionOutputHub("session-1", "worker-1", 8);
        hub.PublishDisplayBatch(SnapshotBatch(ConsoleSnapshot.Empty, Transaction(1, "current")));

        await using RealtimeSubscription subscription = hub.Subscribe();
        RealtimeFrame frame = await subscription.ReadAsync();

        Assert.Equal(RealtimeFrameKind.Snapshot, frame.Kind);
        Assert.Equal(1, frame.FirstSequence);
        Assert.Equal(1, frame.LastSequence);
        Assert.Contains("current", Encoding.UTF8.GetString(frame.Payload.Span));
    }

    [Fact]
    public async Task TerminalCompletionDrainsAlreadyQueuedOutputBeforeCompleted()
    {
        await using var hub = new SessionOutputHub("session-1", "worker-1", 10);
        await using RealtimeSubscription subscription = hub.Subscribe();
        hub.PublishDisplayBatch(SnapshotBatch(ConsoleSnapshot.Empty, Transaction(1, "one")));
        Assert.Equal(RealtimeFrameKind.Snapshot, (await subscription.ReadAsync()).Kind);
        hub.PublishDisplayBatch(DeltaBatch(Transaction(2, "two")));
        hub.Complete("runtime-completed");

        RealtimeFrame queued = await subscription.ReadAsync();
        RealtimeFrame terminal = await subscription.ReadAsync();
        Assert.Equal(RealtimeFrameKind.TransactionBatch, queued.Kind);
        Assert.Equal(RealtimeFrameKind.Completed, terminal.Kind);
        Assert.Equal("runtime-completed", terminal.Reason);
    }

    [Fact]
    public async Task SequenceGapFaultsTheHubWithoutPublishingAPartialState()
    {
        await using var hub = new SessionOutputHub("session-1", "worker-1", 9);
        hub.PublishDisplayBatch(SnapshotBatch(ConsoleSnapshot.Empty));
        long before = hub.SnapshotSequence;

        RealtimePublishResult result = hub.PublishDisplayBatch(DeltaBatch(Transaction(2, "gap")));

        Assert.Equal(RealtimePublishDisposition.Faulted, result.Disposition);
        Assert.Equal(SessionOutputHubState.Faulted, hub.State);
        Assert.Equal(before, hub.SnapshotSequence);
        Assert.Throws<InvalidOperationException>(() => hub.Subscribe());
    }

    [Fact]
    public async Task QueueOverflowDropsPendingDeltasAndWakesTheReaderWithResync()
    {
        var queue = new BoundedRealtimeQueue(softMessages: 2, hardMessages: 4, softBytes: 100, hardBytes: 200);
        Assert.True(queue.TryEnqueue(Payload(1, 1, 20)));
        Assert.True(queue.TryEnqueue(Payload(1, 2, 20)));
        Assert.False(queue.TryEnqueue(Payload(1, 3, 20)));

        RealtimeQueueStatistics statistics = queue.Statistics;
        Assert.True(statistics.NeedsResync);
        Assert.Equal(1, statistics.SoftOverflowCount);
        RealtimeQueueRead resync = await queue.ReadAsync();
        Assert.Equal(RealtimeQueueReadKind.ResyncRequired, resync.Kind);
    }

    [Fact]
    public async Task QueueResyncRequestWakesAWaitingReader()
    {
        var queue = new BoundedRealtimeQueue(softMessages: 2, hardMessages: 4, softBytes: 100, hardBytes: 200);
        Task<RealtimeQueueRead> pending = queue.ReadAsync().AsTask();
        queue.RequestResync("sequence-gap");

        RealtimeQueueRead result = await pending;
        Assert.Equal(RealtimeQueueReadKind.ResyncRequired, result.Kind);
        Assert.Equal("sequence-gap", result.Reason);
    }

    [Fact]
    public void JsonPayloadUsesClosedDiscriminatorsAndBase64ForPng()
    {
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10];
        var snapshot = new ConsoleSnapshot(
            3,
            [new ConsoleLine("line-1", [new TextNode("safe")])],
            canvasScene: new CanvasScene([
                new RasterDrawable("raster-1", png, new ConsoleRect(0, 0, 1, 1))
            ]),
            currentPrompt: new ConsolePrompt(
                "prompt-1",
                ConsoleInputType.Text,
                timeout: TimeSpan.FromSeconds(10),
                openedAtUnixMilliseconds: 1000,
                deadlineUnixMilliseconds: 11000));

        byte[] payload = new RealtimePayloadSerializer().SerializeSnapshot(3, snapshot).Bytes.ToArray();
        string json = Encoding.UTF8.GetString(payload);

        Assert.Contains("\"workerEpoch\":3", json);
        Assert.Contains("\"snapshotSequence\":3", json);
        Assert.Contains("\"type\":\"text\"", json);
        Assert.Contains("iVBORw0KGgo=", json);
        Assert.Contains("\"deadlineUnixMilliseconds\":11000", json);
        Assert.DoesNotContain("$type", json);
        Assert.DoesNotContain("<html", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BatcherHonorsTheTransactionCountLimit()
    {
        var options = RealtimeOutputOptions.Default with
        {
            BatchMaxTransactions = 2,
            BatchTargetBytes = 256 * 1024
        };
        var batcher = new RealtimeBatcher(options);

        Assert.Empty(batcher.Add(4, Transaction(1, "one")));
        IReadOnlyList<RealtimeEncodedPayload> flushed = batcher.Add(4, Transaction(2, "two"));

        RealtimeEncodedPayload result = Assert.Single(flushed);
        Assert.Equal(1, result.FirstSequence);
        Assert.Equal(2, result.LastSequence);
        Assert.Empty(batcher.Add(4, Transaction(3, "three")));
    }

    [Fact]
    public async Task BatcherCombinesTransactionsAcrossPublishCallsAndTerminalFlushDrainsIt()
    {
        var options = RealtimeOutputOptions.Default with
        {
            BatchMaxTransactions = 8,
            BatchMaxDelay = TimeSpan.FromMilliseconds(900),
            BatchTargetBytes = 256 * 1024
        };
        await using var hub = new SessionOutputHub("session-1", "worker-1", 11, options);
        await using RealtimeSubscription subscription = hub.Subscribe();
        hub.PublishDisplayBatch(SnapshotBatch(ConsoleSnapshot.Empty, Transaction(1, "one")));
        Assert.Equal(RealtimeFrameKind.Snapshot, (await subscription.ReadAsync()).Kind);

        hub.PublishDisplayBatch(DeltaBatch(Transaction(2, "two")));
        hub.PublishDisplayBatch(DeltaBatch(Transaction(3, "three")));
        hub.Complete();

        RealtimeFrame combined = await subscription.ReadAsync();
        Assert.Equal(RealtimeFrameKind.TransactionBatch, combined.Kind);
        Assert.Equal(2, combined.FirstSequence);
        Assert.Equal(3, combined.LastSequence);
        Assert.Equal(RealtimeFrameKind.Completed, (await subscription.ReadAsync()).Kind);
    }

    [Fact]
    public void BatcherFlushesWhenItsWindowExpiresAndEmitsAnAtomicLargeTransaction()
    {
        var clock = new ManualTimeProvider();
        var options = RealtimeOutputOptions.Default with
        {
            BatchMaxDelay = TimeSpan.FromMilliseconds(16),
            BatchTargetBytes = 1_000
        };
        var batcher = new RealtimeBatcher(options, clock);

        Assert.Empty(batcher.Add(12, Transaction(1, "one")));
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Empty(batcher.Add(12, Transaction(2, "two")));
        clock.Advance(TimeSpan.FromMilliseconds(6));
        RealtimeEncodedPayload delayed = Assert.Single(batcher.FlushIfDue());
        Assert.Equal(1, delayed.FirstSequence);
        Assert.Equal(2, delayed.LastSequence);

        RealtimeEncodedPayload oversized = Assert.Single(batcher.Add(12, Transaction(3, new string('x', 2000))));
        Assert.Equal(3, oversized.FirstSequence);
        Assert.Equal(3, oversized.LastSequence);
    }

    [Fact]
    public async Task QueueHardByteOverflowIsRecordedAndForcesResync()
    {
        var queue = new BoundedRealtimeQueue(softMessages: 2, hardMessages: 4, softBytes: 100, hardBytes: 200);
        Assert.False(queue.TryEnqueue(Payload(1, 1, 201)));

        RealtimeQueueStatistics statistics = queue.Statistics;
        Assert.Equal(1, statistics.HardOverflowCount);
        Assert.True(statistics.NeedsResync);
        Assert.Equal(RealtimeQueueReadKind.ResyncRequired, (await queue.ReadAsync()).Kind);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;
        private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration)
        {
            timestamp += duration.Ticks;
            utcNow = utcNow.Add(duration);
        }
    }

    private static SequencedConsoleTransaction Transaction(long sequence, string text) =>
        new(sequence, new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine($"line-{sequence}", [new TextNode(text)]))
        ]));

    private static W.DisplayBatch SnapshotBatch(ConsoleSnapshot snapshot, params SequencedConsoleTransaction[] transactions)
    {
        var batch = new W.DisplayBatch
        {
            IsSnapshot = true,
            Snapshot = StructuredConsoleWireMapper.ToProto(snapshot)
        };
        batch.Transactions.AddRange(transactions.Select(StructuredConsoleWireMapper.ToProto));
        return batch;
    }

    private static W.DisplayBatch DeltaBatch(params SequencedConsoleTransaction[] transactions)
    {
        var batch = new W.DisplayBatch();
        batch.Transactions.AddRange(transactions.Select(StructuredConsoleWireMapper.ToProto));
        return batch;
    }

    private static RealtimeEncodedPayload Payload(ulong epoch, long sequence, int length) =>
        new(RealtimePayloadKind.TransactionBatch, epoch, sequence, sequence, new byte[length]);
}
