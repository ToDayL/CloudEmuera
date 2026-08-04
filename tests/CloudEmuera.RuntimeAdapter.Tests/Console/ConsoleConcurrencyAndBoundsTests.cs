using System.Collections.Concurrent;
using System.Globalization;
using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.ConsoleContract;

[Trait("Category", "ConsoleContract")]
public sealed class ConsoleConcurrencyAndBoundsTests
{
    [Fact]
    public async Task ConcurrentEmitAllocatesExactlyOneSequencePerAcceptedOperation()
    {
        const int operationCount = 64;
        var store = new ConsoleStateStore();
        var barrier = new Barrier(operationCount + 1);
        var events = new ConcurrentBag<SequencedConsoleEvent>();
        Task[] tasks = Enumerable.Range(0, operationCount)
            .Select(index => Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    events.Add(store.Apply(new AppendNodesOperation([new TextNode(index.ToString(CultureInfo.InvariantCulture))])));
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        barrier.SignalAndWait();
        await Task.WhenAll(tasks);

        long[] sequences = events.OrderBy(item => item.Sequence).Select(item => item.Sequence).ToArray();
        Assert.Equal(Enumerable.Range(1, operationCount).Select(value => (long)value), sequences);
        Assert.Equal(operationCount, store.CurrentSequence);
    }

    [Fact]
    public void EstimatedStateHistoryAndReceiptsStayWithinConfiguredBounds()
    {
        var options = new ConsoleHistoryOptions
        {
            MaxVisibleNodes = 100,
            MaxVisibleTextLength = 100,
            MaxDeltaCount = 100,
            MaxEstimatedBytes = 256,
            MaxInputReceiptCount = 2
        };
        var store = new ConsoleStateStore(options);
        for (int index = 0; index < 32; index++)
        {
            store.Apply(new AppendNodesOperation([new TextNode(index.ToString(CultureInfo.InvariantCulture))]));
        }

        Assert.True(store.Snapshot.EstimatedBytes <= options.MaxEstimatedBytes);
        Assert.True(store.HistoryEstimatedBytes <= options.MaxEstimatedBytes);

        var coordinator = new InputCoordinator(options);
        for (int index = 0; index < 16; index++)
        {
            string promptId = $"p{index}";
            coordinator.OpenPrompt(new ConsolePrompt(promptId, ConsoleInputType.Text));
            Assert.Equal(ConsoleInputResultKind.Accepted,
                coordinator.Submit(new ConsoleInputCommand(promptId, $"m{index}", "ok")).Kind);
        }

        Assert.True(coordinator.ReceiptCount <= options.MaxInputReceiptCount);
        Assert.True(coordinator.WaiterCount <= options.MaxInputReceiptCount);
    }

    [Fact]
    public void RolloverReturnsSnapshotAndSubsequentDeltasThatReduceToCurrentState()
    {
        var store = new ConsoleStateStore(new ConsoleHistoryOptions
        {
            MaxVisibleNodes = 20,
            MaxVisibleTextLength = 100,
            MaxDeltaCount = 2,
            MaxEstimatedBytes = 10_000
        });
        store.Apply(new AppendNodesOperation([new TextNode("one")]));
        store.Apply(new AppendNodesOperation([new TextNode("two")]));
        store.Apply(new AppendNodesOperation([new TextNode("three")]));
        store.Apply(new AppendNodesOperation([new TextNode("four")]));

        var resume = Assert.IsType<ConsoleSnapshotWithDeltasResult>(store.ReadSince(0));
        Assert.Equal(3, resume.Snapshot.SnapshotSequence);
        Assert.Single(resume.EventsAfterSnapshot);

        List<ConsoleNode> reduced = resume.Snapshot.VisibleNodes.ToList();
        foreach (SequencedConsoleEvent item in resume.EventsAfterSnapshot)
        {
            switch (item.Operation)
            {
                case AppendNodesOperation append:
                    reduced.AddRange(append.Nodes);
                    break;
                case ClearConsoleOperation:
                    reduced.Clear();
                    break;
            }
        }

        Assert.Equal(
            store.Snapshot.VisibleNodes.Select(node => node is TextNode text ? text.Text : node.Kind.ToString()),
            reduced.Select(node => node is TextNode text ? text.Text : node.Kind.ToString()));
    }

    [Fact]
    public void ClearImmediatelyAdvancesEmptySnapshotBaseline()
    {
        var store = new ConsoleStateStore();
        store.Apply(new AppendNodesOperation([new TextNode("old")]));
        long beforeClear = store.CurrentSequence;
        store.Apply(new ClearConsoleOperation());

        Assert.Empty(store.History);
        Assert.Equal(store.CurrentSequence, store.BaselineSnapshot.SnapshotSequence);
        Assert.Empty(store.Snapshot.VisibleNodes);
        Assert.IsType<ConsoleSnapshotWithDeltasResult>(store.ReadSince(beforeClear));
    }
}
