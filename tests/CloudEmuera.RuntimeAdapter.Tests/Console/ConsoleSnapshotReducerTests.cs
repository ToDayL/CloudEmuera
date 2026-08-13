using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.ConsoleContract;

[Trait("Category", "Snapshot")]
[Trait("Category", "ConsoleContract")]
public sealed class ConsoleSnapshotReducerTests
{
    [Fact]
    public void ApplyBatchReconstructsTheCompleteStructuredState()
    {
        var source = new ConsoleStateStore();
        SequencedConsoleTransaction first = source.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine("line-1", [new TextNode("hello")]))
        ]));
        SequencedConsoleTransaction second = source.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.SetWindow(new WindowMetadata("Cloud", 800, 600)),
            ConsoleOperation.AppendLine(new ConsoleLine("line-2", [new TextNode("world")]))
        ]));

        ConsoleSnapshot reduced = ConsoleSnapshotReducer.ApplyBatch(
            ConsoleSnapshot.Empty,
            [first, second],
            ConsoleHistoryOptions.Default);

        Assert.Equal(source.StructuredSnapshot.SnapshotSequence, reduced.SnapshotSequence);
        Assert.Equal(source.StructuredSnapshot.Scrollback, reduced.Scrollback);
        Assert.Equal(source.StructuredSnapshot.WindowMetadata, reduced.WindowMetadata);
        Assert.Equal(source.StructuredSnapshot.VisibleNodes, reduced.VisibleNodes);
    }

    [Fact]
    public void ApplyBatchRejectsASequenceGapWithoutMutatingTheBaseline()
    {
        ConsoleSnapshot baseline = new ConsoleSnapshot(
            4,
            [new ConsoleLine("line-1", [new TextNode("stable")])]);
        var invalid = new SequencedConsoleTransaction(
            6,
            new ConsoleTransaction([
                ConsoleOperation.SetWindow(new WindowMetadata("must-not-apply"))
            ]));

        Assert.Throws<ConsoleContractException>(() =>
            ConsoleSnapshotReducer.ApplyBatch(baseline, [invalid], ConsoleHistoryOptions.Default));

        Assert.Equal(4, baseline.SnapshotSequence);
        Assert.Equal("stable", Assert.IsType<TextNode>(baseline.Scrollback[0].Nodes[0]).Text);
        Assert.Equal(string.Empty, baseline.WindowMetadata.Title);
    }

    [Fact]
    public void ApplyBatchIsAtomicWhenAnOperationFailsAfterAValidOperation()
    {
        var invalid = new SequencedConsoleTransaction(
            1,
            new ConsoleTransaction([
                ConsoleOperation.SetWindow(new WindowMetadata("temporary")),
                ConsoleOperation.DeleteLines(["missing"])
            ]));

        ConsoleContractException exception = Assert.Throws<ConsoleContractException>(() =>
            ConsoleSnapshotReducer.ApplyBatch(ConsoleSnapshot.Empty, [invalid], ConsoleHistoryOptions.Default));

        Assert.Equal(ConsoleContractViolationReason.InvalidIdentifier, exception.Reason);
    }

    [Fact]
    public void ApplyBatchFailsClosedAtTheMaximumSequence()
    {
        var transaction = new SequencedConsoleTransaction(
            long.MaxValue,
            new ConsoleTransaction([
                ConsoleOperation.ClearScrollback()
            ]));

        ConsoleContractException exception = Assert.Throws<ConsoleContractException>(() =>
            ConsoleSnapshotReducer.ApplyBatch(
                new ConsoleSnapshot(long.MaxValue, Array.Empty<ConsoleLine>()),
                [transaction],
                ConsoleHistoryOptions.Default));

        Assert.Equal(ConsoleContractViolationReason.SequenceExhausted, exception.Reason);
    }

    [Fact]
    public void ApplyBatchHonorsInjectedTransactionOperationLimits()
    {
        ConsoleHistoryOptions options = ConsoleHistoryOptions.Default with
        {
            ContractLimits = ConsoleContractLimits.Default with { MaxTransactionOperations = 1 }
        };
        var transaction = new SequencedConsoleTransaction(
            1,
            new ConsoleTransaction([
                ConsoleOperation.ClearScrollback(),
                ConsoleOperation.SetWindow(new WindowMetadata("too-many"))
            ]));

        ConsoleContractException exception = Assert.Throws<ConsoleContractException>(() =>
            ConsoleSnapshotReducer.ApplyBatch(ConsoleSnapshot.Empty, [transaction], options));

        Assert.Equal(ConsoleContractViolationReason.BatchTooLarge, exception.Reason);
    }

    [Fact]
    public void ApplyBatchHonorsInjectedNodeBatchLimits()
    {
        ConsoleHistoryOptions options = ConsoleHistoryOptions.Default with
        {
            ContractLimits = ConsoleContractLimits.Default with { MaxBatchNodeCount = 1 }
        };
        var transaction = new SequencedConsoleTransaction(
            1,
            new ConsoleTransaction([
                ConsoleOperation.Append([new TextNode("one"), new TextNode("two")])
            ]));

        ConsoleContractException exception = Assert.Throws<ConsoleContractException>(() =>
            ConsoleSnapshotReducer.ApplyBatch(ConsoleSnapshot.Empty, [transaction], options));

        Assert.Equal(ConsoleContractViolationReason.BatchTooLarge, exception.Reason);
    }

    [Fact]
    public void StructuredEmptyLinesWithLegacyLookingIdsAreNotDiscarded()
    {
        ConsoleSnapshot baseline = new ConsoleSnapshot(
            0,
            [new ConsoleLine("legacy-user-line", Array.Empty<ConsoleNode>())]);
        var transaction = new SequencedConsoleTransaction(
            1,
            new ConsoleTransaction([
                ConsoleOperation.SetWindow(new WindowMetadata("preserve-line"))
            ]));

        ConsoleSnapshot result = ConsoleSnapshotReducer.ApplyBatch(
            baseline,
            [transaction],
            ConsoleHistoryOptions.Default);

        Assert.Contains(result.Scrollback, line => line.LineId == "legacy-user-line");
    }
}
