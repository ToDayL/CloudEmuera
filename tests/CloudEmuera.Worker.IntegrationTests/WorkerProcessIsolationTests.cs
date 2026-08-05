using System.Diagnostics;
using System.Text;
using CloudEmuera.Ipc;
using CloudEmuera.Ipc.V1;
using CloudEmuera.RuntimeAdapter;
using CloudEmuera.Supervisor;
using CloudEmuera.Worker;
using ProtoConsoleOperation = CloudEmuera.Ipc.V1.ConsoleOperation;
using Xunit;

namespace CloudEmuera.Worker.IntegrationTests;

[Trait("Category", "ProcessIsolation")]
public sealed class WorkerProcessIsolationTests
{
    [Theory]
    [InlineData("v18-core", "v18-compatible", RuntimeSaveLayout.Root, "7")]
    [InlineData("em-ee-core", "em-ee-current", RuntimeSaveLayout.SavDirectory, "4")]
    public async Task RealWorkerCompletesInputRoundtripThroughUds(
        string fixtureId,
        string profile,
        RuntimeSaveLayout saveLayout,
        string input)
    {
        await using var fixture = FixtureWorkspace.Create(fixtureId, saveLayout);
        string workerAssembly = typeof(ConsoleWireMapper).Assembly.Location;
        await using SupervisorHost supervisor = await SupervisorHost.StartAsync(
            new SupervisorOptions(fixture.SupervisorRoot, workerAssembly)
            {
                RegistrationTimeout = TimeSpan.FromSeconds(15),
                WorkerShutdownTimeout = TimeSpan.FromSeconds(5)
            });

        var binding = new WorkerBinding($"sess_{fixtureId}", $"wrk_{fixtureId}", 1);
        SupervisorWorkerSession session = await supervisor.LaunchWorkerAsync(
            new WorkerLaunchRequest(
                binding,
                fixture.SessionRoot,
                profile,
                saveLayout,
                fixture.Manifest.ManifestDigest));

        Assert.NotEqual(Environment.ProcessId, session.ProcessId);
        await session.SendStartRuntimeAsync(TimeSpan.FromSeconds(15));
        WorkerEnvelope startReceipt = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.CommandResult &&
                value.CommandResult.Accepted,
            TimeSpan.FromSeconds(5));
        Assert.Equal("accepted", startReceipt.CommandResult.ReasonCode);

        WorkerEnvelope ready = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready,
            TimeSpan.FromSeconds(15));
        Assert.Equal(RuntimeBaseline.CloudEmueraIntegrationVersion, ready.Ready.RuntimeIntegrationVersion);
        Assert.Equal(RuntimeBaseline.UpstreamCommit, ready.Ready.UpstreamCommit);
        Assert.Equal(fixture.Manifest.ManifestDigest, ready.Ready.SessionRootManifestDigest);

        WorkerEnvelope promptBatch = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.DisplayBatch &&
                value.DisplayBatch.Operations.Any(operation =>
                    operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt),
            TimeSpan.FromSeconds(15));
        string promptId = promptBatch.DisplayBatch.Operations
            .Where(operation => operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt)
            .Select(operation => operation.OpenPrompt.Prompt.PromptId)
            .Single();

        await session.SendInputAsync(promptId, $"client_{fixtureId}", input);
        WorkerEnvelope accepted = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.InputResult &&
                value.InputResult.Kind == InputResultKind.InputResultAccepted,
            TimeSpan.FromSeconds(5));
        Assert.Equal(promptId, accepted.InputResult.PromptId);
        Assert.Equal(input, accepted.InputResult.Value);

        // The same client message is submitted before the runtime has a chance
        // to expose another prompt. InputCoordinator must make this a no-op.
        await session.SendInputAsync(promptId, $"client_{fixtureId}", input);
        WorkerEnvelope duplicate = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.InputResult &&
                value.InputResult.Kind == InputResultKind.InputResultDuplicate,
            TimeSpan.FromSeconds(5));
        Assert.Equal(accepted.InputResult.ClientMessageId, duplicate.InputResult.ClientMessageId);

        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.RuntimeCompleted,
            TimeSpan.FromSeconds(15));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.WorkerStopped,
            TimeSpan.FromSeconds(15));
        Assert.Equal(0, await session.WaitForExitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            Normalize(File.ReadAllText(Path.Combine(fixture.FixtureRoot, "expected-transcript.txt"))),
            ProjectTranscript(session.DisplayBatches));
        Assert.True(Directory.Exists(fixture.SessionRoot));
        Assert.True(File.Exists(Path.Combine(fixture.SessionRoot, SessionRootLayoutBuilder.BindingMetadataFileName)));
        Assert.Equal(fixture.PublishedDigest, fixture.ComputePublishedDigest());
    }

    [Fact]
    public async Task StartIsIdempotentAndStopCancelsWaitingInput()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using SupervisorHost supervisor = await SupervisorHost.StartAsync(
            new SupervisorOptions(fixture.SupervisorRoot, typeof(ConsoleWireMapper).Assembly.Location));
        var binding = new WorkerBinding("sess_stop", "wrk_stop", 4);
        SupervisorWorkerSession session = await supervisor.LaunchWorkerAsync(
            new WorkerLaunchRequest(binding, fixture.SessionRoot, "v18-compatible", RuntimeSaveLayout.Root,
                fixture.Manifest.ManifestDigest));

        await session.SendStartRuntimeAsync(TimeSpan.FromSeconds(10));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.CommandResult && value.CommandResult.Accepted,
            TimeSpan.FromSeconds(5));
        await session.SendStartRuntimeAsync(TimeSpan.FromSeconds(10));
        WorkerEnvelope duplicateStart = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.CommandResult &&
                !value.CommandResult.Accepted && value.CommandResult.ReasonCode == IpcReasonCodes.AlreadyStarted,
            TimeSpan.FromSeconds(5));
        Assert.Equal("start_runtime", duplicateStart.CommandResult.CommandType);

        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready,
            TimeSpan.FromSeconds(15));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.DisplayBatch &&
                value.DisplayBatch.Operations.Any(operation =>
                    operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt),
            TimeSpan.FromSeconds(15));

        await session.StopAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await session.WaitForExitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(Directory.Exists(fixture.SessionRoot));
    }

    [Fact]
    public async Task DisconnectingSupervisorStreamKeepsPromptAndWorkerAlive()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using SupervisorHost supervisor = await SupervisorHost.StartAsync(
            new SupervisorOptions(fixture.SupervisorRoot, typeof(ConsoleWireMapper).Assembly.Location)
            {
                RegistrationTimeout = TimeSpan.FromSeconds(15)
            });
        SupervisorWorkerSession session = await supervisor.LaunchWorkerAsync(new WorkerLaunchRequest(
            new WorkerBinding("sess_reconnect", "wrk_reconnect", 6), fixture.SessionRoot,
            "v18-compatible", RuntimeSaveLayout.Root, fixture.Manifest.ManifestDigest));

        await session.SendStartRuntimeAsync();
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.CommandResult && value.CommandResult.Accepted,
            TimeSpan.FromSeconds(5));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready,
            TimeSpan.FromSeconds(15));
        WorkerEnvelope promptBatch = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.DisplayBatch &&
                value.DisplayBatch.Operations.Any(operation =>
                    operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt),
            TimeSpan.FromSeconds(15));
        string promptId = promptBatch.DisplayBatch.Operations
            .Single(operation => operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt)
            .OpenPrompt.Prompt.PromptId;
        int previousConnectionCount = session.ConnectionCount;

        await session.DisconnectCurrentConnectionForTestAsync();
        await session.WaitForConnectionCountAsync(previousConnectionCount + 1, TimeSpan.FromSeconds(10));
        Assert.False(session.HasExited);

        await session.SendInputAsync(promptId, "client_reconnect", "7");
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.InputResult &&
                value.InputResult.Kind == InputResultKind.InputResultAccepted,
            TimeSpan.FromSeconds(5));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.WorkerStopped,
            TimeSpan.FromSeconds(15));
        Assert.Equal(0, await session.WaitForExitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task IndependentControlClientProcessExitDoesNotStopWorker()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using SupervisorHost supervisor = await SupervisorHost.StartAsync(
            new SupervisorOptions(fixture.SupervisorRoot, typeof(ConsoleWireMapper).Assembly.Location));
        SupervisorWorkerSession session = await supervisor.LaunchWorkerAsync(new WorkerLaunchRequest(
            new WorkerBinding("sess_control_exit", "wrk_control_exit", 7),
            fixture.SessionRoot,
            "v18-compatible",
            RuntimeSaveLayout.Root,
            fixture.Manifest.ManifestDigest));

        await session.SendStartRuntimeAsync();
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready,
            TimeSpan.FromSeconds(15));
        WorkerEnvelope promptBatch = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.DisplayBatch &&
                value.DisplayBatch.Operations.Any(operation =>
                    operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt),
            TimeSpan.FromSeconds(15));
        string promptId = promptBatch.DisplayBatch.Operations
            .Single(operation => operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt)
            .OpenPrompt.Prompt.PromptId;

        // P0-06 has no formal API↔Supervisor business IPC. This independent
        // probe process exercises the WorkerControl endpoint and exits after
        // its rejected registration; it must not own or cancel the real Worker.
        string probeBootstrapPath = Path.Combine(fixture.SupervisorRoot, "bootstrap", "control-probe.json");
        WorkerBootstrapFile.Write(probeBootstrapPath, new WorkerBootstrapDocument
        {
            SessionId = "sess_control_probe",
            WorkerId = "wrk_control_probe",
            WorkerEpoch = 1,
            SessionRoot = fixture.SessionRoot,
            CompatibilityProfile = "v18-compatible",
            SupervisorSocketPath = supervisor.SocketPath,
            BootstrapToken = IpcProtocol.CreateBootstrapToken(),
            ConnectDeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds(),
            HeartbeatIntervalMilliseconds = 500,
            ShutdownGracePeriodMilliseconds = 5_000,
            SaveLayout = (int)RuntimeSaveLayout.Root,
            SessionRootManifestDigest = fixture.Manifest.ManifestDigest
        });

        ProcessStartInfo probeStart = new("dotnet")
        {
            WorkingDirectory = fixture.SessionRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        probeStart.ArgumentList.Add(typeof(ConsoleWireMapper).Assembly.Location);
        probeStart.ArgumentList.Add("--bootstrap-file");
        probeStart.ArgumentList.Add(probeBootstrapPath);
        using Process probe = Process.Start(probeStart) ?? throw new InvalidOperationException("The control probe could not start.");
        try
        {
            await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(11, probe.ExitCode);
        }
        finally
        {
            if (!probe.HasExited)
            {
                probe.Kill(entireProcessTree: true);
                await probe.WaitForExitAsync();
            }

            WorkerBootstrapFile.DeleteIfOwned(probeBootstrapPath);
        }

        Assert.False(session.HasExited);
        await session.SendInputAsync(promptId, "client_control_exit", "7");
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.InputResult &&
                value.InputResult.Kind == InputResultKind.InputResultAccepted,
            TimeSpan.FromSeconds(5));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.WorkerStopped,
            TimeSpan.FromSeconds(15));
        Assert.Equal(0, await session.WaitForExitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task MismatchedCommandBindingIsRejectedBeforeInputExecution()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using SupervisorHost supervisor = await SupervisorHost.StartAsync(
            new SupervisorOptions(fixture.SupervisorRoot, typeof(ConsoleWireMapper).Assembly.Location));
        SupervisorWorkerSession session = await supervisor.LaunchWorkerAsync(new WorkerLaunchRequest(
            new WorkerBinding("sess_binding", "wrk_binding", 8), fixture.SessionRoot,
            "v18-compatible", RuntimeSaveLayout.Root, fixture.Manifest.ManifestDigest));

        await session.SendStartRuntimeAsync();
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.CommandResult && value.CommandResult.Accepted,
            TimeSpan.FromSeconds(5));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready,
            TimeSpan.FromSeconds(15));
        WorkerEnvelope promptBatch = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.DisplayBatch &&
                value.DisplayBatch.Operations.Any(operation =>
                    operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt),
            TimeSpan.FromSeconds(15));
        string promptId = promptBatch.DisplayBatch.Operations
            .Single(operation => operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt)
            .OpenPrompt.Prompt.PromptId;
        int previousConnectionCount = session.ConnectionCount;

        await session.SendRawAsync(new SupervisorEnvelope
        {
            ProtocolVersion = IpcProtocol.CurrentVersion,
            MessageId = "wrong_binding_command",
            SessionId = "other_session",
            WorkerId = session.Binding.WorkerId,
            WorkerEpoch = session.Binding.WorkerEpoch,
            SubmitInput = new SubmitInput
            {
                PromptId = promptId,
                ClientMessageId = "wrong_binding_client",
                Value = "7",
                DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds()
            }
        });
        await session.WaitForConnectionCountAsync(previousConnectionCount + 1, TimeSpan.FromSeconds(10));
        Assert.False(session.HasExited);

        await session.SendInputAsync(promptId, "client_binding", "7");
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.InputResult &&
                value.InputResult.Kind == InputResultKind.InputResultAccepted,
            TimeSpan.FromSeconds(5));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.WorkerStopped,
            TimeSpan.FromSeconds(15));
        Assert.Equal(0, await session.WaitForExitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WrongBootstrapTokenIsRejectedBeforeRuntimeStarts()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using SupervisorHost supervisor = await SupervisorHost.StartAsync(
            new SupervisorOptions(fixture.SupervisorRoot, typeof(ConsoleWireMapper).Assembly.Location)
            {
                BootstrapTransformForTest = document => document with { BootstrapToken = "wrong_bootstrap_token" }
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.LaunchWorkerAsync(new WorkerLaunchRequest(
            new WorkerBinding("sess_wrong_token", "wrk_wrong_token", 10),
            fixture.SessionRoot,
            "v18-compatible",
            RuntimeSaveLayout.Root,
            fixture.Manifest.ManifestDigest)));

        Assert.Empty(supervisor.Workers);
        Assert.Equal(fixture.PublishedDigest, fixture.ComputePublishedDigest());
    }

    [Fact]
    public async Task WorkerLogsCorrelateLifecycleAndRedactSensitiveValues()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using SupervisorHost supervisor = await SupervisorHost.StartAsync(
            new SupervisorOptions(fixture.SupervisorRoot, typeof(ConsoleWireMapper).Assembly.Location));
        var binding = new WorkerBinding("sess_logging", "wrk_logging", 12);
        SupervisorWorkerSession session = await supervisor.LaunchWorkerAsync(new WorkerLaunchRequest(
            binding,
            fixture.SessionRoot,
            "v18-compatible",
            RuntimeSaveLayout.Root,
            fixture.Manifest.ManifestDigest));
        string bootstrapToken = session.BootstrapToken;
        const string secretInput = "sensitive-input-value";

        await session.SendStartRuntimeAsync();
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready,
            TimeSpan.FromSeconds(15));
        WorkerEnvelope promptBatch = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.DisplayBatch &&
                value.DisplayBatch.Operations.Any(operation =>
                    operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt),
            TimeSpan.FromSeconds(15));
        string promptId = promptBatch.DisplayBatch.Operations
            .Single(operation => operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt)
            .OpenPrompt.Prompt.PromptId;

        await session.SendInputAsync(promptId, "client_logging", secretInput);
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.InputResult,
            TimeSpan.FromSeconds(5));
        await session.StopAsync(TimeSpan.FromSeconds(5));
        await WaitForDiagnosticsAsync(session, value => value.Contains("worker_event", StringComparison.Ordinal));

        string diagnostics = session.ProcessDiagnostics;
        Assert.Contains($"sessionId={binding.SessionId}", diagnostics, StringComparison.Ordinal);
        Assert.Contains($"workerId={binding.WorkerId}", diagnostics, StringComparison.Ordinal);
        Assert.Contains($"workerEpoch={binding.WorkerEpoch}", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(bootstrapToken, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.SessionRoot, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(supervisor.SocketPath, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(secretInput, diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoWorkerProcessesKeepBindingsAndDisplaySequencesSeparate()
    {
        await using var firstFixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using var secondFixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "cloudemuera-ipc-supervisor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeRoot);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(runtimeRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await using SupervisorHost supervisor = await SupervisorHost.StartAsync(
                new SupervisorOptions(runtimeRoot, typeof(ConsoleWireMapper).Assembly.Location)
                {
                    MaxConcurrentWorkers = 2
                });
            SupervisorWorkerSession first = await supervisor.LaunchWorkerAsync(new WorkerLaunchRequest(
                new WorkerBinding("sess_first", "wrk_first", 1), firstFixture.SessionRoot,
                "v18-compatible", RuntimeSaveLayout.Root, firstFixture.Manifest.ManifestDigest));
            SupervisorWorkerSession second = await supervisor.LaunchWorkerAsync(new WorkerLaunchRequest(
                new WorkerBinding("sess_second", "wrk_second", 9), secondFixture.SessionRoot,
                "v18-compatible", RuntimeSaveLayout.Root, secondFixture.Manifest.ManifestDigest));

            await Task.WhenAll(first.SendStartRuntimeAsync(), second.SendStartRuntimeAsync());
            await Task.WhenAll(
                first.WaitForAsync(value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready, TimeSpan.FromSeconds(15)),
                second.WaitForAsync(value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready, TimeSpan.FromSeconds(15)));
            WorkerEnvelope firstPrompt = await first.WaitForAsync(
                value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.DisplayBatch &&
                    value.DisplayBatch.Operations.Any(operation => operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt),
                TimeSpan.FromSeconds(15));
            WorkerEnvelope secondPrompt = await second.WaitForAsync(
                value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.DisplayBatch &&
                    value.DisplayBatch.Operations.Any(operation => operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt),
                TimeSpan.FromSeconds(15));
            await Task.WhenAll(
                CompleteSessionAsync(first, firstPrompt, "one"),
                CompleteSessionAsync(second, secondPrompt, "two"));

            Assert.All(first.DisplayBatches, batch => Assert.True(batch.FirstSequence > 0));
            Assert.All(second.DisplayBatches, batch => Assert.True(batch.FirstSequence > 0));
            Assert.Equal(firstFixture.PublishedDigest, firstFixture.ComputePublishedDigest());
            Assert.Equal(secondFixture.PublishedDigest, secondFixture.ComputePublishedDigest());
        }
        finally
        {
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    private static async Task CompleteSessionAsync(
        SupervisorWorkerSession session,
        WorkerEnvelope promptBatch,
        string value)
    {
        string promptId = promptBatch.DisplayBatch.Operations
            .Single(operation => operation.PayloadCase == ProtoConsoleOperation.PayloadOneofCase.OpenPrompt)
            .OpenPrompt.Prompt.PromptId;
        await session.SendInputAsync(promptId, $"client_{value}", value == "one" ? "1" : "2");
        await session.WaitForAsync(
            eventValue => eventValue.PayloadCase == WorkerEnvelope.PayloadOneofCase.InputResult &&
                eventValue.InputResult.Kind == InputResultKind.InputResultAccepted,
            TimeSpan.FromSeconds(5));
        await session.WaitForAsync(
            eventValue => eventValue.PayloadCase == WorkerEnvelope.PayloadOneofCase.WorkerStopped,
            TimeSpan.FromSeconds(15));
        Assert.Equal(0, await session.WaitForExitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task WaitForDiagnosticsAsync(
        SupervisorWorkerSession session,
        Func<string, bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!predicate(session.ProcessDiagnostics) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(predicate(session.ProcessDiagnostics), session.ProcessDiagnostics);
    }

    private static string ProjectTranscript(IEnumerable<DisplayBatch> batches)
    {
        var nodes = new List<CloudEmuera.RuntimeAdapter.ConsoleNode>();
        foreach (DisplayBatch batch in batches.OrderBy(item => item.LastSequence))
        {
            if (batch.IsSnapshot)
                nodes.Clear();
            foreach (ProtoConsoleOperation operation in batch.Operations)
            {
                CloudEmuera.RuntimeAdapter.ConsoleOperation runtimeOperation = ConsoleWireMapper.FromProto(operation);
                switch (runtimeOperation)
                {
                    case AppendNodesOperation append:
                        nodes.AddRange(append.Nodes);
                        break;
                    case ClearConsoleOperation:
                        nodes.Clear();
                        break;
                }
            }
        }

        var result = new StringBuilder();
        foreach (CloudEmuera.RuntimeAdapter.ConsoleNode node in nodes)
        {
            switch (node)
            {
                case CloudEmuera.RuntimeAdapter.TextNode text:
                    result.Append(text.Text);
                    break;
                case CloudEmuera.RuntimeAdapter.LineBreakNode:
                    result.Append('\n');
                    break;
                case CloudEmuera.RuntimeAdapter.ButtonNode button:
                    result.Append(string.Concat(button.Children.Cast<CloudEmuera.RuntimeAdapter.TextNode>().Select(child => child.Text)));
                    break;
            }
        }

        if (result.Length > 0 && result[^1] == '\n')
            result.Length--;
        return result.ToString();
    }

    private static string Normalize(string value)
    {
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.EndsWith('\n') ? normalized[..^1] : normalized;
    }

    private sealed class FixtureWorkspace : IAsyncDisposable
    {
        private FixtureWorkspace(
            string fixtureRoot,
            string root,
            string publishedRoot,
            string sessionRoot,
            string supervisorRoot,
            SessionRootPublishedManifest manifest,
            string publishedDigest)
        {
            FixtureRoot = fixtureRoot;
            Root = root;
            PublishedRoot = publishedRoot;
            SessionRoot = sessionRoot;
            SupervisorRoot = supervisorRoot;
            Manifest = manifest;
            PublishedDigest = publishedDigest;
        }

        public string FixtureRoot { get; }
        public string Root { get; }
        public string PublishedRoot { get; }
        public string SessionRoot { get; }
        public string SupervisorRoot { get; }
        public SessionRootPublishedManifest Manifest { get; }
        public string PublishedDigest { get; }

        public static FixtureWorkspace Create(string fixtureId, RuntimeSaveLayout saveLayout)
        {
            string repositoryRoot = FindRepositoryRoot();
            string fixtureRoot = Path.Combine(repositoryRoot, "tests", "fixtures", "runtime", fixtureId);
            string root = Path.Combine(Path.GetTempPath(), "cloudemuera-worker-tests", Guid.NewGuid().ToString("N"));
            string publishedRoot = Path.Combine(root, "published-game");
            string sessionRoot = Path.Combine(root, "session-root");
            string workspaceRoot = Path.Combine(root, "session-workspace");
            string supervisorRoot = Path.Combine(root, "supervisor-runtime");
            Directory.CreateDirectory(workspaceRoot);
            Directory.CreateDirectory(supervisorRoot);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(supervisorRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            CopyPublishedGameVersion(fixtureRoot, publishedRoot);
            SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(publishedRoot, fixtureId);
            string publishedDigest = DigestDirectory(publishedRoot);
            _ = new SessionRootLayoutBuilder(publishedRoot, sessionRoot, workspaceRoot, saveLayout)
                .WithPublishedManifest(manifest)
                .Build(manifest, new SessionRootCopyLimits());
            return new FixtureWorkspace(fixtureRoot, root, publishedRoot, sessionRoot, supervisorRoot, manifest, publishedDigest);
        }

        public string ComputePublishedDigest() => DigestDirectory(PublishedRoot);

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }

            return ValueTask.CompletedTask;
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "tests", "fixtures", "runtime", "manifest.json")))
                    return current.FullName;
                current = current.Parent;
            }

            throw new InvalidOperationException("The repository root could not be located.");
        }

        private static void CopyPublishedGameVersion(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (FileSystemInfo entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
            {
                if (entry.Name is "scenario.json" or "save-scenario.json" or "expected-transcript.txt")
                    continue;
                string target = Path.Combine(destination, entry.Name);
                if (entry is DirectoryInfo)
                    CopyPublishedGameVersion(entry.FullName, target);
                else if (entry is FileInfo)
                    File.Copy(entry.FullName, target);
            }
        }

        private static string DigestDirectory(string root)
        {
            using System.Security.Cryptography.IncrementalHash hash =
                System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(value => value, StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')));
                hash.AppendData(File.ReadAllBytes(file));
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
    }
}
