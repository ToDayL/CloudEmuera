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
        Assert.Equal("headless-p0.4.1", report.IntegrationVersion);
        Assert.Contains(
            report.AssertionEvidence,
            evidence => evidence.Name == "score=3" && evidence.Passed && evidence.VerifiedByVisibleOutput);
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
    public async Task InitializationDeadlineReleasesGateAndPrivateViewForNextHost()
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
        Assert.Empty(Directory.EnumerateDirectories(timedOutFixture.Paths.TemporaryRoot, "upstream-view-*"));

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
    [Trait("Category", "RuntimeBridge")]
    public async Task UpstreamLoaderConsumesGameContentFromFilePortOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudemuera-port-only", Guid.NewGuid().ToString("N"));
        string game = Path.Combine(root, "empty-game-root");
        string session = Path.Combine(root, "session");
        string workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(session);
        Directory.CreateDirectory(workspace);
        try
        {
            var paths = new RuntimePaths(session, game, workspace, RuntimeSaveLayout.Root);
            Directory.CreateDirectory(paths.ConfigurationRoot);
            Directory.CreateDirectory(paths.TemporaryRoot);
            Directory.CreateDirectory(paths.RootSaveRoot);
            Directory.CreateDirectory(paths.SavDirectoryRoot);
            File.WriteAllText(Path.Combine(paths.ConfigurationRoot, "emuera.config"), "UseSaveFolder:YES\n");
            var local = new LocalRuntimeFileSystem(paths);
            var fileSystem = new PortOnlyGameFileSystem(local, new Dictionary<string, string>
            {
                ["CSV/GAMEBASE.CSV"] = "タイトル,port-only\n",
                ["ERB/START.ERB"] = "@SYSTEM_TITLE\nPRINTL PORT-ONLY\nQUIT\n"
            });
            var console = new StructuredGameConsole();
            var options = new EmueraRuntimeOptions(
                paths,
                console,
                fileSystem,
                console.Clock,
                new RuntimeImageMetadataPort(fileSystem),
                new RecordingRuntimeAudioPort(),
                EmueraCompatibilityProfiles.EmEeCurrent,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));

            await using EmueraRuntimeHost host = EmueraRuntimeHost.Create(options);
            EmueraRuntimeResult initialized = await host.InitializeAsync();
            Assert.True(
                initialized.Status == EmueraRuntimeStatus.Completed,
                string.Join(" | ", initialized.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
            Assert.Equal(EmueraRuntimeStatus.Completed, (await host.RunAsync()).Status);
            Assert.Equal("PORT-ONLY", RuntimeTranscriptProjector.Project(console.Snapshot.VisibleNodes));
            Assert.Empty(Directory.EnumerateFileSystemEntries(game));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
            RuntimePaths paths,
            LocalRuntimeFileSystem fileSystem,
            StructuredGameConsole console,
            RecordingRuntimeAudioPort audioPort)
        {
            Root = root;
            Paths = paths;
            FileSystem = fileSystem;
            Console = console;
            AudioPort = audioPort;
        }

        public string Root { get; }
        public RuntimePaths Paths { get; }
        public LocalRuntimeFileSystem FileSystem { get; }
        public StructuredGameConsole Console { get; }
        public RecordingRuntimeAudioPort AudioPort { get; }

        public static RuntimeHostFixture Create(string erb)
        {
            string root = Path.Combine(Path.GetTempPath(), "cloudemuera-runtime-bridge", Guid.NewGuid().ToString("N"));
            string game = Path.Combine(root, "game");
            string session = Path.Combine(root, "session");
            string workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(Path.Combine(game, "CSV"));
            Directory.CreateDirectory(Path.Combine(game, "ERB"));
            Directory.CreateDirectory(Path.Combine(game, "resources"));
            Directory.CreateDirectory(session);
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(game, "CSV", "GAMEBASE.CSV"), "title,bridge-test\n");
            File.WriteAllText(Path.Combine(game, "ERB", "START.ERB"), erb);
            var paths = new RuntimePaths(session, game, workspace, RuntimeSaveLayout.Root);
            Directory.CreateDirectory(paths.ConfigurationRoot);
            Directory.CreateDirectory(paths.TemporaryRoot);
            Directory.CreateDirectory(paths.RootSaveRoot);
            Directory.CreateDirectory(paths.SavDirectoryRoot);
            File.WriteAllText(Path.Combine(paths.ConfigurationRoot, "emuera.config"), "UseSaveFolder:NO\n");
            var fileSystem = new LocalRuntimeFileSystem(paths);
            var console = new StructuredGameConsole();
            return new RuntimeHostFixture(root, paths, fileSystem, console, new RecordingRuntimeAudioPort());
        }

        public EmueraRuntimeHost CreateHost(
            IRuntimeClock? runtimeClock = null,
            TimeSpan? initializationDeadline = null,
            TimeSpan? runDeadline = null,
            Action? upstreamGateAcquired = null)
        {
            var options = new EmueraRuntimeOptions(
                Paths,
                Console,
                FileSystem,
                runtimeClock ?? Console.Clock,
                new RuntimeImageMetadataPort(FileSystem),
                AudioPort,
                EmueraCompatibilityProfiles.V18Compatible,
                initializationDeadline ?? TimeSpan.FromSeconds(5),
                runDeadline ?? TimeSpan.FromSeconds(5));
            return EmueraRuntimeHost.Create(options with { UpstreamGateAcquired = upstreamGateAcquired });
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
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

    private sealed class PortOnlyGameFileSystem(
        IRuntimeFileSystem writableFileSystem,
        IReadOnlyDictionary<string, string> gameFiles) : IRuntimeFileSystem
    {
        public bool FileExists(RuntimeFilePath path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return path.Area == RuntimeFileArea.GameContent
                ? gameFiles.ContainsKey(path.RelativePath.Value)
                : writableFileSystem.FileExists(path, cancellationToken);
        }

        public bool DirectoryExists(RuntimeFilePath path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.Area != RuntimeFileArea.GameContent)
                return writableFileSystem.DirectoryExists(path, cancellationToken);
            string prefix = path.RelativePath.Value.TrimEnd('/') + "/";
            return gameFiles.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal));
        }

        public Stream OpenRead(RuntimeFilePath path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.Area != RuntimeFileArea.GameContent)
                return writableFileSystem.OpenRead(path, cancellationToken);
            if (!gameFiles.TryGetValue(path.RelativePath.Value, out string? content))
                throw new FileNotFoundException("The logical fixture file was not declared.");
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content), writable: false);
        }

        public Stream OpenWrite(RuntimeFilePath path, RuntimeFileOpenMode mode, CancellationToken cancellationToken = default) =>
            path.Area == RuntimeFileArea.GameContent
                ? throw new RuntimeFileAccessException(RuntimePathReasonCodes.ReadOnlyArea, "GameContent is read-only.", path.LogicalPath, path.Area)
                : writableFileSystem.OpenWrite(path, mode, cancellationToken);

        public void CreateDirectory(RuntimeFilePath path, CancellationToken cancellationToken = default)
        {
            if (path.Area == RuntimeFileArea.GameContent)
                throw new RuntimeFileAccessException(RuntimePathReasonCodes.ReadOnlyArea, "GameContent is read-only.", path.LogicalPath, path.Area);
            writableFileSystem.CreateDirectory(path, cancellationToken);
        }

        public IReadOnlyList<RuntimeFileEntry> Enumerate(RuntimeFilePath directory, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory.Area != RuntimeFileArea.GameContent)
                return writableFileSystem.Enumerate(directory, cancellationToken);
            string prefix = directory.RelativePath.Value.TrimEnd('/') + "/";
            return gameFiles
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && !pair.Key[prefix.Length..].Contains('/'))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new RuntimeFileEntry(
                    new RuntimeFilePath(RuntimeFileArea.GameContent, pair.Key),
                    RuntimeFileEntryKind.File,
                    System.Text.Encoding.UTF8.GetByteCount(pair.Value),
                    DateTimeOffset.UnixEpoch))
                .ToArray();
        }

        public IReadOnlyList<RuntimeFileEntry> Enumerate(RuntimeFileArea area, CancellationToken cancellationToken = default) =>
            area == RuntimeFileArea.GameContent
                ? Array.Empty<RuntimeFileEntry>()
                : writableFileSystem.Enumerate(area, cancellationToken);

        public RuntimeFileMetadata GetMetadata(RuntimeFilePath path, CancellationToken cancellationToken = default) =>
            path.Area == RuntimeFileArea.GameContent && gameFiles.TryGetValue(path.RelativePath.Value, out string? content)
                ? new RuntimeFileMetadata(RuntimeFileEntryKind.File, System.Text.Encoding.UTF8.GetByteCount(content), DateTimeOffset.UnixEpoch)
                : writableFileSystem.GetMetadata(path, cancellationToken);

        public void Move(RuntimeFilePath source, RuntimeFilePath destination, bool overwrite = false, CancellationToken cancellationToken = default) =>
            writableFileSystem.Move(source, destination, overwrite, cancellationToken);
        public void Replace(RuntimeFilePath source, RuntimeFilePath destination, RuntimeFilePath? backupPath = null, CancellationToken cancellationToken = default) =>
            writableFileSystem.Replace(source, destination, backupPath, cancellationToken);
        public void Delete(RuntimeFilePath path, bool recursive = false, CancellationToken cancellationToken = default) =>
            writableFileSystem.Delete(path, recursive, cancellationToken);
    }
}
