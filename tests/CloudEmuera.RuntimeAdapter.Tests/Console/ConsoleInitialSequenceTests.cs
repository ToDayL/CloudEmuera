using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.ConsoleContract;

[Trait("Category", "ConsoleContract")]
public sealed class ConsoleInitialSequenceTests
{
    [Fact]
    public void FreshConsoleStartsAfterPersistedSequenceAndDoesNotReplayHistory()
    {
        var store = new ConsoleStateStore();
        store.InitializeSequence(41);

        Assert.Equal(41, store.CurrentSequence);
        Assert.Empty(store.History);

        SequencedConsoleEvent emitted = store.Apply(new AppendNodesOperation(
            [new TextNode("new output")]));

        Assert.Equal(42, emitted.Sequence);
        ConsoleResumeResult resumed = store.ReadSince(41);
        ConsoleDeltaBatchResult delta = Assert.IsType<ConsoleDeltaBatchResult>(resumed);
        Assert.Single(delta.Events);
        Assert.Equal(42, delta.Events[0].Sequence);
    }

    [Fact]
    public void SequenceCannotBeReinitializedAfterOutput()
    {
        var store = new ConsoleStateStore();
        store.Apply(new ClearConsoleOperation());

        Assert.Throws<InvalidOperationException>(() => store.InitializeSequence(5));
    }
}
