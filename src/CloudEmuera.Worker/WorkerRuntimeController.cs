using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using CloudEmuera.EmueraRuntime.Headless;
using CloudEmuera.Ipc;
using CloudEmuera.Ipc.V3;
using CloudEmuera.Realtime;
using CloudEmuera.RuntimeAdapter;
using R = CloudEmuera.RuntimeAdapter;
using Microsoft.Extensions.Logging;

namespace CloudEmuera.Worker;

internal static class WorkerExitCodes
{
    public const int Normal = 0;
    public const int BootstrapInvalid = 10;
    public const int RegistrationRejected = 11;
    public const int SessionRootInvalid = 12;
    public const int RuntimeInitializationFailed = 13;
    public const int RuntimeExecutionFailed = 14;
    public const int ShutdownDeadlineExceeded = 15;
}

internal sealed class WorkerRuntimeController : IAsyncDisposable
{
    private readonly WorkerBootstrapDocument bootstrap;
    private readonly WorkerBinding binding;
    private readonly WorkerConnectionLoop connection;
    private readonly ILogger logger;
    private readonly object sync = new();
    private readonly Dictionary<string, StartReceipt> startReceipts = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<int> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WorkerRuntimeState state = WorkerRuntimeState.Registered;
    private StructuredGameConsole? console;
    private EmueraRuntimeHost? host;
    private CancellationTokenSource? runtimeCancellation;
    private Task? runtimeTask;
    private Task? outputTask;
    private Task? heartbeatTask;
    private long lastSentSequence;
    private bool forceSnapshot = true;
    private bool stoppedMessageSent;

    public WorkerRuntimeController(
        WorkerBootstrapDocument bootstrap,
        WorkerConnectionLoop connection,
        ILogger logger)
    {
        this.bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        binding = bootstrap.Binding;
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        lastSentSequence = bootstrap.InitialOutputSequence;
    }

    public Task<int> Completion => completion.Task;

    public StructuredGameConsole? Console => console;

    public async Task RequestShutdownAsync()
    {
        lock (sync)
        {
            if (state is not WorkerRuntimeState.Stopped)
            {
                state = WorkerRuntimeState.Stopping;
                runtimeCancellation?.Cancel();
            }
        }

        if (runtimeTask is null)
        {
            await SendStoppedAsync(
                    "process_signal",
                    graceful: true,
                    DateTimeOffset.UtcNow.AddMilliseconds(bootstrap.ShutdownGracePeriodMilliseconds)
                        .ToUnixTimeMilliseconds(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            Complete(WorkerExitCodes.Normal);
        }
        else
        {
            try
            {
                await runtimeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async Task HandleCommandAsync(WorkerCommandEnvelope command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.PayloadCase)
        {
            case WorkerCommandEnvelope.PayloadOneofCase.StartRuntime:
                await HandleStartAsync(command, cancellationToken).ConfigureAwait(false);
                break;
            case WorkerCommandEnvelope.PayloadOneofCase.SubmitInput:
                await HandleInputAsync(command, cancellationToken).ConfigureAwait(false);
                break;
            case WorkerCommandEnvelope.PayloadOneofCase.Stop:
                await HandleStopAsync(command, cancellationToken).ConfigureAwait(false);
                break;
            case WorkerCommandEnvelope.PayloadOneofCase.RegistrationResult:
                // RegistrationResult is consumed by WorkerConnectionLoop.
                break;
            default:
                await SendCommandResultAsync(command, accepted: false, IpcReasonCodes.UnsupportedMessage, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            runtimeCancellation?.Cancel();
            if (runtimeTask is not null)
                await runtimeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (host is not null)
            await host.DisposeAsync().ConfigureAwait(false);
        runtimeCancellation?.Dispose();
    }

    private async Task HandleStartAsync(WorkerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        StartReceipt? cached;
        lock (sync)
        {
            startReceipts.TryGetValue(envelope.MessageId, out cached);
        }

        if (cached is not null)
        {
            LogLifecycle("start_replayed", cached.ReasonCode);
            await SendCommandResultAsync(envelope, cached.Accepted, cached.ReasonCode, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!bootstrap.Binding.Matches(
                envelope.StartRuntime.ExpectedSessionId,
                envelope.StartRuntime.ExpectedWorkerId,
                envelope.StartRuntime.ExpectedWorkerEpoch) ||
            !string.Equals(
                bootstrap.CompatibilityProfile,
                envelope.StartRuntime.ExpectedCompatibilityProfile,
                StringComparison.Ordinal))
        {
            LogLifecycle("start_rejected", IpcReasonCodes.BindingMismatch, LogLevel.Warning);
            await SendCommandResultAsync(envelope, accepted: false, IpcReasonCodes.BindingMismatch, cancellationToken)
                .ConfigureAwait(false);
            Complete(WorkerExitCodes.RegistrationRejected);
            return;
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > envelope.StartRuntime.DeadlineUnixMilliseconds)
        {
            LogLifecycle("start_rejected", IpcReasonCodes.DeadlineExceeded, LogLevel.Warning);
            await SendCommandResultAsync(envelope, accepted: false, IpcReasonCodes.DeadlineExceeded, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        bool accepted;
        string reason;
        lock (sync)
        {
            if (state != WorkerRuntimeState.Registered)
            {
                accepted = false;
                reason = IpcReasonCodes.AlreadyStarted;
            }
            else
            {
                state = WorkerRuntimeState.Initializing;
                accepted = true;
                reason = IpcReasonCodes.Accepted;
                startReceipts[envelope.MessageId] = new StartReceipt
                {
                    Accepted = true,
                    ReasonCode = IpcReasonCodes.Accepted
                };
            }
        }

        await SendCommandResultAsync(envelope, accepted, reason, cancellationToken).ConfigureAwait(false);
        LogLifecycle(accepted ? "runtime_starting" : "start_rejected", reason, accepted ? LogLevel.Information : LogLevel.Warning);
        if (accepted)
        {
            runtimeTask = Task.Run(() => InitializeAndRunAsync(), CancellationToken.None);
            _ = runtimeTask.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        _ = task.Exception;
                        Complete(WorkerExitCodes.RuntimeExecutionFailed);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleInputAsync(WorkerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        WorkerInputResult inputResult;
        lock (sync)
        {
            if (state is WorkerRuntimeState.Stopping or WorkerRuntimeState.Stopped)
            {
                inputResult = new WorkerInputResult(
                    InputResultKind.InvalidCommand,
                    IpcReasonCodes.WorkerStopping,
                    string.Empty);
                goto send;
            }
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > envelope.SubmitInput.DeadlineUnixMilliseconds)
        {
            inputResult = new WorkerInputResult(
                InputResultKind.InvalidCommand,
                IpcReasonCodes.DeadlineExceeded,
                string.Empty);
            goto send;
        }

        if (console is null)
        {
            inputResult = new WorkerInputResult(
                InputResultKind.NoActivePrompt,
                "no_active_prompt",
                string.Empty);
            goto send;
        }

        try
        {
            ConsolePointerPayload? pointer = envelope.SubmitInput.PayloadCase == SubmitInput.PayloadOneofCase.Pointer
                ? new ConsolePointerPayload(
                    envelope.SubmitInput.Pointer.Position.X,
                    envelope.SubmitInput.Pointer.Position.Y,
                    envelope.SubmitInput.Pointer.Button,
                    envelope.SubmitInput.Pointer.Pressed)
                : null;
            ConsoleKeyPayload? key = envelope.SubmitInput.PayloadCase == SubmitInput.PayloadOneofCase.Key
                ? new ConsoleKeyPayload(
                    envelope.SubmitInput.Key.KeyCode,
                    envelope.SubmitInput.Key.Control,
                    envelope.SubmitInput.Key.Alt,
                    envelope.SubmitInput.Key.Shift)
                : null;
            var command = new ConsoleInputCommand(
                envelope.SubmitInput.PromptId,
                envelope.SubmitInput.ClientMessageId,
                envelope.SubmitInput.Value,
                (ConsoleInputSource)(int)envelope.SubmitInput.Source,
                pointer,
                key);
            ConsoleInputResult result = console.SubmitInput(command);
            inputResult = new WorkerInputResult(
                ToProto(result.Kind),
                result.ReasonCode ?? result.Kind.ToString(),
                result.Value ?? string.Empty);
        }
        catch (ConsoleContractException exception)
        {
            inputResult = new WorkerInputResult(
                InputResultKind.InvalidCommand,
                exception.ReasonCode,
                string.Empty);
        }

    send:
        var response = new WorkerEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = IpcProtocol.NewMessageId("input"),
            CorrelationId = envelope.MessageId,
            SessionId = binding.SessionId,
            WorkerId = binding.WorkerId,
            WorkerEpoch = binding.WorkerEpoch,
            ControlPlaneInstanceId = bootstrap.ControlPlaneInstanceId,
            CapabilitySetDigest = bootstrap.CapabilitySetDigest,
            InputResult = new InputResult
            {
                PromptId = envelope.SubmitInput.PromptId,
                ClientMessageId = envelope.SubmitInput.ClientMessageId,
                Kind = inputResult.Kind,
                ReasonCode = inputResult.ReasonCode,
                NormalizedValue = inputResult.Value,
                HasNormalizedValue = inputResult.Value.Length != 0
            }
        };
        await connection.SendControlAsync(response, cancellationToken).ConfigureAwait(false);
        LogLifecycle("input_result", inputResult.ReasonCode);
    }

    private async Task HandleStopAsync(WorkerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        bool alreadyStopping;
        lock (sync)
        {
            alreadyStopping = state is WorkerRuntimeState.Stopping or WorkerRuntimeState.Stopped;
            if (!alreadyStopping)
            {
                state = WorkerRuntimeState.Stopping;
                runtimeCancellation?.Cancel();
            }
        }

        await SendCommandResultAsync(
                envelope,
                accepted: !alreadyStopping,
                alreadyStopping ? IpcReasonCodes.WorkerStopping : IpcReasonCodes.Accepted,
                cancellationToken)
            .ConfigureAwait(false);
        LogLifecycle(alreadyStopping ? "stop_replayed" : "stop_requested", alreadyStopping ? IpcReasonCodes.WorkerStopping : IpcReasonCodes.Accepted);

        if (runtimeTask is null)
        {
            await SendStoppedAsync(
                    envelope.Stop.ReasonCode,
                    graceful: true,
                    envelope.Stop.DeadlineUnixMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
            Complete(WorkerExitCodes.Normal);
        }
    }

    private async Task InitializeAndRunAsync()
    {
        try
        {
            RuntimeSaveLayout saveLayout = await ValidateSessionRootAsync().ConfigureAwait(false);
            RuntimePaths paths = RuntimePaths.ForExistingSessionRoot(bootstrap.SessionRoot, saveLayout);
            paths.ValidateSessionRoot();
            WorkerLifecycleLog.WriteRuntimeWidth(logger, binding, bootstrap.BrowserWidth);
            var fileSystem = new LocalRuntimeFileSystem(paths);
            console = new StructuredGameConsole();
            host = EmueraRuntimeHost.Create(new EmueraRuntimeOptions(
                paths,
                console,
                fileSystem,
                console.Clock,
                new RuntimeImageMetadataPort(fileSystem),
                new StructuredRuntimeAudioPort(console, fileSystem),
                bootstrap.CompatibilityProfile,
                TimeSpan.FromMilliseconds(bootstrap.RuntimeInitializationTimeoutMilliseconds),
                bootstrap.RuntimeExecutionTimeoutMilliseconds < 0
                    ? Timeout.InfiniteTimeSpan
                    : TimeSpan.FromMilliseconds(bootstrap.RuntimeExecutionTimeoutMilliseconds),
                browserWidth: bootstrap.BrowserWidth));
            runtimeCancellation = new CancellationTokenSource();
            console.StateStore.InitializeSequence(bootstrap.InitialOutputSequence);

            EmueraRuntimeResult initialized = await host.InitializeAsync(runtimeCancellation.Token).ConfigureAwait(false);
            if (initialized.Status != EmueraRuntimeStatus.Completed)
            {
                await SendRuntimeFailureAsync(initialized, "initialization").ConfigureAwait(false);
                Complete(WorkerExitCodes.RuntimeInitializationFailed);
                return;
            }

            lock (sync)
            {
                if (state == WorkerRuntimeState.Stopping)
                {
                    runtimeCancellation.Cancel();
                }
                else
                {
                    state = WorkerRuntimeState.Running;
                }
            }

            await connection.SendControlAsync(new WorkerEnvelope
            {
                ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
                MessageId = IpcProtocol.NewMessageId("ready"),
                SessionId = binding.SessionId,
                WorkerId = binding.WorkerId,
                WorkerEpoch = binding.WorkerEpoch,
                ControlPlaneInstanceId = bootstrap.ControlPlaneInstanceId,
                CapabilitySetDigest = bootstrap.CapabilitySetDigest,
                Ready = new WorkerReady
                {
                    RuntimeIntegrationVersion = initialized.IntegrationVersion,
                    UpstreamCommit = initialized.UpstreamCommit,
                    SaveLayout = saveLayout == RuntimeSaveLayout.Root ? SaveLayout.Root : SaveLayout.SavDirectory,
                    LastOutputSequence = console.StateStore.CurrentSequence,
                    CompatibilityProfile = bootstrap.CompatibilityProfile,
                    SessionRootManifestDigest = bootstrap.SessionRootManifestDigest,
                    CapabilitySetDigest = bootstrap.CapabilitySetDigest
                }
            }).ConfigureAwait(false);
            LogLifecycle("runtime_ready");

            outputTask = Task.Run(() => OutputPumpAsync(runtimeCancellation.Token), CancellationToken.None);
            heartbeatTask = Task.Run(() => HeartbeatAsync(runtimeCancellation.Token), CancellationToken.None);
            EmueraRuntimeResult result = await host.RunAsync(runtimeCancellation.Token).ConfigureAwait(false);
            runtimeCancellation.Cancel();
            await outputTask.ConfigureAwait(false);
            if (heartbeatTask is not null)
            {
                try
                {
                    await heartbeatTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            await SendRuntimeResultAsync(result).ConfigureAwait(false);
            LogLifecycle("runtime_finished", result.Status.ToString().ToLowerInvariant());

            bool wasStopping;
            lock (sync)
            {
                wasStopping = state == WorkerRuntimeState.Stopping;
                if (!wasStopping)
                    state = WorkerRuntimeState.Completed;
            }

            await SendStoppedAsync(
                    wasStopping ? "stop_requested" : "runtime_completed",
                    graceful: result.Status == EmueraRuntimeStatus.Completed || wasStopping,
                    DateTimeOffset.UtcNow.AddMilliseconds(bootstrap.ShutdownGracePeriodMilliseconds)
                        .ToUnixTimeMilliseconds(),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Complete(result.Status is EmueraRuntimeStatus.Completed or EmueraRuntimeStatus.Cancelled
                ? WorkerExitCodes.Normal
                : WorkerExitCodes.RuntimeExecutionFailed);
        }
        catch (WorkerSessionRootException exception)
        {
            LogLifecycle("runtime_failed", exception.Code, LogLevel.Warning);
            await SendFailureCodeAsync(exception.Code, "initialization", exception.SafeMessage, fatal: true).ConfigureAwait(false);
            Complete(WorkerExitCodes.SessionRootInvalid);
        }
        catch (OperationCanceledException)
        {
            LogLifecycle("runtime_cancelled", "stop_requested");
            await FinishAfterCancellationAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogLifecycle(
                "runtime_failed",
                $"runtime_worker_failure:{exception.GetType().Name}:{SafeMessage(exception.Message)}",
                LogLevel.Error);
            await SendFailureCodeAsync("runtime_worker_failure", "execution", SafeMessage(exception.Message), fatal: true).ConfigureAwait(false);
            Complete(WorkerExitCodes.RuntimeExecutionFailed);
        }
    }

    private async Task<RuntimeSaveLayout> ValidateSessionRootAsync()
    {
        string root = Path.GetFullPath(bootstrap.SessionRoot);
        if (!Path.IsPathFullyQualified(root) ||
            string.Equals(root, Path.GetPathRoot(root), StringComparison.Ordinal) ||
            PathsOverlap(root, bootstrap.ControlSocketPath))
        {
            throw new WorkerSessionRootException("session_root_invalid", "The bound SessionRoot is invalid.");
        }

        FileSystemInfo rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists || rootInfo.LinkTarget is not null)
        {
            throw new WorkerSessionRootException("session_root_missing", "The bound SessionRoot is not a normal directory.");
        }

        string metadataPath = Path.Combine(root, SessionRootLayoutBuilder.BindingMetadataFileName);
        string configurationPath = Path.Combine(root, "emuera.config");
        EnsureRegularFile(metadataPath, "session_binding_invalid");
        EnsureRegularFile(configurationPath, "session_config_invalid");
        RuntimeSaveLayout actualLayout;
        await using (FileStream stream = new(configurationPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            actualLayout = EmueraSaveLayoutInspector.Inspect(stream);
        }

        if ((int)actualLayout != bootstrap.SaveLayout)
        {
            throw new WorkerSessionRootException(
                "session_save_layout_mismatch",
                "The SessionRoot save layout does not match the Worker binding.");
        }

        using (JsonDocument metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false)))
        {
            JsonElement document = metadata.RootElement;
            if (!document.TryGetProperty("SchemaVersion", out JsonElement schema) || schema.GetInt32() != 1 ||
                !document.TryGetProperty("SaveLayout", out JsonElement layout) ||
                layout.GetInt32() != (int)actualLayout)
            {
                throw new WorkerSessionRootException("session_binding_invalid", "The SessionRoot binding metadata is invalid.");
            }

            if (!string.IsNullOrEmpty(bootstrap.SessionRootManifestDigest) &&
                (!document.TryGetProperty("ManifestDigest", out JsonElement digest) ||
                 !string.Equals(digest.GetString(), bootstrap.SessionRootManifestDigest, StringComparison.OrdinalIgnoreCase)))
            {
                throw new WorkerSessionRootException("session_binding_mismatch", "The SessionRoot binding does not match the Worker binding.");
            }
        }

        return actualLayout;
    }

    private async Task OutputPumpAsync(CancellationToken cancellationToken)
    {
        StructuredGameConsole gameConsole = console ?? throw new InvalidOperationException("Console is not initialized.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool sent = await SendPendingOutputAsync(gameConsole, cancellationToken).ConfigureAwait(false);
                if (!sent)
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                else
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        using var finalCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await SendPendingOutputAsync(gameConsole, finalCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> SendPendingOutputAsync(StructuredGameConsole gameConsole, CancellationToken cancellationToken)
    {
        StructuredConsoleResumeResult result;
        if (forceSnapshot)
        {
            R.ConsoleSnapshot snapshot = gameConsole.StateStore.StructuredSnapshot;
            result = new StructuredConsoleSnapshotWithDeltasResult(
                snapshot,
                Array.Empty<SequencedConsoleTransaction>(),
                snapshot.SnapshotSequence);
        }
        else
        {
            result = gameConsole.StateStore.ReadStructuredSince(Interlocked.Read(ref lastSentSequence));
        }
        if (result is StructuredConsoleUpToDateResult)
            return true;

        if (result is StructuredConsoleSnapshotWithDeltasResult snapshotResult)
        {
            SequencedConsoleTransaction[] transactions = snapshotResult.TransactionsAfterSnapshot.ToArray();
            if (transactions.Length == 0)
            {
                var snapshotBatch = new DisplayBatch
                {
                    IsSnapshot = true,
                    Snapshot = StructuredConsoleWireMapper.ToProto(snapshotResult.Snapshot)
                };
                await SendDisplayAsync(snapshotBatch, cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref lastSentSequence, snapshotResult.Snapshot.SnapshotSequence);
                connection.SetLastOutputSequence(snapshotResult.Snapshot.SnapshotSequence);
                forceSnapshot = false;
            }
            else
            {
                bool firstBatch = true;
                foreach (SequencedConsoleTransaction[] batch in transactions.Chunk(StructuredIpcLimits.MaxTransactions))
                {
                    var displayBatch = new DisplayBatch
                    {
                        IsSnapshot = firstBatch
                    };
                    if (firstBatch)
                        displayBatch.Snapshot = StructuredConsoleWireMapper.ToProto(snapshotResult.Snapshot);
                    displayBatch.Transactions.AddRange(batch.Select(StructuredConsoleWireMapper.ToProto));
                    await SendDisplayAsync(displayBatch, cancellationToken).ConfigureAwait(false);
                    Interlocked.Exchange(ref lastSentSequence, batch[^1].Sequence);
                    connection.SetLastOutputSequence(batch[^1].Sequence);
                    firstBatch = false;
                }
                forceSnapshot = false;
            }
            return true;
        }

        StructuredConsoleDeltaBatchResult delta = (StructuredConsoleDeltaBatchResult)result;
        foreach (SequencedConsoleTransaction[] batch in delta.Transactions.Chunk(StructuredIpcLimits.MaxTransactions))
        {
            var displayBatch = new DisplayBatch();
            displayBatch.Transactions.AddRange(batch.Select(StructuredConsoleWireMapper.ToProto));
            await SendDisplayAsync(displayBatch, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref lastSentSequence, batch[^1].Sequence);
            connection.SetLastOutputSequence(batch[^1].Sequence);
        }
        return true;
    }

    private Task SendDisplayAsync(DisplayBatch batch, CancellationToken cancellationToken) =>
        SendDisplayEnvelopeAsync(new WorkerEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = IpcProtocol.NewMessageId("display"),
            SessionId = binding.SessionId,
            WorkerId = binding.WorkerId,
            WorkerEpoch = binding.WorkerEpoch,
            ControlPlaneInstanceId = bootstrap.ControlPlaneInstanceId,
            CapabilitySetDigest = bootstrap.CapabilitySetDigest,
            DisplayBatch = batch
        }, cancellationToken);

    private Task SendDisplayEnvelopeAsync(WorkerEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.CalculateSize() > StructuredIpcLimits.MaxEnvelopeBytes)
            throw new InvalidDataException("The Worker display envelope exceeds the IPC size limit.");
        return connection.SendDisplayAsync(envelope, cancellationToken);
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(bootstrap.HeartbeatIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
            StructuredGameConsole? gameConsole = console;
            long outputSequence = gameConsole?.StateStore.CurrentSequence ?? 0;
            // Sample the prompt exactly once. Reading CurrentPrompt again for
            // each field allowed a prompt transition between reads to emit
            // WaitingForInput=true with an empty CurrentPromptId (or the
            // inverse), which the API durable store rejects as an invalid
            // heartbeat payload.
            CloudEmuera.RuntimeAdapter.ConsolePrompt? currentPrompt = gameConsole?.CurrentPrompt;
            await connection.SendControlAsync(new WorkerEnvelope
            {
                ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
                MessageId = IpcProtocol.NewMessageId("heartbeat"),
                SessionId = binding.SessionId,
                WorkerId = binding.WorkerId,
                WorkerEpoch = binding.WorkerEpoch,
                ControlPlaneInstanceId = bootstrap.ControlPlaneInstanceId,
                CapabilitySetDigest = bootstrap.CapabilitySetDigest,
                Heartbeat = CreateHeartbeat(outputSequence, currentPrompt)
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds a heartbeat whose WaitingForInput, CurrentPromptId and
    /// PromptTiming all describe the same prompt snapshot, so the API store
    /// never sees a self-contradictory payload.
    /// </summary>
    internal static WorkerHeartbeat CreateHeartbeat(long outputSequence, CloudEmuera.RuntimeAdapter.ConsolePrompt? currentPrompt)
    {
        return new WorkerHeartbeat
        {
            MonotonicTimestampTicks = Stopwatch.GetTimestamp(),
            OutputSequence = outputSequence,
            WaitingForInput = currentPrompt is not null,
            ResidentMemoryBytes = Environment.WorkingSet,
            CurrentPromptId = currentPrompt?.PromptId ?? string.Empty,
            PromptTiming = currentPrompt is { } prompt
                ? new PromptTiming
                {
                    OpenedAtUnixMilliseconds = prompt.OpenedAtUnixMilliseconds,
                    DeadlineUnixMilliseconds = prompt.DeadlineUnixMilliseconds,
                    ServerNowUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    RemainingMilliseconds = prompt.HasDeadline
                        ? Math.Max(0, prompt.DeadlineUnixMilliseconds - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        : 0
                }
                : null
        };
    }

    private async Task SendRuntimeResultAsync(EmueraRuntimeResult result)
    {
        if (result.Status == EmueraRuntimeStatus.Completed || result.Status == EmueraRuntimeStatus.Cancelled)
        {
            await connection.SendControlAsync(new WorkerEnvelope
            {
                ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
                MessageId = IpcProtocol.NewMessageId("completed"),
                SessionId = binding.SessionId,
                WorkerId = binding.WorkerId,
                WorkerEpoch = binding.WorkerEpoch,
                ControlPlaneInstanceId = bootstrap.ControlPlaneInstanceId,
                CapabilitySetDigest = bootstrap.CapabilitySetDigest,
                RuntimeCompleted = new RuntimeCompleted
                {
                    Status = result.Status.ToString().ToLowerInvariant(),
                    LastOutputSequence = Interlocked.Read(ref lastSentSequence)
                }
            }).ConfigureAwait(false);
            return;
        }

        await SendRuntimeFailureAsync(result, "execution").ConfigureAwait(false);
    }

    private async Task SendRuntimeFailureAsync(EmueraRuntimeResult result, string defaultPhase)
    {
        EmueraRuntimeDiagnostic diagnostic = SelectFailureDiagnostic(result, defaultPhase);
        await SendFailureCodeAsync(
                diagnostic.Code,
                diagnostic.Phase.ToString(),
                SafeMessage(diagnostic.Message),
                diagnostic.IsFatal,
                lastSequence: Interlocked.Read(ref lastSentSequence))
            .ConfigureAwait(false);
    }

    internal static EmueraRuntimeDiagnostic SelectFailureDiagnostic(
        EmueraRuntimeResult result,
        string defaultPhase)
    {
        ArgumentNullException.ThrowIfNull(result);
        EmueraRuntimeDiagnostic? fatal = result.Diagnostics.LastOrDefault(diagnostic => diagnostic.IsFatal);
        if (fatal is not null)
            return fatal;
        if (result.Diagnostics.Count > 0)
            return result.Diagnostics[^1];
        return new EmueraRuntimeDiagnostic(
                "runtime_failed",
                Enum.Parse<EmueraRuntimePhase>(defaultPhase, ignoreCase: true),
                "The runtime failed.",
                true);
    }

    private Task SendFailureCodeAsync(
        string code,
        string phase,
        string message,
        bool fatal,
        long? lastSequence = null) =>
        connection.SendControlAsync(new WorkerEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = IpcProtocol.NewMessageId("failed"),
            SessionId = binding.SessionId,
            WorkerId = binding.WorkerId,
            WorkerEpoch = binding.WorkerEpoch,
            ControlPlaneInstanceId = bootstrap.ControlPlaneInstanceId,
            CapabilitySetDigest = bootstrap.CapabilitySetDigest,
            RuntimeFailed = new RuntimeFailed
            {
                StableCode = NormalizeCode(code),
                Phase = NormalizeCode(phase),
                SafeMessage = message.Length > StructuredIpcLimits.MaxProtocolErrorMessageLength
                    ? message[..StructuredIpcLimits.MaxProtocolErrorMessageLength]
                    : message,
                Fatal = fatal,
                LastOutputSequence = lastSequence ?? Interlocked.Read(ref lastSentSequence)
            }
        });

    private async Task FinishAfterCancellationAsync()
    {
        try
        {
            await SendStoppedAsync(
                    "stop_requested",
                    graceful: true,
                    DateTimeOffset.UtcNow.AddMilliseconds(bootstrap.ShutdownGracePeriodMilliseconds).ToUnixTimeMilliseconds(),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connection.ControlStreamClosed)
        {
            LogLifecycle("worker_stopped", "control_stream_closed", LogLevel.Warning);
        }
        catch (InvalidOperationException) when (connection.ControlStreamClosed)
        {
            LogLifecycle("worker_stopped", "control_stream_closed", LogLevel.Warning);
        }
        catch (OperationCanceledException)
        {
            LogLifecycle("worker_stopped", "shutdown_deadline_exceeded", LogLevel.Warning);
            Complete(WorkerExitCodes.ShutdownDeadlineExceeded);
        }
        Complete(WorkerExitCodes.Normal);
    }

    private async Task SendStoppedAsync(
        string reason,
        bool graceful,
        long deadlineUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (stoppedMessageSent)
                return;
            stoppedMessageSent = true;
            state = WorkerRuntimeState.Stopped;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan remaining = DateTimeOffset.FromUnixTimeMilliseconds(deadlineUnixMilliseconds) - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
            deadline.CancelAfter(remaining);
        else
            deadline.Cancel();

        try
        {
            await connection.SendControlAsync(new WorkerEnvelope
            {
                ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
                MessageId = IpcProtocol.NewMessageId("stopped"),
                SessionId = binding.SessionId,
                WorkerId = binding.WorkerId,
                WorkerEpoch = binding.WorkerEpoch,
                ControlPlaneInstanceId = bootstrap.ControlPlaneInstanceId,
                CapabilitySetDigest = bootstrap.CapabilitySetDigest,
                WorkerStopped = new WorkerStopped
                {
                    ReasonCode = NormalizeCode(reason),
                    LastOutputSequence = Interlocked.Read(ref lastSentSequence),
                    Graceful = graceful
                }
            }, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connection.ControlStreamClosed)
        {
            LogLifecycle("worker_stopped", "control_stream_closed", LogLevel.Warning);
        }
        catch (InvalidOperationException) when (connection.ControlStreamClosed)
        {
            LogLifecycle("worker_stopped", "control_stream_closed", LogLevel.Warning);
        }
        catch (OperationCanceledException)
        {
            LogLifecycle("worker_stopped", "shutdown_deadline_exceeded", LogLevel.Warning);
            Complete(WorkerExitCodes.ShutdownDeadlineExceeded);
        }
        LogLifecycle("worker_stopped", reason);
    }

    private async Task SendCommandResultAsync(
        WorkerCommandEnvelope command,
        bool accepted,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        await connection.SendControlAsync(new WorkerEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = IpcProtocol.NewMessageId("command"),
            CorrelationId = command.MessageId,
            SessionId = binding.SessionId,
            WorkerId = binding.WorkerId,
            WorkerEpoch = binding.WorkerEpoch,
            ControlPlaneInstanceId = bootstrap.ControlPlaneInstanceId,
            CapabilitySetDigest = bootstrap.CapabilitySetDigest,
            CommandResult = new WorkerCommandResult
            {
                CommandType = command.PayloadCase switch
                {
                    WorkerCommandEnvelope.PayloadOneofCase.StartRuntime => "start_runtime",
                    WorkerCommandEnvelope.PayloadOneofCase.SubmitInput => "submit_input",
                    WorkerCommandEnvelope.PayloadOneofCase.Stop => "stop_worker",
                    _ => "unsupported_command"
                },
                Accepted = accepted,
                ReasonCode = NormalizeCode(reasonCode)
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private void Complete(int exitCode) => completion.TrySetResult(exitCode);

    private void LogLifecycle(
        string eventName,
        string reason = "",
        LogLevel level = LogLevel.Information) =>
        WorkerLifecycleLog.Write(logger, binding, eventName, reason, level);

    private string SafeMessage(string message)
    {
        string safe = message ?? string.Empty;
        foreach (string path in new[] { bootstrap.SessionRoot, bootstrap.ControlSocketPath })
        {
            safe = safe.Replace(path, "<runtime-path>", StringComparison.Ordinal);
        }

        return safe.Length <= StructuredIpcLimits.MaxProtocolErrorMessageLength
            ? safe
            : safe[..StructuredIpcLimits.MaxProtocolErrorMessageLength];
    }

    private static string NormalizeCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return IpcReasonCodes.InvalidEnvelope;
        string code = new(value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').ToArray());
        return string.IsNullOrEmpty(code) ? IpcReasonCodes.InvalidEnvelope : code[..Math.Min(code.Length, 128)];
    }

    private static bool PathsOverlap(string first, string second)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(a, b, StringComparison.Ordinal) ||
            a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void EnsureRegularFile(string path, string code)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new WorkerSessionRootException(code, "The SessionRoot contains an invalid metadata file.");
    }

    private static InputResultKind ToProto(ConsoleInputResultKind kind) => kind switch
    {
        ConsoleInputResultKind.Accepted => InputResultKind.Accepted,
        ConsoleInputResultKind.Duplicate => InputResultKind.Duplicate,
        ConsoleInputResultKind.StalePrompt => InputResultKind.StalePrompt,
        ConsoleInputResultKind.NoActivePrompt => InputResultKind.NoActivePrompt,
        ConsoleInputResultKind.InvalidFormat => InputResultKind.InvalidFormat,
        ConsoleInputResultKind.MessageConflict => InputResultKind.Conflict,
        ConsoleInputResultKind.InvalidCommand => InputResultKind.InvalidCommand,
        ConsoleInputResultKind.Cancelled => InputResultKind.Cancelled,
        ConsoleInputResultKind.TimedOut => InputResultKind.TimedOut,
        _ => InputResultKind.InvalidCommand
    };

    private sealed record WorkerInputResult(InputResultKind Kind, string ReasonCode, string Value);

    private sealed record StartReceipt
    {
        public bool Accepted { get; init; }
        public string ReasonCode { get; init; } = IpcReasonCodes.InvalidCommand;
    }

    private enum WorkerRuntimeState
    {
        Registered,
        Initializing,
        Running,
        Completed,
        Stopping,
        Stopped
    }

    private sealed class WorkerSessionRootException(string code, string safeMessage) : Exception(code)
    {
        public string Code { get; } = code;
        public string SafeMessage { get; } = safeMessage;
    }
}
