using System.Buffers.Binary;
using System.Security.Cryptography;
using CloudEmuera.EmueraRuntime.Headless;
using CloudEmuera.EmueraRuntime.UpstreamHeadless;
using CloudEmuera.RuntimeAdapter;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.UI.Game.Image;
using MinorShift.Emuera.UI.Game;
using System.Drawing;
using System.Text;
using static MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;
using RuntimeConsoleColor = CloudEmuera.RuntimeAdapter.ConsoleColor;
using Xunit;

namespace CloudEmuera.RuntimeCompatibility.Tests;

[Trait("Category", "RuntimeCompatibility")]
public sealed class HeadlessRuntimeFixtureTests
{
    private const string ValidWebpBase64 = "UklGRhwAAABXRUJQVlA4TA8AAAAvA4AAAAcQ/Y/+ByKi/wEA";

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void EraYenCompatibilityTransformsOnlyVisibleTextAndCanBeDisabled()
    {
        // PLAY-016: the compatibility seam runs before authoritative layout,
        // while button input values remain exact runtime data.
        var enabledAdapter = new StructuredGameConsole();
        var enabled = new EmueraConsole(enabledAdapter, enabledAdapter.Clock, CancellationToken.None, convertBackslashToYen: true);
        enabled.BeginExecutionOutput();
        enabled.PrintButton("price\\100", "route\\keep");
        enabled.NewLine();

        ButtonNode button = Assert.IsType<ButtonNode>(Assert.Single(Assert.Single(enabledAdapter.Snapshot.Scrollback).Nodes));
        Assert.Equal("price\u00a5100", Assert.IsType<TextNode>(Assert.Single(button.Children)).Text);
        Assert.Equal("route\\keep", button.Value);

        var disabledAdapter = new StructuredGameConsole();
        var disabled = new EmueraConsole(disabledAdapter, disabledAdapter.Clock, CancellationToken.None, convertBackslashToYen: false);
        disabled.BeginExecutionOutput();
        disabled.Print("price\\100");
        disabled.NewLine();

        Assert.Equal("price\\100", Assert.IsType<TextNode>(Assert.Single(Assert.Single(disabledAdapter.Snapshot.Scrollback).Nodes)).Text);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void Issue18LiteralPrintTabsBecomeDisplaySpacesBeforeStructuredValidation()
    {
        // PLAY-001/PLAY-014/issue #18: the real SHOP.ERB output contains two
        // literal tabs. They must use the pinned eight-space display width,
        // while the browser-facing TextNode remains control-character-free.
        var adapter = new StructuredGameConsole();
        var headless = new EmueraConsole(adapter, adapter.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.Print("[100] - 調教開始\t\t");
        headless.NewLine();

        string transcript = RuntimeTranscriptProjector.Project(adapter.Snapshot.VisibleNodes);
        Assert.Equal("[100] - 調教開始                ", transcript);
        Assert.DoesNotContain('\t', transcript);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task Issue18LiteralPrintTabsDoNotAbortPinnedInterpreter()
    {
        // The direct bridge regression above isolates the projection boundary;
        // this fixture proves the same literal SHOP.ERB shape completes the
        // pinned interpreter path instead of becoming runtime_script_failed.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTL [100] - 調教開始\t\t\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));

        EmueraRuntimeResult initialized = await host.InitializeAsync();
        Assert.True(initialized.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", initialized.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));

        EmueraRuntimeResult result = await host.RunAsync();

        Assert.True(result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        string transcript = RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes);
        Assert.Contains("[100] - 調教開始", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain('\t', transcript);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task Issue15EnumeratedWindowsPathLoadsXmlOnLinux()
    {
        // COMP-002/COMP-006/GAME-008/issue #15: ENUMFILES returns a path
        // relative to Program.ExeDir, and the pinned upstream helper rewrites
        // its '/' separator to '\\' before LOADTEXT reads it. The complete
        // EraFL loader shape must still reach XML_GET with the file contents.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "VARS DOCS, 100\n" +
            "VARI FILE_COUNT\n" +
            "FILE_COUNT = ENUMFILES(\"XML\", \"SKILL_BATTLE*\")\n" +
            "ARRAYCOPY \"RESULTS\", \"DOCS\"\n" +
            "FOR LOCAL, 0, FILE_COUNT\n" +
            "    LOADTEXT DOCS:LOCAL\n" +
            "    XML_GET RESULTS, \"/skilldef\", 1, 2\n" +
            "NEXT\n" +
            "PRINTFORML ISSUE15-FILES={FILE_COUNT}\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\nLOADTEXTとSAVETEXTで使える拡張子:txt,xml\n",
            configureGame: game =>
            {
                File.WriteAllText(
                    Path.Combine(game, "setting.json"),
                    "{\"UseScopedVariableInstruction\":true}");
                string xmlRoot = Path.Combine(game, "XML");
                Directory.CreateDirectory(xmlRoot);
                File.WriteAllText(
                    Path.Combine(xmlRoot, "SKILL_BATTLE_A.xml"),
                    "<skilldef><skill id=\"A\" /></skilldef>");
                File.WriteAllText(
                    Path.Combine(xmlRoot, "SKILL_BATTLE_B.xml"),
                    "<skilldef><skill id=\"B\" /></skilldef>");
            });
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));

        EmueraRuntimeResult initialized = await host.InitializeAsync();
        Assert.Equal(EmueraRuntimeStatus.Completed, initialized.Status);

        EmueraRuntimeResult result = await host.RunAsync();

        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        Assert.Contains(
            "ISSUE15-FILES=2",
            RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "EmueraFeatureMatrix")]
    public void DynamicGraphicsPublishesBoundedBrowserRasterDrawable()
    {
        var adapter = new StructuredGameConsole();
        var headless = new EmueraConsole(adapter, adapter.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();
        using var graphics = new GraphicsImage(7);
        graphics.GCreate(16, 12, useGDI: false);
        graphics.GClear(Color.FromArgb(unchecked((int)0xffff0000)));

        Assert.True(headless.CBG_SetGraphics(graphics, 3, 4, 9));

        RasterDrawable raster = Assert.IsType<RasterDrawable>(Assert.Single(adapter.Snapshot.CanvasScene.Drawables));
        Assert.Equal(new ConsoleRect(3, 4, 16, 12), raster.Bounds);
        Assert.Equal(9, raster.ZIndex);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, raster.PngData.Take(4));
        Assert.InRange(raster.PngData.Count, 1, ConsoleContractLimits.Default.MaxInlineRasterBytes);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void SetBgColorPublishesWindowBackgroundInsteadOfTextBackground()
    {
        // PLAY-002: SETBGCOLOR changes the whole Emuera console surface, not
        // the background rectangle of each text run.
        var adapter = new StructuredGameConsole();
        var headless = new EmueraConsole(adapter, adapter.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();
        headless.SetBgColor(Color.FromArgb(0x12, 0x34, 0x56));
        headless.Print("TEXT");
        headless.NewLine();

        Assert.Equal(new RuntimeConsoleColor(0x12, 0x34, 0x56), adapter.Snapshot.WindowMetadata.DefaultBackground);
        TextNode text = Assert.IsType<TextNode>(Assert.Single(Assert.Single(adapter.Snapshot.Scrollback).Nodes));
        Assert.Null(text.Style.Background);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task FindElementEscapedLiteralUsesLiteralPathAndKeepsRegexFallback()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "#DIMS values, 6\n" +
            "#DIMS query\n" +
            "values:0 = aXb\n" +
            "values:1 = a.b\n" +
            "values:2 = prefix a.b suffix\n" +
            "values:3 = a.b\n" +
            "values:4 = plain\n" +
            "values:5 = \n" +
            "query = a.b\n" +
            "PRINTFORML ESCAPED-DYNAMIC-EXACT={FINDELEMENT(values, ESCAPE(query), 0, 6, 1)}\n" +
            "PRINTFORML ESCAPED-CONSTANT-EXACT={FINDELEMENT(values, ESCAPE(\"a.b\"), 0, 6, 1)}\n" +
            "PRINTFORML ESCAPED-PARTIAL={FINDELEMENT(values, ESCAPE(query), 0, 6, 0)}\n" +
            "PRINTFORML ESCAPED-LAST={FINDLASTELEMENT(values, ESCAPE(query), 0, 6, 1)}\n" +
            "PRINTFORML PLAIN-LITERAL-EXACT={FINDELEMENT(values, \"aXb\", 0, 6, 1)}\n" +
            "PRINTFORML PLAIN-LITERAL-PARTIAL={FINDELEMENT(values, \"aXb\", 0, 6, 0)}\n" +
            "PRINTFORML REGEX-EXACT={FINDELEMENT(values, \"a.b\", 0, 6, 1)}\n" +
            "PRINTFORML REGEX-CLASS={FINDELEMENT(values, \"a[.]b\", 0, 6, 1)}\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        EmueraRuntimeResult result = await host.RunAsync();

        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        string transcript = RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes);
        Assert.Contains("ESCAPED-DYNAMIC-EXACT=1", transcript, StringComparison.Ordinal);
        Assert.Contains("ESCAPED-CONSTANT-EXACT=1", transcript, StringComparison.Ordinal);
        Assert.Contains("ESCAPED-PARTIAL=1", transcript, StringComparison.Ordinal);
        Assert.Contains("ESCAPED-LAST=3", transcript, StringComparison.Ordinal);
        Assert.Contains("PLAIN-LITERAL-EXACT=0", transcript, StringComparison.Ordinal);
        Assert.Contains("PLAIN-LITERAL-PARTIAL=0", transcript, StringComparison.Ordinal);
        Assert.Contains("REGEX-EXACT=0", transcript, StringComparison.Ordinal);
        Assert.Contains("REGEX-CLASS=1", transcript, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "EmueraFeatureMatrix")]
    public async Task DynamicGraphicsRunsThroughPinnedInterpreterAndPublishesScene()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTFORML CREATED={GCREATE(0, 16, 12)}\n" +
            "PRINTFORML CLEARED={GCLEAR(0, 4294901760)}\n" +
            "PRINTFORML PIXELSET={GSETCOLOR(0, 4278255360, 1, 2)}\n" +
            "PRINTFORML PIXEL={GGETCOLOR(0, 1, 2)}\n" +
            "PRINTFORML PIXELOOB={GGETCOLOR(0, 1, -1)}\n" +
            "PRINTFORML BRUSH={GSETBRUSH(0, 4278190335)}\n" +
            "PRINTFORML FILLED={GFILLRECTANGLE(0, 4, 5, 3, 2)}\n" +
            "PRINTFORML FILLPIXEL={GGETCOLOR(0, 4, 5)}\n" +
            "PRINTFORML DRAWN={CBGSETG(0, 3, 4, 9)}\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        EmueraRuntimeResult result = await host.RunAsync();

        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        string transcript = RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes);
        Assert.Contains("CREATED=1", transcript, StringComparison.Ordinal);
        Assert.Contains("CLEARED=1", transcript, StringComparison.Ordinal);
        Assert.Contains("PIXELSET=1", transcript, StringComparison.Ordinal);
        Assert.Contains("PIXEL=4278255360", transcript, StringComparison.Ordinal);
        Assert.Contains("PIXELOOB=-1", transcript, StringComparison.Ordinal);
        Assert.Contains("BRUSH=1", transcript, StringComparison.Ordinal);
        Assert.Contains("FILLED=1", transcript, StringComparison.Ordinal);
        Assert.Contains("FILLPIXEL=4278190335", transcript, StringComparison.Ordinal);
        Assert.Contains("DRAWN=1", transcript, StringComparison.Ordinal);
        RasterDrawable raster = Assert.IsType<RasterDrawable>(Assert.Single(fixture.Console.Snapshot.CanvasScene.Drawables));
        Assert.Equal(new ConsoleRect(3, 4, 16, 12), raster.Bounds);
    }

    [Fact]
    [Trait("Category", "Tooltip")]
    [Trait("Category", "RuntimeBridge")]
    public async Task TooltipSettersRunThroughPinnedInterpreterAndPublishPresentationState()
    {
        // PLAY-017: all eight EE tooltip setters are browser presentation
        // state and must no longer terminate the runtime as HOST_SHIM calls.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "TOOLTIP_SETCOLOR 1122867, 4478310\n" +
            "TOOLTIP_SETDELAY 250\n" +
            "TOOLTIP_SETDURATION 4000\n" +
            "TOOLTIP_SETFONT \"untrusted-game-font\"\n" +
            "TOOLTIP_SETFONTSIZE 19\n" +
            "TOOLTIP_CUSTOM 1\n" +
            "TOOLTIP_FORMAT 32785\n" +
            "TOOLTIP_IMG 1\n" +
            "HTML_PRINT \"<button value='7' title='line1<br>line2'>choice</button>\"\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        EmueraRuntimeResult initialized = await host.InitializeAsync();
        Assert.True(initialized.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", initialized.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));

        EmueraRuntimeResult result = await host.RunAsync();

        Assert.True(result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        ConsoleTooltipPresentation tooltip = fixture.Console.Snapshot.TooltipPresentation;
        Assert.True(tooltip.CustomEnabled);
        Assert.Equal(new RuntimeConsoleColor(0x11, 0x22, 0x33), tooltip.Foreground);
        Assert.Equal(new RuntimeConsoleColor(0x44, 0x55, 0x66), tooltip.Background);
        Assert.Equal(250, tooltip.DelayMilliseconds);
        Assert.Equal(4000, tooltip.DurationMilliseconds);
        Assert.Equal("session-default", tooltip.FontFamily);
        Assert.Equal(19, tooltip.FontSize);
        Assert.Equal(ConsoleTooltipHorizontalAlignment.Center, tooltip.TextFormat.Horizontal);
        Assert.True(tooltip.TextFormat.Wrap);
        Assert.Equal(ConsoleTooltipTrimming.CharacterEllipsis, tooltip.TextFormat.Trimming);
        Assert.True(tooltip.ImageMode);
        Assert.Equal(7, tooltip.Revision);
        ButtonNode button = Assert.IsType<ButtonNode>(fixture.Console.Snapshot.Scrollback.SelectMany(line => line.Nodes).Single(node => node is ButtonNode));
        Assert.Equal("line1<br>line2", button.Tooltip);
        Assert.Contains(fixture.Console.StateStore.TransactionHistory.SelectMany(transaction => transaction.Transaction.Operations), operation => operation is SetTooltipPresentationOperation);
    }

    [Fact]
    [Trait("Category", "Tooltip")]
    [Trait("Category", "RuntimeBridge")]
    public async Task TooltipGraphicsRewriteFlushesLatestPngAtInputBoundary()
    {
        // PLAY-017: a referenced Graphics mutation immediately before INPUT
        // must replace the prior projection even when no later PRINT occurs.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTFORML CREATED={GCREATE(0, 8, 6)}\n" +
            "PRINTFORML RED={GCLEAR(0, 4294901760)}\n" +
            "TOOLTIP_IMG 1\n" +
            "HTML_PRINT \"<button value='7' title='0'>choice</button>\"\n" +
            "LOCAL = GCLEAR(0, 4278255360)\n" +
            "INPUT\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        Assert.True(SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));

        ConsoleTooltipResource resource = Assert.Single(fixture.Console.CommittedSnapshot!.TooltipResources);
        Assert.Equal(0, resource.GraphicsId);
        Assert.Equal(8, resource.Width);
        Assert.Equal(6, resource.Height);
        Assert.Contains(
            fixture.Console.StateStore.TransactionHistory.SelectMany(item => item.Transaction.Operations),
            operation => operation is UpsertTooltipResourceOperation projected &&
                projected.Resource.GraphicsId == 0 && projected.Resource.Revision < resource.Revision);

        ConsolePrompt prompt = fixture.Console.CurrentPrompt!;
        Assert.Equal(ConsoleInputResultKind.Accepted, fixture.Console.SubmitCurrentInput(
            new ConsoleInputAttempt("tooltip-graphics", "1", ConsoleInputSource.Keyboard)).Kind);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await run).Status);
    }

    [Fact]
    [Trait("Category", "Tooltip")]
    [Trait("Category", "RuntimeBridge")]
    public async Task TooltipImageModeDisablePublishesExplicitResourceClearDelta()
    {
        // PLAY-017 / ADR-0036: resource reclamation must be represented in
        // the committed operation stream. Clearing only the Worker's private
        // Snapshot would leave API/Web delta mirrors holding a stale image.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "LOCAL = GCREATE(0, 8, 6)\n" +
            "LOCAL = GCLEAR(0, 4294901760)\n" +
            "TOOLTIP_IMG 1\n" +
            "HTML_PRINT \"<button value='7' title='0'>choice</button>\"\n" +
            "TOOLTIP_IMG 0\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        ConsoleOperation[] operations = fixture.Console.StateStore.TransactionHistory
            .SelectMany(transaction => transaction.Transaction.Operations)
            .ToArray();
        Assert.Contains(operations, operation => operation is UpsertTooltipResourceOperation);
        Assert.Contains(operations, operation => operation is ClearTooltipResourcesOperation);
        Assert.False(fixture.Console.Snapshot.TooltipPresentation.ImageMode);
        Assert.Empty(fixture.Console.Snapshot.TooltipResources);
    }

    [Fact]
    [Trait("Category", "Tooltip")]
    [Trait("Category", "RuntimeBridge")]
    public void DestroyedTooltipGraphicsRemovesOldProjectionAndKeepsRawText()
    {
        var adapter = new StructuredGameConsole();
        var headless = new EmueraConsole(adapter, adapter.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();
        using var graphics = AppContents.GetGraphics(11);
        graphics.GCreate(4, 4, useGDI: false);
        graphics.GClear(Color.Red);
        headless.SetToolTipImg(true);
        headless.PrintButton("choice", "1");
        headless.NewLine();

        // Replace the ordinary button with a visible numeric-tooltip target.
        headless.PrintHtml("<button value='1' title='11'>choice</button>", toPrintBuffer: false);
        headless.PrintFlush(force: true);
        Assert.Contains(adapter.Snapshot.TooltipResources, item => item.GraphicsId == 11);

        graphics.GDispose();
        headless.Quit();

        Assert.DoesNotContain(adapter.Snapshot.TooltipResources, item => item.GraphicsId == 11);
        Assert.Contains(adapter.Snapshot.Scrollback.SelectMany(line => line.Nodes).OfType<ButtonNode>(),
            button => button.Tooltip == "11");
        Assert.Contains(headless.RuntimeWarnings, warning => warning.StartsWith(
            "tooltip_graphics_unavailable:11:", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "EmueraFeatureMatrix")]
    public async Task AnimatedSpriteCsvPublishesAllFramesWithTiming()
    {
        string sourceImage = Path.Combine(
            RuntimeCompatibilityCli.FindRepositoryRoot(),
            "tests", "fixtures", "runtime", "v18-core", "resources", "cloudemuera-v18.png");
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINT_IMG \"WALK\"\nPRINTFORML CBG={CBGSETSPRITE(\"WALK\", 7, 8, 9)}\nPRINTL AFTER-ANIME\nQUIT\n",
            configureGame: game =>
            {
                string resources = Path.Combine(game, "resources");
                File.Copy(sourceImage, Path.Combine(resources, "frame.png"));
                File.WriteAllText(
                    Path.Combine(resources, "sprites.csv"),
                    "WALK,ANIME,2,2\n" +
                    "WALK,frame.png,0,0,2,2,0,0,50\n" +
                    "WALK,frame.png,0,0,2,2,1,0,75\n");
            });
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        SpriteNode sprite = Assert.IsType<SpriteNode>(
            fixture.Console.Snapshot.Scrollback.SelectMany(line => line.Nodes).Single(node => node is SpriteNode));
        Assert.Collection(
            sprite.AnimationFrames,
            frame =>
            {
                Assert.Equal(50, frame.DurationMilliseconds);
                Assert.Equal(new ConsolePoint(0, 0), frame.Offset);
            },
            frame =>
            {
                Assert.Equal(75, frame.DurationMilliseconds);
                Assert.Equal(new ConsolePoint(1, 0), frame.Offset);
            });
        SpriteDrawable drawable = Assert.IsType<SpriteDrawable>(Assert.Single(fixture.Console.Snapshot.CanvasScene.Drawables));
        Assert.Equal(new ConsoleRect(7, 8, 2, 2), drawable.Bounds);
        Assert.Equal(9, drawable.ZIndex);
        Assert.Equal([50, 75], drawable.AnimationFrames.Select(frame => frame.DurationMilliseconds));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task WebpSpriteLoadsThroughMetadataAndNativeSpritePaths()
    {
        byte[] webp = Convert.FromBase64String(ValidWebpBase64);
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINT_IMG \"WEBP\"\nQUIT\n",
            configureGame: game =>
            {
                string resources = Path.Combine(game, "resources");
                File.WriteAllBytes(Path.Combine(resources, "webp.webp"), webp);
                File.WriteAllText(
                    Path.Combine(resources, "sprites.csv"),
                    "WEBP,WEBP.WEBP,0,0,4,3\n");
            });
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));

        EmueraRuntimeResult initialized = await host.InitializeAsync();
        Assert.True(
            initialized.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", initialized.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));

        EmueraRuntimeResult result = await host.RunAsync();
        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));

        SpriteNode sprite = Assert.IsType<SpriteNode>(
            fixture.Console.Snapshot.Scrollback.SelectMany(line => line.Nodes).Single(node => node is SpriteNode));
        Assert.Equal(new ConsoleRect(0, 0, 4, 3), sprite.SourceRect);
        Assert.Equal("path-cmVzb3VyY2VzL3dlYnAud2VicA", sprite.AssetId.Value);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void WebpMetadataRejectsMalformedAndOversizedContainers()
    {
        byte[] malformed = "RIFF\u0004\u0000\u0000\u0000WEBP"u8.ToArray();
        Assert.Throws<InvalidDataException>(() =>
            WebpMetadataReader.Read(new MemoryStream(malformed), malformed.Length));

        byte[] oversized = CreateVp8xMetadataWebp(8_193, 1);
        Assert.Throws<InvalidDataException>(() =>
            WebpMetadataReader.Read(new MemoryStream(oversized), oversized.Length));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void AppContentsRegistryLoadsLinuxResourcesForSpriteCreatedLookup()
    {
        // COMP-007: eraTW gates portraits on SPRITECREATED (Look.ERB →
        // PRINT_TARGET_IMAGE → 画像セット). The pinned AppContents must resolve
        // Windows-style CSV resource paths on Linux or every declared sprite
        // stays invisible to script gates.
        string root = Path.Combine(Path.GetTempPath(), "cloudemuera-appcontents", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "resources"));
        Directory.CreateDirectory(Path.Combine(root, "CSV"));
        Directory.CreateDirectory(Path.Combine(root, "ERB"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        Directory.CreateDirectory(Path.Combine(root, "sound"));
        Directory.CreateDirectory(Path.Combine(root, "font"));
        try
        {
            string sourceImage = Path.Combine(
                RuntimeCompatibilityCli.FindRepositoryRoot(),
                "tests", "fixtures", "runtime", "v18-core", "resources", "cloudemuera-v18.png");
            File.Copy(sourceImage, Path.Combine(root, "resources", "1.png"));
            File.WriteAllBytes(
                Path.Combine(root, "resources", "webp.webp"),
                Convert.FromBase64String(ValidWebpBase64));
            File.WriteAllText(
                Path.Combine(root, "resources", "立ち絵.csv"),
                "立絵_服_通常_1,1.png,0,0,180,180\n" +
                "WEBP_SPRITE,webp.webp,0,0,4,3\n");

            HeadlessPathResolver.Configure(root);
            MinorShift.Emuera.Program.ConfigureHeadless(
                root,
                Path.Combine(root, "CSV"),
                Path.Combine(root, "ERB"),
                Path.Combine(root, "tmp"),
                Path.Combine(root, "resources"),
                Path.Combine(root, "sound"),
                Path.Combine(root, "font"));
            MinorShift.Emuera.GlobalStatic.Reset();
            MinorShift.Emuera.Runtime.Config.ConfigData.ResetHeadless();
            MinorShift.Emuera.Runtime.Config.ConfigData.Instance.LoadConfig();

            Exception error = AppContents.LoadContents(reload: false);

            Assert.Null(error);
            Assert.NotNull(AppContents.GetSprite("立絵_服_通常_1"));
            Assert.NotNull(AppContents.GetSprite("立絵_服_通常_1".ToUpperInvariant()));
            Assert.NotNull(AppContents.GetSprite("WEBP_SPRITE"));
        }
        finally
        {
            AppContents.UnloadContents();
            HeadlessPathResolver.Reset();
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task PrintFormWWaitsForEnterKeyInsideTheEncounterChain()
    {
        // PLAY-002/COMP-007: PRINTFORMW is print + newline + wait-for-any-key
        // (upstream ExpressionMediator.OutputToConsole calls ReadAnyKey), so
        // the eraTW room-encounter kojo "魅魔瞥了一眼…就马上转了回去" is followed
        // by an EnterKey prompt; the movement command then ends and the next
        // INPUT (menu) opens a separate Integer prompt. The original Emuera
        // requires the same sequence of key presses.
        string erb =
            "@SYSTEM_TITLE\n" +
            "PRINTFORML 里好像有魅魔\n" +
            "PRINTBUTTON \"[打声招呼后继续移动]\", 0\n" +
            "PRINTBUTTON \"[无视]\", 1\n" +
            "PRINTBUTTON \"[停下来]\", 2\n" +
            "$INPUT_LOOP\n" +
            "INPUT\n" +
            "IF RESULT < 0 || RESULT > 2\n" +
            "    CLEARLINE 1\n" +
            "    GOTO INPUT_LOOP\n" +
            "ENDIF\n" +
            "IF RESULT == 2\n" +
            "    PRINTFORMW 魅魔瞥了一眼MASTER就马上转了回去\n" +
            "    PRINTFORML\n" +
            "ELSEIF RESULT == 1\n" +
            "    PRINTFORMW 魅魔坦率地打了招呼\n" +
            "ELSE\n" +
            "    PRINTFORMW 魅魔无视了MASTER\n" +
            "ENDIF\n" +
            "PRINTFORML 1回移動したよ\n" +
            "INPUT\n" +
            "PRINTFORML MENU-DONE\n" +
            "QUIT\n";
        using var fixture = RuntimeHostFixture.Create(
            erb,
            configureGame: game => File.WriteAllText(
                Path.Combine(game, "ERB", "START.ERB"),
                erb,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)));
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        StructuredGameConsole console = fixture.Console;
        var prompts = new List<ConsoleInputType>();

        // 1) ASK_M choice INPUT (Integer); an empty value is rejected.
        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ConsolePrompt askM = console.CurrentPrompt!;
        Assert.Equal(ConsoleInputType.Integer, askM.InputType);
        prompts.Add(askM.InputType);
        Assert.Equal(
            ConsoleInputResultKind.InvalidFormat,
            console.SubmitCurrentInput(new ConsoleInputAttempt("empty", string.Empty)).Kind);
        Assert.Equal(askM.PromptId, console.CurrentPrompt!.PromptId);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            console.SubmitCurrentInput(new ConsoleInputAttempt("choice", "2")).Kind);

        // 2) The kojo PRINTFORMW opens a wait-for-any-key (EnterKey) prompt.
        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ConsolePrompt printFormW = console.CurrentPrompt!;
        Assert.Equal(ConsoleInputType.EnterKey, printFormW.InputType);
        prompts.Add(printFormW.InputType);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            console.SubmitCurrentInput(new ConsoleInputAttempt("continue", string.Empty)).Kind);

        // 3) The movement command ends and the next INPUT (menu) opens.
        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ConsolePrompt menu = console.CurrentPrompt!;
        Assert.Equal(ConsoleInputType.Integer, menu.InputType);
        prompts.Add(menu.InputType);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            console.SubmitCurrentInput(new ConsoleInputAttempt("menu", "7")).Kind);

        Assert.True(SpinWait.SpinUntil(() => run.IsCompleted, TimeSpan.FromSeconds(2)));
        EmueraRuntimeResult finalResult = await run;
        Assert.Equal(EmueraRuntimeStatus.Completed, finalResult.Status);
        Assert.Contains(
            "魅魔瞥了一眼",
            RuntimeTranscriptProjector.Project(console.Snapshot.VisibleNodes),
            StringComparison.Ordinal);
        Assert.Contains(
            "MENU-DONE",
            RuntimeTranscriptProjector.Project(console.Snapshot.VisibleNodes),
            StringComparison.Ordinal);
        Assert.Equal(
            new[] { ConsoleInputType.Integer, ConsoleInputType.EnterKey, ConsoleInputType.Integer },
            prompts);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task RightPointerSkipsConsecutivePressAnyKeyWaitsUntilValueInput()
    {
        // Issue #2 / PLAY-009: a desktop right click is a message-skip gesture.
        // It must skip both the EnterKey wait emitted by PRINTFORMW and a
        // following explicit AnyKey wait, then stop at the first value input.
        string erb =
            "@SYSTEM_TITLE\n" +
            "PRINTFORMW FIRST-WAIT\n" +
            "PRINTL BETWEEN-WAITS\n" +
            "WAITANYKEY\n" +
            "PRINTL AFTER-WAITS\n" +
            "INPUT\n" +
            "PRINTL DONE\n" +
            "QUIT\n";
        using var fixture = RuntimeHostFixture.Create(
            erb,
            configureGame: game => File.WriteAllText(
                Path.Combine(game, "ERB", "START.ERB"),
                erb,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)));
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        StructuredGameConsole console = fixture.Console;
        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        Assert.Equal(ConsoleInputType.EnterKey, console.CurrentPrompt!.InputType);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            console.SubmitCurrentInput(new ConsoleInputAttempt(
                "right-skip",
                string.Empty,
                ConsoleInputSource.Pointer,
                pointer: new ConsolePointerPayload(0, 0, button: 2))).Kind);

        Assert.True(SpinWait.SpinUntil(
            () => run.IsCompleted || console.CurrentPrompt?.InputType == ConsoleInputType.Integer,
            TimeSpan.FromSeconds(2)));
        Assert.Equal(ConsoleInputType.Integer, console.CurrentPrompt!.InputType);
        Assert.Equal(
            2,
            console.StateStore.TransactionHistory
                .SelectMany(item => item.Transaction.Operations)
                .OfType<OpenPromptOperation>()
                .Count());
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            console.SubmitCurrentInput(new ConsoleInputAttempt("value", "7")).Kind);

        EmueraRuntimeResult result = await run;
        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        string transcript = RuntimeTranscriptProjector.Project(console.Snapshot.VisibleNodes);
        Assert.Contains("FIRST-WAIT", transcript, StringComparison.Ordinal);
        Assert.Contains("BETWEEN-WAITS", transcript, StringComparison.Ordinal);
        Assert.Contains("AFTER-WAITS", transcript, StringComparison.Ordinal);
        Assert.Contains("DONE", transcript, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task TwoPlainInputsOpenExactlyTwoPrompts()
    {
        // Control: without a PRINTFORMW the same shape opens exactly two
        // prompts, confirming the extra EnterKey prompt is PRINTFORMW's own
        // upstream wait-for-any-key semantics and not a duplicated INPUT.
        string erb =
            "@SYSTEM_TITLE\n" +
            "PRINTFORML A\n" +
            "INPUT\n" +
            "PRINTFORML B\n" +
            "INPUT\n" +
            "PRINTFORML DONE\n" +
            "QUIT\n";
        using var fixture = RuntimeHostFixture.Create(
            erb,
            configureGame: game => File.WriteAllText(
                Path.Combine(game, "ERB", "START.ERB"),
                erb,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)));
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        StructuredGameConsole console = fixture.Console;
        var prompts = new List<ConsoleInputType>();
        for (int i = 0; i < 4 && !run.IsCompleted; i++)
        {
            Assert.True(SpinWait.SpinUntil(() => run.IsCompleted || console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
            if (run.IsCompleted)
                break;
            ConsolePrompt current = console.CurrentPrompt!;
            prompts.Add(current.InputType);
            Assert.Equal(
                ConsoleInputResultKind.Accepted,
                console.SubmitCurrentInput(new ConsoleInputAttempt($"driver-{i}", "7")).Kind);
            SpinWait.SpinUntil(() => run.IsCompleted || console.CurrentPrompt?.PromptId != current.PromptId, TimeSpan.FromMilliseconds(300));
        }
        Assert.True(SpinWait.SpinUntil(() => run.IsCompleted, TimeSpan.FromSeconds(2)));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await run).Status);
        Assert.Equal(2, prompts.Count);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task StaticSpriteCsvClipsLegacyRectangleWhilePreservingDestinationSize()
    {
        string sourceImage = Path.Combine(
            RuntimeCompatibilityCli.FindRepositoryRoot(),
            "tests", "fixtures", "runtime", "v18-core", "resources", "cloudemuera-v18.png");
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINT_IMG \"EDGE\"\nQUIT\n",
            configureGame: game =>
            {
                string resources = Path.Combine(game, "resources");
                File.Copy(sourceImage, Path.Combine(resources, "edge.png"));
                File.WriteAllText(Path.Combine(resources, "sprites.csv"), "EDGE,edge.png,0,1,2,2\n");
            });
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));

        EmueraRuntimeResult initialized = await host.InitializeAsync();
        Assert.True(
            initialized.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", initialized.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
        Assert.Contains(initialized.Diagnostics, diagnostic =>
            diagnostic.Code == "runtime_warning" &&
            diagnostic.Message.Contains("was clipped", StringComparison.Ordinal));

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        SpriteNode sprite = Assert.IsType<SpriteNode>(
            fixture.Console.Snapshot.Scrollback.SelectMany(line => line.Nodes).Single(node => node is SpriteNode));
        Assert.Equal(new ConsoleRect(0, 1, 2, 1), sprite.SourceRect);
        Assert.Equal(sprite.Destination.Width, sprite.Destination.Height);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task CheckfontTreatsUnavailablePrivateFontsAsNotInstalled()
    {
        // COMP-002: the desktop runtime can populate GlobalStatic.Pfc from
        // font/. Headless intentionally does not load those GDI font files,
        // so a normal CHKFONT query must return false rather than crash.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "CHKFONT \"CloudEmuera Test Font\"\n" +
            "PRINTFORML FONT={RESULT}\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        EmueraRuntimeResult result = await host.RunAsync();

        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        Assert.Contains(
            "FONT=0",
            RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task StaticSpriteCsvKeepsUpstreamFallbackForMalformedOptionalRectangles()
    {
        string sourceImage = Path.Combine(
            RuntimeCompatibilityCli.FindRepositoryRoot(),
            "tests", "fixtures", "runtime", "v18-core", "resources", "cloudemuera-v18.png");
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINT_IMG \"BROKEN_RECT\"\nPRINT_IMG \"EMPTY_RECT\"\nQUIT\n",
            configureGame: game =>
            {
                string resources = Path.Combine(game, "resources");
                File.Copy(sourceImage, Path.Combine(resources, "fallback.png"));
                File.WriteAllText(
                    Path.Combine(resources, "sprites.csv"),
                    "BROKEN_RECT,fallback.png,3\\600,0,2,2\n" +
                    "EMPTY_RECT,fallback.png,0,0,,\n");
            });
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));

        EmueraRuntimeResult initialized = await host.InitializeAsync();

        Assert.Equal(EmueraRuntimeStatus.Completed, initialized.Status);
        Assert.Equal(2, initialized.Diagnostics.Count(diagnostic =>
            diagnostic.Code == "runtime_warning" &&
            diagnostic.Message.Contains("invalid Sprite rectangle", StringComparison.Ordinal)));

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);
        SpriteNode[] sprites = fixture.Console.Snapshot.Scrollback
            .SelectMany(line => line.Nodes)
            .OfType<SpriteNode>()
            .ToArray();
        Assert.Equal(2, sprites.Length);
        Assert.All(sprites, sprite => Assert.Equal(new ConsoleRect(0, 0, 2, 2), sprite.SourceRect));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task SpriteLoadWarningsDoNotRejectTheWholeGame()
    {
        string sourceImage = Path.Combine(
            RuntimeCompatibilityCli.FindRepositoryRoot(),
            "tests", "fixtures", "runtime", "v18-core", "resources", "cloudemuera-v18.png");
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nQUIT\n",
            configureGame: game =>
            {
                string resources = Path.Combine(game, "resources");
                File.Copy(sourceImage, Path.Combine(resources, "valid.png"));
                File.WriteAllText(
                    Path.Combine(resources, "sprites.csv"),
                    "MISSING,missing.png\n" +
                    "DUP,valid.png\n" +
                    "DUP,valid.png\n" +
                    "ANIM,ANIME,2,2\n" +
                    "ANIM,valid.png,0,0,2,2,10,0,50\n" +
                    "BROKEN_ANIM,ANIME,2\n");
            });
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));

        EmueraRuntimeResult result = await host.InitializeAsync();

        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsFatal);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("failed to load Sprite resource", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("duplicate Sprite name", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("outside its canvas", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("invalid animated Sprite declaration", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "EmueraFeatureMatrix")]
    public async Task HtmlPrintResolvesCsvSpriteAndParagraphAlignment()
    {
        string sourceImage = Path.Combine(
            RuntimeCompatibilityCli.FindRepositoryRoot(),
            "tests", "fixtures", "runtime", "em-ee-core", "resources", "cloudemuera-em-ee.png");
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "HTML_PRINT \"<p align='center'><img src='TW_title000'></p>\"\n" +
            "QUIT\n",
            configureGame: game =>
            {
                string resources = Path.Combine(game, "resources");
                File.Copy(sourceImage, Path.Combine(resources, "title.png"));
                File.WriteAllText(Path.Combine(resources, "list.csv"), "TW_title000,title.png,0,0,2,2\n");
            });
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        EmueraRuntimeResult result = await host.RunAsync();

        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        ConsoleLine line = Assert.Single(fixture.Console.Snapshot.Scrollback, item => item.Nodes.Any(node => node is SpriteNode));
        Assert.Equal(ConsoleLineAlignment.Center, line.Alignment);
        SpriteNode sprite = Assert.IsType<SpriteNode>(Assert.Single(line.Nodes));
        Assert.Equal(new ConsoleRect(0, 0, 2, 2), sprite.SourceRect);
        Assert.Equal(new ConsoleRect(0, 0, 18, 18), sprite.Destination);
        Assert.Equal("TW_title000", sprite.AltText);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "FontLayout")]
    public async Task HtmlPrintKeepsExplicitPositionsForLayeredPortraits()
    {
        // PLAY-002/COMP-007: multiple portrait layers use the same
        // button `pos` origin. The physical layout must preserve that
        // absolute coordinate instead of advancing each layer by its width.
        string sourceImage = Path.Combine(
            RuntimeCompatibilityCli.FindRepositoryRoot(),
            "tests", "fixtures", "runtime", "em-ee-core", "resources", "cloudemuera-em-ee.png");
        const string layeredHtml = "<p align='left'><nobr>" +
            "<button value='back' pos='0'><img src='PORTRAIT' width='153px' height='153px'></button>" +
            "<button value='middle' pos='0'><img src='PORTRAIT' width='153px' height='153px'></button>" +
            "<button value='front' pos='0'><img src='PORTRAIT' width='153px' height='153px'></button>" +
            "</nobr></p>";
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            $"HTML_PRINT \"{layeredHtml}\"\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:870\n字体大小:18\n每行高度:21\n",
            configureGame: game =>
            {
                string resources = Path.Combine(game, "resources");
                File.Copy(sourceImage, Path.Combine(resources, "portrait.png"));
                File.WriteAllText(Path.Combine(resources, "list.csv"), "PORTRAIT,portrait.png,0,0,2,2\n");
            });
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
            runtimeFontFamilyName: "Sarasa Fixed SC",
            webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

        EmueraRuntimeResult initialized = await host.InitializeAsync();
        Assert.True(
            initialized.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", initialized.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
        EmueraRuntimeResult result = await host.RunAsync();
        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));

        ConsoleLine line = Assert.Single(
            fixture.Console.Snapshot.Scrollback,
            item => item.Nodes.Count == 3 && item.Nodes.All(node =>
                node is PositionedInlineSegmentNode segment && segment.Action is not null));
        PositionedInlineSegmentNode[] layers = line.Nodes.Cast<PositionedInlineSegmentNode>().ToArray();
        Assert.Collection(
            layers,
            layer =>
            {
                Assert.Equal(0, layer.PositionX);
                Assert.Equal("back", layer.Action!.Value);
                Assert.Equal(153, layer.MeasuredWidth);
            },
            layer =>
            {
                Assert.Equal(0, layer.PositionX);
                Assert.Equal("middle", layer.Action!.Value);
                Assert.Equal(153, layer.MeasuredWidth);
            },
            layer =>
            {
                Assert.Equal(0, layer.PositionX);
                Assert.Equal("front", layer.Action!.Value);
                Assert.Equal(153, layer.MeasuredWidth);
            });
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "FontLayout")]
    public async Task HtmlPrintConvertsRelativeButtonPositionsToPhysicalPixels()
    {
        // PLAY-002/COMP-007: the pinned HtmlManager stores button pos in
        // hundredths of the configured font size (800 means 8 em), while the
        // browser-facing positioned segment uses physical pixels. Both
        // interactive and noninteractive positioned parts must cross that
        // boundary exactly once.
        const string html = "<p align='left'><nobr>" +
            "<button value='left' pos='800'>L</button>" +
            "<nonbutton title='right' pos='1600'>R</nonbutton>" +
            "</nobr></p>";
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            $"HTML_PRINT \"{html}\"\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:400\n字体大小:12\n一行の高さ:14\n");
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
            runtimeFontFamilyName: "Sarasa Fixed SC",
            webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        EmueraRuntimeResult result = await host.RunAsync();

        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
        ConsoleLine line = Assert.Single(
            fixture.Console.Snapshot.Scrollback,
            item => item.Nodes.Count == 2 && item.Nodes.All(node => node is PositionedInlineSegmentNode));
        PositionedInlineSegmentNode[] segments = line.Nodes.Cast<PositionedInlineSegmentNode>().ToArray();
        // RuntimeHostFixture binds the default 18px face; 800/1600 relative
        // units therefore become 144/288 physical pixels.
        Assert.Equal(144, segments[0].PositionX);
        Assert.Equal(288, segments[1].PositionX);
        Assert.Equal("L", RuntimeTranscriptProjector.Project(segments[0].Children));
        Assert.Equal("R", RuntimeTranscriptProjector.Project(segments[1].Children));
        Assert.NotNull(segments[0].Action);
        Assert.NotNull(segments[1].Action);
        Assert.False(segments[1].Action!.Enabled);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "FontLayout")]
    public async Task HtmlPrintPreservesNegativeButtonPositionForViewportClipping()
    {
        // PLAY-002/COMP-007: Emuera permits a negative HTML button pos. Games
        // use it to move the transparent shoulders of a wide composite beyond
        // the console viewport; the viewport, not alpha inspection, clips it.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "HTML_PRINT \"<p align='left'><nobr><button value='cutin' pos='-100'>X</button><nonbutton pos='100'>Y</nonbutton></nobr></p>\"\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:400\n字体大小:12\n一行の高さ:14\n");
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
            runtimeFontFamilyName: "Sarasa Fixed SC",
            webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        PositionedInlineSegmentNode[] segments = Assert.Single(fixture.Console.Snapshot.Scrollback)
            .Nodes
            .Cast<PositionedInlineSegmentNode>()
            .ToArray();
        Assert.Equal(2, segments.Length);
        Assert.Equal(-18, segments[0].PositionX);
        Assert.Equal("cutin", segments[0].Action?.Value);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "FontLayout")]
    public async Task HtmlPrintResetsFlowCursorAfterExplicitPositionMovesBack()
    {
        // PLAY-002/COMP-007: WindowDrawer concatenates independently
        // positioned windows into one no-wrap row. When a later window moves
        // back to a lower absolute position, an unpositioned path button must
        // continue from that window, not from the row's maximum extent.
        const string html = "<p align='left'><nobr>" +
            "<nonbutton title='module' pos='8400'>H</nonbutton>" +
            "<nonbutton title='path' pos='2000'>L</nonbutton>" +
            "<button value='continue'>P</button>" +
            "</nobr></p>";
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            $"HTML_PRINT \"{html}\"\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:2000\n字体大小:18\n每行高度:21\n");
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
            runtimeFontFamilyName: "Sarasa Fixed SC",
            webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        EmueraRuntimeResult result = await host.RunAsync();

        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
        ConsoleLine line = Assert.Single(
            fixture.Console.Snapshot.Scrollback,
            item => item.Nodes.Count == 3 && item.Nodes.All(node => node is PositionedInlineSegmentNode));
        PositionedInlineSegmentNode[] segments = line.Nodes.Cast<PositionedInlineSegmentNode>().ToArray();

        Assert.True(segments[0].PositionX > segments[1].PositionX);
        Assert.Equal(segments[1].PositionX + segments[1].MeasuredWidth, segments[2].PositionX);
        Assert.True(segments[2].PositionX < segments[0].PositionX);
        Assert.Equal("H", RuntimeTranscriptProjector.Project(segments[0].Children));
        Assert.Equal("L", RuntimeTranscriptProjector.Project(segments[1].Children));
        Assert.Equal("P", RuntimeTranscriptProjector.Project(segments[2].Children));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "FontLayout")]
    public async Task HtmlPrintDivsDoNotConsumeInlineFlowWidth()
    {
        // PLAY-002/COMP-007: upstream ConsoleDivPart paints its rect as an
        // overlay and leaves the inline cursor unchanged. A screen composed
        // from sibling rect divs must therefore keep every layer at the same
        // physical-line origin instead of shifting later panels to the right.
        const string panels =
            "<div rect='81,31,1937,2812' border='31'></div>" +
            "<div rect='2050,31,3875,2812' border='31'></div>" +
            "<div rect='2130,430,3720,700' border='31'></div>";
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            $"HTML_PRINT \"{panels}\"\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:1600\n字体大小:16\n每行高度:16\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
            runtimeFontFamilyName: "Sarasa Fixed SC",
            webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        ConsoleLine line = Assert.Single(
            fixture.Console.Snapshot.Scrollback,
            item => item.Nodes.Count == 3 && item.Nodes.All(node => node is PositionedInlineSegmentNode));
        PositionedInlineSegmentNode[] layers = line.Nodes.Cast<PositionedInlineSegmentNode>().ToArray();
        Assert.All(layers, layer =>
        {
            Assert.Equal(0, layer.PositionX);
            Assert.Equal(0, layer.MeasuredWidth);
            Assert.IsType<DivNode>(Assert.Single(layer.Children));
        });
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintButtonsUseTheCurrentRuntimeGeneration()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();
        headless.UpdateGeneration();
        headless.PrintHtml("<p align='left'><nobr><button value='1'>One</button></nobr></p>", toPrintBuffer: false);

        ButtonNode button = Assert.IsType<ButtonNode>(
            Assert.Single(console.Snapshot.Scrollback.Single().Nodes));
        Assert.Equal(1, button.Generation);
        Assert.True(button.Enabled);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintDoesNotCreateAnEmptyLineForAnEmptyFragment()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.PrintHtml(string.Empty, toPrintBuffer: false);

        Assert.Empty(console.Snapshot.Scrollback);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPopPrintingStringConsumesPendingStructuredOutputOnly()
    {
        // PLAY-002/COMP-007: HTML_POPPRINTINGSTR must pop the headless
        // pending PRINT line, not the already committed browser scrollback.
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.Print("COMMITTED");
        headless.NewLine();
        headless.Print("POPPED", lineEnd: false);

        ConsoleDisplayLine[] popped = headless.PopDisplayingLines();

        Assert.Equal("POPPED", HtmlManager.DisplayLine2Html(popped, needPandN: false));
        Assert.Single(console.Snapshot.Scrollback);
        Assert.Equal("COMMITTED", RuntimeTranscriptProjector.Project(console.Snapshot.Scrollback[0].Nodes));
        Assert.Null(headless.PopDisplayingLines());
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task HtmlPopPrintingStringKeepsTheInteractivePanelInTheSameHtmlPrint()
    {
        // PLAY-002/COMP-007: eraAM2 builds the panel by embedding
        // HTML_POPPRINTINGSTR() into a later HTML_PRINT fragment. The popped
        // dialogue text, an image part and the following button must survive as
        // one output. This matches PRINTSTR's mixed print buffer more closely
        // than a text-only regression would.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "CALL BUILD_PANEL\n" +
            "QUIT\n" +
            "@BUILD_PANEL\n" +
            "#DIMS PANELHTML\n" +
            "PRINT \"POP-NAME\"\n" +
            "PRINT_IMG \"POP-IMG\"\n" +
            "PANELHTML = <nobr>%HTML_POPPRINTINGSTR()%\n" +
            "PANELHTML += \"<button value='panel'>PANEL</button></nobr>\"\n" +
            "HTML_PRINT PANELHTML\n" +
            "RETURN\n",
            configureGame: game =>
            {
                string sourceImage = Path.Combine(
                    RuntimeCompatibilityCli.FindRepositoryRoot(),
                    "tests", "fixtures", "runtime", "v18-core", "resources", "cloudemuera-v18.png");
                string resources = Path.Combine(game, "resources");
                File.Copy(sourceImage, Path.Combine(resources, "pop.png"));
                File.WriteAllText(Path.Combine(resources, "sprites.csv"), "POP-IMG,pop.png,0,0,2,2\n");
            });
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));

        EmueraRuntimeResult initialized = await host.InitializeAsync();
        Assert.True(
            initialized.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", initialized.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
        EmueraRuntimeResult result = await host.RunAsync();

        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
        ConsoleLine line = Assert.Single(fixture.Console.Snapshot.Scrollback);
        ButtonNode button = Assert.IsType<ButtonNode>(Assert.Single(line.Nodes, node => node is ButtonNode));
        Assert.Equal("panel", button.Value);
        Assert.Equal("PANEL", RuntimeTranscriptProjector.Project(button.Children));
        Assert.Contains("POP-NAME", RuntimeTranscriptProjector.Project(line.Nodes), StringComparison.Ordinal);
        Assert.Single(EnumerateSpriteNodes(line.Nodes));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintStartsImagesOnANewLineAfterPartialPrintOutput()
    {
        // PLAY-002/COMP-007: HTML_PRINT flushes a partial PRINT line but
        // starts its own physical display row. This keeps the right-aligned
        // clock between the leading marker and the following status output.
        // Direct console tests must not inherit the last Runtime fixture's
        // static DrawableWidth, which varies with the selected font fixture.
        MinorShift.Emuera.Runtime.Config.ConfigData.ResetHeadless();
        MinorShift.Emuera.Runtime.Config.Config.SetConfig(MinorShift.Emuera.Runtime.Config.ConfigData.Instance);
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(
            console,
            console.Clock,
            CancellationToken.None,
            name => string.Equals(name, "CLOCK", StringComparison.OrdinalIgnoreCase)
                ? new RuntimeSpriteDefinition("sha256-clock", 0, 0, 54, 16, 0, 0, 54, 16)
                : null);
        headless.BeginExecutionOutput();

        headless.Print("*", lineEnd: false);
        headless.PrintHtml("<p align='right'><nobr><img src='CLOCK' height='8px' ypos='4px'></nobr></p>", toPrintBuffer: false);
        headless.Print("春之月");
        headless.NewLine();
        headless.printCustomBar("--Status---------", isConst: false);
        headless.NewLine();

        Assert.Collection(
            console.Snapshot.Scrollback,
            line => Assert.Equal("*", RuntimeTranscriptProjector.Project(line.Nodes)),
            line =>
            {
                Assert.Equal(ConsoleLineAlignment.Right, line.Alignment);
                Assert.IsType<SpriteNode>(Assert.Single(line.Nodes));
            },
            line => Assert.Equal("春之月", RuntimeTranscriptProjector.Project(line.Nodes)),
            line =>
            {
                // COMP-007: DRAWLINEFORM fills the drawable width like the
                // desktop PrintBar; the headless no longer emits one glyph.
                Assert.True(line.NoWrap);
                string projected = RuntimeTranscriptProjector.Project(line.Nodes);
                Assert.StartsWith("--Status---------", projected, StringComparison.Ordinal);
                Assert.True(projected.Length > 40, "DRAWLINEFORM should expand past a single copy.");
            });
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintImageKeepsTheYposPercentageInTheSpriteDestination()
    {
        // PLAY-002/COMP-007: eraTW draws its clock with ypos as a font-size
        // percentage so the image overlays the rows below the DRAWLINE. The
        // translated SpriteNode must preserve that offset for the browser.
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(
            console,
            console.Clock,
            CancellationToken.None,
            name => string.Equals(name, "CLOCK", StringComparison.OrdinalIgnoreCase)
                ? new RuntimeSpriteDefinition("sha256-clock", 0, 0, 100, 100, 0, 0, 100, 100)
                : null);
        headless.BeginExecutionOutput();

        headless.PrintHtml(
            "<p align='right'><nobr><img src='CLOCK' height='500' ypos='201'></nobr></p>",
            toPrintBuffer: false);

        SpriteNode sprite = Assert.IsType<SpriteNode>(
            Assert.Single(Assert.Single(console.Snapshot.Scrollback).Nodes));
        Assert.Equal(0, sprite.Destination.X);
        Assert.True(sprite.Destination.Y > 0, "ypos must not be dropped by the translator.");
        Assert.Equal(
            sprite.Destination.Height * 201 / 500,
            sprite.Destination.Y);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintStampsButtonsInsideDivsWithTheCurrentRuntimeGeneration()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();
        headless.UpdateGeneration();

        headless.PrintHtml(
            "<div width='80px' height='20px'><button value='1'>One</button></div>",
            toPrintBuffer: false);

        DivNode div = Assert.IsType<DivNode>(Assert.Single(console.Snapshot.Scrollback.Single().Nodes));
        Assert.Equal(1, Assert.IsType<ButtonNode>(Assert.Single(div.Children)).Generation);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintCarriesParagraphLayoutAcrossExplicitBreaks()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.PrintHtml("<p align='right'><nobr>One<br>Two</nobr></p>", toPrintBuffer: false);

        Assert.Collection(
            console.Snapshot.Scrollback,
            line =>
            {
                Assert.Equal(ConsoleLineAlignment.Right, line.Alignment);
                Assert.True(line.NoWrap);
                Assert.Equal("One", Assert.IsType<TextNode>(Assert.Single(line.Nodes)).Text);
            },
            line =>
            {
                Assert.Equal(ConsoleLineAlignment.Right, line.Alignment);
                Assert.True(line.NoWrap);
                Assert.Equal("Two", Assert.IsType<TextNode>(Assert.Single(line.Nodes)).Text);
            });
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintIslandUsesTheSameUpstreamParserAndStructuredNodes()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.PrintHTMLIsland("<p align='center'><nobr><button value='go'>Go</button><br><shape type='rect' param='4px'></nobr></p>");

        HtmlIslandDrawable island = Assert.IsType<HtmlIslandDrawable>(Assert.Single(console.Snapshot.CanvasScene.Drawables));
        Assert.True(island.IsStructured);
        Assert.Collection(
            island.StructuredNodes!,
            node => Assert.IsType<ButtonNode>(node),
            node => Assert.IsType<LineBreakNode>(node),
            node => Assert.IsType<ShapeNode>(node));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintBufferKeepsUpstreamBreaksInsideThePrintBufferBoundary()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.Print("before", lineEnd: false);
        headless.PrintHtml("<br>after", toPrintBuffer: true);
        Assert.Empty(console.Snapshot.Scrollback);

        headless.PrintFlush(force: true);

        Assert.Collection(
            console.Snapshot.Scrollback,
            line => Assert.Equal("before", RuntimeTranscriptProjector.Project(line.Nodes)),
            line => Assert.Equal("after", RuntimeTranscriptProjector.Project(line.Nodes)));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintDoesNotCreateAnExtraLineAfterAnUpstreamTrailingBreak()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.PrintHtml("<p align='left'>One<br>", toPrintBuffer: false);

        Assert.Single(console.Snapshot.Scrollback);
        Assert.Equal("One", RuntimeTranscriptProjector.Project(console.Snapshot.Scrollback[0].Nodes));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintUsesUpstreamStylesEntitiesAndOmittedParagraphClosures()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        // The pinned parser permits only the p/nobr closing tags to be
        // omitted at fragment end, and applies style bits independently.
        headless.PrintHtml(
            "<p align='center'><nobr><b><i><u><s><font face='logical' color='#112233' bcolor='#445566'>A &amp; &#x42;</font></s></u></i></b>",
            toPrintBuffer: false);

        ConsoleLine line = Assert.Single(console.Snapshot.Scrollback);
        Assert.Equal(ConsoleLineAlignment.Center, line.Alignment);
        Assert.True(line.NoWrap);
        TextNode text = Assert.IsType<TextNode>(Assert.Single(line.Nodes));
        Assert.Equal("A & B", text.Text);
        Assert.Equal(
            ConsoleFontStyle.Bold | ConsoleFontStyle.Italic | ConsoleFontStyle.Underline | ConsoleFontStyle.Strike,
            text.Style.Decorations);
        Assert.Equal("session-default", text.Style.FontFamily);
        Assert.Equal(new RuntimeConsoleColor(0x11, 0x22, 0x33), text.Style.Foreground);
        Assert.Equal(new RuntimeConsoleColor(0x44, 0x55, 0x66), text.Style.ButtonColor);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void PrintButtonsPreserveTheCurrentErbForegroundColor()
    {
        // PLAY-002/COMP-007: PRINTBUTTON_EX's bright and dark branches must
        // survive the structured button projection.
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.SetStringStyle(Color.FromArgb(255, 255, 255));
        headless.PrintButton("明るい", "bright");
        headless.NewLine();
        headless.SetStringStyle(Color.FromArgb(96, 96, 96));
        headless.PrintButton("暗い", "dark");
        headless.NewLine();

        Assert.Collection(
            console.Snapshot.Scrollback,
            line => Assert.Equal(
                new RuntimeConsoleColor(255, 255, 255),
                Assert.IsType<TextNode>(Assert.IsType<ButtonNode>(Assert.Single(line.Nodes)).Children.Single()).Style.Foreground),
            line => Assert.Equal(
                new RuntimeConsoleColor(96, 96, 96),
                Assert.IsType<TextNode>(Assert.IsType<ButtonNode>(Assert.Single(line.Nodes)).Children.Single()).Style.Foreground));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void AutoButtonizedNumericButtonPreservesLabelsAboveTheLegacyNodeLimit()
    {
        // COMP-007: desktop Emuera keeps every styled display segment in an
        // implicit numeric button. CloudEmuera must not reject valid labels
        // merely because they cross the former 16-node protocol threshold.
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        for (int index = 0; index < 17; index++)
        {
            headless.SetStringStyle(index % 2 == 0 ? Color.White : Color.LightGray);
            headless.Print(index == 0 ? "[1]" : " choice", lineEnd: false);
        }
        headless.NewLine();

        ButtonNode button = Assert.IsType<ButtonNode>(Assert.Single(console.Snapshot.Scrollback.Single().Nodes));
        Assert.Equal("1", button.Value);
        Assert.Equal(17, button.Children.Count);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void PrintLineEndMergesPartialOutputThroughTheStructuredInlineOperation()
    {
        // PLAY-002/COMP-007: PRINT/PRINTL boundaries must retain upstream
        // physical-line versus logical-line semantics.
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.Print("春の", lineEnd: false);
        headless.PrintFlush(force: false);
        headless.Print("月", lineEnd: true);
        headless.NewLine();

        Assert.Single(console.Snapshot.Scrollback);
        Assert.Equal("春の月", RuntimeTranscriptProjector.Project(console.Snapshot.Scrollback[0].Nodes));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void DrawLineUsesTheFollowingNewLineInsteadOfEmittingAnExtraBlankLine()
    {
        // PLAY-002/COMP-007: DRAWLINEFORM must not add an extra structured row.
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.printCustomBar("--Status---------", isConst: false);
        headless.NewLine();

        Assert.Single(console.Snapshot.Scrollback);
        ConsoleLine barLine = console.Snapshot.Scrollback[0];
        Assert.True(barLine.NoWrap);
        string projected = RuntimeTranscriptProjector.Project(barLine.Nodes);
        Assert.StartsWith("--Status---------", projected, StringComparison.Ordinal);
        Assert.True(projected.Length > 40, "DRAWLINEFORM should expand past a single copy.");
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void DrawLineExpandsTheConfiguredCharacterAcrossTheDrawableWidth()
    {
        // PLAY-002/COMP-007: DRAWLINE/PRINTBAR prints the stored bar string
        // repeated until it fills Config.DrawableWidth, matching upstream
        // StringMeasure.GetDisplayLength semantics (single "*" is not enough).
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.setStBar("*");
        headless.PrintBar();
        headless.NewLine();

        ConsoleLine barLine = Assert.Single(console.Snapshot.Scrollback);
        Assert.True(barLine.NoWrap);
        string projected = RuntimeTranscriptProjector.Project(barLine.Nodes);
        Assert.False(projected.Contains(' ', StringComparison.Ordinal));
        Assert.All(projected, character => Assert.Equal('*', character));
        Assert.True(projected.Length > 40, "DRAWLINE must produce a full-width bar, not a single glyph.");
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task BrowserWidthCapsTheConfiguredWindowWidthAfterConfigLoading()
    {
        // PLAY-009/COMP-007: the Worker chooses the effective runtime width
        // when it starts; loading emuera.config must not overwrite that cap.
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(browserWidth: 390);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        Assert.Equal(390, fixture.Console.Snapshot.WindowMetadata.ViewportWidth);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task HostOverridesFixedGameWindowAndFontConfiguration()
    {
        // PLAY-009/PLAY-013/COMP-007: _fixed.config may lock game values, but
        // the Worker still owns the browser-capped width and bundled font.
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINTL FIXED-CONFIG\nQUIT\n",
            configuration: "Window width:870\nFont name:MS Gothic\n",
            configureGame: game => File.WriteAllText(
                Path.Combine(game, "CSV", "_fixed.config"),
                "Window width:870\nFont name:MS Gothic\n"));
        await using EmueraRuntimeHost host = fixture.CreateHost(
            browserWidth: 390,
            fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
            runtimeFontFamilyName: "Sarasa Fixed SC",
            webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        Assert.Equal(390, fixture.Console.Snapshot.WindowMetadata.ViewportWidth);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);
        Assert.Contains(
            fixture.Console.Snapshot.Scrollback,
            line => RuntimeTranscriptProjector.Project(line.Nodes).Contains("FIXED-CONFIG", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(RuntimeWidthMode.Original, null, 390, 760)]
    [InlineData(RuntimeWidthMode.Max, null, 2500, 2000)]
    [InlineData(RuntimeWidthMode.Max, null, 900, 900)]
    [InlineData(RuntimeWidthMode.Adaptive, null, 390, 390)]
    [InlineData(RuntimeWidthMode.Custom, 1200, 1600, 1200)]
    [InlineData(RuntimeWidthMode.Custom, 1200, 900, 1200)]
    [Trait("Category", "RuntimeBridge")]
    public async Task ConfiguredWidthModeSelectsTheAuthoritativeLayoutWidth(RuntimeWidthMode widthMode, int? customWidth, int browserWidth, int expectedWidth)
    {
        // SESS-014/PLAY-015: Original uses WindowX exactly; Max keeps its
        // 2000px cap; Adaptive is the former Origin behavior; Custom uses the
        // persisted value without a browser-width cap.
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(browserWidth: browserWidth, widthMode: widthMode, customWidth: customWidth);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        Assert.Equal(expectedWidth, fixture.Console.Snapshot.WindowMetadata.ViewportWidth);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void CustomDrawLineRejectsAnEmptyBarString()
    {
        // Upstream printCustomBar throws Error.EmptyDrawline for an empty bar;
        // the headless console must keep that failure path instead of silently
        // emitting an empty row.
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        Assert.Throws<MinorShift.Emuera.Runtime.Utils.CodeEE>(
            () => headless.printCustomBar(string.Empty, isConst: true));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void UpstreamDesktopEntrypointsAndHeadlessParseFragmentShareButtonSemantics()
    {
        const string fragment = "<p align='left'><nobr><button value='42'>Answer</button></nobr></p>";

        ConsoleButtonString[] buttons = HtmlManager.Html2ButtonList(fragment, null, null);
        ConsoleButtonString button = Assert.Single(buttons);
        Assert.True(button.IsButton);
        Assert.True(button.IsInteger);
        Assert.Equal("42", button.Inputs);

        ConsoleDisplayLine[] displayLines = HtmlManager.Html2DisplayLine(fragment, null, null);
        ConsoleDisplayLine line = Assert.Single(displayLines);
        Assert.Equal(DisplayLineAlignment.LEFT, line.Align);

        UpstreamHtmlFragment semantic = HtmlManager.ParseFragment(fragment, new UpstreamHtmlParseOptions
        {
            Mode = UpstreamHtmlParseMode.PrintBufferParts,
            Budget = new UpstreamHtmlParseBudget(1024, 32, 4, 32, 64, 1024)
        });
        UpstreamHtmlSegment segment = Assert.IsType<UpstreamHtmlSegment>(
            Assert.Single(semantic.Sequence.Items).Segment);
        Assert.True(segment.IsInteractive);
        Assert.Equal(UpstreamHtmlButtonValueKind.Integer, segment.ValueKind);
        Assert.Equal("42", segment.Value);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task HtmlPrintRealFixturePreservesUpstreamOmissionAndInterleavedStyleState()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "HTML_PRINT \"<p align='center'><nobr><b><i>REAL-UPSTREAM</b></i>\"\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        EmueraRuntimeResult result = await host.RunAsync();

        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        ConsoleLine line = Assert.Single(fixture.Console.Snapshot.Scrollback);
        Assert.Equal(ConsoleLineAlignment.Center, line.Alignment);
        Assert.True(line.NoWrap);
        TextNode text = Assert.IsType<TextNode>(Assert.Single(line.Nodes));
        Assert.Equal("REAL-UPSTREAM", text.Text);
        Assert.Equal(ConsoleFontStyle.Bold | ConsoleFontStyle.Italic, text.Style.Decorations);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintPreservesButtonMetadataAndClearbuttonDisablesSelection()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.PrintHtml(
            "<p align='left'><nobr><button value='42' title='go' pos='7'>Go</button><nonbutton title='static' pos='8'>Static</nonbutton></nobr></p>",
            toPrintBuffer: false);

        ConsoleLine line = Assert.Single(console.Snapshot.Scrollback);
        Assert.Collection(
            line.Nodes,
            node =>
            {
                ButtonNode button = Assert.IsType<ButtonNode>(node);
                Assert.Equal("42", button.Value);
                Assert.Equal("go", button.Tooltip);
                Assert.True(button.Enabled);
                Assert.Equal(7, button.PositionX);
            },
            node =>
            {
                ButtonNode nonbutton = Assert.IsType<ButtonNode>(node);
                Assert.Equal("static", nonbutton.Tooltip);
                Assert.False(nonbutton.Enabled);
                Assert.Equal(8, nonbutton.PositionX);
            });

        var clearConsole = new StructuredGameConsole();
        var clearHeadless = new EmueraConsole(clearConsole, clearConsole.Clock, CancellationToken.None);
        clearHeadless.BeginExecutionOutput();
        clearHeadless.PrintHtml(
            "<p align='left'><nobr><clearbutton notooltip='true'><button value='7' title='hidden'>Clear</button></clearbutton></nobr></p>",
            toPrintBuffer: false);

        ConsoleLine clearLine = Assert.Single(clearConsole.Snapshot.Scrollback);
        Assert.DoesNotContain(clearLine.Nodes, node => node is ButtonNode);
        Assert.Equal("Clear", RuntimeTranscriptProjector.Project(clearLine.Nodes));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintMapsImageVariantsAndMixedNumberGeometryThroughTheResolver()
    {
        var console = new StructuredGameConsole();
        var sprites = new Dictionary<string, RuntimeSpriteDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["main"] = new(
                "asset-main", 1, 2, 8, 10, 2, 3, 16, 20,
                [new RuntimeSpriteFrame("asset-frame", 0, 0, 4, 5, 1, 2, 50)]),
            ["hover"] = new("asset-hover", 0, 0, 8, 10, 0, 0, 16, 20),
            ["map"] = new("asset-map", 0, 0, 8, 10, 0, 0, 16, 20)
        };
        var headless = new EmueraConsole(
            console,
            console.Clock,
            CancellationToken.None,
            name => sprites.GetValueOrDefault(name));
        headless.BeginExecutionOutput();

        headless.PrintHtml(
            "<img src='main' srcb='hover' srcm='map' height='20px' width='-30px' ypos='-4px'>",
            toPrintBuffer: false);

        SpriteNode sprite = Assert.IsType<SpriteNode>(Assert.Single(console.Snapshot.Scrollback.Single().Nodes));
        Assert.Equal("asset-main", sprite.AssetId.Value);
        Assert.Equal(new ConsoleRect(1, 2, 8, 10), sprite.SourceRect);
        Assert.Equal(new ConsoleRect(33, -1, 30, 20), sprite.Destination);
        Assert.Equal("asset-hover", sprite.HoverAssetId?.Value);
        Assert.Equal("asset-map", sprite.MappingAssetId?.Value);
        Assert.Single(sprite.AnimationFrames);
        Assert.Equal("asset-frame", sprite.AnimationFrames[0].AssetId.Value);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintMapsDivBoxModelAndAbsoluteLayoutWithoutReparsingAltText()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.PrintHtml(
            "<div rect='10px,20px,80px,40px' depth='3' color='#112233' display='absolute' margin='1px,2px,3px,4px' padding='5px,6px,7px,8px' border='1px' radius='2px' bcolor='#445566'>D</div>",
            toPrintBuffer: false);

        DivNode div = Assert.IsType<DivNode>(Assert.Single(console.Snapshot.Scrollback.Single().Nodes));
        Assert.Equal(new ConsoleRect(10, 20, 80, 40), div.Bounds);
        Assert.Equal(3, div.ZIndex);
        Assert.False(div.IsRelative);
        Assert.Equal(new RuntimeConsoleColor(0x11, 0x22, 0x33), div.Background);
        ConsoleBoxModel box = Assert.IsType<ConsoleBoxModel>(div.Box);
        Assert.Equal(new ConsoleInsets(1, 2, 3, 4), box.Margin);
        Assert.Equal(new ConsoleInsets(5, 6, 7, 8), box.Padding);
        Assert.Equal(new ConsoleInsets(1, 1, 1, 1), box.Border);
        Assert.Equal(new ConsoleInsets(2, 2, 2, 2), box.Radius);
        Assert.All(box.BorderColors, color => Assert.Equal(new RuntimeConsoleColor(0x44, 0x55, 0x66), color));
        Assert.Equal("D", RuntimeTranscriptProjector.Project(div.Children));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintBudgetRejectionDoesNotConsumePendingOutput()
    {
        ConsoleContractLimits limits = ConsoleContractLimits.Default with { MaxHtmlTagCount = 1 };
        var console = new StructuredGameConsole(
            clock: new TimeProviderRuntimeClock(),
            options: new ConsoleHistoryOptions { ContractLimits = limits });
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();
        headless.Print("pending", lineEnd: false);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => headless.PrintHtml("<b>rejected</b>", toPrintBuffer: false));
        Assert.Equal("EMUERA_HTML_TAG_LIMIT", exception.Message);
        Assert.Empty(console.Snapshot.Scrollback);

        headless.PrintFlush(force: true);
        Assert.Equal("pending", RuntimeTranscriptProjector.Project(Assert.Single(console.Snapshot.Scrollback).Nodes));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HtmlPrintAllowsLargeLayeredPortraitFragmentWithinParserBudget()
    {
        // PLAY-002/COMP-007: the portrait converter emits a large fragment
        // containing one image tag per visible layer. The default parser
        // budget must allow a normal scene while remaining finite.
        const int imageCount = 600;
        string source = new string('P', 96);
        string image = $"<img src='{source}' height='2px' width='2px' ypos='0px'>";
        string fragment = string.Concat(Enumerable.Repeat(image, imageCount));
        Assert.True(fragment.Length > 32_768);

        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(
            console,
            console.Clock,
            CancellationToken.None,
            name => string.Equals(name, source, StringComparison.Ordinal)
                ? new RuntimeSpriteDefinition("asset-portrait", 0, 0, 2, 2, 0, 0, 2, 2)
                : null);
        headless.BeginExecutionOutput();

        headless.PrintHtml(fragment, toPrintBuffer: false);

        SpriteNode[] sprites = EnumerateSpriteNodes(
            console.Snapshot.Scrollback.SelectMany(line => line.Nodes)).ToArray();
        Assert.Equal(imageCount, sprites.Length);
        Assert.All(sprites, sprite => Assert.Equal("asset-portrait", sprite.AssetId.Value));
    }

    [Fact]
    [Trait("Category", "EmueraFeatureMatrix")]
    public void HeadlessConsolePreservesTemporaryDeleteAlignmentAndSpriteInteractionMetadata()
    {
        var console = new StructuredGameConsole();
        var sprites = new Dictionary<string, RuntimeSpriteDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["NORMAL"] = new("sha256-normal", 4, 5, 8, 10, 2, 3, 16, 20),
            ["HOVER"] = new("sha256-hover", 1, 2, 8, 10, 0, 0, 16, 20),
            ["MAP"] = new("sha256-map", 0, 0, 16, 20, 0, 0, 16, 20)
        };
        var headless = new EmueraConsole(
            console,
            console.Clock,
            CancellationToken.None,
            name => sprites.GetValueOrDefault(name));
        headless.BeginExecutionOutput();

        headless.PrintTemporaryLine("temporary");
        headless.PrintC("right", alignmentRight: true);
        headless.PrintImg(
            "NORMAL",
            "HOVER",
            "MAP",
            new MixedNum { num = 40, isPx = true },
            null,
            new MixedNum { num = 5, isPx = true });
        headless.NewLine();

        ConsoleLine temporary = console.Snapshot.Scrollback[0];
        Assert.True(temporary.Temporary);
        ConsoleLine combined = console.Snapshot.Scrollback[1];
        Assert.Equal(ConsoleLineAlignment.Left, combined.Alignment);
        Assert.Contains(combined.Nodes, node => node is TextNode text && text.Text.Contains("right", StringComparison.Ordinal));
        SpriteNode sprite = Assert.IsType<SpriteNode>(Assert.Single(combined.Nodes, node => node is SpriteNode));
        Assert.Equal(new ConsoleRect(4, 5, 8, 10), sprite.SourceRect);
        Assert.Equal(new ConsoleRect(4, 11, 32, 40), sprite.Destination);
        Assert.Equal("sha256-hover", sprite.HoverAssetId?.Value);
        Assert.Equal("sha256-map", sprite.MappingAssetId?.Value);

        headless.deleteLine(1);
        headless.RefreshStrings(false);
        Assert.Single(console.Snapshot.Scrollback);
        Assert.Equal("temporary", Assert.IsType<TextNode>(Assert.Single(console.Snapshot.Scrollback[0].Nodes)).Text);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HeadlessConsoleReusesLineIdentityForClearAndImmediateReprint()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();
        headless.Print("old");
        headless.NewLine();
        string lineId = Assert.Single(console.Snapshot.Scrollback).LineId;

        headless.deleteLine(1);
        headless.Print("new");
        headless.NewLine();

        ConsoleLine line = Assert.Single(console.Snapshot.Scrollback);
        Assert.Equal(lineId, line.LineId);
        Assert.Equal("new", Assert.IsType<TextNode>(Assert.Single(line.Nodes)).Text);
        Assert.Contains(console.StateStore.TransactionHistory, transaction => transaction.Transaction.Operations.Any(operation => operation is ReplaceLineOperation));
        Assert.DoesNotContain(console.StateStore.TransactionHistory, transaction => transaction.Transaction.Operations.Any(operation => operation is DeleteLinesOperation));
    }

    [Fact]
    [Trait("Category", "FontLayout")]
    [Trait("Category", "RuntimeBridge")]
    public async Task ClearLineRemovesAllPhysicalRowsOfWrappedLogicalLine()
    {
        // P1-S04/COMP-007: the eraTW movement loop prints a fullwidth-space
        // progress line and immediately CLEARLINEs it. Once Worker layout is
        // authoritative, one logical line can span several physical rows;
        // CLEARLINE 1 must remove the complete group, not only its last row.
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        string movementLine = new string('　', 40) + "（少女移動中…）";
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTL BEFORE\n" +
            $"PRINTL {movementLine}\n" +
            "CLEARLINE 1\n" +
            "PRINTL AFTER\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:120\n字体大小:18\n每行高度:20\n",
            erbCodePage: 932);
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
            runtimeFontFamilyName: "Sarasa Fixed SC",
            webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

        EmueraRuntimeResult initialized = await host.InitializeAsync();
        Assert.True(
            initialized.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", initialized.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        IReadOnlyList<ConsoleLine> lines = fixture.Console.Snapshot.Scrollback;
        Assert.Equal(["BEFORE", "AFTER"], lines.Select(line => RuntimeTranscriptProjector.Project(line.Nodes)));
        Assert.DoesNotContain(lines, line => string.IsNullOrWhiteSpace(RuntimeTranscriptProjector.Project(line.Nodes)));
        Assert.Contains(
            fixture.Console.StateStore.TransactionHistory,
            transaction => transaction.Transaction.Operations
                .OfType<AppendLineOperation>()
                .Any(operation => operation.Line.PhysicalIndex > 0));
        Assert.Contains(
            fixture.Console.StateStore.TransactionHistory,
            transaction => transaction.Transaction.Operations.Any(operation => operation is DeleteLinesOperation));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task PrintCUsesPrintLBoundariesAndPreservesFixedWidthButtonColumns()
    {
        // PLAY-002/COMP-007: PRINTC is a fixed-width field append operation;
        // only PRINTL creates the two logical rows in this menu-shaped output.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTC FIRST[800]\n" +
            "PRINTC SECOND[801]\n" +
            "PRINTL\n" +
            "PRINTC THIRD[803]\n" +
            "PRINTC FOURTH[804]\n" +
            "PRINTC FIFTH[809]\n" +
            "PRINTC SIXTH [888]\n" +
            "PRINTL\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\nPRINTC并列数量:4\nPRINTC文字数量:25\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        IReadOnlyList<ConsoleLine> lines = fixture.Console.Snapshot.Scrollback;
        Assert.Equal(2, lines.Count);
        Assert.Equal(["800", "801"], lines[0].Nodes.OfType<ButtonNode>().Select(button => button.Value));
        Assert.Equal(["803", "804", "809", "888"], lines[1].Nodes.OfType<ButtonNode>().Select(button => button.Value));

        string firstRow = RuntimeTranscriptProjector.Project(lines[0].Nodes);
        string secondRow = RuntimeTranscriptProjector.Project(lines[1].Nodes);
        Assert.Equal(50, EncodingHandler.shiftjisEncoding.GetByteCount(firstRow));
        Assert.Equal(100, EncodingHandler.shiftjisEncoding.GetByteCount(secondRow));
        Assert.DoesNotContain("\n", firstRow, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", secondRow, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task EraFlStyleSingleTableIsNotTruncatedByStructuredByteBudget()
    {
        // PLAY-002/COMP-007: eraFL redraws one fixed-height table made of
        // positioned button columns. A byte-budget eviction must not remove
        // part of this legal table or create a partial physical row.
        const int rowCount = 39;
        const int columnCount = 27;
        var script = new StringBuilder("@SYSTEM_TITLE\n");
        for (int row = 0; row < rowCount; row++)
        {
            for (int column = 0; column < columnCount; column++)
                script.Append("PRINTBUTTON \"C").Append(column).Append("\", ").Append(column + 1).Append('\n');
            script.Append("PRINTL\n");
        }
        script.Append("QUIT\n");

        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogDigest = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(Path.Combine(fontRoot, "catalog.json"))))
            .ToLowerInvariant();
        using var fixture = RuntimeHostFixture.Create(
            script.ToString(),
            configuration: "Use sav folder:NO\n窗口宽度:1400\n字体大小:16\n一行の高さ:16\nボタンの途中で行を折りかえさない:YES\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            browserWidth: 1180,
            fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
            runtimeFontFamilyName: "Sarasa Fixed SC",
            webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        EmueraRuntimeResult result = await host.RunAsync();

        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));

        ConsoleSnapshot snapshot = fixture.Console.Snapshot;
        Assert.False(snapshot.Truncation.WasTruncated);
        Assert.Equal(rowCount, snapshot.Scrollback.Count);
        Assert.All(snapshot.Scrollback, line =>
        {
            Assert.Equal(line.LogicalLineId, line.LineId);
            Assert.Equal(0, line.PhysicalIndex);
            Assert.Equal(columnCount, line.Nodes.OfType<PositionedInlineSegmentNode>().Count());
            Assert.Equal(columnCount, line.Nodes.OfType<PositionedInlineSegmentNode>().Count(segment => segment.Action is not null));
        });
        Assert.True(snapshot.EstimatedBytes <= new ConsoleHistoryOptions().MaxEstimatedBytes);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task PrintCUsesReplacementWidthForCharactersOutsideShiftJis()
    {
        // COMP-007: a Unicode display label must not crash width calculation
        // when it cannot be represented in the legacy Shift-JIS code page.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTC 动作[800]\n" +
            "PRINTL\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\nPRINTC文字数量:25\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        ConsoleLine line = Assert.Single(fixture.Console.Snapshot.Scrollback);
        ButtonNode button = Assert.IsType<ButtonNode>(Assert.Single(line.Nodes));
        Assert.Equal("800", button.Value);
        Assert.Contains("动作", RuntimeTranscriptProjector.Project(button.Children), StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "FontLayout")]
    [Trait("Category", "PrintCCompatibility")]
    [InlineData("sarasa-fixed-sc-1.0.40-light", "sarasa-fixed-sc-1.0.40-light.ttf", "Sarasa Fixed SC", "46a4532b5eea58684509df92552107d93c3102f352a988ab1c31f21812d64427")]
    [InlineData("sarasa-fixed-sc-1.0.40-regular", "sarasa-fixed-sc-1.0.40-regular.ttf", "Sarasa Fixed SC", "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3")]
    [InlineData("sarasa-fixed-sc-1.0.40-medium", "sarasa-fixed-sc-1.0.40-medium.ttf", "Sarasa Fixed SC", "3fc2b9e5d026108d4bf3a62e9d34850f2c480b8d62455db29ea1ff262152cb69")]
    [InlineData("lxgw-bright-code-2.922-extralight", "lxgw-bright-code-2.922-extralight.ttf", "LXGW Bright Code", "fa949cdbe0aa291e4a9facdbd4f475d5529a4a5bd3542855a2aae79502443dc0")]
    [InlineData("lxgw-bright-code-2.922-light", "lxgw-bright-code-2.922-light.ttf", "LXGW Bright Code", "5d37f7c267fae54dd87bb2efea3f3753464746a1b6b236b96ff4d4ee2c9a5098")]
    [InlineData("lxgw-bright-code-2.922-regular", "lxgw-bright-code-2.922-regular.ttf", "LXGW Bright Code", "9a9ee8e1ea7de3cb42b96b1fdde7953698e07be458eb6e73385052a799b60c1c")]
    public async Task EveryBundledFaceBindsBeforeConfigAndPublishesMeasuredPhysicalLines(
        string faceId,
        string ttfFileName,
        string runtimeFamilyName,
        string webFontAssetDigest)
    {
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTL 中文中文中文中文中文中文中文 ABCDEFGHIJKLM\n" +
            "PRINTBUTTON \"[继续]\", 7\n" +
            "PRINTL\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:120\n字体大小:18\n每行高度:20\n",
            erbCodePage: 932);
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: faceId,
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", ttfFileName),
            runtimeFontFamilyName: runtimeFamilyName,
            webFontAssetDigest: webFontAssetDigest);

        EmueraRuntimeResult initialized = await host.InitializeAsync();
        Assert.True(
            initialized.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", initialized.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        Assert.Equal(faceId, fixture.Console.Snapshot.WindowMetadata.FontFaceId);
        Assert.Equal(webFontAssetDigest, fixture.Console.Snapshot.WindowMetadata.WebFontAssetDigest);
        Assert.Contains(fixture.Console.Snapshot.Scrollback, line => line.PhysicalIndex > 0);
        Assert.All(fixture.Console.Snapshot.Scrollback, line =>
        {
            Assert.True(line.LayoutWidth > 0);
            Assert.Equal(19, line.LineHeight);
            Assert.All(line.Nodes, node => Assert.IsType<PositionedInlineSegmentNode>(node));
            Assert.All(
                line.Nodes.Cast<PositionedInlineSegmentNode>(),
                segment =>
                {
                    Assert.InRange(segment.PositionX, 0, 1_000_000);
                    Assert.InRange(segment.MeasuredWidth, 0, 1_000_000);
                    Assert.InRange((long)segment.PositionX + segment.MeasuredWidth, 0, 1_000_000);
                    Assert.All(segment.Children.OfType<TextNode>(), text => Assert.Equal("session-default", text.Style.FontFamily));
                });
        });
    }

    [Fact]
    [Trait("Category", "FontLayout")]
    [Trait("Category", "RuntimeBridge")]
    public async Task GraphicsModeUsesBundledMetricsForEraSqcTitleLine()
    {
        // PLAY-014/COMP-007: eraSQC selects GRAPHICS in emuera.config. The
        // headless worker must still measure "[0] 开始新游戏" with the bound
        // TTF rather than libgdiplus, because the browser renders its
        // matching WOFF2 face.
        const string title = "[0] 开始新游戏";
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();

        async Task<int> MeasureTitleAsync(string drawingMode)
        {
            using var fixture = RuntimeHostFixture.Create(
                $"@SYSTEM_TITLE\nPRINTL {title}\nQUIT\n",
                configuration: $"Use sav folder:NO\n描画インターフェース:{drawingMode}\n窗口宽度:1150\n字体大小:19\n一行の高さ:19\n");
            await using EmueraRuntimeHost host = fixture.CreateHost(
                runDeadline: TimeSpan.FromSeconds(8),
                fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
                fontCatalogDigest: catalogDigest,
                runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
                runtimeFontFamilyName: "Sarasa Fixed SC",
                webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

            Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
            Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

            ConsoleLine line = Assert.Single(
                fixture.Console.Snapshot.Scrollback,
                item => RuntimeTranscriptProjector.Project(item.Nodes) == title);
            Assert.Contains(title, RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes), StringComparison.Ordinal);
            return Assert.IsType<PositionedInlineSegmentNode>(Assert.Single(line.Nodes)).MeasuredWidth;
        }

        int graphicsWidth = await MeasureTitleAsync("GRAPHICS");
        int textRendererWidth = await MeasureTitleAsync("TEXTRENDERER");
        Assert.Equal(textRendererWidth, graphicsWidth);
    }

    [Fact]
    [Trait("Category", "FontLayout")]
    public async Task BundledFontMetricsPreserveFullwidthCjkAdvance()
    {
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTL AAAAAA\n" +
            "PRINTL 中文中文中文\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:320\n字体大小:18\n每行高度:20\n",
            erbCodePage: 932);
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
            runtimeFontFamilyName: "Sarasa Fixed SC",
            webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        ConsoleLine ascii = Assert.Single(fixture.Console.Snapshot.Scrollback, line => RuntimeTranscriptProjector.Project(line.Nodes) == "AAAAAA");
        ConsoleLine cjk = Assert.Single(fixture.Console.Snapshot.Scrollback, line => RuntimeTranscriptProjector.Project(line.Nodes) == "中文中文中文");
        int asciiWidth = Assert.IsType<PositionedInlineSegmentNode>(Assert.Single(ascii.Nodes)).MeasuredWidth;
        int cjkWidth = Assert.IsType<PositionedInlineSegmentNode>(Assert.Single(cjk.Nodes)).MeasuredWidth;
        Assert.True(cjkWidth > asciiWidth, $"Expected CJK advance to exceed Latin advance, got ASCII={asciiWidth}, CJK={cjkWidth}.");
    }

    [Fact]
    [Trait("Category", "FontLayout")]
    public async Task BrightCodeKeepsWesternPunctuationHalfwidth()
    {
        const string western = "[]{}<>!@#$%^&*()";
        const string cjk = "中文中文中文中文中文中文中文中文";
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            $"PRINTL {western}\n" +
            $"PRINTL {cjk}\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:640\n字体大小:18\n每行高度:20\n",
            erbCodePage: 932);
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: "lxgw-bright-code-2.922-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "lxgw-bright-code-2.922-regular.ttf"),
            runtimeFontFamilyName: "LXGW Bright Code",
            webFontAssetDigest: "9a9ee8e1ea7de3cb42b96b1fdde7953698e07be458eb6e73385052a799b60c1c");

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        ConsoleLine westernLine = Assert.Single(fixture.Console.Snapshot.Scrollback, line => RuntimeTranscriptProjector.Project(line.Nodes) == western);
        ConsoleLine cjkLine = Assert.Single(fixture.Console.Snapshot.Scrollback, line => RuntimeTranscriptProjector.Project(line.Nodes) == cjk);
        int westernWidth = Assert.IsType<PositionedInlineSegmentNode>(Assert.Single(westernLine.Nodes)).MeasuredWidth;
        int cjkWidth = Assert.IsType<PositionedInlineSegmentNode>(Assert.Single(cjkLine.Nodes)).MeasuredWidth;
        Assert.InRange((double)cjkWidth / westernWidth, 1.8, 2.2);
    }

    [Theory]
    [Trait("Category", "FontLayout")]
    [Trait("Category", "RuntimeBridge")]
    [InlineData("sarasa-fixed-sc-1.0.40-regular", "sarasa-fixed-sc-1.0.40-regular.ttf", "Sarasa Fixed SC", "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3")]
    [InlineData("lxgw-bright-code-2.922-regular", "lxgw-bright-code-2.922-regular.ttf", "LXGW Bright Code", "9a9ee8e1ea7de3cb42b96b1fdde7953698e07be458eb6e73385052a799b60c1c")]
    public async Task EraTwMapButtonsKeepOneFullwidthDigitEqualToTwoAsciiDigits(
        string faceId,
        string ttfFileName,
        string runtimeFamilyName,
        string webFontAssetDigest)
    {
        // PLAY-014/COMP-007: eraTW reads two ASCII digits from its map and
        // displays one-digit destinations through TOFULL. Both forms occupy
        // one CJK cell; otherwise every following wall drifts horizontally.
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTBUTTON \"12\", 12\n" +
            "PRINTBUTTON \"１\", 1\n" +
            "PRINTL\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:320\n字体大小:16\n每行高度:16\n",
            erbCodePage: 932);
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: faceId,
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", ttfFileName),
            runtimeFontFamilyName: runtimeFamilyName,
            webFontAssetDigest: webFontAssetDigest);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        ConsoleLine line = Assert.Single(fixture.Console.Snapshot.Scrollback);
        PositionedInlineSegmentNode[] buttons = line.Nodes
            .Cast<PositionedInlineSegmentNode>()
            .Where(segment => segment.Action is not null)
            .ToArray();
        Assert.Equal(2, buttons.Length);
        Assert.Equal(buttons[0].MeasuredWidth, buttons[1].MeasuredWidth);
        Assert.Equal(buttons[0].MeasuredWidth, buttons[1].PositionX - buttons[0].PositionX);
    }

    [Theory]
    [Trait("Category", "FontLayout")]
    [Trait("Category", "RuntimeBridge")]
    [InlineData("sarasa-fixed-sc-1.0.40-regular", "sarasa-fixed-sc-1.0.40-regular.ttf", "Sarasa Fixed SC", "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3")]
    [InlineData("lxgw-bright-code-2.922-regular", "lxgw-bright-code-2.922-regular.ttf", "LXGW Bright Code", "9a9ee8e1ea7de3cb42b96b1fdde7953698e07be458eb6e73385052a799b60c1c")]
    public async Task EraTwMapWideSymbolsKeepTheCjkCellAdvance(
        string faceId,
        string ttfFileName,
        string runtimeFamilyName,
        string webFontAssetDigest)
    {
        const string mapSymbols = "■│｜∥￤─―●┏━┓￣。〓＼／☆萃";
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        string script = "@SYSTEM_TITLE\nPRINTL 中\n" +
            string.Concat(mapSymbols.Select(symbol => $"PRINTL {symbol}\n")) +
            "QUIT\n";
        using var fixture = RuntimeHostFixture.Create(
            script,
            configuration: "Use sav folder:NO\n窗口宽度:320\n字体大小:16\n每行高度:16\n",
            erbCodePage: 932);
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: faceId,
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", ttfFileName),
            runtimeFontFamilyName: runtimeFamilyName,
            webFontAssetDigest: webFontAssetDigest);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        Dictionary<string, int> widths = fixture.Console.Snapshot.Scrollback.ToDictionary(
            line => RuntimeTranscriptProjector.Project(line.Nodes),
            line => Assert.IsType<PositionedInlineSegmentNode>(Assert.Single(line.Nodes)).MeasuredWidth,
            StringComparer.Ordinal);
        int cellWidth = widths["中"];
        Assert.True(
            mapSymbols.All(symbol => widths[symbol.ToString()] == cellWidth),
            $"Expected every eraTW map symbol to use the {cellWidth}px CJK cell in {faceId}; got " +
            string.Join(", ", mapSymbols.Select(symbol => $"{symbol}= {widths[symbol.ToString()]}")));
    }

    [Fact]
    [Trait("Category", "FontLayout")]
    [Trait("Category", "RuntimeBridge")]
    public async Task PrintSingleFormsTruncatesInsteadOfWrapping()
    {
        // COMP-007: PRINTSINGLEFORMS is a single physical row. It may lose
        // the suffix at the drawable edge, but it must never create a second
        // row that can split an eraTW status/progress display.
        const string prefix = "SINGLE-FORMS-";
        string value = prefix + new string('X', 64);
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        using var fixture = RuntimeHostFixture.Create(
            $"@SYSTEM_TITLE\nPRINTSINGLEFORMS \"{value}\"\nPRINTL AFTER\nQUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:500\n字体大小:18\n每行高度:20\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
            runtimeFontFamilyName: "Sarasa Fixed SC",
            webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        ConsoleLine[] matchingLines = fixture.Console.Snapshot.Scrollback
            .Where(line => RuntimeTranscriptProjector.Project(line.Nodes).StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        ConsoleLine single = Assert.Single(matchingLines);
        string projected = RuntimeTranscriptProjector.Project(single.Nodes);
        Assert.True(single.NoWrap);
        Assert.StartsWith(prefix, projected, StringComparison.Ordinal);
        Assert.True(projected.Length < value.Length, $"Expected PRINTSINGLEFORMS to truncate '{value}', got '{projected}'.");
        Assert.All(single.Nodes.OfType<PositionedInlineSegmentNode>(), segment =>
            Assert.InRange((long)segment.PositionX + segment.MeasuredWidth, 0, single.LayoutWidth));
        Assert.Contains(fixture.Console.Snapshot.Scrollback, line => RuntimeTranscriptProjector.Project(line.Nodes) == "AFTER");
    }

    [Fact]
    [Trait("Category", "PrintCCompatibility")]
    public async Task PrintButtonCMeasuresPaddingOutsideTheActionBox()
    {
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTBUTTONC \"RIGHT\", 7\n" +
            "PRINTBUTTONLC \"LEFT\", \"left-value\"\n" +
            "PRINTL\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:320\n字体大小:18\n每行高度:20\n",
            erbCodePage: 932);
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
            runtimeFontFamilyName: "Sarasa Fixed SC",
            webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        PositionedInlineSegmentNode[] segments = fixture.Console.Snapshot.Scrollback
            .SelectMany(line => line.Nodes)
            .OfType<PositionedInlineSegmentNode>()
            .ToArray();
        PositionedInlineSegmentNode[] actions = segments.Where(segment => segment.Action is not null).ToArray();
        Assert.Collection(
            actions,
            right =>
            {
                Assert.Equal("7", right.Action!.Value);
                Assert.Equal("RIGHT", RuntimeTranscriptProjector.Project(right.Children));
            },
            left =>
            {
                Assert.Equal("left-value", left.Action!.Value);
                Assert.Equal("LEFT", RuntimeTranscriptProjector.Project(left.Children));
            });
        Assert.Contains(segments, segment => segment.Action is null && RuntimeTranscriptProjector.Project(segment.Children).Contains(' '));
        Assert.All(actions, action => Assert.True(action.MeasuredWidth < 240));
    }

    [Fact]
    [Trait("Category", "TimedInput")]
    [Trait("Category", "EmueraFeatureMatrix")]
    public async Task TimedInputRunsThroughPinnedInterpreterAndPublishesTimeoutState()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "TINPUT 30, 7, 1, \"TIME-UP\"\n" +
            "PRINTFORML RESULT={RESULT}\n" +
            "PRINTFORML ISTIMEOUT={ISTIMEOUT}\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        Assert.True(SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        Assert.Null(fixture.Console.CurrentPrompt!.DefaultValue);
        Assert.Equal(ConsolePromptTimeoutAction.ContinueWithoutValue, fixture.Console.CurrentPrompt.TimeoutAction);

        EmueraRuntimeResult result = await run;

        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        Assert.Null(fixture.Console.CurrentPrompt);
        Assert.True(fixture.Console.IsTimeOut);
        string transcript = RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes);
        Assert.Contains("TIME-UP", transcript, StringComparison.Ordinal);
        Assert.Contains("RESULT=7", transcript, StringComparison.Ordinal);
        Assert.Contains("ISTIMEOUT=1", transcript, StringComparison.Ordinal);
        Assert.Single(
            fixture.Console.StateStore.TransactionHistory.SelectMany(item => item.Transaction.Operations)
                .OfType<ClosePromptOperation>(),
            operation => operation.Reason == ConsolePromptCloseReason.TimedOut);
    }

    [Fact]
    [Trait("Category", "TimedInput")]
    [Trait("Category", "RuntimeBridge")]
    public async Task CurrentSlotInputAfterAnimatedClearlineDoesNotAbortRuntime()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTL FRAME-0\n" +
            "TINPUT 40, 9999, 1, \"TIME-UP\"\n" +
            "CLEARLINE 1\n" +
            "PRINTL FRAME-1\n" +
            "TINPUT 500, 9999, 1, \"TIME-UP\"\n" +
            "PRINTL DONE\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        Assert.True(SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        string stalePromptId = fixture.Console.CurrentPrompt!.PromptId;
        long timedOutGeneration = fixture.Console.CurrentPrompt.ButtonGeneration;
        Assert.True(SpinWait.SpinUntil(
            () => fixture.Console.CurrentPrompt is { PromptId: not null } current && current.PromptId != stalePromptId,
            TimeSpan.FromSeconds(2)));
        Assert.NotEqual(timedOutGeneration, fixture.Console.CurrentPrompt!.ButtonGeneration);

        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("late-animation-input", "1")).Kind);

        EmueraRuntimeResult result = await run;
        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        Assert.Contains("DONE", RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "TimedInput")]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "ButtonGenerationCompatibility")]
    public async Task RestartingTheSameTimedInputLineKeepsRetainedButtonsActive()
    {
        // eraAM SHOP_TIME.ERB loops on the same 30 ms TINPUT while only its
        // animation row is redrawn. The pinned newGeneration() keeps the
        // retained menu generation active until the script moves to another
        // input source line or emits a new button generation.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTBUTTON \"CHOICE\", 1\nPRINTL\n" +
            "CALL ANIMATED_INPUT\n" +
            "PRINTL DONE\nQUIT\n\n" +
            "@ANIMATED_INPUT\n" +
            "TINPUT 30, 9999, 0\n" +
            "IF RESULT == 9999\nRESTART\nENDIF\nRETURN\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        Assert.True(SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ConsolePrompt firstPrompt = fixture.Console.CurrentPrompt!;
        ButtonNode retainedButton = fixture.Console.Snapshot.VisibleNodes.OfType<ButtonNode>().Single(button => button.Value == "1");
        Assert.Equal(retainedButton.Generation, firstPrompt.ButtonGeneration);

        Assert.True(SpinWait.SpinUntil(
            () => fixture.Console.CurrentPrompt is { } current && current.PromptId != firstPrompt.PromptId,
            TimeSpan.FromSeconds(2)));
        ConsolePrompt refreshedPrompt = fixture.Console.CurrentPrompt!;
        Assert.Equal(firstPrompt.ButtonGeneration, refreshedPrompt.ButtonGeneration);
        Assert.Equal(retainedButton.Generation, refreshedPrompt.ButtonGeneration);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("era-am-retained-choice", "1", ConsoleInputSource.Button)).Kind);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await run).Status);
        Assert.Contains("DONE", RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "TimedInput")]
    [Trait("Category", "EmueraFeatureMatrix")]
    public async Task TwaitAcceptingInputUsesOneTimedEnterKeyPrompt()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTL BEFORE-TWAIT\n" +
            "TWAIT 500, 0\n" +
            "PRINTL AFTER-TWAIT\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        Assert.True(SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ConsolePrompt prompt = fixture.Console.CurrentPrompt!;
        Assert.Equal(ConsoleInputType.EnterKey, prompt.InputType);
        Assert.Equal(TimeSpan.FromMilliseconds(500), prompt.Timeout.GetValueOrDefault());
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("twait-input", string.Empty)).Kind);

        EmueraRuntimeResult result = await run;
        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        Assert.Equal(
            "BEFORE-TWAIT\nAFTER-TWAIT",
            RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes));
        Assert.Single(
            fixture.Console.StateStore.TransactionHistory.SelectMany(item => item.Transaction.Operations)
                .OfType<OpenPromptOperation>());
    }

    [Fact]
    [Trait("Category", "TimedInput")]
    [Trait("Category", "EmueraFeatureMatrix")]
    public async Task TwaitForcedWaitRejectsInputUntilItsDeadline()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "TWAIT 50, 1\n" +
            "PRINTFORML FORCED-TWAIT-DONE ISTIMEOUT={ISTIMEOUT}\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        Assert.True(SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ConsolePrompt prompt = fixture.Console.CurrentPrompt!;
        Assert.Equal(ConsoleInputType.WaitOnly, prompt.InputType);
        Assert.Equal(TimeSpan.FromMilliseconds(50), prompt.Timeout.GetValueOrDefault());
        Assert.Equal(
            ConsoleInputResultKind.InvalidFormat,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("twait-forced", string.Empty)).Kind);
        Assert.Equal(prompt.PromptId, fixture.Console.CurrentPrompt!.PromptId);

        EmueraRuntimeResult result = await run;
        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        Assert.True(fixture.Console.IsTimeOut);
        Assert.Contains(
            "FORCED-TWAIT-DONE ISTIMEOUT=1",
            RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes),
            StringComparison.Ordinal);
        Assert.Single(
            fixture.Console.StateStore.TransactionHistory.SelectMany(item => item.Transaction.Operations)
                .OfType<OpenPromptOperation>());
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "EmueraFeatureMatrix")]
    public async Task RichConsoleFixtureRunsThroughPinnedInterpreterAndPublishesAllDrawableKinds()
    {
        string sourceImage = Path.Combine(
            RuntimeCompatibilityCli.FindRepositoryRoot(),
            "tests", "fixtures", "runtime", "em-ee-core", "resources", "cloudemuera-em-ee.png");
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTL RICH-READY\n" +
            "HTML_PRINT \"<b>RICH-HTML</b><i>RICH-ITALIC</i>\"\n" +
            "HTML_PRINT_ISLAND \"<b>RICH-ISLAND</b>\"\n" +
            "PRINT_IMG \"RICH\"\n" +
            "PRINT_RECT 10,10,40,40\n" +
            "SETBGIMAGE RICH,0,128\n" +
            "PRINTL RICH-INPUT\n" +
            "INPUT\n" +
            "PRINTL RICH-AFTER\n" +
            "QUIT\n",
            configureGame: game =>
            {
                string resources = Path.Combine(game, "resources");
                File.Copy(sourceImage, Path.Combine(resources, "rich.png"));
                File.WriteAllText(Path.Combine(resources, "sprites.csv"), "RICH,rich.png,0,0,2,2\n");
            });
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(5));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> runtime = host.RunAsync();
        Assert.True(SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ConsolePrompt prompt = Assert.IsType<ConsolePrompt>(fixture.Console.CurrentPrompt);
        Assert.Equal(ConsoleInputResultKind.Accepted, fixture.Console.SubmitCurrentInput(
            new ConsoleInputAttempt("rich-client", "7")).Kind);
        EmueraRuntimeResult result = await runtime;

        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        string transcript = RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes);
        Assert.Contains("RICH-READY", transcript, StringComparison.Ordinal);
        Assert.Contains("RICH-HTML", transcript, StringComparison.Ordinal);
        Assert.Contains("RICH-INPUT", transcript, StringComparison.Ordinal);
        Assert.Contains("RICH-AFTER", transcript, StringComparison.Ordinal);
        IReadOnlyList<ConsoleNode> nodes = fixture.Console.Snapshot.VisibleNodes;
        Assert.Contains(nodes, node => node is SpriteNode);
        Assert.Contains(nodes, node => node is ShapeNode);
        Assert.Contains(fixture.Console.Snapshot.BackgroundLayers, background => background.LayerId == "RICH");
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HeadlessSystemLinesAndWarningsAreNotBlockingScriptDiagnostics()
    {
        // P1-04 GAME-007: the pinned upstream DEBUG build emits elapsed-time
        // status lines through PrintSystemLine and non-fatal parser warnings via
        // PrintWarning. Both are informational and must never gate activation;
        // only an explicit interpreter error transition is fatal.
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.PrintSystemLine("経過時間:1234.5ms");
        headless.PrintWarning("システム関数\"@COM1000\"に引数は設定できません", null, 2);
        Assert.Empty(headless.RuntimeMessages);
        Assert.Contains(headless.RuntimeWarnings, message => message.Contains("COM1000", StringComparison.Ordinal));
        headless.PrintError("real error");
        Assert.Contains(headless.RuntimeMessages, message => message.Contains("real error", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task LoadingReportMessageIsNonBlockingWhileParserFailureRemainsFatal()
    {
        using var validFixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nQUIT\n",
            configuration: "Use sav folder:NO\nロード時にレポートを表示する:YES\n");
        await using (EmueraRuntimeHost validHost = validFixture.CreateHost())
        {
            EmueraRuntimeResult valid = await validHost.InitializeAsync();

            Assert.Equal(EmueraRuntimeStatus.Completed, valid.Status);
            Assert.Contains(valid.Diagnostics, diagnostic =>
                diagnostic.Code == "runtime_message" && !diagnostic.IsFatal);
            Assert.DoesNotContain(valid.Diagnostics, diagnostic => diagnostic.IsFatal);
        }

        using var invalidFixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n+NOT_INCREMENT\nQUIT\n");
        await using EmueraRuntimeHost invalidHost = invalidFixture.CreateHost();

        EmueraRuntimeResult invalid = await invalidHost.InitializeAsync();

        Assert.Equal(EmueraRuntimeStatus.InitializationFailed, invalid.Status);
        Assert.Contains(invalid.Diagnostics, diagnostic =>
            diagnostic.Code == "runtime_initialization_failed" && diagnostic.IsFatal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task HeadlessRuntimeCreatesMissingSettingJsonInsideSessionRoot()
    {
        // SESS-011/SAVE-011: upstream JSON settings are runtime state. A
        // missing file is created in the private SessionRoot, never in the
        // Game source tree used to materialize that Session.
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nQUIT\n");
        string sourceSettingPath = Path.Combine(fixture.GameContentRoot, "setting.json");
        string sessionSettingPath = Path.Combine(fixture.Paths.SessionRoot, "setting.json");

        Assert.False(File.Exists(sourceSettingPath));
        Assert.False(File.Exists(sessionSettingPath));

        await using EmueraRuntimeHost host = fixture.CreateHost();
        EmueraRuntimeResult result = await host.InitializeAsync();

        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        Assert.False(File.Exists(sourceSettingPath));
        Assert.True(File.Exists(sessionSettingPath));
        Assert.Contains("\"UseScopedVariableInstruction\":false", File.ReadAllText(sessionSettingPath), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task HeadlessRuntimeLoadsScopedVariableSettingPerSessionRoot()
    {
        // COMP-002: VARI is registered only when the Session-local upstream
        // setting enables the Emuera.NET scoped-variable extension. Reusing a
        // process must not make a later Session inherit the first path/data.
        using var firstFixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nVARI IDX\nQUIT\n",
            configureGame: game => File.WriteAllText(
                Path.Combine(game, "setting.json"),
                "{\"UseScopedVariableInstruction\":true}"));
        await using (EmueraRuntimeHost firstHost = firstFixture.CreateHost())
        {
            Assert.Equal(EmueraRuntimeStatus.Completed, (await firstHost.InitializeAsync()).Status);
        }

        using RuntimeHostFixture secondFixture = firstFixture.CreateAdditionalSession();
        File.WriteAllText(
            Path.Combine(secondFixture.Paths.SessionRoot, "setting.json"),
            "{\"UseScopedVariableInstruction\":false}");
        await using EmueraRuntimeHost secondHost = secondFixture.CreateHost();

        EmueraRuntimeResult second = await secondHost.InitializeAsync();

        Assert.Equal(EmueraRuntimeStatus.InitializationFailed, second.Status);
        Assert.Contains(second.Diagnostics, diagnostic =>
            diagnostic.Code == "runtime_initialization_failed" && diagnostic.IsFatal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task DynamicComAbleCallbackWithArgumentRemainsCallableAndWarningStaysOutOfConsole()
    {
        // GAME-007/COMP-002: real games dynamically invoke @COM_ABLE<n>(ARG)
        // through TRYCCALLFORM. The compatibility warning must not poison the
        // label state or appear in the player-visible console transcript.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "TRYCCALLFORM COM_ABLE{1}(42)\n" +
            "CATCH\n" +
            "PRINTL CATCH-RAN\n" +
            "ENDCATCH\n" +
            "PRINTFORML CALLBACK-RETURN={RESULT}\n" +
            "QUIT\n" +
            "@COM_ABLE1(ARG)\n" +
            "PRINTFORML CALLBACK-ARG={ARG}\n" +
            "RETURN 1\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));

        EmueraRuntimeResult initialized = await host.InitializeAsync();
        Assert.Equal(EmueraRuntimeStatus.Completed, initialized.Status);
        Assert.Contains(initialized.Diagnostics, diagnostic =>
            diagnostic.Code == "runtime_warning" &&
            diagnostic.Message.Contains("COM_ABLE1", StringComparison.Ordinal));

        EmueraRuntimeResult result = await host.RunAsync();

        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        string transcript = RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes);
        Assert.Contains("CALLBACK-ARG=42", transcript, StringComparison.Ordinal);
        Assert.Contains("CALLBACK-RETURN=1", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("CATCH-RAN", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("COM_ABLE1", transcript, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HeadlessErrorsReachTheConsoleOnlyAfterFatalTransition()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();

        headless.PrintWarning("脚本警告", null, 2);
        headless.PrintErrorButton("脚本错误", null, 2);
        headless.PrintError("错误详情");

        string transcript = RuntimeTranscriptProjector.Project(console.Snapshot.VisibleNodes);
        Assert.DoesNotContain("脚本警告", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("脚本错误", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("错误详情", transcript, StringComparison.Ordinal);

        headless.ThrowError(playSound: false);

        transcript = RuntimeTranscriptProjector.Project(console.Snapshot.VisibleNodes);
        Assert.Contains("⚠ 脚本错误", transcript, StringComparison.Ordinal);
        Assert.Contains("⚠ 错误详情", transcript, StringComparison.Ordinal);
        Assert.Contains(headless.RuntimeWarnings, warning => warning.Contains("脚本警告", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void StartupWarningsRemainDiagnosticsAfterExecutionOutputBegins()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.PrintWarning("启动阶段脚本警告", null, 2);

        Assert.Empty(console.Snapshot.VisibleNodes);

        headless.BeginExecutionOutput();

        string transcript = RuntimeTranscriptProjector.Project(console.Snapshot.VisibleNodes);
        Assert.DoesNotContain("启动阶段脚本警告", transcript, StringComparison.Ordinal);
        Assert.Single(headless.RuntimeWarnings);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void PrintedErrorDoesNotBecomeFatalUntilInterpreterTransitionsToError()
    {
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);

        headless.PrintError("recoverable script diagnostic");

        Assert.False(headless.HasFatalError);
        Assert.True(headless.IsRunning);

        headless.ThrowError(playSound: false);

        Assert.True(headless.HasFatalError);
        Assert.False(headless.IsRunning);
    }


    [Theory]
    [InlineData("v18-core")]
    [InlineData("em-ee-core")]
    public async Task FixtureRunsThroughInputToQuit(string fixtureId)
    {
        RuntimeScenarioReport report = await RuntimeScenarioRunner.RunAsync(RuntimeCompatibilityCli.FindRepositoryRoot(), fixtureId);

        Assert.Equal("Completed", report.Status);
        Assert.Empty(report.Errors);
        Assert.True(report.AssertionCount >= 14);
        Assert.Equal(RuntimeBaseline.UpstreamCommit, report.UpstreamCommit);
        Assert.Equal("headless-p0.5.3", report.IntegrationVersion);
        Assert.Contains(
            report.AssertionEvidence,
            evidence => evidence.Name == "score=3" && evidence.Passed && evidence.VerifiedByVisibleOutput);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task MixedCaseTalentCsvUsesWindowsCompatibleLookupOnLinux()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "ADDDEFCHARA\n" +
            "TALENT:0:性別 = 1\n" +
            "PRINTFORML GENDER={TALENT:0:性別}\n" +
            "QUIT\n",
            configureGame: game => File.WriteAllText(
                Path.Combine(game, "CSV", "Talent.csv"),
                "2,性別\n"));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.CsvRoot, "TALENT.CSV")));
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));

        EmueraRuntimeResult initialized = await host.InitializeAsync();
        Assert.Equal(EmueraRuntimeStatus.Completed, initialized.Status);

        EmueraRuntimeResult result = await host.RunAsync();

        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        Assert.Contains(
            "GENDER=1",
            RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v18-core")]
    [InlineData("em-ee-core")]
    [Trait("Category", "NativeSave")]
    public async Task NativeSaveRoundTripsAcrossTwoHosts(string fixtureId)
    {
        RuntimeScenarioReport report = await RuntimeScenarioRunner.RunSaveAsync(
            RuntimeCompatibilityCli.FindRepositoryRoot(),
            fixtureId);

        Assert.Equal("Completed", report.Status);
        Assert.Empty(report.Errors);
        Assert.Contains(
            report.AssertionEvidence,
            evidence => evidence.Name == "native-save-values" && evidence.Passed && evidence.VerifiedByVisibleOutput);
    }

    [Fact]
    [Trait("Category", "NativeSave")]
    public async Task SaveLayoutMismatchFailsBeforeErbExecution()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nPRINTL SHOULD-NOT-RUN\nQUIT\n");
        File.WriteAllText(Path.Combine(fixture.Paths.SessionRoot, "emuera.config"), "Use sav folder:YES\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();

        EmueraRuntimeResult result = await host.InitializeAsync();

        Assert.Equal(EmueraRuntimeStatus.InitializationFailed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "save_layout_mismatch" && diagnostic.IsFatal);
        Assert.DoesNotContain("SHOULD-NOT-RUN", RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("前", RuntimeSaveLayout.SavDirectory)]
    [InlineData("後", RuntimeSaveLayout.Root)]
    [Trait("Category", "NativeSave")]
    public async Task JapaneseSaveBooleanValuesMatchPinnedUpstream(string value, RuntimeSaveLayout expectedLayout)
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINTL JAPANESE-CONFIG-OK\nQUIT\n",
            expectedLayout,
            $"セーブデータをsavフォルダ内に作成する:{value}\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();

        EmueraRuntimeResult initialized = await host.InitializeAsync();

        Assert.Equal(EmueraRuntimeStatus.Completed, initialized.Status);
        Assert.Equal(expectedLayout, fixture.Paths.SaveLayout);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);
        Assert.Contains(
            "JAPANESE-CONFIG-OK",
            RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "NativeSave")]
    public async Task TwoSessionsKeepNativeGlobalValuesIndependent()
    {
        using var first = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINTL ISOLATION-START\nINPUT\n" +
            "IF RESULT == 1\n" +
            "    savedValue = 101\n" +
            "    globalValue = 1001\n" +
            "    SAVEDATA 0, \"SESSION-A\"\n" +
            "    SAVEGLOBAL\n" +
            "    PRINTL ISOLATION-SAVE-A\n" +
            "    QUIT\n" +
            "ELSEIF RESULT == 2\n" +
            "    savedValue = 202\n" +
            "    globalValue = 2002\n" +
            "    SAVEDATA 0, \"SESSION-B\"\n" +
            "    SAVEGLOBAL\n" +
            "    PRINTL ISOLATION-SAVE-B\n" +
            "    QUIT\n" +
            "ELSEIF RESULT == 3\n" +
            "    savedValue = -1\n" +
            "    globalValue = -1\n" +
            "    LOADGLOBAL\n" +
            "    LOADDATA 0\n" +
            "ENDIF\n" +
            "@EVENTLOAD\n" +
            "PRINTFORML LOADED-SAVE={savedValue}\n" +
            "PRINTFORML LOADED-GLOBAL={globalValue}\n" +
            "QUIT\n",
            RuntimeSaveLayout.Root,
            "Use sav folder:NO\n",
            "#DIM SAVEDATA savedValue\n#DIM GLOBAL SAVEDATA globalValue\n");
        using RuntimeHostFixture second = first.CreateAdditionalSession();

        string saveA = await RunWithInputAsync(first, "1");
        string saveB = await RunWithInputAsync(second, "2");
        string loadA = await RunWithInputAsync(first, "3");
        string loadB = await RunWithInputAsync(second, "3");

        Assert.Contains("ISOLATION-SAVE-A", saveA, StringComparison.Ordinal);
        Assert.Contains("ISOLATION-SAVE-B", saveB, StringComparison.Ordinal);
        Assert.Contains("LOADED-SAVE=101", loadA, StringComparison.Ordinal);
        Assert.Contains("LOADED-GLOBAL=1001", loadA, StringComparison.Ordinal);
        Assert.Contains("LOADED-SAVE=202", loadB, StringComparison.Ordinal);
        Assert.Contains("LOADED-GLOBAL=2002", loadB, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(first.Paths.SessionRoot, "save00.sav")));
        Assert.True(File.Exists(Path.Combine(second.Paths.SessionRoot, "save00.sav")));
        Assert.True(File.Exists(Path.Combine(first.Paths.SessionRoot, "global.sav")));
        Assert.True(File.Exists(Path.Combine(second.Paths.SessionRoot, "global.sav")));
    }

    [Fact]
    [Trait("Category", "NativeSave")]
    [Trait("Category", "RuntimeBridge")]
    public async Task MixedCaseSavDirectoryAndFilesRoundTripWithLocalizedConfiguration()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINTL CASE-SAVE-START\nINPUT\n" +
            "IF RESULT == 1\n" +
            "    savedValue = 321\n" +
            "    globalValue = 654\n" +
            "    SAVEDATA 0, \"CASE-SAVE\"\n" +
            "    SAVEGLOBAL\n" +
            "    QUIT\n" +
            "ELSEIF RESULT == 2\n" +
            "    savedValue = -1\n" +
            "    globalValue = -1\n" +
            "    LOADGLOBAL\n" +
            "    LOADDATA 0\n" +
            "ENDIF\n" +
            "@EVENTLOAD\n" +
            "PRINTFORML CASE-SAVE={savedValue}\n" +
            "PRINTFORML CASE-GLOBAL={globalValue}\n" +
            "QUIT\n",
            RuntimeSaveLayout.SavDirectory,
            "在sav文件夹中创建存档:YES\n",
            "#DIM SAVEDATA savedValue\n#DIM GLOBAL SAVEDATA globalValue\n");

        _ = await RunWithInputAsync(fixture, "1");
        string lowerSav = Path.Combine(fixture.Paths.SessionRoot, "sav");
        string mixedSav = Path.Combine(fixture.Paths.SessionRoot, "Sav");
        Directory.Move(lowerSav, mixedSav);
        File.Move(Path.Combine(mixedSav, "save00.sav"), Path.Combine(mixedSav, "Save00.SAV"));
        File.Move(Path.Combine(mixedSav, "global.sav"), Path.Combine(mixedSav, "GLOBAL.SAV"));

        string transcript = await RunWithInputAsync(fixture, "2");

        Assert.Contains("CASE-SAVE=321", transcript, StringComparison.Ordinal);
        Assert.Contains("CASE-GLOBAL=654", transcript, StringComparison.Ordinal);
        Assert.Single(
            Directory.EnumerateDirectories(fixture.Paths.SessionRoot),
            path => Path.GetFileName(path).Equals("sav", StringComparison.OrdinalIgnoreCase));
        Assert.Single(
            Directory.EnumerateFiles(mixedSav),
            path => Path.GetFileName(path).Equals("save00.sav", StringComparison.OrdinalIgnoreCase));
        Assert.Single(
            Directory.EnumerateFiles(mixedSav),
            path => Path.GetFileName(path).Equals("global.sav", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void HeadlessPathLookupRejectsAmbiguousCaseVariants()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudemuera-path-case", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Image.png"), "first");
            File.WriteAllText(Path.Combine(root, "image.PNG"), "second");
            HeadlessPathResolver.Configure(root);

            Assert.Throws<IOException>(() => HeadlessFile.Exists(Path.Combine(root, "IMAGE.PNG")));
        }
        finally
        {
            HeadlessPathResolver.Reset();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "NativeSave")]
    public async Task CancellationPreservesPersistentSessionRoot()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nINPUT\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        File.WriteAllText(Path.Combine(fixture.Paths.SessionRoot, "save00.sav"), "cancel-survivor");

        using var cancellation = new CancellationTokenSource();
        Task<EmueraRuntimeResult> run = host.RunAsync(cancellation.Token);
        Assert.True(SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        cancellation.Cancel();

        Assert.Equal(EmueraRuntimeStatus.Cancelled, (await run).Status);
        Assert.True(Directory.Exists(fixture.Paths.SessionRoot));
        Assert.Equal("cancel-survivor", File.ReadAllText(Path.Combine(fixture.Paths.SessionRoot, "save00.sav")));
        host.Dispose();
        Assert.True(Directory.Exists(fixture.Paths.SessionRoot));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.SessionRoot, "emuera.config")));
    }

    [Fact]
    [Trait("Category", "NativeSave")]
    public async Task DisposingInitializedHostPreservesRootForSimulatedWorkerTermination()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        File.WriteAllText(Path.Combine(fixture.Paths.SessionRoot, "global.sav"), "termination-survivor");

        host.Dispose();

        Assert.True(Directory.Exists(fixture.Paths.SessionRoot));
        Assert.Equal(
            "termination-survivor",
            File.ReadAllText(Path.Combine(fixture.Paths.SessionRoot, "global.sav")));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.SessionRoot, "emuera.config")));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void TranscriptProjectionDoesNotInventImageText()
    {
        ConsoleNode[] nodes = [
            new TextNode("before"),
            new ImageNode("SPRITE", 2, 2),
            LineBreakNode.Instance,
            new TextNode("after", new ConsoleTextStyle(decorations: ConsoleFontStyle.Bold))
        ];

        Assert.Equal("before\nafter", RuntimeTranscriptProjector.Project(nodes));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void StringInputContractRemainsTextual()
    {
        var input = new GameConsoleInput("prompt-1", ConsoleInputType.Text, "001 text");

        Assert.Equal(ConsoleInputType.Text, input.InputType);
        Assert.Equal("001 text", input.Value);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task InputsRoundTripPreservesStringValue()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nINPUTS\nPRINTFORML TEXT=%RESULTS%\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        Assert.True(SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ConsolePrompt prompt = Assert.IsType<ConsolePrompt>(fixture.Console.CurrentPrompt);
        Assert.Equal(ConsoleInputType.Text, prompt.InputType);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("text-message", "001 text")).Kind);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await run).Status);
        Assert.Equal("TEXT=001 text", RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task PrintButtonPreservesIntegerAndStringSubmissionValues()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINTBUTTON \"INTEGER\", 42\nPRINTBUTTON \"STRING\", \"001 text\"\n" +
            "PRINTBUTTONC \"RIGHT\", 7\nPRINTBUTTONLC \"LEFT\", \"left-value\"\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        ButtonNode[] buttons = fixture.Console.Snapshot.VisibleNodes.OfType<ButtonNode>().ToArray();
        Assert.Collection(
            buttons,
            button =>
            {
                Assert.Equal("INTEGER", Assert.IsType<TextNode>(Assert.Single(button.Children)).Text);
                Assert.Equal("42", button.Value);
                Assert.Null(button.Tooltip);
            },
            button =>
            {
                Assert.Equal("STRING", Assert.IsType<TextNode>(Assert.Single(button.Children)).Text);
                Assert.Equal("001 text", button.Value);
                Assert.Null(button.Tooltip);
            },
            button =>
            {
                Assert.Equal("RIGHT", Assert.IsType<TextNode>(Assert.Single(button.Children)).Text.Trim());
                Assert.Equal("7", button.Value);
                Assert.Null(button.Tooltip);
            },
            button =>
            {
                Assert.Equal("LEFT", Assert.IsType<TextNode>(Assert.Single(button.Children)).Text.Trim());
                Assert.Equal("left-value", button.Value);
                Assert.Null(button.Tooltip);
            });
        Assert.All(
            buttons,
            button => Assert.Equal(
                new RuntimeConsoleColor(255, 255, 0),
                Assert.IsType<TextNode>(Assert.Single(button.Children)).Style.ButtonColor));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "Input")]
    public async Task BinputSeesHeadlessPrintButtonBeforeLineBreak()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINTBUTTON \"START\", 0\nBINPUT\n" +
            "PRINTFORML RESULT={RESULT}\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        if (!SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)))
        {
            EmueraRuntimeResult earlyResult = await run;
            string diagnostics = string.Join(" | ", earlyResult.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message));
            Assert.Fail("BINPUT did not open a prompt: " + earlyResult.Status + "; " + diagnostics);
        }
        ConsolePrompt prompt = Assert.IsType<ConsolePrompt>(fixture.Console.CurrentPrompt);
        Assert.Equal(ConsoleInputType.IntegerButton, prompt.InputType);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("binput-start", "0", ConsoleInputSource.Button)).Kind);

        EmueraRuntimeResult result = await run;
        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            result.Status + ": " + string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        Assert.Contains("RESULT=0", RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "Input")]
    public async Task BinputSeesIntegerButtonFromHtmlPrint()
    {
        // PLAY-002/COMP-007: HTML_PRINT must preserve the upstream numeric
        // button kind because BINPUT only accepts integer buttons.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nHTML_PRINT \"<button value='7'>[7] - CONTINUE</button>\"\nBINPUT\n" +
            "PRINTFORML HTML_RESULT={RESULT}\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        if (!SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)))
        {
            EmueraRuntimeResult earlyResult = await run;
            string diagnostics = string.Join(" | ", earlyResult.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message));
            Assert.Fail("BINPUT did not open a prompt: " + earlyResult.Status + "; " + diagnostics);
        }
        ConsolePrompt prompt = Assert.IsType<ConsolePrompt>(fixture.Console.CurrentPrompt);
        Assert.Equal(ConsoleInputType.IntegerButton, prompt.InputType);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("html-binput", "7", ConsoleInputSource.Button)).Kind);

        EmueraRuntimeResult result = await run;
        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            result.Status + ": " + string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        Assert.Contains("HTML_RESULT=7", RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "Input")]
    public async Task OneinputsPreservesLongHtmlButtonValueWhenConfigAllowsMouseInput()
    {
        // PLAY-002/COMP-007: EraFL uses ONEINPUTS with multi-character HTML
        // button IDs. The pinned upstream config explicitly allows long
        // values from mouse buttons, so the headless bridge must preserve the
        // full value while keeping keyboard ONEINPUTS single-character.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nHTML_PRINT \"<button value='4000'>PRESET</button>\"\nONEINPUTS\n" +
            "PRINTFORML ONEINPUTS_HTML_RESULT=%RESULTS%\nQUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:400\n字体大小:18\n每行高度:20\n" +
                "ONEINPUT系命令でマウスによる2文字以上の入力を許可する:YES\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        if (!SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)))
        {
            EmueraRuntimeResult earlyResult = await run;
            string diagnostics = string.Join(" | ", earlyResult.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message));
            Assert.Fail("ONEINPUTS did not open a prompt: " + earlyResult.Status + "; " + diagnostics);
        }

        ConsolePrompt prompt = Assert.IsType<ConsolePrompt>(fixture.Console.CurrentPrompt);
        Assert.Equal(ConsoleInputType.Text, prompt.InputType);
        Assert.True(prompt.OneInput);
        Assert.True(prompt.AllowLongInputByButton);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt(
                "oneinputs-html-button",
                "4000",
                ConsoleInputSource.Button)).Kind);

        EmueraRuntimeResult result = await run;
        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            result.Status + ": " + string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        Assert.Contains(
            "ONEINPUTS_HTML_RESULT=4000",
            RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "Input")]
    public async Task BinputAcceptsNegativeIntegerButtonValue()
    {
        // issue #14: eraBlue uses negative HTML button values for navigation
        // and actions. They are valid signed Emuera integer input values.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nHTML_PRINT \"<button value='-1'>[-1] BACK</button>\"\nBINPUT\n" +
            "PRINTFORML NEGATIVE_HTML_RESULT={RESULT}\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        if (!SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)))
        {
            EmueraRuntimeResult earlyResult = await run;
            string diagnostics = string.Join(" | ", earlyResult.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message));
            Assert.Fail("Negative HTML BINPUT did not open a prompt: " + earlyResult.Status + "; " + diagnostics);
        }
        ConsolePrompt prompt = Assert.IsType<ConsolePrompt>(fixture.Console.CurrentPrompt);
        Assert.Equal(ConsoleInputType.IntegerButton, prompt.InputType);
        IntegerInputConstraints constraints = Assert.IsType<IntegerInputConstraints>(prompt.Constraints);
        Assert.True(constraints.AllowSign);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("negative-html-binput", "-1", ConsoleInputSource.Button)).Kind);

        EmueraRuntimeResult result = await run;
        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            result.Status + ": " + string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        Assert.Contains("NEGATIVE_HTML_RESULT=-1", RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "Input")]
    public async Task BinputSeesIntegerButtonNestedInHtmlPrintDivAfterLineBreaks()
    {
        // PLAY-002/COMP-007/issue #14: HTML_PRINT can place numeric buttons
        // inside a div, and later PRINTL calls must not hide them from BINPUT.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nHTML_PRINT \"<div width='80px' height='20px'><button value='110'>[110] INVITE</button></div>\"\n" +
            "FOR LOCAL, 0, 26\nPRINTL\nNEXT\nBINPUT\n" +
            "PRINTFORML NESTED_HTML_RESULT={RESULT}\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        if (!SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)))
        {
            EmueraRuntimeResult earlyResult = await run;
            string diagnostics = string.Join(" | ", earlyResult.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message));
            Assert.Fail("Nested HTML BINPUT did not open a prompt: " + earlyResult.Status + "; " + diagnostics);
        }
        ConsolePrompt prompt = Assert.IsType<ConsolePrompt>(fixture.Console.CurrentPrompt);
        Assert.Equal(ConsoleInputType.IntegerButton, prompt.InputType);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("nested-html-binput", "110", ConsoleInputSource.Button)).Kind);

        EmueraRuntimeResult result = await run;
        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            result.Status + ": " + string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        Assert.Contains("NESTED_HTML_RESULT=110", RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "Input")]
    public async Task BinputRefreshesNestedHtmlPrintBuffer()
    {
        // PLAY-002/COMP-007/issue #14: the same recursive projection must
        // flush a buffered HTML_PRINT fragment when BINPUT calls RefreshStrings.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nHTML_PRINT \"<div width='80px' height='20px'><button value='110'>[110] INVITE</button></div>\", 1\n" +
            "BINPUT\nPRINTFORML BUFFERED_HTML_RESULT={RESULT}\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        if (!SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)))
        {
            EmueraRuntimeResult earlyResult = await run;
            string diagnostics = string.Join(" | ", earlyResult.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message));
            Assert.Fail("Buffered nested HTML BINPUT did not open a prompt: " + earlyResult.Status + "; " + diagnostics);
        }
        ConsolePrompt prompt = Assert.IsType<ConsolePrompt>(fixture.Console.CurrentPrompt);
        Assert.Equal(ConsoleInputType.IntegerButton, prompt.InputType);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("buffered-html-binput", "110", ConsoleInputSource.Button)).Kind);

        EmueraRuntimeResult result = await run;
        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            result.Status + ": " + string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        Assert.Contains("BUFFERED_HTML_RESULT=110", RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "Input")]
    [Trait("Category", "FontLayout")]
    public async Task BinputUsesHtmlButtonAfterFontBoundClearlineReplacement()
    {
        // PLAY-002/COMP-007/issue #14: CLEARLINE replaces the structured
        // logical line in place. The server-side legacy projection must
        // replace its input inventory at the same boundary; browser rendering
        // must not determine whether BINPUT can proceed.
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "HTML_PRINT \"<button value='7'>OLD</button>\"\n" +
            "CLEARLINE 1\n" +
            "HTML_PRINT \"<button value='8'>NEW</button>\"\n" +
            "BINPUT\n" +
            "PRINTFORML CLEARLINE_HTML_RESULT={RESULT}\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:400\n字体大小:18\n每行高度:20\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
            runtimeFontFamilyName: "Sarasa Fixed SC",
            webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        Task<EmueraRuntimeResult> run = host.RunAsync();
        if (!SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)))
        {
            EmueraRuntimeResult earlyResult = await run;
            string diagnostics = string.Join(" | ", earlyResult.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message));
            Assert.Fail("CLEARLINE replacement BINPUT did not open a prompt: " + earlyResult.Status + "; " + diagnostics);
        }

        ConsolePrompt prompt = Assert.IsType<ConsolePrompt>(fixture.Console.CurrentPrompt);
        Assert.Equal(ConsoleInputType.IntegerButton, prompt.InputType);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("clearline-html-binput", "8", ConsoleInputSource.Button)).Kind);

        EmueraRuntimeResult result = await run;
        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        Assert.Contains("CLEARLINE_HTML_RESULT=8", RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BINPUT", "<button value='8'>NEW</button>", "8", "BUTTON_VARIANT_RESULT={RESULT}", ConsoleInputType.IntegerButton)]
    [InlineData("BINPUTS", "<button value='new-value'>NEW</button>", "new-value", "BUTTON_VARIANT_RESULT=%RESULTS%", ConsoleInputType.TextButton)]
    [InlineData("ONEBINPUT", "<button value='8'>NEW</button>", "8", "BUTTON_VARIANT_RESULT={RESULT}", ConsoleInputType.IntegerButton)]
    [InlineData("ONEBINPUTS", "<button value='n'>NEW</button>", "n", "BUTTON_VARIANT_RESULT=%RESULTS%", ConsoleInputType.TextButton)]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "Input")]
    [Trait("Category", "FontLayout")]
    public async Task AllButtonInputVariantsUseTheSameServerSideInventory(
        string instruction,
        string html,
        string input,
        string resultFormat,
        ConsoleInputType expectedInputType)
    {
        // issue #14: all four BINPUT-family instructions share the same
        // server-side display-line projection. Verify that a CLEARLINE
        // replacement updates that projection for both integer and string
        // button modes, including the ONE variants.
        string oldHtml = instruction.EndsWith('S')
            ? "<button value='old-value'>OLD</button>"
            : "<button value='7'>OLD</button>";
        string repositoryRoot = RuntimeCompatibilityCli.FindRepositoryRoot();
        string fontRoot = Path.Combine(repositoryRoot, "assets", "runtime-fonts");
        string catalogPath = Path.Combine(fontRoot, "catalog.json");
        string catalogDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            $"HTML_PRINT \"{oldHtml}\"\n" +
            "CLEARLINE 1\n" +
            $"HTML_PRINT \"{html}\"\n" +
            $"{instruction}\n" +
            $"PRINTFORML {resultFormat}\n" +
            "QUIT\n",
            configuration: "Use sav folder:NO\n窗口宽度:400\n字体大小:18\n每行高度:20\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(
            runDeadline: TimeSpan.FromSeconds(8),
            fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
            fontCatalogDigest: catalogDigest,
            runtimeFontPath: Path.Combine(fontRoot, "runtime-ttf", "sarasa-fixed-sc-1.0.40-regular.ttf"),
            runtimeFontFamilyName: "Sarasa Fixed SC",
            webFontAssetDigest: "e1f5a8837b6dd9cc1fdd11684c55f4f46bbcf879b7f0f64a48e4db3f3009a0c3");

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);
        Task<EmueraRuntimeResult> run = host.RunAsync();
        if (!SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)))
        {
            EmueraRuntimeResult earlyResult = await run;
            string diagnostics = string.Join(" | ", earlyResult.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message));
            Assert.Fail($"{instruction} did not open a prompt: {earlyResult.Status}; {diagnostics}");
        }

        ConsolePrompt prompt = Assert.IsType<ConsolePrompt>(fixture.Console.CurrentPrompt);
        Assert.Equal(expectedInputType, prompt.InputType);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt($"{instruction}-clearline", input, ConsoleInputSource.Button)).Kind);

        EmueraRuntimeResult result = await run;
        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        Assert.Contains(
            $"BUTTON_VARIANT_RESULT={input}",
            RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "Input")]
    public async Task BinputDoesNotReuseButtonRemovedByClearline()
    {
        // issue #14: deleting a logical line must remove its server-side
        // input inventory as well as its structured browser representation.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nHTML_PRINT \"<button value='7'>OLD</button>\"\n" +
            "CLEARLINE 1\nBINPUT\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        EmueraRuntimeResult result = await host.RunAsync();

        Assert.Equal(EmueraRuntimeStatus.ScriptFailed, result.Status);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "runtime_script_failed" &&
                diagnostic.Message.Contains("BINPUT", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "Input")]
    public void LegacyButtonProjectionPreservesGenerationWhenAppendingToPartialLine()
    {
        // issue #14: refreshing the server-side projection for a same-line
        // append must not re-stamp an existing button as part of the new
        // input generation.
        var console = new StructuredGameConsole();
        var headless = new EmueraConsole(console, console.Clock, CancellationToken.None);
        headless.BeginExecutionOutput();
        headless.Print("PREFIX", lineEnd: false);
        headless.PrintButton("OLD", 1);
        headless.PrintFlush(force: false);

        headless.forceUpdateGeneration();
        headless.PrintButton("NEW", 2);
        headless.PrintFlush(force: false);

        ConsoleDisplayLine line = Assert.Single(headless.DisplayLineList);
        Assert.Equal([1, 2], line.Buttons.Select(button => button.Generation));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "Input")]
    public async Task BinputDoesNotReuseConsumedButtonGeneration()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINTBUTTON \"START\", 0\nBINPUT\n" +
            "PRINTL NO-CURRENT-BUTTON\nBINPUT\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        Assert.True(SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("consume-start", "0", ConsoleInputSource.Button)).Kind);

        EmueraRuntimeResult result = await run;
        Assert.Equal(EmueraRuntimeStatus.ScriptFailed, result.Status);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "runtime_script_failed" &&
                diagnostic.Message.Contains("BINPUT", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "ButtonGenerationCompatibility")]
    public async Task ConsecutiveMenusPublishTheCurrentButtonGenerationOnEachPrompt()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINTBUTTON \"FIRST\", 1\nPRINTL\nINPUT\n" +
            "PRINTBUTTON \"SECOND\", 2\nPRINTL\nINPUT\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(3));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        Assert.True(SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ConsolePrompt firstPrompt = fixture.Console.CurrentPrompt!;
        ButtonNode firstButton = fixture.Console.Snapshot.VisibleNodes.OfType<ButtonNode>().Single(button => button.Value == "1");
        Assert.Equal(firstButton.Generation, firstPrompt.ButtonGeneration);
        Assert.Equal(ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("first-menu", "1", ConsoleInputSource.Button)).Kind);

        Assert.True(SpinWait.SpinUntil(
            () => fixture.Console.CurrentPrompt is { } current && current.PromptId != firstPrompt.PromptId,
            TimeSpan.FromSeconds(2)));
        ConsolePrompt secondPrompt = fixture.Console.CurrentPrompt!;
        ButtonNode secondButton = fixture.Console.Snapshot.VisibleNodes.OfType<ButtonNode>().Single(button => button.Value == "2");
        Assert.Equal(secondButton.Generation, secondPrompt.ButtonGeneration);
        Assert.NotEqual(firstPrompt.ButtonGeneration, secondPrompt.ButtonGeneration);
        Assert.Equal(ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("second-menu", "2", ConsoleInputSource.Button)).Kind);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await run).Status);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task PrintButtonAllowsEmptyStringSubmissionValue()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINTBUTTON \"UNCHANGED\", \"\"\nPRINTL\nINPUTS\nPRINTFORML VALUE=[%RESULTS%]\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        Assert.True(SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ButtonNode button = Assert.Single(fixture.Console.Snapshot.VisibleNodes.OfType<ButtonNode>());
        Assert.Equal(string.Empty, button.Value);
        ConsolePrompt prompt = Assert.IsType<ConsolePrompt>(fixture.Console.CurrentPrompt);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            fixture.Console.SubmitCurrentInput(new ConsoleInputAttempt("empty-button-message",
                button.Value,
                ConsoleInputSource.Button)).Kind);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await run).Status);
        Assert.Contains("VALUE=[]", RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task OrdinaryPrintLinesPreserveUpstreamImplicitNumericButtons()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nPRINTFORML [1000] - YES  [1001] - NAME  [1002] - BASE\n" +
            "PRINTL [1003] - TALENT  [1004] - ABILITY\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        ButtonNode[] buttons = fixture.Console.Snapshot.VisibleNodes.OfType<ButtonNode>().ToArray();
        Assert.Equal(["1000", "1001", "1002", "1003", "1004"], buttons.Select(button => button.Value));
        Assert.All(buttons, button => Assert.NotEmpty(button.Children));
        Assert.Contains("[1000] - YES", string.Concat(buttons.SelectMany(button => button.Children).Cast<TextNode>().Select(node => node.Text)), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task EastAsianWidthConversionWorksWithoutWindowsStrConv()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\nTOHALF \"１２３ＡＢＣ　ガ\"\nPRINTFORML HALF=%RESULTS%\n" +
            "TOFULL \"123 ABC ｶﾞ\"\nPRINTFORML FULL=%RESULTS%\nQUIT\n",
            erbCodePage: 932);
        await using EmueraRuntimeHost host = fixture.CreateHost();
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);

        string transcript = RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes);
        Assert.Contains("HALF=123ABC ｶﾞ", transcript, StringComparison.Ordinal);
        Assert.Contains("FULL=１２３　ＡＢＣ　ガ", transcript, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task PrintedAssignmentTextIsNotReportedAsRuntimeVariable()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nPRINTL SCORE=3\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        EmueraRuntimeResult result = await host.RunAsync();

        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        Assert.Empty(result.Variables);
        Assert.Equal("SCORE=3", RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task CancellationUnblocksInputAndHostCannotRunTwice()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nINPUT\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        using var cancellation = new CancellationTokenSource();
        Task<EmueraRuntimeResult> run = host.RunAsync(cancellation.Token);
        Assert.True(SpinWait.SpinUntil(() => fixture.Console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        cancellation.Cancel();

        Assert.Equal(EmueraRuntimeStatus.Cancelled, (await run).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunAsync());
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task RunDeadlineUnblocksUpstreamInput()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nINPUT\nQUIT\n");
        var clock = new RunDeadlineClock();
        await using EmueraRuntimeHost host = fixture.CreateHost(clock, runDeadline: TimeSpan.FromSeconds(1));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Assert.Equal(EmueraRuntimeStatus.DeadlineExceeded, (await host.RunAsync()).Status);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task RunDeadlineStopsCpuBoundErbLoopWithinHardTimeout()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nWHILE 1\nWEND\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromMilliseconds(50));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        EmueraRuntimeResult result = await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(EmueraRuntimeStatus.DeadlineExceeded, result.Status);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task InitializationDeadlineReleasesGateAndPreservesSessionRootForNextHost()
    {
        using var timedOutFixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nQUIT\n");
        using var gateAcquired = new ManualResetEventSlim();
        var deadlineClock = new GateAcquiredDeadlineClock(gateAcquired);
        await using EmueraRuntimeHost timedOutHost = timedOutFixture.CreateHost(
            deadlineClock,
            initializationDeadline: TimeSpan.FromSeconds(1),
            upstreamGateAcquired: gateAcquired.Set);

        EmueraRuntimeResult timedOut = await timedOutHost.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(EmueraRuntimeStatus.DeadlineExceeded, timedOut.Status);
        Assert.True(Directory.Exists(timedOutFixture.Paths.SessionRoot));
        Assert.True(File.Exists(Path.Combine(timedOutFixture.Paths.SessionRoot, "emuera.config")));

        using var recoveryFixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nQUIT\n");
        await using EmueraRuntimeHost recoveryHost = recoveryFixture.CreateHost();
        EmueraRuntimeResult recovered = await recoveryHost.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(EmueraRuntimeStatus.Completed, recovered.Status);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task MissingGameBaseDoesNotAddAStaticInitializationGate()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nQUIT\n");
        File.Delete(Path.Combine(fixture.Paths.CsvRoot, "GAMEBASE.CSV"));
        await using EmueraRuntimeHost host = fixture.CreateHost();

        EmueraRuntimeResult result = await host.InitializeAsync();

        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "runtime_initialization_failed");
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task UnsupportedInstructionDoesNotAddAStaticInitializationGate()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nCALLSHARP forbidden\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();

        EmueraRuntimeResult result = await host.InitializeAsync();

        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "unsupported_runtime_capability");
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task GraphicsFunctionIsAcceptedByHeadlessCapabilityGate()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nRESULT = GCREATE(0, 2, 2)\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();

        EmueraRuntimeResult result = await host.InitializeAsync();

        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "unsupported_runtime_capability");
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    [Trait("Category", "EmueraFeatureMatrix")]
    public async Task GraphicsSaveLoadRoundTripsAndCreateFromFileRejectsTraversal()
    {
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTFORML CREATED={GCREATE(0, 2, 2)}\n" +
            "PRINTFORML CLEARED={GCLEAR(0, 4294901760)}\n" +
            "PRINTFORML SAVED={GSAVE(0, 7)}\n" +
            "PRINTFORML LOADED={GLOAD(1, 7)}\n" +
            "PRINTFORML PIXEL={GGETCOLOR(1, 0, 0)}\n" +
            "PRINTFORML TRAVERSAL={GCREATEFROMFILE(2, \"../../outside.png\", 1)}\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        EmueraRuntimeResult result = await host.RunAsync();

        Assert.Equal(EmueraRuntimeStatus.Completed, result.Status);
        string transcript = RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes);
        Assert.Contains("SAVED=1", transcript, StringComparison.Ordinal);
        Assert.Contains("LOADED=1", transcript, StringComparison.Ordinal);
        Assert.Contains("PIXEL=4294901760", transcript, StringComparison.Ordinal);
        Assert.Contains("TRAVERSAL=0", transcript, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.Paths.SessionRoot, "img0007.png")));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task SavedDynamicSpriteResolvesThroughHtmlPrintInSavLayout()
    {
        // COMP-007/SAVE-011: a SpriteG backed by GLOAD must point at the
        // SessionRoot sav asset used by the browser, not disappear because the
        // static resource resolver has no entry for the generated name.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTFORML CREATED={GCREATE(0, 2, 2)}\n" +
            "PRINTFORML CLEARED={GCLEAR(0, 4294901760)}\n" +
            "PRINTFORML SAVED={GSAVE(0, 7)}\n" +
            "PRINTFORML LOADED={GLOAD(1, 7)}\n" +
            "PRINTFORML SPRITE={SPRITECREATE(\"DYNAMIC\", 1)}\n" +
            "HTML_PRINT \"<img src='DYNAMIC' height='2px' width='2px'>\"\n" +
            "QUIT\n",
            saveLayout: RuntimeSaveLayout.SavDirectory,
            configuration: "Use sav folder:YES\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(8));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        EmueraRuntimeResult result = await host.RunAsync();

        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
        SpriteNode sprite = Assert.Single(
            EnumerateSpriteNodes(fixture.Console.Snapshot.Scrollback.SelectMany(line => line.Nodes)));
        Assert.True(ConsoleAssetIdCodec.TryDecodePath(sprite.AssetId.Value, out string logicalPath));
        Assert.Equal("sav/img0007.png", logicalPath);
        Assert.True(File.Exists(Path.Combine(fixture.Paths.SavDirectoryRoot, "img0007.png")));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task UnsavedDynamicSpriteMaterializesCurrentGraphicsSurface()
    {
        // COMP-007/SAVE-011: GCREATE/OVERLAY_GCREATE produces an in-memory
        // composite. It must become a SessionRoot asset on first HTML use even
        // when the game never calls GSAVE for that graphics id.
        using var fixture = RuntimeHostFixture.Create(
            "@SYSTEM_TITLE\n" +
            "PRINTFORML CREATED={GCREATE(0, 2, 2)}\n" +
            "PRINTFORML CLEARED={GCLEAR(0, 4294901760)}\n" +
            "PRINTFORML SPRITE={SPRITECREATE(\"DYNAMIC\", 0)}\n" +
            "HTML_PRINT \"<img src='DYNAMIC' height='2px' width='2px'>\"\n" +
            "QUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost(runDeadline: TimeSpan.FromSeconds(8));
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        EmueraRuntimeResult result = await host.RunAsync();

        Assert.True(
            result.Status == EmueraRuntimeStatus.Completed,
            string.Join(" | ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
        SpriteNode sprite = Assert.Single(
            EnumerateSpriteNodes(fixture.Console.Snapshot.Scrollback.SelectMany(line => line.Nodes)));
        Assert.True(ConsoleAssetIdCodec.TryDecodePath(sprite.AssetId.Value, out string logicalPath));
        Assert.StartsWith("tmp/cloudemuera-runtime-assets/", logicalPath, StringComparison.Ordinal);
        Assert.EndsWith(".png", logicalPath, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            fixture.Paths.SessionRoot,
            logicalPath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task UnsupportedIdentifierInPrintedTextIsNotMisclassified()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nPRINTL GCREATE\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);
        Assert.Equal("GCREATE", RuntimeTranscriptProjector.Project(fixture.Console.Snapshot.VisibleNodes));
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task AudioInstructionUsesPortAndFailsClosedWhenUnsupported()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nPLAYSOUND \"beep.wav\"\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        EmueraRuntimeResult result = await host.RunAsync();

        Assert.Equal(EmueraRuntimeStatus.UnsupportedCapability, result.Status);
        RuntimeAudioRequest request = Assert.Single(fixture.AudioPort.PlayedRequests);
        Assert.Equal("sound/beep.wav", request.ResourcePath.LogicalPath);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task AwaitInstructionUsesRuntimeClock()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nAWAIT 25\nPRINTL CLOCK-DONE\nQUIT\n");
        var clock = new RecordingRuntimeClock();
        await using EmueraRuntimeHost host = fixture.CreateHost(clock);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);
        Assert.Contains(TimeSpan.FromMilliseconds(25), clock.Delays);
    }

    [Fact]
    public void HeadlessAssemblyDoesNotReferenceDesktopFrameworks()
    {
        var runtimeAssembly = typeof(EmueraRuntimeHost).Assembly;
        var upstreamAssembly = typeof(UpstreamRuntimeSession).Assembly;
        string[] references = runtimeAssembly.GetReferencedAssemblies()
            .Concat(upstreamAssembly.GetReferencedAssemblies())
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("System.Windows.Forms", references);
        Assert.DoesNotContain("PresentationFramework", references);
        Assert.DoesNotContain("NAudio", references);
        Assert.DoesNotContain("WMPLib", references);
        Assert.Contains("CloudEmuera.EmueraRuntime.UpstreamHeadless", runtimeAssembly.GetReferencedAssemblies().Select(name => name.Name));
        Assert.DoesNotContain(runtimeAssembly.GetTypes(), type => type.Name.StartsWith("VendoredErb", StringComparison.Ordinal));
    }

    private static IEnumerable<SpriteNode> EnumerateSpriteNodes(IEnumerable<ConsoleNode> nodes)
    {
        foreach (ConsoleNode node in nodes)
        {
            switch (node)
            {
                case SpriteNode sprite:
                    yield return sprite;
                    break;
                case ButtonNode button:
                    foreach (SpriteNode child in EnumerateSpriteNodes(button.Children))
                        yield return child;
                    break;
                case PositionedInlineSegmentNode segment:
                    foreach (SpriteNode child in EnumerateSpriteNodes(segment.Children))
                        yield return child;
                    break;
                case DivNode div:
                    foreach (SpriteNode child in EnumerateSpriteNodes(div.Children))
                        yield return child;
                    break;
            }
        }
    }

    private sealed class RuntimeHostFixture : IDisposable
    {
        private RuntimeHostFixture(
            string root,
            string gameContentRoot,
            SessionRootPublishedManifest manifest,
            RuntimePaths paths,
            LocalRuntimeFileSystem fileSystem,
            StructuredGameConsole console,
            RecordingRuntimeAudioPort audioPort,
            bool ownsRoot)
        {
            Root = root;
            GameContentRoot = gameContentRoot;
            Manifest = manifest;
            Paths = paths;
            FileSystem = fileSystem;
            Console = console;
            AudioPort = audioPort;
            this.ownsRoot = ownsRoot;
        }

        private readonly bool ownsRoot;

        public string Root { get; }
        public string GameContentRoot { get; }
        public SessionRootPublishedManifest Manifest { get; }
        public RuntimePaths Paths { get; }
        public LocalRuntimeFileSystem FileSystem { get; }
        public StructuredGameConsole Console { get; }
        public RecordingRuntimeAudioPort AudioPort { get; }

        public static RuntimeHostFixture Create(
            string erb,
            RuntimeSaveLayout saveLayout = RuntimeSaveLayout.Root,
            string? configuration = null,
            string? saveDeclarations = null,
            Action<string>? configureGame = null,
            int? erbCodePage = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "cloudemuera-runtime-bridge", Guid.NewGuid().ToString("N"));
            string game = Path.Combine(root, "game");
            string session = Path.Combine(root, "session");
            string workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(Path.Combine(game, "CSV"));
            Directory.CreateDirectory(Path.Combine(game, "ERB"));
            Directory.CreateDirectory(Path.Combine(game, "resources"));
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(game, "CSV", "GAMEBASE.CSV"), "title,bridge-test\n");
            if (erbCodePage is int codePage)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                File.WriteAllText(Path.Combine(game, "ERB", "START.ERB"), erb, Encoding.GetEncoding(codePage));
            }
            else
            {
                File.WriteAllText(Path.Combine(game, "ERB", "START.ERB"), erb);
            }
            if (saveDeclarations is not null)
            {
                File.WriteAllText(Path.Combine(game, "ERB", "SAVE.ERH"), saveDeclarations);
            }

            configureGame?.Invoke(game);

            File.WriteAllText(Path.Combine(game, "emuera.config"), configuration ?? "Use sav folder:NO\n");
            SessionRootLayout layout = new SessionRootLayoutBuilder(
                game,
                session,
                workspace,
                saveLayout).Build();
            SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(game, "runtime-bridge");
            RuntimePaths paths = layout.RuntimePaths;
            var fileSystem = new LocalRuntimeFileSystem(paths);
            var console = new StructuredGameConsole();
            return new RuntimeHostFixture(
                root,
                game,
                manifest,
                paths,
                fileSystem,
                console,
                new RecordingRuntimeAudioPort(),
                ownsRoot: true);
        }

        public RuntimeHostFixture CreateAdditionalSession()
        {
            string sessionRoot = Path.Combine(Root, "session-b");
            string sessionWorkspace = Path.Combine(Root, "session-b-workspace");
            SessionRootLayout layout = new SessionRootLayoutBuilder(
                GameContentRoot,
                sessionRoot,
                sessionWorkspace,
                [Paths.SessionRoot])
                .Build(Manifest, new SessionRootCopyLimits());
            var fileSystem = new LocalRuntimeFileSystem(layout.RuntimePaths);
            return new RuntimeHostFixture(
                Root,
                GameContentRoot,
                Manifest,
                layout.RuntimePaths,
                fileSystem,
                new StructuredGameConsole(),
                new RecordingRuntimeAudioPort(),
                ownsRoot: false);
        }

        public EmueraRuntimeHost CreateHost(
            IRuntimeClock? runtimeClock = null,
            TimeSpan? initializationDeadline = null,
            TimeSpan? runDeadline = null,
            Action? upstreamGateAcquired = null,
            int browserWidth = 0,
            RuntimeWidthMode widthMode = RuntimeWidthMode.Adaptive,
            int? customWidth = null,
            string fontFaceId = "sarasa-fixed-sc-1.0.40-regular",
            string fontCatalogDigest = "",
            string runtimeFontPath = "",
            string runtimeFontFamilyName = "",
            string webFontAssetDigest = "",
            bool convertBackslashToYen = true)
            => CreateHost(
                Console,
                runtimeClock,
                initializationDeadline,
                runDeadline,
                upstreamGateAcquired,
                browserWidth,
                widthMode,
                customWidth,
                fontFaceId,
                fontCatalogDigest,
                runtimeFontPath,
                runtimeFontFamilyName,
                webFontAssetDigest,
                convertBackslashToYen);

        public EmueraRuntimeHost CreateHost(
            StructuredGameConsole console,
            IRuntimeClock? runtimeClock = null,
            TimeSpan? initializationDeadline = null,
            TimeSpan? runDeadline = null,
            Action? upstreamGateAcquired = null,
            int browserWidth = 0,
            RuntimeWidthMode widthMode = RuntimeWidthMode.Adaptive,
            int? customWidth = null,
            string fontFaceId = "sarasa-fixed-sc-1.0.40-regular",
            string fontCatalogDigest = "",
            string runtimeFontPath = "",
            string runtimeFontFamilyName = "",
            string webFontAssetDigest = "",
            bool convertBackslashToYen = true)
        {
            var fileSystem = new LocalRuntimeFileSystem(Paths);
            var options = new EmueraRuntimeOptions(
                Paths,
                console,
                fileSystem,
                runtimeClock ?? console.Clock,
                new RuntimeImageMetadataPort(fileSystem),
                AudioPort,
                EmueraCompatibilityProfiles.V18Compatible,
                initializationDeadline ?? TimeSpan.FromSeconds(5),
                runDeadline ?? TimeSpan.FromSeconds(5),
                browserWidth: browserWidth,
                widthMode: widthMode,
                customWidth: customWidth,
                fontFaceId: fontFaceId,
                fontCatalogDigest: fontCatalogDigest,
                runtimeFontPath: runtimeFontPath,
                runtimeFontFamilyName: runtimeFontFamilyName,
                webFontAssetDigest: webFontAssetDigest,
                convertBackslashToYen: convertBackslashToYen);
            return EmueraRuntimeHost.Create(options with { UpstreamGateAcquired = upstreamGateAcquired });
        }

        public void Dispose()
        {
            if (!ownsRoot)
            {
                return;
            }

            try
            {
                Directory.Delete(Root, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static byte[] CreateVp8xMetadataWebp(int width, int height)
    {
        const int vp8xPayloadLength = 10;
        const int vp8PayloadLength = 10;
        const int riffPayloadLength = 4 + 8 + vp8xPayloadLength + 8 + vp8PayloadLength;
        byte[] result = new byte[8 + riffPayloadLength];
        "RIFF"u8.CopyTo(result.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), riffPayloadLength);
        "WEBP"u8.CopyTo(result.AsSpan(8, 4));

        int offset = 12;
        "VP8X"u8.CopyTo(result.AsSpan(offset, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + 4, 4), vp8xPayloadLength);
        WriteUInt24LittleEndian(result.AsSpan(offset + 12, 3), checked(width - 1));
        WriteUInt24LittleEndian(result.AsSpan(offset + 15, 3), checked(height - 1));
        offset += 8 + vp8xPayloadLength;

        "VP8 "u8.CopyTo(result.AsSpan(offset, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + 4, 4), vp8PayloadLength);
        result[offset + 8] = 0;
        result[offset + 11] = 0x9d;
        result[offset + 12] = 0x01;
        result[offset + 13] = 0x2a;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(offset + 14, 2), checked((ushort)width));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(offset + 16, 2), checked((ushort)height));
        return result;
    }

    private static void WriteUInt24LittleEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }

    private static async Task<string> RunWithInputAsync(RuntimeHostFixture fixture, string input)
    {
        var console = new StructuredGameConsole();
        await using EmueraRuntimeHost host = fixture.CreateHost(console);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ConsolePrompt prompt = Assert.IsType<ConsolePrompt>(console.CurrentPrompt);
        Assert.Equal(ConsoleInputResultKind.Accepted, console.SubmitCurrentInput(
            new ConsoleInputAttempt($"isolation-{input}", input)).Kind);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await run).Status);
        return RuntimeTranscriptProjector.Project(console.Snapshot.VisibleNodes);
    }

    private sealed class RecordingRuntimeClock : IRuntimeClock
    {
        public List<TimeSpan> Delays { get; } = [];
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            Delays.Add(delay);
            return delay == TimeSpan.FromMilliseconds(25)
                ? ValueTask.CompletedTask
                : new ValueTask(Task.Delay(delay, cancellationToken));
        }
    }

    private sealed class RunDeadlineClock : IRuntimeClock
    {
        private int delayCount;
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref delayCount) == 1
                ? new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken))
                : ValueTask.CompletedTask;
    }

    private sealed class GateAcquiredDeadlineClock(ManualResetEventSlim gateAcquired) : IRuntimeClock
    {
        private readonly TimeProviderRuntimeClock systemClock = new();

        public DateTimeOffset UtcNow => systemClock.UtcNow;
        public long GetTimestamp() => systemClock.GetTimestamp();
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            systemClock.GetElapsedTime(startingTimestamp, endingTimestamp);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            new(Task.Run(() => gateAcquired.Wait(cancellationToken), cancellationToken));
    }

}
