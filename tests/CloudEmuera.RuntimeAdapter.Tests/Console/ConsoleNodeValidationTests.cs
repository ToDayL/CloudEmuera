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
    public void ButtonLabelsCannotContainBehaviorOrNestedNodes()
    {
        Assert.Throws<ConsoleContractException>(() =>
            new ButtonNode(
                new ConsoleNode[] { new ImageNode("hero") },
                "image"));
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
}
