using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.ConsoleContract;

[Trait("Category", "ConsoleContract")]
public sealed class ConsoleNodeValidationTests
{
    [Fact]
    public void NodesExposeTypedImmutableValues()
    {
        var children = new List<ConsoleNode> { new TextNode("Run") };
        var button = new ButtonNode(children, "run", tooltip: "Run the action");
        children.Add(new TextNode("mutated"));

        Assert.Single(button.Children);
        Assert.Equal("run", button.Value);

        var style = new ConsoleTextStyle(
            foreground: ConsoleColor.FromRgb(1, 2, 3),
            decorations: ConsoleFontStyle.Bold | ConsoleFontStyle.Underline);
        var text = new TextNode("styled", style);

        Assert.Equal(ConsoleFontStyle.Bold | ConsoleFontStyle.Underline, text.Style.Decorations);
        Assert.Equal((byte)1, text.Style.Foreground!.Value.Red);
    }

    [Fact]
    public void InvalidAssetIdsAndUnknownFontBitsAreRejected()
    {
        Assert.Throws<ConsoleContractException>(() => new ConsoleAssetId("https://example.invalid/image"));
        Assert.Throws<ConsoleContractException>(() => new ConsoleAssetId("../secret"));
        Assert.Throws<ConsoleContractException>(() => new ConsoleTextStyle(decorations: (ConsoleFontStyle)128));
        Assert.Throws<ConsoleContractException>(() => new ImageNode("hero", width: 0));
    }

    [Fact]
    public void ButtonLabelsMayContainStructuredPresentationNodes()
    {
        var button = new ButtonNode(
            new ConsoleNode[] { new ImageNode("hero"), new TextNode("Run") },
            "image");

        Assert.Collection(
            button.Children,
            node => Assert.IsType<ImageNode>(node),
            node => Assert.Equal("Run", Assert.IsType<TextNode>(node).Text));
    }

    [Fact]
    public void ButtonLabelsAllowTheDefaultLineBudgetButRejectLargerLabels()
    {
        // PLAY-002/PLAY-005: a button's flat label may contain one complete
        // line budget of styled segments, while the aggregate output remains bounded.
        ConsoleNode[] atLimit = Enumerable.Range(0, ConsoleContractLimits.Default.MaxButtonLabelNodeCount)
            .Select(_ => (ConsoleNode)new TextNode("x"))
            .ToArray();

        var button = new ButtonNode(atLimit, "choice");

        Assert.Equal(ConsoleContractLimits.Default.MaxButtonLabelNodeCount, button.Children.Count);
        ConsoleContractException exception = Assert.Throws<ConsoleContractException>(() =>
            new ButtonNode(atLimit.Append(new TextNode("x")), "choice"));
        Assert.Equal(ConsoleContractViolationReason.TooManyButtonLabelNodes, exception.Reason);
    }

    [Fact]
    public void ButtonValuesAndPresentationTextUseTheInputTextBudget()
    {
        // PLAY-002/PLAY-007: original Emuera accepts arbitrary string button
        // values. The structured representation keeps a bounded value that is
        // still large enough for every legal text input payload.
        string atLimit = new('x', ConsoleContractLimits.Default.MaxInputValueLength);

        var button = new ButtonNode([new TextNode("choice")], atLimit, tooltip: atLimit);
        var image = new ImageNode("asset", altText: atLimit);

        Assert.Equal(atLimit, button.Value);
        Assert.Equal(atLimit, button.Tooltip);
        Assert.Equal(atLimit, image.AltText);
    }

    [Fact]
    public void NestedPresentationNodesUseTheIpcDepthBudget()
    {
        ConsoleNode withinLimit = CreateNestedDivs(ConsoleContractLimits.Default.MaxNodeDepth - 1);
        ConsoleNode overLimit = CreateNestedDivs(ConsoleContractLimits.Default.MaxNodeDepth);

        var store = new ConsoleStateStore();
        store.Apply(new AppendNodesOperation([withinLimit]));

        ConsoleContractException exception = Assert.Throws<ConsoleContractException>(() =>
            store.Apply(new AppendNodesOperation([overLimit])));
        Assert.Equal(ConsoleContractViolationReason.NodeTooDeep, exception.Reason);
    }

    [Fact]
    public void EmptyButtonValuesRemainValidEmueraInput()
    {
        var button = new ButtonNode("Unchanged", string.Empty);
        var store = new ConsoleStateStore();

        store.Apply(new AppendNodesOperation([button]));

        Assert.Equal(string.Empty, Assert.IsType<ButtonNode>(Assert.Single(store.Snapshot.VisibleNodes)).Value);
    }

    [Fact]
    public void StoreDefensivelyCopiesAppendCollections()
    {
        var nodes = new List<ConsoleNode> { new TextNode("before") };
        var operation = new AppendNodesOperation(nodes);
        nodes[0] = new TextNode("after");

        var store = new ConsoleStateStore();
        store.Apply(operation);

        var text = Assert.IsType<TextNode>(Assert.Single(store.Snapshot.VisibleNodes));
        Assert.Equal("before", text.Text);
    }

    private static ConsoleNode CreateNestedDivs(int count)
    {
        ConsoleNode node = new TextNode("x");
        for (int index = 0; index < count; index++)
            node = new DivNode([node], new ConsoleRect(0, 0, 1, 1));
        return node;
    }
}
