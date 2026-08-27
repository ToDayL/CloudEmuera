using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.ConsoleContract;

[Trait("Category", "Tooltip")]
[Trait("Category", "ConsoleContract")]
public sealed class ConsoleTooltipReferenceIndexTests
{
    private static readonly byte[] MinimalPng = [137, 80, 78, 71, 13, 10, 26, 10];

    [Fact]
    public void ChangedLinesAndHitRegionsMaintainIncrementalReferenceCounts()
    {
        var store = new ConsoleStateStore();
        store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine("line", [
                new ButtonNode("one", "1", tooltip: "7"),
                new PositionedInlineSegmentNode(0, 10, [new TextNode("two")],
                    new ConsoleInlineAction("2", tooltip: "7"))
            ])),
            ConsoleOperation.UpsertHitRegion(new HitRegion(
                "region", new ConsoleRect(0, 0, 10, 10), "3", tooltip: "9"))
        ]));

        Assert.Equal(2, store.TooltipGraphicsReferences[7]);
        Assert.Equal(1, store.TooltipGraphicsReferences[9]);

        store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.ReplaceLine(new ConsoleLine("line", [
                new ButtonNode("replacement", "4", tooltip: "9")
            ])),
            ConsoleOperation.RemoveHitRegion("region")
        ]));

        Assert.False(store.TooltipGraphicsReferences.ContainsKey(7));
        Assert.Equal(1, store.TooltipGraphicsReferences[9]);
    }

    [Fact]
    public void RemovingLastVisibleReferenceRequiresExplicitResourceDeltaAndKeepsMirrorsEqual()
    {
        var store = new ConsoleStateStore();
        var mirror = new ConsoleStateStore();
        var seed = new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine("line", [
                new ButtonNode("choice", "1", tooltip: "5")
            ])),
            ConsoleOperation.SetTooltipPresentation(new ConsoleTooltipPresentation(imageMode: true, revision: 1)),
            ConsoleOperation.UpsertTooltipResource(new ConsoleTooltipResource(5, MinimalPng, 1, 1, revision: 1))
        ]);
        store.ApplyTransaction(seed);
        mirror.ApplyTransaction(seed);

        var removeLine = new ConsoleTransaction([
            ConsoleOperation.DeleteLines(["line"])
        ]);
        store.ApplyTransaction(removeLine);
        mirror.ApplyTransaction(removeLine);

        Assert.Empty(store.TooltipGraphicsReferences);
        Assert.Single(store.Snapshot.TooltipResources);

        var removeResource = new ConsoleTransaction([ConsoleOperation.RemoveTooltipResource(5)]);
        store.ApplyTransaction(removeResource);
        mirror.ApplyTransaction(removeResource);
        Assert.Empty(store.Snapshot.TooltipResources);
        Assert.Equal(store.Snapshot.TooltipResources, mirror.Snapshot.TooltipResources);
    }

    [Fact]
    public void DisablingImageModeRequiresExplicitClearDelta()
    {
        var store = new ConsoleStateStore();
        store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.SetTooltipPresentation(new ConsoleTooltipPresentation(imageMode: true, revision: 1)),
            ConsoleOperation.UpsertTooltipResource(new ConsoleTooltipResource(5, MinimalPng, 1, 1, revision: 1))
        ]));

        store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.SetTooltipPresentation(new ConsoleTooltipPresentation(imageMode: false, revision: 2))
        ]));

        Assert.Single(store.Snapshot.TooltipResources);
        store.ApplyTransaction(new ConsoleTransaction([ConsoleOperation.ClearTooltipResources()]));
        Assert.Empty(store.Snapshot.TooltipResources);
    }

    [Fact]
    public void ScrollbackEvictionDropsOnlyReferencesOwnedByEvictedLines()
    {
        ConsoleHistoryOptions options = ConsoleHistoryOptions.Default with
        {
            ContractLimits = ConsoleContractLimits.Default with { MaxScrollbackLines = 1 }
        };
        var store = new ConsoleStateStore(options);
        store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine("old", [new ButtonNode("old", "1", tooltip: "3")]))
        ]));
        store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine("new", [new ButtonNode("new", "2", tooltip: "4")]))
        ]));

        Assert.False(store.TooltipGraphicsReferences.ContainsKey(3));
        Assert.Equal(1, store.TooltipGraphicsReferences[4]);
    }

    [Fact]
    public void UnrelatedTransactionsDoNotRequeueEveryVisibleGraphicsReference()
    {
        var store = new ConsoleStateStore();
        store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine("line", [
                new ButtonNode("choice", "1", tooltip: "7")
            ]))
        ]));
        Assert.Equal([7], store.TakeTooltipProjectionCandidates());

        store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.SetWindow(new WindowMetadata(title: "unchanged references"))
        ]));

        Assert.Empty(store.TakeTooltipProjectionCandidates());
    }
}
