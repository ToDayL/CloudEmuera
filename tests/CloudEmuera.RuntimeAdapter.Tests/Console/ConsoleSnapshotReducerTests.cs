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

    [Fact]
    public void RandomizedReductionMatchesTheAuthoritativeStore()
    {
        var random = new Random(20260813);
        var store = new ConsoleStateStore();
        var transactions = new List<SequencedConsoleTransaction>();
        var lineIds = new List<string>();

        for (int index = 0; index < 250; index++)
        {
            var operations = new List<ConsoleOperation>();
            int operationCount = random.Next(1, 4);
            for (int offset = 0; offset < operationCount; offset++)
            {
                switch (random.Next(5))
                {
                    case 0:
                    {
                        string lineId = $"line-{index}-{offset}";
                        operations.Add(ConsoleOperation.AppendLine(
                            new ConsoleLine(lineId, [new TextNode(RandomText(random, 1, 40))])));
                        lineIds.Add(lineId);
                        break;
                    }
                    case 1:
                        operations.Add(ConsoleOperation.SetWindow(
                            new WindowMetadata($"title-{index}", random.Next(1, 1920), random.Next(1, 1080))));
                        break;
                    case 2:
                        operations.Add(ConsoleOperation.UpsertBackground(
                            new BackgroundLayer(
                                $"bg-{index % 8}",
                                new ConsoleAssetId($"asset-{index}-{offset}"),
                                (ConsoleBackgroundMode)random.Next(0, 5),
                                1f,
                                index)));
                        break;
                    case 3 when lineIds.Count != 0:
                    {
                        string target = lineIds[random.Next(lineIds.Count)];
                        operations.Add(ConsoleOperation.ReplaceLine(
                            new ConsoleLine(target, [new TextNode(RandomText(random, 1, 20))])));
                        break;
                    }
                    default:
                        operations.Add(ConsoleOperation.UpsertDrawable(
                            new ShapeDrawable(
                                $"d-{index}-{offset}",
                                ConsoleShapeKind.Rectangle,
                                new ConsoleRect(0, 0, 10, 10),
                                new ConsoleColor(1, 2, 3),
                                zIndex: index)));
                        break;
                }
            }

            transactions.Add(store.ApplyTransaction(new ConsoleTransaction(operations)));
        }

        ConsoleSnapshot reduced = ConsoleSnapshotReducer.ApplyBatch(
            ConsoleSnapshot.Empty,
            transactions,
            ConsoleHistoryOptions.Default);
        ConsoleSnapshot authoritative = store.StructuredSnapshot;

        Assert.Equal(authoritative.SnapshotSequence, reduced.SnapshotSequence);
        Assert.Equal(ProjectLines(authoritative), ProjectLines(reduced));
        Assert.Equal(ProjectBackgrounds(authoritative), ProjectBackgrounds(reduced));
        Assert.Equal(ProjectDrawables(authoritative), ProjectDrawables(reduced));
        Assert.Equal(authoritative.WindowMetadata.Title, reduced.WindowMetadata.Title);
        Assert.Equal(authoritative.WindowMetadata.ViewportWidth, reduced.WindowMetadata.ViewportWidth);
        Assert.Equal(authoritative.WindowMetadata.ViewportHeight, reduced.WindowMetadata.ViewportHeight);
        Assert.Equal(authoritative.Truncation, reduced.Truncation);
    }

    private static string RandomText(Random random, int minimumLength, int maximumLength)
    {
        int length = random.Next(minimumLength, maximumLength + 1);
        var builder = new System.Text.StringBuilder(length);
        for (int index = 0; index < length; index++)
            builder.Append((char)('a' + random.Next(26)));
        return builder.ToString();
    }

    private static object[] ProjectLines(ConsoleSnapshot snapshot) =>
        snapshot.Scrollback
            .Select(line => new
            {
                line.LineId,
                line.Alignment,
                line.Temporary,
                Text = string.Concat(line.Nodes.OfType<TextNode>().Select(node => node.Text))
            })
            .ToArray();

    private static object[] ProjectBackgrounds(ConsoleSnapshot snapshot) =>
        snapshot.BackgroundLayers
            .Select(layer => new
            {
                layer.LayerId,
                Asset = layer.AssetId.Value,
                layer.Mode,
                layer.Opacity,
                layer.Depth
            })
            .ToArray();

    private static object[] ProjectDrawables(ConsoleSnapshot snapshot) =>
        snapshot.CanvasScene.Drawables
            .Select(drawable => new
            {
                drawable.DrawableId,
                drawable.ZIndex,
                drawable.Bounds
            })
            .ToArray();
}
