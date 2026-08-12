using CloudEmuera.RuntimeAdapter;
using CloudEmuera.RuntimeAdapter.Tests.Time;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.ConsoleContract;

[Trait("Category", "RichOutput")]
[Trait("Category", "ConsoleContract")]
public sealed class StructuredConsoleContractTests
{
    [Fact]
    public void RasterDrawableRejectsNonPngAndCombinedPayloadOverLimit()
    {
        ConsoleContractException nonPng = Assert.Throws<ConsoleContractException>(() =>
            new RasterDrawable("raster", new byte[8], new ConsoleRect(0, 0, 1, 1)));
        Assert.Equal(ConsoleContractViolationReason.InvalidImagePayload, nonPng.Reason);

        byte[] largePng = new byte[ConsoleContractLimits.Default.MaxInlineRasterBytes / 2 + 1];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(largePng, 0);
        ConsoleContractException oversized = Assert.Throws<ConsoleContractException>(() =>
            new RasterDrawable("raster", largePng, new ConsoleRect(0, 0, 1, 1), hoverPngData: largePng));
        Assert.Equal(ConsoleContractViolationReason.ImageTooLarge, oversized.Reason);
    }

    [Fact]
    public void RichTransactionIsAtomicAndPublishesOneSequence()
    {
        var store = new ConsoleStateStore();
        var line = new ConsoleLine(
            "line-1",
            [new TextNode("CJK", new ConsoleTextStyle(fontFamily: "noto-cjk", fontSize: 18, lineHeight: 24))],
            ConsoleLineAlignment.Center,
            temporary: true);
        var sprite = new SpriteDrawable(
            "sprite-1",
            new ConsoleAssetId("asset-sprite"),
            new ConsoleRect(0, 0, 32, 32),
            new ConsoleRect(10, 20, 64, 64),
            zIndex: 4);

        SequencedConsoleTransaction applied = store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.AppendLine(line),
            ConsoleOperation.UpsertBackground(new BackgroundLayer("bg-1", new ConsoleAssetId("asset-bg"))),
            ConsoleOperation.UpsertDrawable(sprite),
            ConsoleOperation.UpsertHitRegion(new HitRegion("hit-1", new ConsoleRect(10, 20, 64, 64), "7", tooltip: "go")),
            ConsoleOperation.SetMediaChannel(new MediaChannelState(
                "music",
                new ConsoleAssetId("asset-audio"),
                ConsoleMediaPlaybackState.Requested,
                loop: true,
                volume: 0.5f,
                revision: 1,
                ConsoleMediaStartPolicy.OnUserGesture)),
            ConsoleOperation.SetWindow(new WindowMetadata("title", 800, 600))
        ]));

        Assert.Equal(1, applied.Sequence);
        Assert.Equal(1, store.Snapshot.SnapshotSequence);
        Assert.Single(store.Snapshot.Scrollback);
        Assert.Equal(ConsoleLineAlignment.Center, store.Snapshot.Scrollback[0].Alignment);
        Assert.Single(store.Snapshot.BackgroundLayers);
        Assert.Single(store.Snapshot.CanvasScene.Drawables);
        Assert.Single(store.Snapshot.CanvasScene.HitRegions);
        Assert.Equal(ConsoleMediaStartPolicy.OnUserGesture, store.Snapshot.MediaState.Channels[0].StartPolicy);
        Assert.Equal("title", store.Snapshot.WindowMetadata.Title);

        ConsoleContractException failure = Assert.Throws<ConsoleContractException>(() =>
            store.ApplyTransaction(new ConsoleTransaction([
                ConsoleOperation.SetWindow(new WindowMetadata("must-not-publish")),
                ConsoleOperation.DeleteLines(["missing-line"])
            ])));

        Assert.Equal(ConsoleContractViolationReason.InvalidIdentifier, failure.Reason);
        Assert.Equal(1, store.CurrentSequence);
        Assert.Equal("title", store.Snapshot.WindowMetadata.Title);
        Assert.Single(store.TransactionHistory);
    }

    [Fact]
    public void StructuredResumeCompactsToABaselineWithoutLosingCurrentState()
    {
        var store = new ConsoleStateStore(new ConsoleHistoryOptions { MaxDeltaCount = 2 });
        store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine("line-1", [new TextNode("one")]))
        ]));
        store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine("line-2", [new TextNode("two")]))
        ]));

        StructuredConsoleSnapshotWithDeltasResult initial =
            Assert.IsType<StructuredConsoleSnapshotWithDeltasResult>(store.ReadStructuredSince(0));
        Assert.Equal(0, initial.Snapshot.SnapshotSequence);
        Assert.Empty(initial.Snapshot.Scrollback);
        Assert.Equal([1L, 2L], initial.TransactionsAfterSnapshot.Select(item => item.Sequence));

        StructuredConsoleDeltaBatchResult delta =
            Assert.IsType<StructuredConsoleDeltaBatchResult>(store.ReadStructuredSince(1));
        Assert.Equal(1, delta.FromSequence);
        Assert.Equal(2, delta.ToSequence);
        Assert.Equal(2, Assert.Single(delta.Transactions).Sequence);

        store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine("line-3", [new TextNode("three")]))
        ]));

        StructuredConsoleSnapshotWithDeltasResult compacted =
            Assert.IsType<StructuredConsoleSnapshotWithDeltasResult>(store.ReadStructuredSince(2));
        Assert.Equal(3, compacted.Snapshot.SnapshotSequence);
        Assert.Empty(compacted.TransactionsAfterSnapshot);
        Assert.Equal(["line-1", "line-2", "line-3"], compacted.Snapshot.Scrollback.Select(line => line.LineId));
    }

    [Fact]
    public void ScrollbackTrimsOldCompleteLinesAndReportsDroppedCounts()
    {
        ConsoleContractLimits limits = ConsoleContractLimits.Default with
        {
            MaxScrollbackLines = 2,
            MaxScrollbackNodes = 8,
            MaxScrollbackTextLength = 64
        };
        var store = new ConsoleStateStore(new ConsoleHistoryOptions
        {
            ContractLimits = limits,
            MaxVisibleNodes = 32,
            MaxVisibleTextLength = 128,
            MaxEstimatedBytes = 64 * 1024
        });

        store.ApplyTransaction(new ConsoleTransaction([
            ConsoleOperation.AppendLine(new ConsoleLine("line-1", [new TextNode("one")])),
            ConsoleOperation.AppendLine(new ConsoleLine("line-2", [new TextNode("two")])),
            ConsoleOperation.AppendLine(new ConsoleLine("line-3", [new TextNode("three")]))
        ]));

        ConsoleSnapshot snapshot = store.Snapshot;
        Assert.Equal(["line-2", "line-3"], snapshot.Scrollback.Select(line => line.LineId));
        Assert.True(snapshot.Truncation.WasTruncated);
        Assert.Equal(1, snapshot.Truncation.DroppedLineCount);
        Assert.Equal("two", Assert.IsType<TextNode>(snapshot.VisibleNodes[0]).Text);
    }

    [Fact]
    public void HtmlIslandIsAnExecutableFreeAllowlistedTree()
    {
        var parser = new EmueraHtmlParser();
        HtmlIslandNode island = parser.ParseIsland("<div><strong>safe</strong><br/><img asset=\"sprite-1\" alt=\"icon\"/></div>");

        var root = Assert.IsType<ConsoleHtmlElementNode>(island.Root);
        Assert.Equal("div", root.Tag);
        ConsoleHtmlElementNode content = Assert.IsType<ConsoleHtmlElementNode>(Assert.Single(root.Children));
        Assert.Contains(content.Children, child => child is ConsoleHtmlElementNode element && element.Tag == "strong");
        Assert.Contains(content.Children, child => child is ConsoleHtmlBreakNode);

        Assert.Throws<ConsoleContractException>(() => parser.ParseIsland("<script>alert(1)</script>"));
        Assert.Throws<ConsoleContractException>(() => parser.ParseIsland("<img src=\"https://example.invalid/a.png\"/>"));
    }

    [Fact]
    public void StructuredAudioPreservesChannelRevisionAndUserGesturePolicy()
    {
        var console = new StructuredGameConsole(new ManualRuntimeClock());
        var audio = new StructuredRuntimeAudioPort(console);
        RuntimeFilePath path = new(RuntimeFileArea.GameContent, "audio/theme.ogg");

        Assert.Equal(
            RuntimeAudioPlaybackResult.Played,
            audio.Play(new RuntimeAudioRequest(
                path,
                loop: true,
                volume: 0.75f,
                channel: "music",
                startPolicy: RuntimeAudioStartPolicy.OnUserGesture)));
        MediaChannelState playing = Assert.Single(console.Snapshot.MediaState.Channels);
        Assert.Equal(ConsoleMediaStartPolicy.OnUserGesture, playing.StartPolicy);
        Assert.Equal(1, playing.Revision);

        audio.Stop(path);
        MediaChannelState stopped = Assert.Single(console.Snapshot.MediaState.Channels);
        Assert.Equal(ConsoleMediaPlaybackState.Stopped, stopped.PlaybackState);
        Assert.Equal(2, stopped.Revision);
    }
}

[Trait("Category", "TimedInput")]
[Trait("Category", "ConsoleContract")]
public sealed class StructuredTimedInputContractTests
{
    [Fact]
    public async Task OneInputAndSourceConstraintAreAppliedBeforeAcceptance()
    {
        var clock = new ManualRuntimeClock();
        var console = new StructuredGameConsole(clock, new FixedPromptIdGenerator("prompt-one"));
        Task<GameConsoleInput> runtime = Task.Run(() => console.Read(new ConsolePrompt(
            ConsoleInputType.Text,
            constraints: new TextInputConstraints(4),
            oneInput: true,
            allowedSources: ConsoleInputSource.Keyboard)));

        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(10)));
        string promptId = console.CurrentPrompt!.PromptId;
        ConsoleInputResult accepted = console.SubmitInput(new ConsoleInputCommand(
            promptId,
            "client-one",
            "abcd",
            ConsoleInputSource.Keyboard,
            key: new ConsoleKeyPayload(65)));

        Assert.Equal(ConsoleInputResultKind.Accepted, accepted.Kind);
        GameConsoleInput input = await runtime;
        Assert.Equal("a", input.Value);
        Assert.False(console.IsTimeOut);
    }

    [Fact]
    public async Task MonotonicTimeoutIgnoresWallClockJumpAndContinuesWithoutValue()
    {
        var clock = new ManualRuntimeClock();
        var console = new StructuredGameConsole(clock, new FixedPromptIdGenerator("prompt-timeout"));
        Task<GameConsoleInput> runtime = Task.Run(() => console.Read(new ConsolePrompt(
            ConsoleInputType.WaitOnly,
            timeout: TimeSpan.FromSeconds(5),
            timeoutAction: ConsolePromptTimeoutAction.ContinueWithoutValue)));

        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => clock.PendingWaiterCount == 1, TimeSpan.FromSeconds(10)));
        clock.SetUtcNow(DateTimeOffset.UtcNow.AddYears(10));
        clock.Advance(TimeSpan.FromSeconds(5));

        GameConsoleInput input = await runtime;
        Assert.Equal(string.Empty, input.Value);
        Assert.True(console.IsTimeOut);
        Assert.Null(console.CurrentPrompt);
    }

    private sealed class FixedPromptIdGenerator(string id) : IPromptIdGenerator
    {
        public string Next() => id;
    }
}
