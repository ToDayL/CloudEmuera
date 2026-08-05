using CloudEmuera.EmueraRuntime.Headless;
using CloudEmuera.EmueraRuntime.UpstreamHeadless;
using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeCompatibility.Tests;

[Trait("Category", "RuntimeCompatibility")]
public sealed class HeadlessRuntimeFixtureTests
{
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
                Assert.Equal("RIGHT", Assert.IsType<TextNode>(Assert.Single(button.Children)).Text);
                Assert.Equal("7", button.Value);
                Assert.Null(button.Tooltip);
            },
            button =>
            {
                Assert.Equal("LEFT", Assert.IsType<TextNode>(Assert.Single(button.Children)).Text);
                Assert.Equal("left-value", button.Value);
                Assert.Null(button.Tooltip);
            });
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
    public async Task GraphicsFunctionFailsClosedBeforeAnyGdiObjectCanBeCreated()
    {
        using var fixture = RuntimeHostFixture.Create("@SYSTEM_TITLE\nRESULT = GCREATE(0, 2, 2)\nQUIT\n");
        await using EmueraRuntimeHost host = fixture.CreateHost();

        EmueraRuntimeResult result = await host.InitializeAsync();

        Assert.Equal(EmueraRuntimeStatus.UnsupportedCapability, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "unsupported_runtime_capability" && diagnostic.Message.Contains("GCREATE", StringComparison.Ordinal));
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
            string gameVersionRoot,
            SessionRootPublishedManifest manifest,
            RuntimePaths paths,
            LocalRuntimeFileSystem fileSystem,
            StructuredGameConsole console,
            RecordingRuntimeAudioPort audioPort,
            bool ownsRoot)
        {
            Root = root;
            GameVersionRoot = gameVersionRoot;
            Manifest = manifest;
            Paths = paths;
            FileSystem = fileSystem;
            Console = console;
            AudioPort = audioPort;
            this.ownsRoot = ownsRoot;
        }

        private readonly bool ownsRoot;

        public string Root { get; }
        public string GameVersionRoot { get; }
        public SessionRootPublishedManifest Manifest { get; }
        public RuntimePaths Paths { get; }
        public LocalRuntimeFileSystem FileSystem { get; }
        public StructuredGameConsole Console { get; }
        public RecordingRuntimeAudioPort AudioPort { get; }

        public static RuntimeHostFixture Create(
            string erb,
            RuntimeSaveLayout saveLayout = RuntimeSaveLayout.Root,
            string? configuration = null,
            string? saveDeclarations = null)
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
            File.WriteAllText(Path.Combine(game, "ERB", "START.ERB"), erb);
            if (saveDeclarations is not null)
            {
                File.WriteAllText(Path.Combine(game, "ERB", "SAVE.ERH"), saveDeclarations);
            }

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
                GameVersionRoot,
                sessionRoot,
                sessionWorkspace,
                [Paths.SessionRoot])
                .Build(Manifest, new SessionRootCopyLimits());
            var fileSystem = new LocalRuntimeFileSystem(layout.RuntimePaths);
            return new RuntimeHostFixture(
                Root,
                GameVersionRoot,
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
            Action? upstreamGateAcquired = null)
            => CreateHost(
                Console,
                runtimeClock,
                initializationDeadline,
                runDeadline,
                upstreamGateAcquired);

        public EmueraRuntimeHost CreateHost(
            StructuredGameConsole console,
            IRuntimeClock? runtimeClock = null,
            TimeSpan? initializationDeadline = null,
            TimeSpan? runDeadline = null,
            Action? upstreamGateAcquired = null)
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
                runDeadline ?? TimeSpan.FromSeconds(5));
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
