using CloudEmuera.RuntimeAdapter;
using CloudEmuera.Worker;
using RuntimeColor = CloudEmuera.RuntimeAdapter.ConsoleColor;
using Xunit;

namespace CloudEmuera.Worker.IntegrationTests;

[Trait("Category", "ConsoleProtocol")]
public sealed class StructuredConsoleWireMapperTests
{
    [Fact]
    public void SnapshotMapperPreservesScrollbackSceneMediaAndPrompt()
    {
        var store = new ConsoleStateStore();
        store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine("line-1", [
                new TextNode("structured", new ConsoleTextStyle(
                    decorations: ConsoleFontStyle.Bold,
                    fontFamily: "noto-cjk",
                    fontSize: 18,
                    lineHeight: 24)),
                new SpriteNode("sprite-asset", new ConsoleRect(0, 0, 16, 16), new ConsoleRect(10, 20, 32, 32))
            ], ConsoleLineAlignment.Right)),
            ConsoleOperation.UpsertDrawable(new ShapeDrawable(
                "shape-1",
                ConsoleShapeKind.Rectangle,
                new ConsoleRect(0, 0, 100, 50),
                fill: RuntimeColor.FromRgb(1, 2, 3))),
            ConsoleOperation.SetMediaChannel(new MediaChannelState(
                "music",
                new ConsoleAssetId("audio-asset"),
                ConsoleMediaPlaybackState.Requested,
                loop: true,
                volume: 0.5f,
                revision: 4,
                ConsoleMediaStartPolicy.OnUserGesture)),
            ConsoleOperation.Open(new ConsolePrompt(
                "prompt-1",
                ConsoleInputType.AnyValue,
                promptText: "value",
                constraints: new AnyValueInputConstraints(64),
                timeoutAction: ConsolePromptTimeoutAction.ContinueWithoutValue,
                allowedSources: ConsoleInputSource.Keyboard | ConsoleInputSource.Button,
                openedAtUnixMilliseconds: 100,
                deadlineUnixMilliseconds: 1_100))
        ]));

        ConsoleSnapshot original = store.Snapshot;
        var wire = StructuredConsoleWireMapper.ToProto(original);
        ConsoleSnapshot roundTripped = StructuredConsoleWireMapper.FromProto(wire);

        Assert.Equal(original.SnapshotSequence, roundTripped.SnapshotSequence);
        Assert.Equal(original.Scrollback[0].LineId, roundTripped.Scrollback[0].LineId);
        Assert.Equal(ConsoleLineAlignment.Right, roundTripped.Scrollback[0].Alignment);
        Assert.Equal("noto-cjk", Assert.IsType<TextNode>(roundTripped.Scrollback[0].Nodes[0]).Style.FontFamily);
        Assert.IsType<SpriteNode>(roundTripped.Scrollback[0].Nodes[1]);
        Assert.IsType<ShapeDrawable>(Assert.Single(roundTripped.CanvasScene.Drawables));
        Assert.Equal(ConsoleMediaStartPolicy.OnUserGesture, Assert.Single(roundTripped.MediaState.Channels).StartPolicy);
        Assert.Equal("prompt-1", roundTripped.CurrentPrompt!.PromptId);
        Assert.Equal(ConsolePromptTimeoutAction.ContinueWithoutValue, roundTripped.CurrentPrompt.TimeoutAction);
    }
}
