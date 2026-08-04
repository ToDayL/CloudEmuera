using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.ConsoleContract;

[Trait("Category", "ConsoleContract")]
public sealed class ConsoleSequenceTests
{
    [Fact]
    public void AcceptedOperationsUseOneStrictSequencePerOperation()
    {
        var store = new ConsoleStateStore();
        SequencedConsoleEvent first = store.Apply(new AppendNodesOperation([new TextNode("one")]));
        SequencedConsoleEvent second = store.Apply(new AppendNodesOperation([new TextNode("two"), LineBreakNode.Instance]));
        SequencedConsoleEvent third = store.Apply(new ClearConsoleOperation());

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(3, third.Sequence);
        Assert.Equal(3, store.CurrentSequence);
    }

    [Fact]
    public void RejectedOperationDoesNotConsumeSequence()
    {
        var limits = new ConsoleContractLimits { MaxTextLength = 2 };
        var store = new ConsoleStateStore(new ConsoleHistoryOptions { ContractLimits = limits });

        ConsoleContractException exception = Assert.Throws<ConsoleContractException>(
            () => store.Apply(new AppendNodesOperation([new TextNode("okay")])))
            ;
        Assert.Equal(ConsoleContractViolationReason.TextTooLong, exception.Reason);

        SequencedConsoleEvent accepted = store.Apply(new AppendNodesOperation([new TextNode("x")]));
        Assert.Equal(1, accepted.Sequence);
    }

    [Fact]
    public void FailedSequenceOverflowDoesNotWrap()
    {
        var store = new ConsoleStateStore();
        typeof(ConsoleStateStore)
            .GetField("currentSequence", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(store, long.MaxValue);

        var exception = Assert.Throws<ConsoleContractException>(() => store.Apply(new ClearConsoleOperation()));
        Assert.Equal(ConsoleContractViolationReason.SequenceExhausted, exception.Reason);
        Assert.Equal(long.MaxValue, store.CurrentSequence);
    }
}
