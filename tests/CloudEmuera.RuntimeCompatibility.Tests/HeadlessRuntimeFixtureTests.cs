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
                "data", "sessions", "sess_01a00f1017c0715798ed61a24837cfc7", "root", "resources", "1.png");
            File.Copy(sourceImage, Path.Combine(root, "resources", "1.png"));
            File.WriteAllText(
                Path.Combine(root, "resources", "立ち絵.csv"),
                "立絵_服_通常_1,1.png,0,0,180,180\n");

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
            console.SubmitInput(new ConsoleInputCommand(askM.PromptId, "empty", string.Empty)).Kind);
        Assert.Equal(askM.PromptId, console.CurrentPrompt!.PromptId);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            console.SubmitInput(new ConsoleInputCommand(askM.PromptId, "choice", "2")).Kind);

        // 2) The kojo PRINTFORMW opens a wait-for-any-key (EnterKey) prompt.
        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ConsolePrompt printFormW = console.CurrentPrompt!;
        Assert.Equal(ConsoleInputType.EnterKey, printFormW.InputType);
        prompts.Add(printFormW.InputType);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            console.SubmitInput(new ConsoleInputCommand(printFormW.PromptId, "continue", string.Empty)).Kind);

        // 3) The movement command ends and the next INPUT (menu) opens.
        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ConsolePrompt menu = console.CurrentPrompt!;
        Assert.Equal(ConsoleInputType.Integer, menu.InputType);
        prompts.Add(menu.InputType);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            console.SubmitInput(new ConsoleInputCommand(menu.PromptId, "menu", "7")).Kind);

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
                console.SubmitInput(new ConsoleInputCommand(current.PromptId, $"driver-{i}", "7")).Kind);
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
        Assert.Equal(EmueraRuntimeStatus.Completed, initialized.Status);
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
    public void HtmlPrintStartsImagesOnANewLineAfterPartialPrintOutput()
    {
        // PLAY-002/COMP-007: HTML_PRINT flushes a partial PRINT line but
        // starts its own physical display row. This keeps the right-aligned
        // clock between the leading marker and the following status output.
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
        Assert.Equal("logical", text.Style.FontFamily);
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
        Assert.Single(console.Snapshot.Scrollback);
        Assert.Equal("temporary", Assert.IsType<TextNode>(Assert.Single(console.Snapshot.Scrollback[0].Nodes)).Text);
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

        EmueraRuntimeResult result = await host.RunAsync();

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
            fixture.Console.SubmitInput(new ConsoleInputCommand(prompt.PromptId, "twait-input", string.Empty)).Kind);

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
            fixture.Console.SubmitInput(new ConsoleInputCommand(prompt.PromptId, "twait-forced", string.Empty)).Kind);
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
        Assert.Equal(ConsoleInputResultKind.Accepted, fixture.Console.SubmitInput(
            new ConsoleInputCommand(prompt.PromptId, "rich-client", "7")).Kind);
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
        Assert.Equal("headless-p0.5.1", report.IntegrationVersion);
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
            fixture.Console.SubmitInput(new ConsoleInputCommand(prompt.PromptId, "text-message", "001 text")).Kind);

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
            fixture.Console.SubmitInput(new ConsoleInputCommand(
                prompt.PromptId,
                "empty-button-message",
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
    public async Task MissingGameBaseReturnsInitializationDiagnostic()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nQUIT\n");
        File.Delete(Path.Combine(fixture.Paths.CsvRoot, "GAMEBASE.CSV"));
        await using EmueraRuntimeHost host = fixture.CreateHost();

        EmueraRuntimeResult result = await host.InitializeAsync();

        Assert.Equal(EmueraRuntimeStatus.InitializationFailed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "runtime_initialization_failed" && diagnostic.IsFatal);
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task UnsupportedInstructionFailsClosedDuringInitialization()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nCALLSHARP forbidden\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();

        EmueraRuntimeResult result = await host.InitializeAsync();

        Assert.Equal(EmueraRuntimeStatus.UnsupportedCapability, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "unsupported_runtime_capability" && diagnostic.IsFatal);
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
            int browserWidth = 0)
            => CreateHost(
                Console,
                runtimeClock,
                initializationDeadline,
                runDeadline,
                upstreamGateAcquired,
                browserWidth);

        public EmueraRuntimeHost CreateHost(
            StructuredGameConsole console,
            IRuntimeClock? runtimeClock = null,
            TimeSpan? initializationDeadline = null,
            TimeSpan? runDeadline = null,
            Action? upstreamGateAcquired = null,
            int browserWidth = 0)
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
                browserWidth: browserWidth);
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

    private static async Task<string> RunWithInputAsync(RuntimeHostFixture fixture, string input)
    {
        var console = new StructuredGameConsole();
        await using EmueraRuntimeHost host = fixture.CreateHost(console);
        Assert.Equal(EmueraRuntimeStatus.Completed, (await host.InitializeAsync()).Status);

        Task<EmueraRuntimeResult> run = host.RunAsync();
        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
        ConsolePrompt prompt = Assert.IsType<ConsolePrompt>(console.CurrentPrompt);
        Assert.Equal(ConsoleInputResultKind.Accepted, console.SubmitInput(
            new ConsoleInputCommand(prompt.PromptId, $"isolation-{input}", input)).Kind);
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
