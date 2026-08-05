using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.ConsoleContract;

[Trait("Category", "ConsoleContract")]
public sealed class ConsoleSnapshotTests
{
    [Fact]
    public void ClosingPromptPreservesVisibleOutput()
    {
        var store = new ConsoleStateStore();
        store.Apply(new AppendNodesOperation([new TextNode("before-input"), LineBreakNode.Instance]));
        store.Apply(new OpenPromptOperation(new ConsolePrompt("p1", ConsoleInputType.Integer)));

        store.Apply(new ClosePromptOperation("p1", ConsolePromptCloseReason.InputAccepted));

        Assert.Null(store.Snapshot.CurrentPrompt);
        Assert.Contains(store.Snapshot.VisibleNodes, node => node is TextNode text && text.Text == "before-input");
    }

    [Fact]
    public void VisibleLimitsTrimOldTopLevelNodesAndKeepPrompt()
    {
        var store = new ConsoleStateStore(new ConsoleHistoryOptions
        {
            MaxVisibleNodes = 2,
            MaxVisibleTextLength = 100,
            MaxDeltaCount = 50,
            MaxEstimatedBytes = 10_000
        });

        store.Apply(new AppendNodesOperation([new TextNode("one"), new TextNode("two")]));
        store.Apply(new OpenPromptOperation(new ConsolePrompt("p1", ConsoleInputType.Text, "Answer")));
        store.Apply(new AppendNodesOperation([new TextNode("three")]));

        ConsoleSnapshot snapshot = store.Snapshot;
        Assert.Equal("two", Assert.IsType<TextNode>(snapshot.VisibleNodes[0]).Text);
        Assert.Equal("three", Assert.IsType<TextNode>(snapshot.VisibleNodes[1]).Text);
        Assert.Equal("p1", snapshot.CurrentPrompt!.PromptId);
        Assert.True(snapshot.WasTruncated);
        Assert.True(snapshot.DroppedNodeCount >= 1);
    }

    [Fact]
    public void ReadSinceReturnsDeltasInWindowAndSnapshotOutsideWindow()
    {
        var store = new ConsoleStateStore(new ConsoleHistoryOptions
        {
            MaxVisibleNodes = 20,
            MaxVisibleTextLength = 100,
            MaxDeltaCount = 3,
            MaxEstimatedBytes = 10_000
        });

        store.Apply(new AppendNodesOperation([new TextNode("one")]));
        store.Apply(new AppendNodesOperation([new TextNode("two")]));
        long cursor = store.CurrentSequence;
        store.Apply(new AppendNodesOperation([new TextNode("three")]));

        ConsoleResumeResult delta = store.ReadSince(cursor);
        var deltaBatch = Assert.IsType<ConsoleDeltaBatchResult>(delta);
        Assert.Equal(cursor, deltaBatch.FromExclusive);
        Assert.Single(deltaBatch.Events);

        ConsoleResumeResult old = store.ReadSince(0);
        var snapshot = Assert.IsType<ConsoleSnapshotWithDeltasResult>(old);
        Assert.Equal(0, snapshot.Snapshot.SnapshotSequence);
        Assert.Equal(3, snapshot.EventsAfterSnapshot.Count);
        long replayedTextLength = snapshot.Snapshot.VisibleTextLength + snapshot.EventsAfterSnapshot
            .Sum(item => item.Operation is AppendNodesOperation append
                ? append.Nodes.Sum(node => node is TextNode text ? text.Text.Length : 0)
                : 0);
        Assert.Equal(store.Snapshot.VisibleTextLength, replayedTextLength);
    }

    [Fact]
    public void InvalidCursorsAreRejected()
    {
        var store = new ConsoleStateStore();
        Assert.Throws<ConsoleContractException>(() => store.ReadSince(-1));
        Assert.Throws<ConsoleContractException>(() => store.ReadSince(1));
    }
}
