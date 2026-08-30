using System.Diagnostics;
using System.Text;
using CloudEmuera.Api.Realtime;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Ipc;
using CloudEmuera.Ipc.V8;
using CloudEmuera.RuntimeAdapter;
using CloudEmuera.Api.Workers;
using CloudEmuera.Worker;
using RuntimeConsoleSnapshot = CloudEmuera.RuntimeAdapter.ConsoleSnapshot;
using Xunit;

namespace CloudEmuera.Worker.IntegrationTests;

[Trait("Category", "ProcessIsolation")]
public sealed class WorkerProcessIsolationTests
{
    [Fact]
    [Trait("Category", "WorkerLifecycle")]
    public async Task ManagerUsesOneSharedGraceAndForceBudgetForMultipleWorkers()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cloudemuera-shutdown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string workerScript = Path.Combine(root, "sleep-worker.sh");
        File.WriteAllText(workerScript, "#!/bin/sh\nsleep 30\n");
        string firstRoot = Path.Combine(root, "session-a");
        string secondRoot = Path.Combine(root, "session-b");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);

        var options = new WorkerManagerOptions(root, workerScript)
        {
            DotnetPath = "/bin/sh",
            WorkerShutdownTimeout = TimeSpan.FromSeconds(1),
        };
        await using var manager = new WorkerManager(options, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        try
        {
            await manager.StartAsync(CreateSleepSpec(options, "sess_shutdown_a", "wrk_shutdown_a", firstRoot));
            await manager.StartAsync(CreateSleepSpec(options, "sess_shutdown_b", "wrk_shutdown_b", secondRoot));
            Assert.Equal(2, manager.Workers.Count);

            Stopwatch stopwatch = Stopwatch.StartNew();
            await manager.ShutdownAsync("control_plane_stopped");
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"shutdown was not shared across Workers: {stopwatch.Elapsed}");
            Assert.All(manager.Workers, worker => Assert.True(worker.HasExited));
            Assert.True(manager.IsDraining);
        }
        finally
        {
            await manager.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkerSurvivesTheCallingThreadExitingAfterLaunch()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using WorkerManagerHost manager = await WorkerManagerHost.StartAsync(
            new WorkerManagerOptions(fixture.ControlRuntimeRoot, typeof(ConsoleWireMapper).Assembly.Location));
        var launched = new TaskCompletionSource<ApiWorkerSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            try
            {
                launched.TrySetResult(manager.LaunchWorkerAsync(new WorkerLaunchRequest(
                        new WorkerBinding("sess_parent_thread", "wrk_parent_thread", 1),
                        fixture.SessionRoot,
                        "v18-compatible",
                        RuntimeSaveLayout.Root,
                        fixture.Manifest.ManifestDigest))
                    .GetAwaiter()
                    .GetResult());
            }
            catch (Exception exception)
            {
                launched.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "short-lived Worker launch caller"
        };

        caller.Start();
        ApiWorkerSession session = await launched.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(caller.Join(TimeSpan.FromSeconds(5)));

        // PR_SET_PDEATHSIG is tied to the native thread that forked the child.
        // If Process.Start ran on caller, its exit above would SIGKILL Worker.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(
            session.HasExited,
            $"exitCode={session.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "running"}{Environment.NewLine}{session.ProcessDiagnostics}");

        await session.SendStartRuntimeAsync(TimeSpan.FromSeconds(15));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready,
            TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData("v18-core", "v18-compatible", RuntimeSaveLayout.Root, "7")]
    [InlineData("em-ee-core", "em-ee-current", RuntimeSaveLayout.SavDirectory, "4")]
    [Trait("Category", "Snapshot")]
    [Trait("Category", "Realtime")]
    [Trait("Category", "Input")]
    [Trait("Category", "InputDeduplication")]
    public async Task RealWorkerCompletesInputRoundtripThroughUds(
        string fixtureId,
        string profile,
        RuntimeSaveLayout saveLayout,
        string input)
    {
        await using var fixture = FixtureWorkspace.Create(fixtureId, saveLayout);
        string workerAssembly = typeof(ConsoleWireMapper).Assembly.Location;
        await using WorkerManagerHost manager = await WorkerManagerHost.StartAsync(
            new WorkerManagerOptions(fixture.ControlRuntimeRoot, workerAssembly)
            {
                RegistrationTimeout = TimeSpan.FromSeconds(15),
                WorkerShutdownTimeout = TimeSpan.FromSeconds(5)
            });

        var binding = new WorkerBinding($"sess_{fixtureId}", $"wrk_{fixtureId}", 1);
        ApiWorkerSession session = await manager.LaunchWorkerAsync(
            new WorkerLaunchRequest(
                binding,
                fixture.SessionRoot,
                profile,
                saveLayout,
                fixture.Manifest.ManifestDigest));
        await using RealtimeSubscription output = session.OutputHub.Subscribe();

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

        RealtimeFrame firstDisplay = await output.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(RealtimeFrameKind.Snapshot, firstDisplay.Kind);
        string promptId = await WaitForPromptIdAsync(session);

        await session.SendInputAsync($"client_{fixtureId}", input);
        WorkerEnvelope accepted = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.InputResult &&
                value.InputResult.Kind == InputResultKind.Accepted,
            TimeSpan.FromSeconds(5));
        Assert.True(accepted.InputResult.HasResolvedPromptId);
        Assert.Equal(promptId, accepted.InputResult.ResolvedPromptId);
        Assert.Equal(input, accepted.InputResult.NormalizedValue);

        // The same client message is submitted before the runtime has a chance
        // to expose another prompt. InputCoordinator must make this a no-op.
        await session.SendInputAsync($"client_{fixtureId}", input);
        WorkerEnvelope duplicate = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.InputResult &&
                value.InputResult.Kind == InputResultKind.Duplicate,
            TimeSpan.FromSeconds(5));
        Assert.Equal(accepted.InputResult.ClientMessageId, duplicate.InputResult.ClientMessageId);

        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.RuntimeCompleted,
            TimeSpan.FromSeconds(15));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.WorkerStopped,
            TimeSpan.FromSeconds(15));
        Assert.Equal(0, await session.WaitForExitAsync(TimeSpan.FromSeconds(5)));

        RealtimeFrame terminalDisplay = await output.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(terminalDisplay.Kind is RealtimeFrameKind.DisplayFrame or RealtimeFrameKind.Snapshot);
        Assert.True(terminalDisplay.LastSequence > firstDisplay.LastSequence);

        Assert.Equal(
            Normalize(File.ReadAllText(Path.Combine(fixture.FixtureRoot, "expected-transcript.txt"))),
            ProjectTranscript(session.OutputHub.CurrentSnapshot!));
        Assert.True(Directory.Exists(fixture.SessionRoot));
        Assert.True(File.Exists(Path.Combine(fixture.SessionRoot, SessionRootLayoutBuilder.BindingMetadataFileName)));
        // SAVE-011: the real Worker uses the same SessionRoot-local upstream
        // setting lifecycle as the headless Validator.
        Assert.True(File.Exists(Path.Combine(fixture.SessionRoot, "setting.json")));
        Assert.Equal(fixture.PublishedDigest, fixture.ComputePublishedDigest());
    }

    [Fact]
    [Trait("Category", "WorkerLifecycle")]
    public async Task WorkerReportsStableReasonWhenFontCatalogDoesNotMatch()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using WorkerManagerHost manager = await WorkerManagerHost.StartAsync(
            new WorkerManagerOptions(fixture.ControlRuntimeRoot, typeof(ConsoleWireMapper).Assembly.Location));
        ApiWorkerSession session = await manager.LaunchWorkerAsync(new WorkerLaunchRequest(
            new WorkerBinding("sess_font_catalog_mismatch", "wrk_font_catalog_mismatch", 2),
            fixture.SessionRoot,
            "v18-compatible",
            RuntimeSaveLayout.Root,
            fixture.Manifest.ManifestDigest,
            fontCatalogDigest: new string('0', 64)));

        await session.SendStartRuntimeAsync(TimeSpan.FromSeconds(10));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.CommandResult && value.CommandResult.Accepted,
            TimeSpan.FromSeconds(5));
        WorkerEnvelope failure = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.RuntimeFailed,
            TimeSpan.FromSeconds(15));

        Assert.Equal("font_catalog_mismatch", failure.RuntimeFailed.StableCode);
        Assert.Equal("initialization", failure.RuntimeFailed.Phase);
        Assert.True(failure.RuntimeFailed.Fatal);
        Assert.Equal(13, await session.WaitForExitAsync(TimeSpan.FromSeconds(5)));

        string errorLogPath = Path.Combine(
            Path.GetDirectoryName(fixture.SessionRoot)!,
            "metadata",
            WorkerErrorLog.FileName);
        Assert.True(File.Exists(errorLogPath));
        string errorLog = File.ReadAllText(errorLogPath);
        Assert.Contains("\"eventName\":\"runtime_failed\"", errorLog, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"font_catalog_mismatch\"", errorLog, StringComparison.Ordinal);
        Assert.Contains("\"phase\":\"initialization\"", errorLog, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.SessionRoot, errorLog, StringComparison.Ordinal);
        Assert.DoesNotContain(manager.SocketPath, errorLog, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Realtime")]
    [Trait("Category", "Input")]
    public async Task StartIsIdempotentAndStopCancelsWaitingInput()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using WorkerManagerHost manager = await WorkerManagerHost.StartAsync(
            new WorkerManagerOptions(fixture.ControlRuntimeRoot, typeof(ConsoleWireMapper).Assembly.Location));
        var binding = new WorkerBinding("sess_stop", "wrk_stop", 4);
        ApiWorkerSession session = await manager.LaunchWorkerAsync(
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
        await WaitForPromptIdAsync(session);

        await session.StopAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await session.WaitForExitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(Directory.Exists(fixture.SessionRoot));
    }

    [Fact]
    [Trait("Category", "Realtime")]
    [Trait("Category", "WorkerDisconnect")]
    public async Task ClosingWorkerControlStreamStopsRuntimeWithinBound()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using WorkerManagerHost manager = await WorkerManagerHost.StartAsync(
            new WorkerManagerOptions(fixture.ControlRuntimeRoot, typeof(ConsoleWireMapper).Assembly.Location)
            {
                RegistrationTimeout = TimeSpan.FromSeconds(15)
            });
        ApiWorkerSession session = await manager.LaunchWorkerAsync(new WorkerLaunchRequest(
            new WorkerBinding("sess_disconnect", "wrk_disconnect", 6), fixture.SessionRoot,
            "v18-compatible", RuntimeSaveLayout.Root, fixture.Manifest.ManifestDigest));

        await session.SendStartRuntimeAsync();
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.CommandResult && value.CommandResult.Accepted,
            TimeSpan.FromSeconds(5));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready,
            TimeSpan.FromSeconds(15));
        await WaitForPromptIdAsync(session);
        await session.DisconnectCurrentConnectionForTestAsync();
        Assert.Equal(0, await session.WaitForExitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(session.HasExited);
    }

    [Fact]
    public async Task IndependentControlClientProcessExitDoesNotStopWorker()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using WorkerManagerHost manager = await WorkerManagerHost.StartAsync(
            new WorkerManagerOptions(fixture.ControlRuntimeRoot, typeof(ConsoleWireMapper).Assembly.Location));
        ApiWorkerSession session = await manager.LaunchWorkerAsync(new WorkerLaunchRequest(
            new WorkerBinding("sess_control_exit", "wrk_control_exit", 7),
            fixture.SessionRoot,
            "v18-compatible",
            RuntimeSaveLayout.Root,
            fixture.Manifest.ManifestDigest));

        await session.SendStartRuntimeAsync();
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready,
            TimeSpan.FromSeconds(15));
        string promptId = await WaitForPromptIdAsync(session);

        // P1-05 has no public API business IPC. This independent
        // probe process exercises the WorkerControl endpoint and exits after
        // its rejected registration; it must not own or cancel the real Worker.
        string probeBootstrapPath = Path.Combine(fixture.ControlRuntimeRoot, "bootstrap", "control-probe.json");
        WorkerBootstrapFile.Write(probeBootstrapPath, new WorkerBootstrapDocument
        {
            SessionId = "sess_control_probe",
            WorkerId = "wrk_control_probe",
            WorkerEpoch = 1,
            SessionRoot = fixture.SessionRoot,
            CompatibilityProfile = "v18-compatible",
            ControlSocketPath = manager.SocketPath,
            ControlPlaneInstanceId = manager.ControlPlaneInstanceId,
            ExpectedParentProcessId = Environment.ProcessId,
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

        Assert.False(
            session.HasExited,
            $"exitCode={session.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "running"}{Environment.NewLine}{session.ProcessDiagnostics}");
        await session.SendInputAsync("client_control_exit", "7");
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.InputResult &&
                value.InputResult.Kind == InputResultKind.Accepted,
            TimeSpan.FromSeconds(5));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.WorkerStopped,
            TimeSpan.FromSeconds(15));
        Assert.Equal(0, await session.WaitForExitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task MismatchedCommandBindingStopsWorkerBeforeInputExecution()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using WorkerManagerHost manager = await WorkerManagerHost.StartAsync(
            new WorkerManagerOptions(fixture.ControlRuntimeRoot, typeof(ConsoleWireMapper).Assembly.Location));
        ApiWorkerSession session = await manager.LaunchWorkerAsync(new WorkerLaunchRequest(
            new WorkerBinding("sess_binding", "wrk_binding", 8), fixture.SessionRoot,
            "v18-compatible", RuntimeSaveLayout.Root, fixture.Manifest.ManifestDigest));

        await session.SendStartRuntimeAsync();
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.CommandResult && value.CommandResult.Accepted,
            TimeSpan.FromSeconds(5));
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready,
            TimeSpan.FromSeconds(15));
        string promptId = await WaitForPromptIdAsync(session);
        await session.SendRawAsync(new WorkerCommandEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = "wrong_binding_command",
            SessionId = "other_session",
                WorkerId = session.Binding.WorkerId,
                WorkerEpoch = session.Binding.WorkerEpoch,
                ControlPlaneInstanceId = manager.ControlPlaneInstanceId,
                CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
                SubmitInput = new SubmitInput
            {
                ClientMessageId = "wrong_binding_client",
                Value = "7",
                Source = InputSource.Keyboard,
                DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds()
            }
        });
        Assert.Equal(0, await session.WaitForExitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(session.HasExited);
    }

    [Fact]
    public async Task WrongBootstrapTokenIsRejectedBeforeRuntimeStarts()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using WorkerManagerHost manager = await WorkerManagerHost.StartAsync(
            new WorkerManagerOptions(fixture.ControlRuntimeRoot, typeof(ConsoleWireMapper).Assembly.Location)
            {
                BootstrapTransformForTest = document => document with { BootstrapToken = "wrong_bootstrap_token" }
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.LaunchWorkerAsync(new WorkerLaunchRequest(
            new WorkerBinding("sess_wrong_token", "wrk_wrong_token", 10),
            fixture.SessionRoot,
            "v18-compatible",
            RuntimeSaveLayout.Root,
            fixture.Manifest.ManifestDigest)));

        Assert.Empty(manager.Workers);
        Assert.Equal(fixture.PublishedDigest, fixture.ComputePublishedDigest());
    }

    [Fact]
    public async Task WorkerLogsCorrelateLifecycleAndRedactSensitiveValues()
    {
        await using var fixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using WorkerManagerHost manager = await WorkerManagerHost.StartAsync(
            new WorkerManagerOptions(fixture.ControlRuntimeRoot, typeof(ConsoleWireMapper).Assembly.Location));
        var binding = new WorkerBinding("sess_logging", "wrk_logging", 12);
        ApiWorkerSession session = await manager.LaunchWorkerAsync(new WorkerLaunchRequest(
            binding,
            fixture.SessionRoot,
            "v18-compatible",
            RuntimeSaveLayout.Root,
            fixture.Manifest.ManifestDigest));
        const string secretInput = "sensitive-input-value";

        await session.SendStartRuntimeAsync();
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready,
            TimeSpan.FromSeconds(15));
        string promptId = await WaitForPromptIdAsync(session);

        await session.SendInputAsync("client_logging", secretInput);
        await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.InputResult,
            TimeSpan.FromSeconds(5));
        await session.StopAsync(TimeSpan.FromSeconds(5));
        await WaitForDiagnosticsAsync(session, value => value.Contains("worker_event", StringComparison.Ordinal));

        string diagnostics = session.ProcessDiagnostics;
        Assert.Contains($"sessionId={binding.SessionId}", diagnostics, StringComparison.Ordinal);
        Assert.Contains($"workerId={binding.WorkerId}", diagnostics, StringComparison.Ordinal);
        Assert.Contains($"workerEpoch={binding.WorkerEpoch}", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.SessionRoot, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(manager.SocketPath, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(secretInput, diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoWorkerProcessesKeepBindingsAndDisplaySequencesSeparate()
    {
        await using var firstFixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using var secondFixture = FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "i", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeRoot);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(runtimeRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await using WorkerManagerHost manager = await WorkerManagerHost.StartAsync(
                new WorkerManagerOptions(runtimeRoot, typeof(ConsoleWireMapper).Assembly.Location));
            ApiWorkerSession first = await manager.LaunchWorkerAsync(new WorkerLaunchRequest(
                new WorkerBinding("sess_first", "wrk_first", 1), firstFixture.SessionRoot,
                "v18-compatible", RuntimeSaveLayout.Root, firstFixture.Manifest.ManifestDigest));
            ApiWorkerSession second = await manager.LaunchWorkerAsync(new WorkerLaunchRequest(
                new WorkerBinding("sess_second", "wrk_second", 9), secondFixture.SessionRoot,
                "v18-compatible", RuntimeSaveLayout.Root, secondFixture.Manifest.ManifestDigest));

            await Task.WhenAll(first.SendStartRuntimeAsync(), second.SendStartRuntimeAsync());
            await Task.WhenAll(
                first.WaitForAsync(value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready, TimeSpan.FromSeconds(15)),
                second.WaitForAsync(value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready, TimeSpan.FromSeconds(15)));
            await WaitForPromptIdAsync(first);
            await WaitForPromptIdAsync(second);
            await Task.WhenAll(
                CompleteSessionAsync(first, "one"),
                CompleteSessionAsync(second, "two"));

            Assert.True(first.OutputHub.CurrentSnapshot?.SnapshotSequence > 0);
            Assert.True(second.OutputHub.CurrentSnapshot?.SnapshotSequence > 0);
            Assert.Equal(firstFixture.PublishedDigest, firstFixture.ComputePublishedDigest());
            Assert.Equal(secondFixture.PublishedDigest, secondFixture.ComputePublishedDigest());
        }
        finally
        {
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    private static async Task CompleteSessionAsync(
        ApiWorkerSession session,
        string value)
    {
        await session.SendInputAsync($"client_{value}", value == "one" ? "1" : "2");
        await session.WaitForAsync(
            eventValue => eventValue.PayloadCase == WorkerEnvelope.PayloadOneofCase.InputResult &&
                eventValue.InputResult.Kind == InputResultKind.Accepted,
            TimeSpan.FromSeconds(5));
        await session.WaitForAsync(
            eventValue => eventValue.PayloadCase == WorkerEnvelope.PayloadOneofCase.WorkerStopped,
            TimeSpan.FromSeconds(15));
        Assert.Equal(0, await session.WaitForExitAsync(TimeSpan.FromSeconds(5)));
    }

    private static WorkerLaunchSpec CreateSleepSpec(
        WorkerManagerOptions options,
        string sessionId,
        string workerId,
        string sessionRoot) => new(
        new SessionRuntimeBinding(
            sessionId,
            workerId,
            1,
            1,
            options.ControlPlaneInstanceId,
            sessionRoot,
            "v18-compatible",
            (int)RuntimeSaveLayout.Root,
            "manifest",
            "runtime",
            0),
        new SessionRootRuntimeDescriptor(sessionRoot, (int)RuntimeSaveLayout.Root, "manifest", "v18-compatible"),
        DateTimeOffset.UtcNow.AddSeconds(10),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1),
        Timeout.InfiniteTimeSpan,
        Environment.ProcessId,
        string.Empty);

    private static async Task WaitForDiagnosticsAsync(
        ApiWorkerSession session,
        Func<string, bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!predicate(session.ProcessDiagnostics) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(predicate(session.ProcessDiagnostics), session.ProcessDiagnostics);
    }

    private static async Task<string> WaitForPromptIdAsync(ApiWorkerSession session)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            string? promptId = session.OutputHub.CurrentSnapshot?.CurrentPrompt?.PromptId;
            if (!string.IsNullOrWhiteSpace(promptId))
                return promptId;
            await Task.Delay(20);
        }

        throw new TimeoutException("The Worker did not publish an active prompt to the realtime snapshot.");
    }

    private static string ProjectTranscript(RuntimeConsoleSnapshot snapshot)
    {
        var nodes = snapshot.Scrollback
            .SelectMany(line => line.Nodes.Concat([CloudEmuera.RuntimeAdapter.LineBreakNode.Instance]))
            .ToList();
        if (nodes.Count > 0)
            nodes.RemoveAt(nodes.Count - 1);

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
                case CloudEmuera.RuntimeAdapter.PositionedInlineSegmentNode segment:
                    result.Append(ProjectTranscriptNodes(segment.Children));
                    break;
            }
        }

        if (result.Length > 0 && result[^1] == '\n')
            result.Length--;
        return result.ToString();
    }

    private static string ProjectTranscriptNodes(IEnumerable<CloudEmuera.RuntimeAdapter.ConsoleNode> nodes)
    {
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
                    result.Append(string.Concat(button.Children.OfType<CloudEmuera.RuntimeAdapter.TextNode>().Select(child => child.Text)));
                    break;
                case CloudEmuera.RuntimeAdapter.PositionedInlineSegmentNode segment:
                    result.Append(ProjectTranscriptNodes(segment.Children));
                    break;
            }
        }

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
            ControlRuntimeRoot = supervisorRoot;
            Manifest = manifest;
            PublishedDigest = publishedDigest;
        }

        public string FixtureRoot { get; }
        public string Root { get; }
        public string PublishedRoot { get; }
        public string SessionRoot { get; }
        public string ControlRuntimeRoot { get; }
        public SessionRootPublishedManifest Manifest { get; }
        public string PublishedDigest { get; }

        public static FixtureWorkspace Create(string fixtureId, RuntimeSaveLayout saveLayout)
        {
            string repositoryRoot = FindRepositoryRoot();
            string fixtureRoot = Path.Combine(repositoryRoot, "tests", "fixtures", "runtime", fixtureId);
            string root = Path.Combine(Path.GetTempPath(), "w", Guid.NewGuid().ToString("N"));
            string publishedRoot = Path.Combine(root, "published-game");
            string sessionRoot = Path.Combine(root, "session-root");
            string workspaceRoot = Path.Combine(root, "session-workspace");
            string supervisorRoot = Path.Combine(root, "r");
            Directory.CreateDirectory(workspaceRoot);
            Directory.CreateDirectory(supervisorRoot);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(supervisorRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            CopyPublishedGameContent(fixtureRoot, publishedRoot);
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

        private static void CopyPublishedGameContent(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (FileSystemInfo entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
            {
                if (entry.Name is "scenario.json" or "save-scenario.json" or "expected-transcript.txt")
                    continue;
                string target = Path.Combine(destination, entry.Name);
                if (entry is DirectoryInfo)
                    CopyPublishedGameContent(entry.FullName, target);
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
