using CloudEmuera.RuntimeAdapter;
using CloudEmuera.RuntimeAdapter.Tests.Time;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.ConsoleContract;

[Trait("Category", "ConsoleContract")]
public sealed class ConsoleTranscriptTests
{
    [Theory]
    [InlineData("v18", "V18-BOOT", "<b>V18-HTML</b>", "v18-prompt", "7", "V18-AFTER")]
    [InlineData("em-ee", "EMEE-BOOT", "<i>EMEE-HTML</i>", "emee-prompt", "42", "EMEE-AFTER")]
    public void SyntheticFixtureCallsProduceStableStructuredTranscript(
        string profile,
        string boot,
        string html,
        string promptId,
        string input,
        string after)
    {
        var console = new StructuredGameConsole(new ManualRuntimeClock(), new FixedPromptIdGenerator(promptId));
        var parser = new EmueraHtmlParser();
        console.Emit(new AppendNodesOperation([new TextNode(boot)]));
        console.Emit(new AppendNodesOperation(parser.Parse(html)));
        console.Emit(new AppendNodesOperation([new ButtonNode("Continue", "continue")]));
        console.Emit(new OpenPromptOperation(new ConsolePrompt(ConsoleInputType.Integer)));
        string assignedPromptId = console.CurrentPrompt!.PromptId;
        ConsoleInputResult result = console.SubmitInput(new ConsoleInputCommand(assignedPromptId, "fixed-client", input));
        console.Emit(new AppendNodesOperation([new TextNode(after)]));

        Assert.Equal(ConsoleInputResultKind.Accepted, result.Kind);
        Assert.Equal(promptId, assignedPromptId);
        ConsoleResumeResult resume = console.ReadSince(0);
        var snapshot = Assert.IsType<ConsoleSnapshotWithDeltasResult>(resume);
        Assert.Equal(0, snapshot.Snapshot.SnapshotSequence);
        Assert.Equal(6, snapshot.EventsAfterSnapshot.Count);
        Assert.Equal(Enumerable.Range(1, 6).Select(value => (long)value), snapshot.EventsAfterSnapshot.Select(item => item.Sequence));
        Assert.Equal(profile == "v18" ? ConsoleFontStyle.Bold : ConsoleFontStyle.Italic,
            Assert.IsType<TextNode>(Assert.IsType<AppendNodesOperation>(snapshot.EventsAfterSnapshot[1].Operation).Nodes[0]).Style.Decorations);
    }

    private sealed class FixedPromptIdGenerator(string id) : IPromptIdGenerator
    {
        public string Next() => id;
    }
}
