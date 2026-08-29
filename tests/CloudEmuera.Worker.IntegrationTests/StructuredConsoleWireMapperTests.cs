using CloudEmuera.RuntimeAdapter;
using CloudEmuera.Worker;
using CloudEmuera.Realtime;
using RuntimeColor = CloudEmuera.RuntimeAdapter.ConsoleColor;
using Xunit;

namespace CloudEmuera.Worker.IntegrationTests;

[Trait("Category", "ConsoleProtocol")]
public sealed class StructuredConsoleWireMapperTests
{
    [Fact]
    public void StructuredHtmlIslandNodesRoundTripWithoutFallingBackToLegacyHtml()
    {
        var original = new HtmlIslandNode(
            [
                new TextNode("island"),
                LineBreakNode.Instance,
                new ButtonNode([new TextNode("go")], "go", generation: 4),
                new ShapeNode(ConsoleShapeKind.Rectangle, new ConsoleRect(0, 0, 4, 8))
            ],
            new ConsoleRect(1, 2, 30, 20));

        ConsoleNode roundTripped = StructuredConsoleWireMapper.FromProto(
            StructuredConsoleWireMapper.ToProto(original));

        HtmlIslandNode island = Assert.IsType<HtmlIslandNode>(roundTripped);
        Assert.True(island.IsStructured);
        Assert.Equal(island.Layout, new ConsoleRect(1, 2, 30, 20));
        Assert.Collection(
            island.StructuredNodes!,
            node => Assert.Equal("island", Assert.IsType<TextNode>(node).Text),
            node => Assert.IsType<LineBreakNode>(node),
            node => Assert.Equal(4, Assert.IsType<ButtonNode>(node).Generation),
            node => Assert.IsType<ShapeNode>(node));
    }

    [Fact]
    public void NegativePositionedSegmentRoundTripsForEmueraViewportClipping()
    {
        var original = new PositionedInlineSegmentNode(
            -60,
            300,
            [new TextNode("cutin")],
            new ConsoleInlineAction("2000"));

        ConsoleNode roundTripped = StructuredConsoleWireMapper.FromProto(
            StructuredConsoleWireMapper.ToProto(original));

        PositionedInlineSegmentNode segment = Assert.IsType<PositionedInlineSegmentNode>(roundTripped);
        Assert.Equal(-60, segment.PositionX);
        Assert.Equal(300, segment.MeasuredWidth);
        Assert.Equal("2000", segment.Action?.Value);
    }

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
                    lineHeight: 24,
                    buttonColor: RuntimeColor.FromRgb(4, 5, 6))),
                new ButtonNode([new TextNode("choice")], "choice", positionX: 42),
                new DivNode(
                    [new TextNode("inside")],
                    new ConsoleRect(1, 2, 30, 12),
                    zIndex: 3,
                    background: RuntimeColor.FromRgb(7, 8, 9),
                    isRelative: false,
                    box: new ConsoleBoxModel(
                        new ConsoleInsets(1, 2, 3, 4),
                        new ConsoleInsets(5, 6, 7, 8),
                        new ConsoleInsets(1, 1, 1, 1),
                        new ConsoleInsets(2, 3, 4, 5),
                        [RuntimeColor.FromRgb(10, 11, 12), null, RuntimeColor.FromRgb(13, 14, 15), null])),
                new SpriteNode(
                    new ConsoleAssetId("sprite-asset"),
                    new ConsoleRect(0, 0, 16, 16),
                    new ConsoleRect(10, 20, 32, 32),
                    hoverAssetId: new ConsoleAssetId("sprite-hover"),
                    hoverSourceRect: new ConsoleRect(16, 0, 16, 16),
                    mappingAssetId: new ConsoleAssetId("sprite-map"),
                    mappingSourceRect: new ConsoleRect(0, 0, 32, 32),
                    animationFrames:
                    [
                        new SpriteAnimationFrame(
                            new ConsoleAssetId("sprite-frame"),
                            new ConsoleRect(0, 0, 16, 16),
                            new ConsolePoint(2, 3),
                            75)
                    ])
            ], ConsoleLineAlignment.Right)),
            ConsoleOperation.UpsertDrawable(new ShapeDrawable(
                "shape-1",
                ConsoleShapeKind.Rectangle,
                new ConsoleRect(0, 0, 100, 50),
                fill: RuntimeColor.FromRgb(1, 2, 3))),
            ConsoleOperation.UpsertDrawable(new SpriteDrawable(
                "sprite-drawable",
                new ConsoleAssetId("sprite-drawable-asset"),
                new ConsoleRect(0, 0, 8, 8),
                new ConsoleRect(4, 5, 8, 8),
                animationFrames:
                [
                    new SpriteAnimationFrame(
                        new ConsoleAssetId("sprite-drawable-frame"),
                        new ConsoleRect(8, 0, 8, 8),
                        new ConsolePoint(1, 2),
                        60)
                ])),
            ConsoleOperation.UpsertDrawable(new RasterDrawable(
                "raster-1",
                [137, 80, 78, 71, 13, 10, 26, 10],
                new ConsoleRect(2, 3, 4, 5),
                zIndex: 7,
                hoverPngData: [137, 80, 78, 71, 13, 10, 26, 10])),
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
        Assert.Equal(RuntimeColor.FromRgb(4, 5, 6), Assert.IsType<TextNode>(roundTripped.Scrollback[0].Nodes[0]).Style.ButtonColor);
        Assert.Equal(42, Assert.IsType<ButtonNode>(roundTripped.Scrollback[0].Nodes[1]).PositionX);
        DivNode div = Assert.IsType<DivNode>(roundTripped.Scrollback[0].Nodes[2]);
        Assert.False(div.IsRelative);
        Assert.Equal(RuntimeColor.FromRgb(7, 8, 9), div.Background);
        Assert.Equal(RuntimeColor.FromRgb(10, 11, 12), div.Box!.BorderColors[0]);
        Assert.Null(div.Box.BorderColors[1]);
        Assert.Equal(RuntimeColor.FromRgb(13, 14, 15), div.Box.BorderColors[2]);
        Assert.Null(div.Box.BorderColors[3]);
        SpriteNode sprite = Assert.IsType<SpriteNode>(roundTripped.Scrollback[0].Nodes[3]);
        Assert.Equal("sprite-hover", sprite.HoverAssetId?.Value);
        Assert.Equal(new ConsoleRect(16, 0, 16, 16), sprite.HoverSourceRect);
        Assert.Equal("sprite-map", sprite.MappingAssetId?.Value);
        SpriteAnimationFrame frame = Assert.Single(sprite.AnimationFrames);
        Assert.Equal(75, frame.DurationMilliseconds);
        Assert.Equal(new ConsolePoint(2, 3), frame.Offset);
        Assert.Collection(
            roundTripped.CanvasScene.Drawables.OrderBy(item => item.DrawableId, StringComparer.Ordinal),
            raster => Assert.IsType<RasterDrawable>(raster),
            shape => Assert.IsType<ShapeDrawable>(shape),
            drawable =>
            {
                SpriteAnimationFrame drawableFrame = Assert.Single(Assert.IsType<SpriteDrawable>(drawable).AnimationFrames);
                Assert.Equal(60, drawableFrame.DurationMilliseconds);
                Assert.Equal(new ConsolePoint(1, 2), drawableFrame.Offset);
            });
        Assert.NotNull(Assert.IsType<RasterDrawable>(roundTripped.CanvasScene.Drawables.Single(item => item.DrawableId == "raster-1")).HoverPngData);
        Assert.Equal(ConsoleMediaStartPolicy.OnUserGesture, Assert.Single(roundTripped.MediaState.Channels).StartPolicy);
        Assert.Equal("prompt-1", roundTripped.CurrentPrompt!.PromptId);
        Assert.Equal(ConsolePromptTimeoutAction.ContinueWithoutValue, roundTripped.CurrentPrompt.TimeoutAction);
    }
}
