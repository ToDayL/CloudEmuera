using System.IO;
using System.Linq;
using System.Text;
using CloudEmuera.Api.Realtime;
using RuntimeColor = CloudEmuera.RuntimeAdapter.ConsoleColor;
using W = CloudEmuera.Ipc.V5;
using CloudEmuera.RuntimeAdapter;
using CloudEmuera.Realtime;
using Xunit;

namespace CloudEmuera.Realtime.Tests;

[Trait("Category", "Snapshot")]
[Trait("Category", "Backpressure")]
[Trait("Category", "Concurrency")]
public sealed class RealtimeOutputTests
{
    [Fact]
    public async Task DisposingWhileTheBatchTimerIsDueDoesNotRaceThePublishGate()
    {
        var options = RealtimeOutputOptions.Default with
        {
            BatchMaxDelay = TimeSpan.FromMilliseconds(1),
            BatchMaxTransactions = 64,
        };

        for (int attempt = 0; attempt < 32; attempt++)
        {
            var hub = new SessionOutputHub("session-1", $"worker-{attempt}", (ulong)(attempt + 1), options);
            hub.PublishDisplayBatch(SnapshotBatch(ConsoleSnapshot.Empty, Transaction(1, "initial")));
            hub.PublishDisplayBatch(DeltaBatch(Transaction(2, "pending")));
            await Task.WhenAll(
                Task.Run(() => hub.PublishDisplayBatch(DeltaBatch(Transaction(3, "race")))),
                hub.DisposeAsync().AsTask());
        }
    }

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
    public async Task DisplayFrameCommitsClearAndReprintAsOneBrowserVisibleUnit()
    {
        await using var hub = new SessionOutputHub("session-1", "worker-1", 30);
        await using RealtimeSubscription subscription = hub.Subscribe();

        Assert.Equal(RealtimePublishDisposition.Applied, hub.PublishDisplayFrame(DisplaySnapshotFrame(1, ConsoleSnapshot.Empty)).Disposition);
        Assert.Equal(RealtimeFrameKind.Snapshot, (await subscription.ReadAsync()).Kind);

        W.DisplayFrame frame = DisplayDeltaFrame(
            2,
            3,
            W.DisplayCommitReason.WaitingForInput,
            new SequencedConsoleTransaction(1, new ConsoleTransaction([
                ConsoleOperation.ClearConsole(),
            ])),
            new SequencedConsoleTransaction(2, new ConsoleTransaction([
                ConsoleOperation.AppendLine(new ConsoleLine("line-1", [new TextNode("reprinted")])),
            ])),
            new SequencedConsoleTransaction(3, new ConsoleTransaction([
                ConsoleOperation.Open(new ConsolePrompt("prompt-1", ConsoleInputType.Text)),
            ])));
        RealtimePublishResult result = hub.PublishDisplayFrame(frame);

        Assert.Equal(RealtimePublishDisposition.Applied, result.Disposition);
        RealtimeFrame visible = await subscription.ReadAsync();
        Assert.Equal(RealtimeFrameKind.DisplayFrame, visible.Kind);
        Assert.Equal(2, visible.FrameId);
        Assert.Equal(1, visible.FirstSequence);
        Assert.Equal(3, visible.LastSequence);
        string payload = Encoding.UTF8.GetString(visible.Payload.Span);
        Assert.Contains("reprinted", payload);
        Assert.Contains("openPrompt", payload);
        Assert.Contains("\"consoleState\":null", payload);
    }

    [Fact]
    public async Task OversizedCommittedDeltaFallsBackToTheCommittedSnapshot()
    {
        var options = RealtimeOutputOptions.Default with
        {
            BatchTargetBytes = 256,
            ConnectionQueueSoftBytes = 4 * 1024,
            ConnectionQueueHardBytes = 8 * 1024,
        };
        await using var hub = new SessionOutputHub("session-1", "worker-1", 31, options);
        await using RealtimeSubscription subscription = hub.Subscribe();
        hub.PublishDisplayFrame(DisplaySnapshotFrame(1, ConsoleSnapshot.Empty));
        Assert.Equal(RealtimeFrameKind.Snapshot, (await subscription.ReadAsync()).Kind);

        hub.PublishDisplayFrame(DisplayDeltaFrame(2, 1, W.DisplayCommitReason.ExplicitRefresh, new SequencedConsoleTransaction(1, new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine("line-1", [new TextNode(new string('x', 2_000))])),
        ]))));

        RealtimeFrame replacement = await subscription.ReadAsync();
        Assert.Equal(RealtimeFrameKind.Snapshot, replacement.Kind);
        Assert.Contains("\"committedFrameId\":2", Encoding.UTF8.GetString(replacement.Payload.Span));
        Assert.Contains(new string('x', 2_000), Encoding.UTF8.GetString(replacement.Payload.Span));
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
    public async Task CommittedDisplayFrameIsVisibleWithoutWaitingForTheTransportTimer()
    {
        var options = RealtimeOutputOptions.Default with
        {
            BatchMaxTransactions = 64,
            BatchMaxDelay = TimeSpan.FromMilliseconds(900),
            BatchTargetBytes = 256 * 1024
        };
        await using var hub = new SessionOutputHub("session-1", "worker-1", 12, options);
        await using RealtimeSubscription subscription = hub.Subscribe();
        hub.PublishDisplayFrame(DisplaySnapshotFrame(1, ConsoleSnapshot.Empty));
        Assert.Equal(RealtimeFrameKind.Snapshot, (await subscription.ReadAsync()).Kind);

        hub.PublishDisplayFrame(DisplayDeltaFrame(2, 1, W.DisplayCommitReason.WaitingForInput, Transaction(1, [
            ConsoleOperation.ClearConsole(),
            ConsoleOperation.AppendLine(new ConsoleLine("line-1", [new TextNode("reprinted")])),
            ConsoleOperation.UpsertDrawable(new ShapeDrawable(
                "portrait-1",
                ConsoleShapeKind.Rectangle,
                new ConsoleRect(0, 0, 1, 1),
                new RuntimeColor(255, 255, 255)))
            ,
            ConsoleOperation.Open(new ConsolePrompt("prompt-1", ConsoleInputType.Text))
        ])));

        RealtimeFrame committed = await subscription.ReadAsync();
        Assert.Equal(RealtimeFrameKind.DisplayFrame, committed.Kind);
        Assert.Equal(1, committed.FirstSequence);
        Assert.Equal(1, committed.LastSequence);
        string json = Encoding.UTF8.GetString(committed.Payload.Span);
        Assert.Contains("reprinted", json);
        Assert.Contains("portrait-1", json);
        Assert.Contains("openPrompt", json);
    }

    [Fact]
    public async Task OpenPromptMustBeTheFinalOperationOfItsTransaction()
    {
        await using var hub = new SessionOutputHub("session-1", "worker-1", 13);
        hub.PublishDisplayBatch(SnapshotBatch(ConsoleSnapshot.Empty, Transaction(1, "initial")));

        RealtimePublishResult result = hub.PublishDisplayBatch(DeltaBatch(Transaction(2, [
            ConsoleOperation.Open(new ConsolePrompt("prompt-1", ConsoleInputType.Text)),
            ConsoleOperation.AppendLine(new ConsoleLine("line-1", [new TextNode("late")]))
        ])));

        Assert.Equal(RealtimePublishDisposition.Faulted, result.Disposition);
        Assert.Equal(SessionOutputHubState.Faulted, hub.State);
    }

    [Fact]
    public async Task LegacyTransportBatchThresholdsDoNotClaimDisplayCommitOwnership()
    {
        var options = RealtimeOutputOptions.Default with
        {
            BatchMaxTransactions = 1,
            BatchMaxDelay = TimeSpan.FromMilliseconds(1),
            BatchTargetBytes = 1,
        };
        await using var hub = new SessionOutputHub("session-1", "worker-1", 14, options);
        await using RealtimeSubscription subscription = hub.Subscribe();
        hub.PublishDisplayBatch(SnapshotBatch(ConsoleSnapshot.Empty, Transaction(1, [
            ConsoleOperation.Open(new ConsolePrompt("prompt-1", ConsoleInputType.Text))
        ])));
        Assert.Equal(RealtimeFrameKind.Snapshot, (await subscription.ReadAsync()).Kind);

        hub.PublishDisplayBatch(DeltaBatch(Transaction(2, [
            ConsoleOperation.Close("prompt-1", ConsolePromptCloseReason.InputAccepted)
        ])));
        RealtimeFrame transportFlush = await subscription.ReadAsync();
        Assert.Equal(RealtimeFrameKind.TransactionBatch, transportFlush.Kind);
        Assert.Equal(2, transportFlush.FirstSequence);
        Assert.Equal(2, transportFlush.LastSequence);

        hub.PublishDisplayBatch(DeltaBatch(Transaction(3, [
            ConsoleOperation.ClearConsole(),
            ConsoleOperation.AppendLine(new ConsoleLine("line-2", [new TextNode("reprinted")]))
        ])));
        RealtimeFrame secondFlush = await subscription.ReadAsync();
        Assert.Equal(3, secondFlush.FirstSequence);
        Assert.Equal(3, secondFlush.LastSequence);
    }

    [Fact]
    public void BatcherFlushesWhenItsWindowExpiresAndEmitsAnAtomicLargeTransaction()
    {
        var clock = new TestTimeProvider();
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

    [Fact]
    public async Task SnapshotPayloadIsEncodedLazilyAndReusedUntilTheMirrorMoves()
    {
        await using var hub = new SessionOutputHub("session-1", "worker-1", 21);
        hub.PublishDisplayBatch(SnapshotBatch(ConsoleSnapshot.Empty, Transaction(1, "one")));
        hub.PublishDisplayBatch(DeltaBatch(Transaction(2, "two")));
        Assert.Equal(0, hub.Statistics.SnapshotEncodingCount);

        await using RealtimeSubscription first = hub.Subscribe();
        RealtimeFrame snapshot = await first.ReadAsync();
        Assert.Equal(RealtimeFrameKind.Snapshot, snapshot.Kind);
        Assert.Equal(1, hub.Statistics.SnapshotEncodingCount);

        await using RealtimeSubscription second = hub.Subscribe();
        RealtimeFrame cached = await second.ReadAsync();
        Assert.Equal(RealtimeFrameKind.Snapshot, cached.Kind);
        Assert.Equal(1, hub.Statistics.SnapshotEncodingCount);

        hub.PublishDisplayBatch(DeltaBatch(Transaction(3, "three")));
        await using RealtimeSubscription third = hub.Subscribe();
        RealtimeFrame refreshed = await third.ReadAsync();
        Assert.Equal(RealtimeFrameKind.Snapshot, refreshed.Kind);
        Assert.Equal(2, hub.Statistics.SnapshotEncodingCount);
    }

    [Fact]
    public async Task ConcurrentSnapshotReadersShareOneInFlightEncoding()
    {
        var snapshot = new ConsoleSnapshot(
            1,
            Enumerable.Range(0, 512)
                .Select(index => new ConsoleLine($"line-{index}", [new TextNode(new string('x', 400))])));
        await using var hub = new SessionOutputHub("session-1", "worker-1", 211);
        hub.PublishDisplayBatch(SnapshotBatch(snapshot));

        SessionOutputHub.RealtimeSnapshotRead read = hub.GetLatestSnapshot();
        Assert.NotNull(read.Snapshot);
        Task<RealtimeEncodedPayload?>[] encodings = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await hub.GetOrCreateSnapshotPayloadAsync(read.Snapshot!)))
            .ToArray();

        RealtimeEncodedPayload?[] payloads = await Task.WhenAll(encodings);

        Assert.Equal(1, hub.Statistics.SnapshotEncodingCount);
        Assert.All(payloads, payload => Assert.NotNull(payload));
        Assert.All(payloads, payload => Assert.Equal(1, payload!.LastSequence));
    }

    [Fact]
    public async Task UnencodableSnapshotFaultsTheHubAndCompletesSubscribers()
    {
        var options = RealtimeOutputOptions.Default with
        {
            SnapshotMaxBytes = 16 * 1024,
            BatchTargetBytes = 4 * 1024,
            ConnectionQueueSoftBytes = 4 * 1024,
            ConnectionQueueHardBytes = 8 * 1024
        };
        await using var hub = new SessionOutputHub("session-1", "worker-1", 22, options);
        string? reportedReason = null;
        hub.FaultReported += reason => reportedReason = reason;

        hub.PublishDisplayBatch(SnapshotBatch(ConsoleSnapshot.Empty, Transaction(1, new string('x', 200))));
        for (int sequence = 2; sequence <= 100; sequence++)
        {
            RealtimePublishResult result = hub.PublishDisplayBatch(DeltaBatch(Transaction(sequence, new string('x', 200))));
            Assert.Equal(RealtimePublishDisposition.Applied, result.Disposition);
        }
        Assert.Equal(SessionOutputHubState.Live, hub.State);

        await using RealtimeSubscription subscription = hub.Subscribe();
        RealtimeFrame frame = await subscription.ReadAsync();

        Assert.Equal(RealtimeFrameKind.Completed, frame.Kind);
        Assert.Equal("snapshot-encoding-too-large", frame.Reason);
        Assert.Equal(SessionOutputHubState.Faulted, hub.State);
        Assert.Equal("snapshot-encoding-too-large", reportedReason);
    }

    [Fact]
    public async Task FirstBatchWithoutASnapshotFaultsTheHub()
    {
        await using var hub = new SessionOutputHub("session-1", "worker-1", 24);

        RealtimePublishResult result = hub.PublishDisplayBatch(DeltaBatch(Transaction(1, "no-snapshot")));

        Assert.Equal(RealtimePublishDisposition.Faulted, result.Disposition);
        Assert.Equal("initial-snapshot-required", result.ReasonCode);
        Assert.Equal(SessionOutputHubState.Faulted, hub.State);
        Assert.Equal(0, hub.SnapshotSequence);
    }

    [Fact]
    public async Task CompletingTheHubFinishesSubscriptionsAndANewHubRequiresItsOwnSnapshot()
    {
        await using var oldHub = new SessionOutputHub("session-1", "worker-1", 25);
        await using RealtimeSubscription subscription = oldHub.Subscribe();
        oldHub.PublishDisplayBatch(SnapshotBatch(ConsoleSnapshot.Empty, Transaction(1, "one")));
        Assert.Equal(RealtimeFrameKind.Snapshot, (await subscription.ReadAsync()).Kind);

        oldHub.Complete("epoch-replaced");
        RealtimeFrame terminal = await subscription.ReadAsync();
        Assert.Equal(RealtimeFrameKind.Completed, terminal.Kind);
        Assert.Equal("epoch-replaced", terminal.Reason);
        Assert.Throws<InvalidOperationException>(() => oldHub.Subscribe());

        await using var newHub = new SessionOutputHub("session-1", "worker-2", 26);
        Assert.Equal(SessionOutputHubState.AwaitingInitialSnapshot, newHub.State);
        Assert.Equal(
            RealtimePublishDisposition.Faulted,
            newHub.PublishDisplayBatch(DeltaBatch(Transaction(5, "five"))).Disposition);
    }

    [Fact]
    public async Task SlowReaderOverflowIsBoundedAndDoesNotBlockAFastReader()
    {
        var options = RealtimeOutputOptions.Default with
        {
            BatchMaxDelay = TimeSpan.FromMilliseconds(1),
            BatchMaxTransactions = 1,
            SnapshotMaxBytes = 64 * 1024,
            BatchTargetBytes = 16 * 1024,
            ConnectionQueueSoftBytes = 16 * 1024,
            ConnectionQueueHardBytes = 32 * 1024
        };
        await using var hub = new SessionOutputHub("session-1", "worker-1", 23, options);
        await using RealtimeSubscription fast = hub.Subscribe();
        await using RealtimeSubscription slow = hub.Subscribe();

        hub.PublishDisplayBatch(SnapshotBatch(ConsoleSnapshot.Empty, Transaction(1, "one")));
        Assert.Equal(RealtimeFrameKind.Snapshot, (await fast.ReadAsync()).Kind);
        Assert.Equal(RealtimeFrameKind.Snapshot, (await slow.ReadAsync()).Kind);

        const int publishCount = 200;
        var fastReaderReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task fastReader = Task.Run(async () =>
        {
            long expected = 2;
            int received = 0;
            while (received < publishCount)
            {
                ValueTask<RealtimeFrame> pending = fast.ReadAsync();
                fastReaderReady.TrySetResult();
                RealtimeFrame frame = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(RealtimeFrameKind.TransactionBatch, frame.Kind);
                Assert.Equal(expected, frame.FirstSequence);
                expected = frame.LastSequence + 1;
                received += (int)(frame.LastSequence - frame.FirstSequence + 1);
            }
        });
        await fastReaderReady.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Task publishing = Task.Run(async () =>
        {
            for (int sequence = 2; sequence <= publishCount + 1; sequence++)
            {
                RealtimePublishResult result = hub.PublishDisplayBatch(DeltaBatch(Transaction(sequence, $"line-{sequence}")));
                Assert.Equal(RealtimePublishDisposition.Applied, result.Disposition);
                await Task.Yield();
            }
        });

        await Task.WhenAll(publishing, fastReader);

        Assert.Equal(SessionOutputHubState.Live, hub.State);
        RealtimeQueueStatistics slowStatistics = slow.QueueStatistics;
        Assert.Equal(0, slowStatistics.QueuedMessages);
        Assert.Equal(0, slowStatistics.QueuedBytes);
        Assert.True(slowStatistics.NeedsResync);

        RealtimeFrame slowResync = await slow.ReadAsync();
        Assert.Equal(RealtimeFrameKind.Snapshot, slowResync.Kind);
        Assert.True(slowResync.ReplacesState);
        Assert.Equal(publishCount + 1, slowResync.LastSequence);
    }

    [Fact]
    public void SnapshotSerializationFailsClosedAboveTheTwelveMiBProtocolLimit()
    {
        var serializer = new RealtimePayloadSerializer();
        var snapshot = new ConsoleSnapshot(
            1,
            Enumerable.Range(0, 4096).Select(index => new ConsoleLine($"line-{index}", [new TextNode(new string('<', 600))])));

        Assert.Throws<RealtimePayloadSizeException>(() => serializer.SerializeSnapshot(1, snapshot));
    }

    [Fact]
    public void GoldenJsonFreezesTheCompleteSnapshotContract()
    {
        byte[] payload = new RealtimePayloadSerializer().SerializeSnapshot(42, BuildGoldenSnapshot()).Bytes.ToArray();
        string actual = Encoding.UTF8.GetString(payload);

        string golden = File.ReadAllText(Path.Combine(TestFixturePath, "snapshot.complete.json"));
        Assert.Equal(golden, actual);
    }

    [Fact]
    public void GoldenJsonFreezesTheTransactionBatchContract()
    {
        byte[] payload = new RealtimePayloadSerializer()
            .SerializeTransactionBatch(42, [Transaction(7, "seven"), Transaction(8, "eight")])
            .Bytes.ToArray();
        string actual = Encoding.UTF8.GetString(payload);

        string golden = File.ReadAllText(Path.Combine(TestFixturePath, "transaction-batch.complete.json"));
        Assert.Equal(golden, actual);
    }

    private static string TestFixturePath
    {
        get
        {
            string baseDirectory = AppContext.BaseDirectory;
            return Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "GoldenJson"));
        }
    }

    private static ConsoleSnapshot BuildGoldenSnapshot()
    {
        var style = new ConsoleTextStyle(
            foreground: new RuntimeColor(255, 255, 255),
            background: new RuntimeColor(0, 0, 0),
            decorations: ConsoleFontStyle.Bold | ConsoleFontStyle.Italic,
            fontFamily: "game-serif",
            fontSize: 18,
            lineHeight: 22);

        var htmlIsland = new HtmlIslandNode(
            new ConsoleHtmlElementNode(
                "span",
                [
                    new ConsoleHtmlTextNode("safe"),
                    ConsoleHtmlBreakNode.Instance,
                    new ConsoleHtmlElementNode("b", [new ConsoleHtmlTextNode("bold")], style, assetId: "assets-font")
                ],
                style,
                assetId: "assets-icon",
                altText: "icon"),
            new ConsoleRect(10, 20, 120, 40));

        var line1 = new ConsoleLine(
            "line-1",
            [
                new TextNode("hello", style),
                new ButtonNode([new TextNode("go")], "go:1", "continue", enabled: true, generation: 3),
                new ImageNode(new ConsoleAssetId("assets-logo"), new ConsoleRect(0, 0, 16, 16), new ConsoleRect(8, 8, 32, 32), altText: "logo", zIndex: 1),
                htmlIsland,
                new SpriteNode(
                    new ConsoleAssetId("assets-hero"),
                    new ConsoleRect(0, 0, 32, 48),
                    new ConsoleRect(100, 100, 64, 96),
                    frame: 2,
                    zIndex: 3,
                    opacity: 0.75f,
                    altText: "hero",
                    hoverAssetId: new ConsoleAssetId("assets-hero-hover"),
                    hoverSourceRect: new ConsoleRect(32, 0, 32, 48),
                    animationFrames: [new SpriteAnimationFrame(new ConsoleAssetId("assets-hero"), new ConsoleRect(0, 0, 32, 48), new ConsolePoint(0, 0), 120)])
            ],
            ConsoleLineAlignment.Center,
            temporary: true);

        var line2 = new ConsoleLine(
            "line-2",
            [
                new TextNode("world"),
                new ShapeNode(
                    ConsoleShapeKind.Polygon,
                    new ConsoleRect(0, 0, 40, 40),
                    new RuntimeColor(255, 0, 0),
                    new RuntimeColor(0, 255, 0),
                    2,
                    [new ConsolePoint(0, 0), new ConsolePoint(10, 0), new ConsolePoint(10, 10)])
            ]);

        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];

        return new ConsoleSnapshot(
            42,
            [line1, line2],
            backgroundLayers: [new BackgroundLayer("bg-1", new ConsoleAssetId("assets-sky"), ConsoleBackgroundMode.Cover, 0.5f, 10)],
            canvasScene: new CanvasScene(
                [
                    new SpriteDrawable(
                        "d-sprite",
                        new ConsoleAssetId("assets-hero"),
                        new ConsoleRect(0, 0, 32, 48),
                        new ConsoleRect(100, 100, 64, 96),
                        1,
                        0.8f,
                        1,
                        [new SpriteAnimationFrame(new ConsoleAssetId("assets-hero"), new ConsoleRect(0, 0, 32, 48), new ConsolePoint(0, 0), 100)]),
                    new ShapeDrawable("d-shape", ConsoleShapeKind.Rectangle, new ConsoleRect(0, 0, 50, 50), new RuntimeColor(10, 20, 30), new RuntimeColor(40, 50, 60), 2, 0.9f),
                    new RasterDrawable("d-raster", png, new ConsoleRect(200, 200, 80, 80), 3, 1f, hitTestMap: true)
                ],
                [new HitRegion("r-1", new ConsoleRect(100, 100, 64, 96), "tap:hero", true, "hero region")]),
            mediaState: new MediaState([
                new MediaChannelState("bgm", new ConsoleAssetId("assets-track"), ConsoleMediaPlaybackState.Requested, true, 0.4f, 7, ConsoleMediaStartPolicy.OnUserGesture)
            ]),
            currentPrompt: new ConsolePrompt(
                "prompt-7",
                ConsoleInputType.Text,
                promptText: "name?",
                defaultValue: "Erina",
                constraints: new TextInputConstraints(maxLength: 12, allowControlCharacters: false),
                timeout: TimeSpan.FromSeconds(30),
                timeoutBehavior: ConsolePromptTimeoutBehavior.ReturnDefaultValue,
                oneInput: true,
                systemInput: false,
                stopMessageSkip: true,
                displayTime: true,
                timeoutMessage: "timeout",
                timeoutAction: ConsolePromptTimeoutAction.ReturnDefaultValue,
                allowedSources: ConsoleInputSource.Keyboard | ConsoleInputSource.Pointer,
                openedAtUnixMilliseconds: 1000,
                deadlineUnixMilliseconds: 31000),
            windowMetadata: new WindowMetadata(
                "CloudEmuera",
                800,
                600,
                new RuntimeColor(255, 255, 255),
                new RuntimeColor(0, 0, 0),
                new ConsoleFontSpec("game-serif", 18, 22)),
            truncation: new ConsoleTruncationMetadata(true, 5, 2, 120));
    }

    private static SequencedConsoleTransaction Transaction(long sequence, string text) =>
        new(sequence, new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine($"line-{sequence}", [new TextNode(text)]))
        ]));

    private static SequencedConsoleTransaction Transaction(long sequence, params ConsoleOperation[] operations) =>
        new(sequence, new ConsoleTransaction(operations));

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

    private static W.DisplayFrame DisplaySnapshotFrame(ulong frameId, ConsoleSnapshot snapshot) => new()
    {
        FrameId = frameId,
        CommitSequence = snapshot.SnapshotSequence,
        Reason = W.DisplayCommitReason.ExplicitRefresh,
        RequiresSnapshot = true,
        Snapshot = StructuredConsoleWireMapper.ToProto(snapshot),
    };

    private static W.DisplayFrame DisplayDeltaFrame(
        ulong frameId,
        long commitSequence,
        W.DisplayCommitReason reason,
        params SequencedConsoleTransaction[] transactions)
    {
        var frame = new W.DisplayFrame
        {
            FrameId = frameId,
            CommitSequence = commitSequence,
            Reason = reason,
            RequiresSnapshot = false,
        };
        frame.Transactions.AddRange(transactions.Select(StructuredConsoleWireMapper.ToProto));
        return frame;
    }

    private static RealtimeEncodedPayload Payload(ulong epoch, long sequence, int length) =>
        new(RealtimePayloadKind.TransactionBatch, epoch, sequence, sequence, new byte[length]);
}
