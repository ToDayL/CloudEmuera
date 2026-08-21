using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Api.Realtime;
using CloudEmuera.Api.Security;
using CloudEmuera.Ipc;
using CloudEmuera.Ipc.V5;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.RuntimeAdapter;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Api.Workers;

public sealed class WorkerManagerHost : IAsyncDisposable
{
    private readonly WebApplication application;
    private readonly WorkerManager manager;
    private readonly WorkerSocketLifecycle socketLifecycle;
    private int disposed;

    private WorkerManagerHost(WebApplication application, WorkerManager manager, WorkerSocketLifecycle socketLifecycle)
    {
        this.application = application;
        this.manager = manager;
        this.socketLifecycle = socketLifecycle;
    }

    public string SocketPath => socketLifecycle.SocketPath;
    public string ControlPlaneInstanceId => manager.ControlPlaneInstanceId;

    public IReadOnlyCollection<ApiWorkerSession> Workers => manager.Workers;

    public static async Task<WorkerManagerHost> StartAsync(
        WorkerManagerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var lifecycle = new WorkerSocketLifecycle(options);
        lifecycle.Prepare();

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(WorkerManagerHost).Assembly.GetName().Name
        });
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.AddServerHeader = false;
            serverOptions.ListenUnixSocket(options.ControlSocketPath, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddGrpc(grpcOptions =>
        {
            grpcOptions.MaxReceiveMessageSize = StructuredIpcLimits.MaxEnvelopeBytes;
            grpcOptions.MaxSendMessageSize = StructuredIpcLimits.MaxEnvelopeBytes;
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(loggingOptions => loggingOptions.IncludeScopes = false);
        builder.Logging.AddFilter((category, level) =>
            category is not null && category.StartsWith("CloudEmuera.Api.Workers", StringComparison.Ordinal) && level >= LogLevel.Information);
        builder.Services.AddSingleton(new ApiControlPlaneIdentity(options.ControlPlaneInstanceId));
        builder.Services.AddSingleton<WorkerManager>(services =>
            new WorkerManager(options, services.GetRequiredService<ILoggerFactory>()));

        WebApplication application = builder.Build();
        WorkerManager manager = application.Services.GetRequiredService<WorkerManager>();
        application.MapGrpcService<WorkerControlGrpcService>();
        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            lifecycle.SealSocket();
            return new WorkerManagerHost(application, manager, lifecycle);
        }
        catch
        {
            await manager.DisposeAsync().ConfigureAwait(false);
            await application.DisposeAsync().ConfigureAwait(false);
            lifecycle.Dispose();
            throw;
        }
    }

    public Task<ApiWorkerSession> LaunchWorkerAsync(
        WorkerLaunchRequest request,
        CancellationToken cancellationToken = default) => manager.LaunchWorkerAsync(request, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        await manager.DisposeAsync().ConfigureAwait(false);
        using var stopCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await application.StopAsync(stopCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            await application.DisposeAsync().ConfigureAwait(false);
            socketLifecycle.Dispose();
        }
    }
}

public sealed class ApiControlPlaneIdentity
{
    public ApiControlPlaneIdentity(string instanceId)
    {
        IpcValidator.ValidateIdentifier(instanceId, nameof(instanceId));
        InstanceId = instanceId;
        ProcessId = Environment.ProcessId;
        ProcessBootId = ProcessIdentityProbe.ReadBootId();
        ProcessStartTicks = ProcessIdentityProbe.ReadStartTicks(ProcessId);
    }

    public string InstanceId { get; }
    public long ProcessId { get; }
    public string ProcessBootId { get; }
    public long ProcessStartTicks { get; }
}

public sealed class WorkerManager : IAsyncDisposable, ISessionWorkerControl, ICurrentWorkerRouter, IRealtimeSessionRegistry
{
    private readonly WorkerManagerOptions options;
    private readonly ILoggerFactory loggerFactory;
    private readonly ISessionRuntimeStore? runtimeStore;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly WorkerProcessLauncher processLauncher;
    private readonly object sync = new();
    private readonly object shutdownSync = new();
    private readonly Dictionary<string, ApiWorkerSession> sessions = new(StringComparer.Ordinal);
    private Task? shutdownTask;
    private int disposed;
    private int draining;

    public WorkerManager(
        WorkerManagerOptions options,
        ILoggerFactory loggerFactory,
        ISessionRuntimeStore? runtimeStore = null,
        TimeProvider? timeProvider = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        this.runtimeStore = runtimeStore;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        logger = loggerFactory.CreateLogger<WorkerManager>();
        processLauncher = new WorkerProcessLauncher();
    }

    public string ControlPlaneInstanceId => options.ControlPlaneInstanceId;

    public bool IsDraining => Volatile.Read(ref draining) != 0;

    public Task<CurrentWorkerRoute?> GetCurrentAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ApiWorkerSession? worker = sessions.Values.SingleOrDefault(item =>
                string.Equals(item.Binding.SessionId, sessionId, StringComparison.Ordinal));
            return Task.FromResult<CurrentWorkerRoute?>(worker is null
                ? null
                : new CurrentWorkerRoute(worker.RuntimeBinding, new ApiWorkerProcessHandle(worker)));
        }
    }

    public Task<RealtimeSubscriptionRoute?> TrySubscribeAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (IsDraining)
                return Task.FromResult<RealtimeSubscriptionRoute?>(null);
            ApiWorkerSession? worker = sessions.Values.SingleOrDefault(item =>
                string.Equals(item.Binding.SessionId, sessionId, StringComparison.Ordinal));
            if (worker is null || worker.OutputHub.State is SessionOutputHubState.Faulted or SessionOutputHubState.Disposed)
                return Task.FromResult<RealtimeSubscriptionRoute?>(null);
            try
            {
                RealtimeSubscription subscription = worker.OutputHub.Subscribe();
                return Task.FromResult<RealtimeSubscriptionRoute?>(new RealtimeSubscriptionRoute(
                    sessionId,
                    worker.Binding.WorkerId,
                    worker.Binding.WorkerEpoch,
                    StructuredIpcProtocol.CapabilitySetDigest,
                    subscription));
            }
            catch (InvalidOperationException)
            {
                return Task.FromResult<RealtimeSubscriptionRoute?>(null);
            }
        }
    }

    public async Task<SessionInputResult> DispatchInputAsync(
        SessionInputCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        RealtimeInputDispatch dispatch = await BeginInputAsync(command, timeout, cancellationToken).ConfigureAwait(false);
        return await dispatch.Completion.ConfigureAwait(false);
    }

    public async Task<RealtimeInputDispatch> BeginInputAsync(
        SessionInputCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.WorkerEpoch == 0 || string.IsNullOrWhiteSpace(command.SessionId))
            return CompletedDispatch(RealtimeSessionResults.Error(command, SessionInputResultCodes.InvalidCommand));
        if (IsDraining)
            return CompletedDispatch(RealtimeSessionResults.Error(command, SessionInputResultCodes.SessionNotAcceptingInput));

        ApiWorkerSession? worker;
        lock (sync)
        {
            worker = sessions.Values.SingleOrDefault(item =>
                string.Equals(item.Binding.SessionId, command.SessionId, StringComparison.Ordinal));
        }
        if (worker is null)
            return CompletedDispatch(RealtimeSessionResults.Error(command, SessionInputResultCodes.SessionNotRunning));
        if (command.WorkerEpoch > long.MaxValue || worker.Binding.WorkerEpoch != command.WorkerEpoch)
            return CompletedDispatch(RealtimeSessionResults.Error(command, SessionInputResultCodes.StaleEpoch));

        if (runtimeStore is ICurrentSessionRuntimeLeaseReader leaseReader)
        {
            try
            {
                SessionRuntimeLease? lease = await leaseReader.GetCurrentLeaseAsync(command.SessionId, cancellationToken).ConfigureAwait(false);
                if (lease is null)
                    return CompletedDispatch(RealtimeSessionResults.Error(command, SessionInputResultCodes.SessionNotAcceptingInput));
                if (lease.Binding.WorkerEpoch != (long)command.WorkerEpoch)
                    return CompletedDispatch(RealtimeSessionResults.Error(command, SessionInputResultCodes.StaleEpoch));
                if (!string.Equals(lease.Binding.WorkerId, worker.Binding.WorkerId, StringComparison.Ordinal) ||
                    !string.Equals(lease.Binding.ControlPlaneInstanceId, ControlPlaneInstanceId, StringComparison.Ordinal) ||
                    lease.Binding.StateVersion != worker.RuntimeBinding.StateVersion ||
                    lease.State != Domain.Sessions.SessionState.Running)
                    return CompletedDispatch(RealtimeSessionResults.Error(command, SessionInputResultCodes.SessionNotAcceptingInput));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                worker.LogLifecycle("realtime_input_persistence_failed", "database_unavailable", LogLevel.Error);
                return CompletedDispatch(RealtimeSessionResults.WorkerUnavailable(command));
            }
        }

        Task<SessionInputResult> receipt = await worker.QueueInputAsync(command, timeout, cancellationToken).ConfigureAwait(false);
        return new RealtimeInputDispatch(receipt);
    }

    private static RealtimeInputDispatch CompletedDispatch(SessionInputResult result) =>
        new(Task.FromResult(result));

    public IReadOnlyCollection<ApiWorkerSession> Workers
    {
        get
        {
            lock (sync)
                return sessions.Values.ToArray();
        }
    }

    public void BeginDraining() => Interlocked.Exchange(ref draining, 1);

    internal void RemoveWorker(ApiWorkerSession worker, string reason = "worker-removed")
    {
        ArgumentNullException.ThrowIfNull(worker);
        lock (sync)
        {
            if (sessions.TryGetValue(worker.Binding.WorkerId, out ApiWorkerSession? current) && ReferenceEquals(current, worker))
                sessions.Remove(worker.Binding.WorkerId);
        }
        worker.OutputHub.Complete(reason);
    }

    public async Task<IWorkerProcessHandle> StartAsync(
        WorkerLaunchSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (IsDraining)
            throw new SessionRuntimeException(SessionRuntimeResultCodes.ControlPlaneDraining, "The control plane is draining.");
        if (!string.Equals(spec.Binding.ControlPlaneInstanceId, options.ControlPlaneInstanceId, StringComparison.Ordinal))
            throw new SessionRuntimeException(SessionRuntimeResultCodes.InvalidBinding, "The Worker binding belongs to another control plane.");
        if (spec.ExpectedParentProcessId != Environment.ProcessId ||
            (!string.IsNullOrWhiteSpace(spec.ControlSocketPath) &&
             !string.Equals(Path.GetFullPath(spec.ControlSocketPath), Path.GetFullPath(options.ControlSocketPath), StringComparison.Ordinal)))
            throw new SessionRuntimeException(SessionRuntimeResultCodes.InvalidBinding, "The Worker launch binding does not belong to this API process.");

        ApiWorkerSession session = await LaunchWorkerAsync(
            new WorkerLaunchRequest(
                new WorkerBinding(spec.Binding.SessionId, spec.Binding.WorkerId, checked((ulong)spec.Binding.WorkerEpoch)),
                spec.SessionRoot.AbsoluteSessionRoot,
                spec.SessionRoot.CompatibilityProfile,
                (RuntimeSaveLayout)spec.SessionRoot.SaveLayout,
                spec.SessionRoot.ManifestDigest,
                spec.Binding.InitialOutputSequence,
                spec.BrowserWidth, spec.FontSize, spec.LineHeight, spec.TextMetrics?.HalfWidthPx ?? 0, spec.TextMetrics?.FullWidthPx ?? 0),
            spec.Binding,
            waitForRegistration: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new ApiWorkerProcessHandle(session);
    }

    public Task<ApiWorkerSession> LaunchWorkerAsync(
        WorkerLaunchRequest request,
        CancellationToken cancellationToken = default) =>
        LaunchWorkerAsync(request, runtimeBinding: null, waitForRegistration: true, cancellationToken: cancellationToken);

    private async Task<ApiWorkerSession> LaunchWorkerAsync(
        WorkerLaunchRequest request,
        SessionRuntimeBinding? runtimeBinding,
        bool waitForRegistration,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (IsDraining)
            throw new SessionRuntimeException(SessionRuntimeResultCodes.ControlPlaneDraining, "The control plane is draining.");
        ArgumentNullException.ThrowIfNull(request);
        ApiWorkerSession session;
        lock (sync)
        {
            if (sessions.Values.Any(item => item.Binding.SessionId == request.Binding.SessionId))
                throw new SessionRuntimeException(SessionRuntimeResultCodes.WorkerStartFailed, "The Session already has a Worker.");
            if (sessions.ContainsKey(request.Binding.WorkerId))
                throw new SessionRuntimeException(SessionRuntimeResultCodes.WorkerStartFailed, "The Worker ID is already registered with this API.");
            session = new ApiWorkerSession(request, options, loggerFactory.CreateLogger<ApiWorkerSession>(), runtimeBinding);
            sessions.Add(request.Binding.WorkerId, session);
        }

        string bootstrapPath = Path.Combine(options.BootstrapDirectory, $"{request.Binding.WorkerId}-{Guid.NewGuid():N}.json");
        session.SetBootstrapPath(bootstrapPath);
        WorkerBootstrapDocument bootstrap = new()
        {
            SessionId = request.Binding.SessionId,
            WorkerId = request.Binding.WorkerId,
            WorkerEpoch = request.Binding.WorkerEpoch,
            SessionRoot = request.SessionRoot,
            CompatibilityProfile = request.CompatibilityProfile,
            ControlSocketPath = options.ControlSocketPath,
            ControlPlaneInstanceId = options.ControlPlaneInstanceId,
            ExpectedParentProcessId = Environment.ProcessId,
            BootstrapToken = IpcProtocol.CreateBootstrapToken(),
            ConnectDeadlineUnixMilliseconds = DateTimeOffset.UtcNow.Add(options.RegistrationTimeout).ToUnixTimeMilliseconds(),
            HeartbeatIntervalMilliseconds = checked((int)options.HeartbeatInterval.TotalMilliseconds),
            ShutdownGracePeriodMilliseconds = checked((int)options.WorkerShutdownTimeout.TotalMilliseconds),
            SaveLayout = (int)request.SaveLayout,
            SessionRootManifestDigest = request.SessionRootManifestDigest,
            InitialOutputSequence = request.InitialOutputSequence,
            BrowserWidth = request.BrowserWidth,
            FontSize = request.FontSize,
            LineHeight = request.LineHeight,
            HalfWidthPx = request.HalfWidthPx,
            FullWidthPx = request.FullWidthPx,
        };
        session.SetBootstrapToken(bootstrap.BootstrapToken);
        WorkerBootstrapDocument bootstrapToWrite = options.BootstrapTransformForTest?.Invoke(bootstrap) ?? bootstrap;
        WorkerBootstrapFile.Write(bootstrapPath, bootstrapToWrite);
        session.LogLifecycle(
            "worker_launch_width",
            $"browserWidth={request.BrowserWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        try
        {
            Process process = StartProcess(request.SessionRoot, bootstrapPath, session);
            session.AttachProcess(process, ProcessIdentityProbe.Read(process));
            if (waitForRegistration)
                await session.WaitForRegistrationAsync(options.RegistrationTimeout, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.LogLifecycle("worker_launch_failed", SessionRuntimeResultCodes.WorkerStartFailed, LogLevel.Warning);
            WorkerBootstrapFile.DeleteIfOwned(bootstrapPath);
            bool exited = await session.TerminateProcessAsync(options.WorkerShutdownTimeout, "launch-failed", CancellationToken.None).ConfigureAwait(false);
            if (exited)
            {
                lock (sync)
                    sessions.Remove(request.Binding.WorkerId);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    public RegistrationDecision Register(WorkerEnvelope registration)
    {
        if (registration is null)
            return RegistrationDecision.Rejected(IpcReasonCodes.InvalidEnvelope);
        ApiWorkerSession? session;
        lock (sync)
            sessions.TryGetValue(registration.WorkerId, out session);
        bool registered = session?.IsRegistered == true;
        IpcValidationResult validation = StructuredIpcValidator.ValidateWorkerEnvelope(
            registration,
            registered,
            registered ? session!.Binding : null,
            StructuredIpcProtocol.CapabilitySetDigest);
        if (!validation.IsValid)
        {
            LogRejected(validation.ReasonCode, registration);
            return RegistrationDecision.Rejected(validation.ReasonCode);
        }
        if (session is null || !session.Binding.Matches(registration.SessionId, registration.WorkerId, registration.WorkerEpoch))
            return RegistrationDecision.Rejected(IpcReasonCodes.BindingMismatch);
        if (!string.Equals(registration.ControlPlaneInstanceId, options.ControlPlaneInstanceId, StringComparison.Ordinal))
            return RegistrationDecision.Rejected(IpcReasonCodes.ControlPlaneMismatch);
        if (registration.Registration.ProcessId != session.ProcessId ||
            !string.Equals(registration.Registration.ProcessBootId, session.ProcessIdentity.ProcessBootId, StringComparison.Ordinal) ||
            registration.Registration.ProcessStartTicks != session.ProcessIdentity.ProcessStartTicks)
            return RegistrationDecision.Rejected(IpcReasonCodes.BindingMismatch);
        if (!session.AcceptRegistrationToken(registration.Registration.StartupToken))
            return RegistrationDecision.Rejected(IpcReasonCodes.InvalidToken);
        if (!string.Equals(registration.Registration.RuntimeIntegrationVersion, RuntimeBaseline.CloudEmueraIntegrationVersion, StringComparison.Ordinal) ||
            !string.Equals(registration.Registration.UpstreamCommit, RuntimeBaseline.UpstreamCommit, StringComparison.Ordinal))
            return RegistrationDecision.Rejected(IpcReasonCodes.RuntimeVersionMismatch);

        var connection = new ApiWorkerConnection(session);
        session.AttachConnection(connection);
        return RegistrationDecision.Accept(session, connection);
    }

    public async Task ReceiveAsync(ApiWorkerConnection connection, WorkerEnvelope message)
    {
        IpcValidationResult validation = StructuredIpcValidator.ValidateWorkerEnvelope(
            message,
            registered: true,
            connection.Session.Binding,
            StructuredIpcProtocol.CapabilitySetDigest);
        if (!validation.IsValid)
        {
            if (message.PayloadCase == WorkerEnvelope.PayloadOneofCase.Heartbeat)
            {
                // Heartbeats are liveness samples, not control transitions.
                // A malformed sample must be ignored so the next valid sample
                // can renew the lease; the bounded watchdog handles a Worker
                // that keeps emitting invalid samples.
                connection.Session.LogLifecycle(
                    "heartbeat_rejected",
                    $"invalid_heartbeat_payload={validation.ReasonCode}",
                    LogLevel.Warning);
                return;
            }
            connection.Session.RecordProtocolError(validation.ReasonCode);
            connection.Cancel();
            return;
        }
        if (!string.Equals(message.ControlPlaneInstanceId, connection.Session.ControlPlaneInstanceId, StringComparison.Ordinal))
        {
            connection.Session.RecordProtocolError(IpcReasonCodes.ControlPlaneMismatch);
            connection.Cancel();
            return;
        }
        if (message.PayloadCase == WorkerEnvelope.PayloadOneofCase.DisplayFrame)
        {
            RealtimePublishResult outputResult = connection.Session.AcceptDisplayFrame(message.DisplayFrame);
            if (message.DisplayFrame.RequiresSnapshot)
            {
                connection.Session.LogLifecycle(
                    "display_snapshot_received",
                    $"frameId={message.DisplayFrame.FrameId.ToString(System.Globalization.CultureInfo.InvariantCulture)};sequence={outputResult.SnapshotSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (!outputResult.Accepted)
            {
                connection.Session.LogLifecycle(
                    "display_frame_not_applied",
                    outputResult.ReasonCode ?? outputResult.Disposition.ToString(),
                    outputResult.Disposition == RealtimePublishDisposition.Faulted ? LogLevel.Error : LogLevel.Warning);
            }
            if (outputResult.Disposition == RealtimePublishDisposition.Faulted)
            {
                connection.Session.RecordProtocolError(outputResult.ReasonCode ?? IpcReasonCodes.OutputResumeGap);
                connection.Cancel();
                return;
            }
            if (!outputResult.Accepted)
                return;
        }
        if (message.PayloadCase == WorkerEnvelope.PayloadOneofCase.DisplayBatch)
        {
            // v5 has one production display boundary: DisplayFrame.  Keep the
            // old mapper/hub entry point only for historical unit fixtures; a
            // live Worker sending DisplayBatch must fail closed instead of
            // reintroducing timer- or batch-boundary visibility.
            connection.Session.RecordProtocolError(IpcReasonCodes.UnsupportedMessage);
            connection.Cancel();
            return;
        }
        if (message.PayloadCase == WorkerEnvelope.PayloadOneofCase.InputResult)
        {
            // Input receipts have their own bounded correlation map.  They
            // must never depend on the lossy lifecycle/event probe.  A valid
            // receipt is still copied into the bounded probe for existing
            // diagnostics/tests; the waiter was already completed above.
            if (connection.Session.TryCompleteInput(message))
                await connection.Session.PublishAsync(message).ConfigureAwait(false);
            return;
        }
        if (message.PayloadCase == WorkerEnvelope.PayloadOneofCase.Heartbeat)
        {
            DateTimeOffset heartbeatObservedAt = timeProvider.GetUtcNow();
            if (!connection.Session.TryBeginHeartbeatProcessing(heartbeatObservedAt))
            {
                connection.Session.LogLifecycle("heartbeat_rejected", "heartbeat_timeout_claimed", LogLevel.Warning);
                connection.Cancel();
                return;
            }

            try
            {
                if (runtimeStore is not null && connection.Session.RuntimePersistenceReady)
                {
                    SessionRuntimeWriteResult heartbeatResult = await runtimeStore.RecordHeartbeatAsync(
                        connection.Session.RuntimeBinding,
                        new WorkerHeartbeatInfo(
                            connection.Session.ProcessIdentity,
                            message.Heartbeat.OutputSequence,
                            message.Heartbeat.WaitingForInput,
                            string.IsNullOrEmpty(message.Heartbeat.CurrentPromptId) ? null : message.Heartbeat.CurrentPromptId,
                            message.Heartbeat.ResidentMemoryBytes,
                            heartbeatObservedAt),
                        options.LeaseDuration).ConfigureAwait(false);
                    if (!heartbeatResult.Applied || heartbeatResult.Binding is null)
                    {
                        connection.Session.LogLifecycle("heartbeat_rejected", SessionRuntimeResultCodes.WorkerStaleEpoch, LogLevel.Warning);
                        connection.Cancel();
                        return;
                    }
                    connection.Session.UpdateRuntimeBinding(heartbeatResult.Binding);
                }
            }
            catch (ArgumentException exception)
            {
                // A heartbeat the durable store cannot accept (for example
                // WaitingForInput=true with an empty CurrentPromptId) is a
                // payload defect, not a database outage. Reject this sample
                // and keep the control stream alive: the next heartbeat renews
                // the lease, and a persistently broken Worker still falls to
                // the heartbeat-timeout watchdog instead of being torn down by
                // one transient inconsistency.
                connection.Session.LogLifecycle(
                    "heartbeat_rejected",
                    $"invalid_heartbeat_payload={SanitizeRuntimeDiagnostic(exception.Message)}",
                    LogLevel.Warning);
                return;
            }
            catch (Exception exception) when (exception is DbUpdateException or SqliteException or IOException or UnauthorizedAccessException)
            {
                connection.Session.LogLifecycle(
                    "heartbeat_persist_failed",
                    $"database_unavailable={SanitizeRuntimeDiagnostic(exception.Message)}",
                    LogLevel.Error);
                connection.Cancel();
                return;
            }
            finally
            {
                // Heartbeat persistence uses the same SQLite busy timeout as
                // other API writes. Give the Worker a fresh liveness window
                // after that bounded write instead of allowing the watchdog
                // to race the write at LeaseDuration.
                connection.Session.CompleteHeartbeatProcessing(timeProvider.GetUtcNow());
            }
        }
        switch (message.PayloadCase)
        {
            case WorkerEnvelope.PayloadOneofCase.RuntimeCompleted:
                connection.Session.LogLifecycle(
                    "runtime_completed_received",
                    $"status={message.RuntimeCompleted.Status};lastSequence={message.RuntimeCompleted.LastOutputSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                connection.Session.MarkGracefulTerminationObserved();
                connection.Session.OutputHub.Complete("runtime-completed");
                await connection.Session.SendTerminalAcknowledgementAsync(message, connection.CancellationToken).ConfigureAwait(false);
                break;
            case WorkerEnvelope.PayloadOneofCase.RuntimeFailed:
                connection.Session.LogLifecycle(
                    "runtime_failed_received",
                    $"code={message.RuntimeFailed.StableCode};phase={message.RuntimeFailed.Phase};fatal={message.RuntimeFailed.Fatal};lastSequence={message.RuntimeFailed.LastOutputSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)};message={SanitizeRuntimeDiagnostic(message.RuntimeFailed.SafeMessage)}",
                    message.RuntimeFailed.Fatal ? LogLevel.Error : LogLevel.Warning);
                connection.Session.OutputHub.Complete(
                    string.IsNullOrWhiteSpace(message.RuntimeFailed.StableCode)
                        ? "runtime-failed"
                        : message.RuntimeFailed.StableCode);
                await connection.Session.SendTerminalAcknowledgementAsync(message, connection.CancellationToken).ConfigureAwait(false);
                break;
            case WorkerEnvelope.PayloadOneofCase.WorkerStopped when message.WorkerStopped.Graceful:
                connection.Session.LogLifecycle(
                    "worker_stopped_received",
                    $"reason={message.WorkerStopped.ReasonCode};graceful=true;lastSequence={message.WorkerStopped.LastOutputSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                connection.Session.MarkGracefulTerminationObserved();
                connection.Session.OutputHub.Complete(
                    string.IsNullOrWhiteSpace(message.WorkerStopped.ReasonCode)
                        ? "worker-stopped"
                        : message.WorkerStopped.ReasonCode);
                break;
            case WorkerEnvelope.PayloadOneofCase.WorkerStopped:
                connection.Session.LogLifecycle(
                    "worker_stopped_received",
                    $"reason={message.WorkerStopped.ReasonCode};graceful=false;lastSequence={message.WorkerStopped.LastOutputSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    LogLevel.Warning);
                connection.Session.OutputHub.Complete(
                    string.IsNullOrWhiteSpace(message.WorkerStopped.ReasonCode)
                        ? "worker-stopped"
                        : message.WorkerStopped.ReasonCode);
                break;
        }
        await connection.Session.PublishAsync(message).ConfigureAwait(false);
    }

    private static string SanitizeRuntimeDiagnostic(string value)
    {
        string safe = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return safe.Length <= 1_000 ? safe : safe[..1_000];
    }

    public static void Disconnect(ApiWorkerConnection connection) => connection.Session.DetachConnection(connection);

    internal Task ShutdownAsync(string reasonCode = "requested")
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
            reasonCode = "requested";

        lock (shutdownSync)
            return shutdownTask ??= ShutdownWorkersAsync(reasonCode);
    }

    private async Task ShutdownWorkersAsync(string reasonCode)
    {
        BeginDraining();
        ApiWorkerSession[] workers = Workers.ToArray();
        if (workers.Length == 0)
            return;

        using var gracefulCancellation = new CancellationTokenSource(options.WorkerShutdownTimeout);
        DateTimeOffset gracefulDeadline = DateTimeOffset.UtcNow.Add(options.WorkerShutdownTimeout);
        await Task.WhenAll(workers.Select(worker => RequestGracefulStopAsync(
            worker,
            reasonCode,
            gracefulDeadline,
            gracefulCancellation.Token))).ConfigureAwait(false);

        ApiWorkerSession[] remaining = workers.Where(worker => !worker.HasExited).ToArray();
        if (remaining.Length == 0)
            return;

        using var forceCancellation = new CancellationTokenSource(options.WorkerShutdownTimeout);
        DateTimeOffset forceDeadline = DateTimeOffset.UtcNow.Add(options.WorkerShutdownTimeout);
        await Task.WhenAll(remaining.Select(worker => ForceStopAsync(
            worker,
            reasonCode,
            forceDeadline,
            forceCancellation.Token))).ConfigureAwait(false);
    }

    private static TimeSpan RemainingUntil(DateTimeOffset deadline)
    {
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
    }

    private static async Task RequestGracefulStopAsync(
        ApiWorkerSession worker,
        string reasonCode,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        try
        {
            await worker.StopAsync(reasonCode, RemainingUntil(deadline), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            worker.LogLifecycle("worker_graceful_stop_deadline", reasonCode, LogLevel.Warning);
        }
        catch (Exception exception)
        {
            worker.LogLifecycle(
                "worker_graceful_stop_failed",
                $"{reasonCode};{SensitiveLogPolicy.SafeReasonCode(exception.GetType().Name)}",
                LogLevel.Warning);
        }
    }

    private static async Task ForceStopAsync(
        ApiWorkerSession worker,
        string reasonCode,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await worker.TerminateProcessAsync(
                RemainingUntil(deadline),
                reasonCode,
                cancellationToken).ConfigureAwait(false))
            {
                worker.LogLifecycle("worker_exit_unconfirmed", SessionRuntimeResultCodes.WorkerExitUnconfirmed, LogLevel.Error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            worker.LogLifecycle("worker_exit_unconfirmed", SessionRuntimeResultCodes.WorkerExitUnconfirmed, LogLevel.Error);
        }
        catch (Exception exception)
        {
            worker.LogLifecycle(
                "worker_force_stop_failed",
                $"{reasonCode};{SensitiveLogPolicy.SafeReasonCode(exception.GetType().Name)}",
                LogLevel.Error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        BeginDraining();
        try
        {
            await ShutdownAsync().ConfigureAwait(false);
            ApiWorkerSession[] workers = Workers.ToArray();
            await Task.WhenAll(workers.Select(worker => worker.DisposeResourcesAsync().AsTask())).ConfigureAwait(false);
        }
        finally
        {
            processLauncher.Dispose();
        }
    }

    private Process StartProcess(string workingDirectory, string bootstrapPath, ApiWorkerSession session)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.DotnetPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        WorkerProcessEnvironment.RemoveHostOrchestratorVariables(startInfo);
        startInfo.ArgumentList.Add(options.WorkerAssemblyPath);
        startInfo.ArgumentList.Add("--bootstrap-file");
        startInfo.ArgumentList.Add(bootstrapPath);
        Process process = processLauncher.Start(startInfo);
        _ = session.CaptureProcessOutputAsync(process.StandardOutput, "stdout");
        _ = session.CaptureProcessOutputAsync(process.StandardError, "stderr");
        return process;
    }

    private void LogRejected(string reasonCode, WorkerEnvelope message) =>
        RegistrationRejectedLog(logger, options.ControlPlaneInstanceId, message.SessionId, message.WorkerId, message.WorkerEpoch, reasonCode, null);

    private static readonly Action<ILogger, string, string, string, ulong, string, Exception?> RegistrationRejectedLog =
        LoggerMessage.Define<string, string, string, ulong, string>(
            LogLevel.Warning,
            new EventId(2101, "WorkerRegistrationRejected"),
            "worker_event=registration_rejected controlPlaneInstanceId={ControlPlaneInstanceId} sessionId={SessionId} workerId={WorkerId} workerEpoch={WorkerEpoch} reason={Reason}");
}

public sealed record RegistrationDecision(
    bool Accepted,
    string ReasonCode,
    ApiWorkerSession? Session,
    ApiWorkerConnection? Connection)
{
    public static RegistrationDecision Accept(ApiWorkerSession session, ApiWorkerConnection connection) =>
        new(true, IpcReasonCodes.Accepted, session, connection);

    public static RegistrationDecision Rejected(string reasonCode) => new(false, reasonCode, null, null);
}

public sealed class ApiWorkerSession : IAsyncDisposable
{
    private readonly WorkerLaunchRequest request;
    private readonly WorkerManagerOptions options;
    private readonly ILogger logger;
    private readonly object sync = new();
    private readonly Channel<WorkerCommandEnvelope> commands = Channel.CreateBounded<WorkerCommandEnvelope>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly List<WorkerEnvelope> pendingEvents = [];
    private readonly Dictionary<string, PendingInput> pendingInputs = new(StringComparer.Ordinal);
    private long pendingEventBytes;
    private long pendingInputBytes;
    private long unknownInputResultCount;
    private long droppedPendingEventCount;
    private readonly StringBuilder processDiagnostics = new();
    private readonly List<string> sensitiveLogValues = [];
    private TaskCompletionSource<bool> eventSignal = NewEventSignal();
    private readonly TaskCompletionSource<bool> registration = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> processExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WorkerEnvelope> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ApiWorkerConnection? connection;
    private Process? process;
    private SessionRuntimeBinding? runtimeBinding;
    private string bootstrapPath = string.Empty;
    private string bootstrapToken = string.Empty;
    private bool registered;
    private long lastHeartbeatUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
    private long heartbeatProcessingStartedUtcTicks;
    private long lastDisplaySequence;
    private int connectionCount;
    private int disposed;
    private int runtimePersistenceReady;
    private int gracefulTerminationObserved;
    private int forceStopRequested;
    private int heartbeatProcessingCount;
    private bool heartbeatTimeoutClaimed;

    private sealed class PendingInput(
        SessionInputCommand command,
        long estimatedBytes,
        TaskCompletionSource<SessionInputResult> completion)
    {
        public SessionInputCommand Command { get; } = command;
        public long EstimatedBytes { get; } = estimatedBytes;
        public TaskCompletionSource<SessionInputResult> Completion { get; } = completion;
    }

    internal ApiWorkerSession(
        WorkerLaunchRequest request,
        WorkerManagerOptions options,
        ILogger logger,
        SessionRuntimeBinding? runtimeBinding = null)
    {
        this.request = request;
        this.options = options;
        this.logger = logger;
        this.runtimeBinding = runtimeBinding;
        sensitiveLogValues.Add(request.SessionRoot);
        sensitiveLogValues.Add(Path.GetFullPath(options.ControlSocketPath));
        lastDisplaySequence = request.InitialOutputSequence;
        OutputHub = new SessionOutputHub(
            request.Binding.SessionId,
            request.Binding.WorkerId,
            request.Binding.WorkerEpoch,
            options.RealtimeOutput,
            minimumInitialSequence: request.InitialOutputSequence);
        OutputHub.FaultReported += reason =>
        {
            // Reader-driven faults (for example an unencodable snapshot) are
            // not visible on the Worker receive path. Retire the Worker so the
            // Session is reconciled instead of leaving an unreadable mirror.
            RecordProtocolError(string.IsNullOrWhiteSpace(reason) ? IpcReasonCodes.OutputResumeGap : reason);
            connection?.Cancel();
        };
    }

    public WorkerBinding Binding => request.Binding;
    public string SessionRoot => request.SessionRoot;
    public string ControlPlaneInstanceId => options.ControlPlaneInstanceId;
    public int ProcessId => process?.Id ?? 0;
    public WorkerProcessIdentity ProcessIdentity { get; private set; } = new(0, string.Empty, 0);
    internal TimeSpan ShutdownTimeout => options.WorkerShutdownTimeout;
    public bool IsRegistered => Volatile.Read(ref registered);
    public bool HasExited => process?.HasExited ?? true;
    public int? ExitCode => process is { HasExited: true } value ? value.ExitCode : null;
    public int ConnectionCount { get { lock (sync) return connectionCount; } }
    public string ProcessDiagnostics { get { lock (sync) return processDiagnostics.ToString(); } }
    public SessionOutputHub OutputHub { get; }
    public long LastOutputSequence { get { lock (sync) return lastDisplaySequence; } }
    public long DroppedPendingEventCount => Interlocked.Read(ref droppedPendingEventCount);
    public DateTimeOffset LastHeartbeatAt => new(Interlocked.Read(ref lastHeartbeatUtcTicks), TimeSpan.Zero);
    internal bool RuntimePersistenceReady => Volatile.Read(ref runtimePersistenceReady) != 0;
    internal bool GracefulTerminationObserved => Volatile.Read(ref gracefulTerminationObserved) != 0;
    internal bool ForceStopRequested => Volatile.Read(ref forceStopRequested) != 0;

    public Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => processExit.Task.WaitAsync(timeout, cancellationToken);

    public async Task<WorkerEnvelope> WaitForAsync(Func<WorkerEnvelope, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            while (true)
            {
                Task signal;
                lock (sync)
                {
                    for (int index = 0; index < pendingEvents.Count; index++)
                    {
                        WorkerEnvelope value = pendingEvents[index];
                        if (predicate(value))
                        {
                            pendingEvents.RemoveAt(index);
                            return value;
                        }
                    }
                    if (eventSignal.Task.IsCompleted)
                        eventSignal = NewEventSignal();
                    signal = eventSignal.Task;
                }
                await signal.WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"The Worker did not produce the expected IPC event. exited={HasExited}; diagnostics_present={ProcessDiagnostics.Length > 0}");
        }
    }

    public async Task SendStartRuntimeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(10));
        await commands.Writer.WriteAsync(CreateEnvelope(IpcProtocol.NewMessageId("start"), new StartRuntime
        {
            ExpectedSessionId = Binding.SessionId,
            ExpectedWorkerId = Binding.WorkerId,
            ExpectedWorkerEpoch = Binding.WorkerEpoch,
            ExpectedCompatibilityProfile = request.CompatibilityProfile,
            ExpectedCapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            DeadlineUnixMilliseconds = deadline.ToUnixTimeMilliseconds(),
        }), cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionInputResult> SubmitInputAsync(
        SessionInputCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Task<SessionInputResult> receipt = await QueueInputAsync(command, timeout, cancellationToken).ConfigureAwait(false);
        return await receipt.ConfigureAwait(false);
    }

    public async Task<Task<SessionInputResult>> QueueInputAsync(
        SessionInputCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        string messageId = IpcProtocol.NewMessageId("input");
        long estimatedBytes = EstimateInputBytes(command);
        var completion = new TaskCompletionSource<SessionInputResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingInput(command, estimatedBytes, completion);
        lock (sync)
        {
            if (Volatile.Read(ref disposed) != 0 || commands.Reader.Completion.IsCompleted)
                return Task.FromResult(RealtimeSessionResults.WorkerUnavailable(command));
            if (pendingInputs.Count >= options.PendingInputMaxMessages ||
                pendingInputBytes > options.PendingInputMaxBytes - estimatedBytes)
                return Task.FromResult(RealtimeSessionResults.Error(command, SessionInputResultCodes.InputBackpressure));
            pendingInputs.Add(messageId, pending);
            pendingInputBytes += estimatedBytes;
        }

        using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        writeDeadline.CancelAfter(timeout);
        try
        {
            SubmitInput input = new()
            {
                ClientMessageId = command.ClientMessageId,
                Value = command.Value,
                Source = (InputSource)(int)command.Source,
                DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.Add(timeout).ToUnixTimeMilliseconds(),
            };
            if (command.PointerData is { } pointer)
                input.Pointer = new PointerPayload
                {
                    Position = new Point { X = pointer.X, Y = pointer.Y },
                    Button = pointer.Button,
                    Pressed = pointer.Pressed,
                };
            if (command.Key is { } key)
                input.Key = new KeyPayload
                {
                    KeyCode = key.KeyCode,
                    Control = key.Control,
                    Alt = key.Alt,
                    Shift = key.Shift,
                };

            await commands.Writer.WriteAsync(CreateEnvelope(messageId, input), writeDeadline.Token).ConfigureAwait(false);
            return AwaitInputReceiptAsync(messageId, pending, completion, timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RemovePendingInput(messageId, pending);
            throw;
        }
        catch (OperationCanceledException) when (writeDeadline.IsCancellationRequested)
        {
            RemovePendingInput(messageId, pending);
            return Task.FromResult(RealtimeSessionResults.WorkerUnavailable(command));
        }
        catch (ChannelClosedException)
        {
            RemovePendingInput(messageId, pending);
            return Task.FromResult(RealtimeSessionResults.WorkerUnavailable(command));
        }
        catch
        {
            RemovePendingInput(messageId, pending);
            throw;
        }
    }

    private async Task<SessionInputResult> AwaitInputReceiptAsync(
        string messageId,
        PendingInput pending,
        TaskCompletionSource<SessionInputResult> completion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return RealtimeSessionResults.WorkerUnavailable(pending.Command);
        }
        finally
        {
            RemovePendingInput(messageId, pending);
        }
    }

    /// <summary>
    /// Completes only a waiter whose binding, correlation and input identity
    /// all match. Unknown or malformed receipts are deliberately not placed in
    /// the lossy lifecycle event probe.
    /// </summary>
    internal bool TryCompleteInput(WorkerEnvelope message)
    {
        if (message.PayloadCase != WorkerEnvelope.PayloadOneofCase.InputResult || string.IsNullOrWhiteSpace(message.CorrelationId))
            return false;
        PendingInput? pending = null;
        SessionInputResult? result = null;
        lock (sync)
        {
            if (!pendingInputs.TryGetValue(message.CorrelationId, out pending) ||
                !string.Equals(message.SessionId, Binding.SessionId, StringComparison.Ordinal) ||
                !string.Equals(message.WorkerId, Binding.WorkerId, StringComparison.Ordinal) ||
                message.WorkerEpoch != Binding.WorkerEpoch ||
                !string.Equals(message.InputResult.ClientMessageId, pending.Command.ClientMessageId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref unknownInputResultCount);
                return false;
            }

            result = new SessionInputResult(
                message.InputResult.HasResolvedPromptId ? message.InputResult.ResolvedPromptId : null,
                message.InputResult.ClientMessageId,
                MapInputResultStatus(message.InputResult.Kind),
                NormalizeReasonCode(message.InputResult.ReasonCode, message.InputResult.Kind),
                message.InputResult.HasNormalizedValue ? message.InputResult.NormalizedValue : null);
            pendingInputs.Remove(message.CorrelationId);
            pendingInputBytes -= pending.EstimatedBytes;
        }
        pending.Completion.TrySetResult(result);
        return true;
    }

    public long UnknownInputResultCount => Interlocked.Read(ref unknownInputResultCount);

    /// <summary>Compatibility helper retained for the existing Worker smoke tests.</summary>
    public async Task SendInputAsync(
        string clientMessageId,
        string value,
        CancellationToken cancellationToken = default)
    {
        SessionInputResult result = await SubmitInputAsync(
            new SessionInputCommand(
                Binding.SessionId,
                Binding.WorkerEpoch,
                clientMessageId,
                value,
                SessionInputSource.Keyboard),
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        if (result.Status is SessionInputResultCodes.WorkerUnavailable or SessionInputResultCodes.InputBackpressure)
            throw new IOException($"Worker input was not accepted: {result.ReasonCode}");
    }

    private void RemovePendingInput(string messageId, PendingInput pending)
    {
        lock (sync)
        {
            if (pendingInputs.Remove(messageId, out PendingInput? current) && ReferenceEquals(current, pending))
                pendingInputBytes -= pending.EstimatedBytes;
        }
    }

    private static long EstimateInputBytes(SessionInputCommand command)
    {
        long value = checked(128L + (long)command.SessionId.Length * 2 +
            command.ClientMessageId.Length * 2 + command.Value.Length * 2);
        if (command.PointerData is not null) value += 32;
        if (command.Key is not null) value += 32;
        return value;
    }

    private static string MapInputResultStatus(InputResultKind kind) => kind switch
    {
        InputResultKind.Accepted => SessionInputResultCodes.Accepted,
        InputResultKind.Duplicate => SessionInputResultCodes.Duplicate,
        InputResultKind.Conflict => SessionInputResultCodes.Conflict,
        InputResultKind.NoActivePrompt => SessionInputResultCodes.NoActivePrompt,
        InputResultKind.InvalidFormat => SessionInputResultCodes.InvalidFormat,
        InputResultKind.InvalidCommand => SessionInputResultCodes.InvalidCommand,
        InputResultKind.Cancelled => SessionInputResultCodes.Cancelled,
        InputResultKind.TimedOut => SessionInputResultCodes.TimedOut,
        _ => SessionInputResultCodes.InvalidCommand,
    };

    private static string NormalizeReasonCode(string value, InputResultKind kind) =>
        string.IsNullOrWhiteSpace(value) ? MapInputResultStatus(kind) : value;

    public Task SendRawAsync(WorkerCommandEnvelope envelope, CancellationToken cancellationToken = default) =>
        commands.Writer.WriteAsync(envelope, cancellationToken).AsTask();

    public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        StopAsync("requested", timeout, cancellationToken);

    internal async Task StopAsync(string reasonCode, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (string.Equals(reasonCode, "admin_force_stopped", StringComparison.Ordinal))
            Interlocked.Exchange(ref forceStopRequested, 1);
        using var stopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stopCancellation.CancelAfter(timeout);
        await commands.Writer.WriteAsync(CreateEnvelope(IpcProtocol.NewMessageId("stop"), new StopWorker
        {
            ReasonCode = reasonCode,
            DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.Add(timeout).ToUnixTimeMilliseconds(),
        }), stopCancellation.Token).ConfigureAwait(false);
        // WorkerStopped is the preferred acknowledgement, but a graceful
        // Worker can close the UDS immediately after writing that final
        // envelope. Treat a confirmed process exit as the terminal fallback
        // so shutdown is not lost in that stream-close race.
        Task firstTerminal = await Task.WhenAny(stopped.Task, processExit.Task)
            .WaitAsync(timeout, stopCancellation.Token)
            .ConfigureAwait(false);
        if (firstTerminal == processExit.Task)
        {
            int exitCode = await processExit.Task.ConfigureAwait(false);
            if (exitCode != 0)
                throw new SessionRuntimeException(SessionRuntimeResultCodes.WorkerExitUnconfirmed, "The Worker exited without a graceful stop acknowledgement.");
        }
        else
            await processExit.Task.WaitAsync(timeout, stopCancellation.Token).ConfigureAwait(false);
    }

    public Task DisconnectCurrentConnectionForTestAsync()
    {
        lock (sync)
            connection?.Cancel();
        return Task.CompletedTask;
    }

    public async Task WaitForConnectionCountAsync(int expectedCount, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        while (true)
        {
            lock (sync)
            {
                if (connectionCount >= expectedCount)
                    return;
            }
            await Task.Delay(20, timeoutCancellation.Token).ConfigureAwait(false);
        }
    }

    internal void SetBootstrapPath(string path)
    {
        bootstrapPath = path;
        lock (sync) sensitiveLogValues.Add(Path.GetFullPath(path));
    }

    internal void SetBootstrapToken(string token)
    {
        bootstrapToken = token;
        lock (sync) sensitiveLogValues.Add(token);
    }

    internal void AttachProcess(Process value, WorkerProcessIdentity identity)
    {
        process = value;
        ProcessIdentity = identity;
        LogLifecycle("worker_process_started");
        if (value.HasExited)
            processExit.TrySetResult(value.ExitCode);
        value.Exited += (_, _) =>
        {
            int exitCode = value.ExitCode;
            processExit.TrySetResult(exitCode);
            if (!IsRegistered)
                registration.TrySetException(new InvalidOperationException("The Worker exited before registration."));
            LogLifecycle("worker_process_exited", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        };
    }

    internal SessionRuntimeBinding RuntimeBinding
    {
        get
        {
            lock (sync)
            {
                SessionRuntimeBinding binding = runtimeBinding ?? new SessionRuntimeBinding(
                    Binding.SessionId,
                    Binding.WorkerId,
                    checked((long)Binding.WorkerEpoch),
                    0,
                    ControlPlaneInstanceId,
                    SessionRoot,
                    request.CompatibilityProfile,
                    (int)request.SaveLayout,
                    request.SessionRootManifestDigest,
                    "",
                    LastOutputSequence);
                return binding with { InitialOutputSequence = Math.Max(binding.InitialOutputSequence, lastDisplaySequence) };
            }
        }
    }

    internal void UpdateRuntimeBinding(SessionRuntimeBinding binding, bool persistenceReady = false)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.SessionId.Equals(Binding.SessionId, StringComparison.Ordinal) ||
            !binding.WorkerId.Equals(Binding.WorkerId, StringComparison.Ordinal) ||
            binding.WorkerEpoch != checked((long)Binding.WorkerEpoch) ||
            !binding.ControlPlaneInstanceId.Equals(ControlPlaneInstanceId, StringComparison.Ordinal))
            throw new SessionRuntimeException(SessionRuntimeResultCodes.InvalidBinding, "The Worker runtime binding does not match the process.");
        lock (sync)
        {
            runtimeBinding = binding;
            if (persistenceReady)
                Interlocked.Exchange(ref runtimePersistenceReady, 1);
        }
    }

    internal bool ReadyConfirmed { get; private set; }

    internal void MarkReadyConfirmed()
    {
        ReadyConfirmed = true;
        MarkHeartbeatReceived(DateTimeOffset.UtcNow);
    }

    internal void MarkGracefulTerminationObserved() => Interlocked.Exchange(ref gracefulTerminationObserved, 1);

    internal void MarkHeartbeatReceived(DateTimeOffset observedAt)
    {
        lock (sync)
            lastHeartbeatUtcTicks = Math.Max(lastHeartbeatUtcTicks, observedAt.UtcTicks);
    }

    internal bool TryBeginHeartbeatProcessing(DateTimeOffset observedAt)
    {
        lock (sync)
        {
            if (heartbeatTimeoutClaimed || Volatile.Read(ref disposed) != 0)
                return false;

            lastHeartbeatUtcTicks = Math.Max(lastHeartbeatUtcTicks, observedAt.UtcTicks);
            if (heartbeatProcessingCount++ == 0)
                heartbeatProcessingStartedUtcTicks = observedAt.UtcTicks;
            return true;
        }
    }

    internal void CompleteHeartbeatProcessing(DateTimeOffset completedAt)
    {
        lock (sync)
        {
            if (heartbeatProcessingCount <= 0)
                throw new InvalidOperationException("No Worker heartbeat is being processed.");

            heartbeatProcessingCount--;
            lastHeartbeatUtcTicks = Math.Max(lastHeartbeatUtcTicks, completedAt.UtcTicks);
            if (heartbeatProcessingCount == 0)
                heartbeatProcessingStartedUtcTicks = 0;
        }
    }

    internal bool TryClaimHeartbeatTimeout(
        DateTimeOffset observedAt,
        TimeSpan leaseDuration,
        TimeSpan persistenceGrace)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(persistenceGrace, TimeSpan.Zero);
        lock (sync)
        {
            if (heartbeatTimeoutClaimed)
                return true;

            DateTimeOffset lastHeartbeat = new(lastHeartbeatUtcTicks, TimeSpan.Zero);
            if (observedAt - lastHeartbeat < leaseDuration)
                return false;

            if (heartbeatProcessingCount > 0)
            {
                DateTimeOffset processingStarted = new(heartbeatProcessingStartedUtcTicks, TimeSpan.Zero);
                if (observedAt - processingStarted < leaseDuration + persistenceGrace)
                    return false;
            }

            heartbeatTimeoutClaimed = true;
            return true;
        }
    }

    internal async Task CaptureProcessOutputAsync(StreamReader reader, string channel)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (sync)
                {
                    string safe = line;
                    foreach (string sensitiveValue in sensitiveLogValues.Where(value => !string.IsNullOrEmpty(value)))
                        safe = safe.Replace(sensitiveValue, "<redacted>", StringComparison.Ordinal);
                    if (processDiagnostics.Length > 2_000)
                        processDiagnostics.Remove(0, processDiagnostics.Length - 2_000);
                    processDiagnostics.Append(channel).Append(':').Append(safe).AppendLine();
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal async Task WaitForRegistrationAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await registration.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        WorkerBootstrapFile.DeleteIfOwned(bootstrapPath);
        LogLifecycle("worker_registered");
    }

    internal bool AcceptRegistrationToken(string token)
    {
        lock (sync)
        {
            if (!registered)
            {
                if (!FixedTimeEquals(bootstrapToken, token))
                    return false;
                bootstrapToken = string.Empty;
                registered = true;
                registration.TrySetResult(true);
                return true;
            }
            return false;
        }
    }

    internal void AttachConnection(ApiWorkerConnection value)
    {
        lock (sync)
        {
            connection?.Cancel();
            connection = value;
            connectionCount++;
        }
        LogLifecycle("worker_connection_attached", connectionCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal void DetachConnection(ApiWorkerConnection value)
    {
        lock (sync)
        {
            if (ReferenceEquals(connection, value))
            {
                connection = null;
                LogLifecycle("worker_connection_disconnected");
            }
        }
    }

    internal RealtimePublishResult AcceptDisplayBatch(DisplayBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        RealtimePublishResult result = OutputHub.PublishDisplayBatch(batch);
        if (!result.Accepted)
            return result;

        lock (sync)
        {
            lastDisplaySequence = Math.Max(lastDisplaySequence, result.SnapshotSequence);
        }
        return result;
    }

    internal RealtimePublishResult AcceptDisplayFrame(DisplayFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        RealtimePublishResult result = OutputHub.PublishDisplayFrame(frame);
        if (!result.Accepted)
            return result;

        lock (sync)
        {
            lastDisplaySequence = Math.Max(lastDisplaySequence, result.SnapshotSequence);
        }
        return result;
    }

    internal void RecordProtocolError(string reasonCode)
    {
        if (OutputHub.State is not SessionOutputHubState.Faulted and not SessionOutputHubState.Disposed)
            OutputHub.Complete(reasonCode);
        _ = PublishAsync(new WorkerEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = IpcProtocol.NewMessageId("protocol"),
            SessionId = Binding.SessionId,
            WorkerId = Binding.WorkerId,
            WorkerEpoch = Binding.WorkerEpoch,
            ControlPlaneInstanceId = ControlPlaneInstanceId,
            CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            RuntimeFailed = new RuntimeFailed
            {
                StableCode = reasonCode,
                Phase = "ipc",
                SafeMessage = "The Worker IPC message was rejected.",
                Fatal = true,
                LastOutputSequence = lastDisplaySequence,
            },
        });
    }

    internal Task PublishAsync(WorkerEnvelope value)
    {
        lock (sync)
        {
            if (Volatile.Read(ref disposed) != 0)
                return Task.CompletedTask;

            // DisplayBatch and DisplayFrame are consumed by SessionOutputHub and must never be
            // duplicated into the control-plane wait probe. All other events
            // are retained only while they fit both explicit budgets.
            if (value.PayloadCase is not WorkerEnvelope.PayloadOneofCase.DisplayBatch and not WorkerEnvelope.PayloadOneofCase.DisplayFrame)
            {
                WorkerEnvelope copy = value.Clone();
                int copyBytes = copy.CalculateSize();
                if (copyBytes <= options.PendingEventMaxBytes)
                {
                    while (pendingEvents.Count >= options.PendingEventMaxMessages ||
                           pendingEventBytes > options.PendingEventMaxBytes - copyBytes)
                    {
                        WorkerEnvelope removed = pendingEvents[0];
                        pendingEvents.RemoveAt(0);
                        pendingEventBytes -= removed.CalculateSize();
                        Interlocked.Increment(ref droppedPendingEventCount);
                    }

                    pendingEvents.Add(copy);
                    pendingEventBytes += copyBytes;
                }
                else
                {
                    Interlocked.Increment(ref droppedPendingEventCount);
                }
            }
            eventSignal.TrySetResult(true);
            if (value.PayloadCase == WorkerEnvelope.PayloadOneofCase.WorkerStopped)
                stopped.TrySetResult(value);
        }
        return Task.CompletedTask;
    }

    internal Task SendTerminalAcknowledgementAsync(WorkerEnvelope terminal, CancellationToken cancellationToken = default) =>
        SendRawAsync(new WorkerCommandEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = IpcProtocol.NewMessageId("terminal-ack"),
            CorrelationId = terminal.MessageId,
            SessionId = Binding.SessionId,
            WorkerId = Binding.WorkerId,
            WorkerEpoch = Binding.WorkerEpoch,
            ControlPlaneInstanceId = ControlPlaneInstanceId,
            CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            Stop = new StopWorker
            {
                ReasonCode = IpcReasonCodes.TerminalAck,
                DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.Add(ShutdownTimeout).ToUnixTimeMilliseconds(),
            },
        }, cancellationToken);

    internal async Task WriteCommandsAsync(IServerStreamWriter<WorkerCommandEnvelope> writer, CancellationToken cancellationToken)
    {
        while (await commands.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (commands.Reader.TryRead(out WorkerCommandEnvelope? command))
                await writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<bool> TryTerminateProcessAsync(
        TimeSpan timeout,
        string reason = "requested",
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (process is null)
            return true;

        try
        {
            if (!process.HasExited)
            {
                LogLifecycle("worker_process_kill_requested", reason, LogLevel.Warning);
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return process.HasExited;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    internal Task<bool> TerminateProcessAsync(
        TimeSpan timeout,
        string reason = "requested",
        CancellationToken cancellationToken = default) =>
        TryTerminateProcessAsync(timeout, reason, cancellationToken);

    internal void LogLifecycle(string eventName, string reason = "", LogLevel level = LogLevel.Information)
    {
        string sanitizedReason = SensitiveLogPolicy.SafeReasonCode(reason);
        Action<ILogger, string, string, string, string, ulong, string, Exception?> log = level switch
        {
            LogLevel.Warning => LifecycleWarningLog,
            LogLevel.Error or LogLevel.Critical => LifecycleErrorLog,
            _ => LifecycleInfoLog,
        };
        log(logger, eventName, ControlPlaneInstanceId, Binding.SessionId, Binding.WorkerId, Binding.WorkerEpoch, sanitizedReason, null);
    }

    private static readonly Action<ILogger, string, string, string, string, ulong, string, Exception?> LifecycleInfoLog =
        LoggerMessage.Define<string, string, string, string, ulong, string>(
            LogLevel.Information,
            new EventId(2102, "WorkerLifecycle"),
            "worker_event={WorkerEvent} controlPlaneInstanceId={ControlPlaneInstanceId} sessionId={SessionId} workerId={WorkerId} workerEpoch={WorkerEpoch} reason={Reason}");

    private static readonly Action<ILogger, string, string, string, string, ulong, string, Exception?> LifecycleWarningLog =
        LoggerMessage.Define<string, string, string, string, ulong, string>(
            LogLevel.Warning,
            new EventId(2103, "WorkerLifecycleWarning"),
            "worker_event={WorkerEvent} controlPlaneInstanceId={ControlPlaneInstanceId} sessionId={SessionId} workerId={WorkerId} workerEpoch={WorkerEpoch} reason={Reason}");

    private static readonly Action<ILogger, string, string, string, string, ulong, string, Exception?> LifecycleErrorLog =
        LoggerMessage.Define<string, string, string, string, ulong, string>(
            LogLevel.Error,
            new EventId(2104, "WorkerLifecycleError"),
            "worker_event={WorkerEvent} controlPlaneInstanceId={ControlPlaneInstanceId} sessionId={SessionId} workerId={WorkerId} workerEpoch={WorkerEpoch} reason={Reason}");

    internal async ValueTask DisposeResourcesAsync()
    {
        await DisposeCoreAsync(terminateProcess: false).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeCoreAsync(terminateProcess: true).ConfigureAwait(false);
    }

    private async ValueTask DisposeCoreAsync(bool terminateProcess)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        OutputHub.Complete("worker-disposed");
        lock (sync)
        {
            foreach (PendingInput pending in pendingInputs.Values)
                pending.Completion.TrySetResult(RealtimeSessionResults.WorkerUnavailable(pending.Command));
            pendingInputs.Clear();
            pendingInputBytes = 0;
            connection?.Cancel();
            connection = null;
            eventSignal.TrySetResult(true);
        }
        commands.Writer.TryComplete();
        if (terminateProcess)
        {
            bool exited = await TerminateProcessAsync(options.WorkerShutdownTimeout, "session-dispose", CancellationToken.None).ConfigureAwait(false);
            if (!exited)
                LogLifecycle("worker_exit_unconfirmed", SessionRuntimeResultCodes.WorkerExitUnconfirmed, LogLevel.Error);
        }
        await OutputHub.DisposeAsync().ConfigureAwait(false);
        WorkerBootstrapFile.DeleteIfOwned(bootstrapPath);
        LogLifecycle("worker_session_disposed");
    }

    private WorkerCommandEnvelope CreateEnvelope(string messageId, StartRuntime payload) => new()
    {
        ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
        MessageId = messageId,
        SessionId = Binding.SessionId,
        WorkerId = Binding.WorkerId,
        WorkerEpoch = Binding.WorkerEpoch,
        ControlPlaneInstanceId = ControlPlaneInstanceId,
        CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
        StartRuntime = payload,
    };

    private WorkerCommandEnvelope CreateEnvelope(string messageId, SubmitInput payload) => new()
    {
        ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
        MessageId = messageId,
        SessionId = Binding.SessionId,
        WorkerId = Binding.WorkerId,
        WorkerEpoch = Binding.WorkerEpoch,
        ControlPlaneInstanceId = ControlPlaneInstanceId,
        CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
        SubmitInput = payload,
    };

    private WorkerCommandEnvelope CreateEnvelope(string messageId, StopWorker payload) => new()
    {
        ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
        MessageId = messageId,
        SessionId = Binding.SessionId,
        WorkerId = Binding.WorkerId,
        WorkerEpoch = Binding.WorkerEpoch,
        ControlPlaneInstanceId = ControlPlaneInstanceId,
        CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
        Stop = payload,
    };

    private static TaskCompletionSource<bool> NewEventSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool FixedTimeEquals(string expected, string actual)
    {
        byte[] left = Encoding.UTF8.GetBytes(expected);
        byte[] right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

internal sealed class ApiWorkerProcessHandle(ApiWorkerSession session) : IWorkerProcessHandle
{
    public WorkerProcessIdentity Identity => session.ProcessIdentity;

    public async Task<WorkerReadyInfo> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await session.WaitForRegistrationAsync(timeout, cancellationToken).ConfigureAwait(false);
        await session.SendStartRuntimeAsync(timeout, cancellationToken).ConfigureAwait(false);
        WorkerEnvelope ready = await session.WaitForAsync(
            value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready,
            timeout,
            cancellationToken).ConfigureAwait(false);
        RuntimeSaveLayout saveLayout = ready.Ready.SaveLayout == SaveLayout.Root
            ? RuntimeSaveLayout.Root
            : RuntimeSaveLayout.SavDirectory;
        WorkerReadyInfo result = new(
            ready.Ready.RuntimeIntegrationVersion,
            ready.Ready.UpstreamCommit,
            (int)saveLayout,
            ready.Ready.LastOutputSequence,
            ready.Ready.CompatibilityProfile,
            ready.Ready.SessionRootManifestDigest);
        session.MarkReadyConfirmed();
        return result;
    }

    public async Task<WorkerExitInfo> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        int exitCode = await session.WaitForExitAsync(timeout, cancellationToken).ConfigureAwait(false);
        return new WorkerExitInfo(
            exitCode,
            true,
            exitCode == 0,
            exitCode == 0 ? "worker_finished" : "worker_exit",
            session.LastOutputSequence,
            DateTimeOffset.UtcNow);
    }

    public Task RequestStopAsync(string reasonCode, DateTimeOffset deadline, CancellationToken cancellationToken = default)
    {
        TimeSpan timeout = deadline - DateTimeOffset.UtcNow;
        if (timeout <= TimeSpan.Zero)
            timeout = TimeSpan.FromMilliseconds(1);
        return session.StopAsync(string.IsNullOrWhiteSpace(reasonCode) ? "requested" : reasonCode, timeout, cancellationToken);
    }

    public Task KillAsync(CancellationToken cancellationToken = default)
    {
        return KillBoundedAsync(cancellationToken);
    }

    public void UpdateRuntimeBinding(SessionRuntimeBinding binding, bool persistenceReady = false) =>
        session.UpdateRuntimeBinding(binding, persistenceReady);

    private async Task KillBoundedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await session.TerminateProcessAsync(session.ShutdownTimeout, "runtime-coordinator", cancellationToken).ConfigureAwait(false))
            throw new SessionRuntimeException(SessionRuntimeResultCodes.WorkerExitUnconfirmed, "The Worker exit could not be confirmed.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class ApiWorkerConnection : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    internal ApiWorkerConnection(ApiWorkerSession session) => Session = session;
    public ApiWorkerSession Session { get; }
    public CancellationToken CancellationToken => cancellation.Token;
    public void Cancel()
        => cancellation.Cancel();
    public void Dispose() => cancellation.Dispose();
}

internal sealed class WorkerControlGrpcService(WorkerManager manager) : WorkerControl.WorkerControlBase
{
    public override async Task Connect(
        IAsyncStreamReader<WorkerEnvelope> requestStream,
        IServerStreamWriter<WorkerCommandEnvelope> responseStream,
        ServerCallContext context)
    {
        var connectionInfo = context.GetHttpContext().Connection;
        if (connectionInfo.LocalIpAddress is not null || connectionInfo.LocalPort > 0)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Worker control is UDS-only."));
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            return;

        WorkerEnvelope registration = requestStream.Current;
        RegistrationDecision decision = manager.Register(registration);
        var response = new WorkerCommandEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = IpcProtocol.NewMessageId("registration"),
            CorrelationId = registration.MessageId,
            SessionId = registration.SessionId,
            WorkerId = registration.WorkerId,
            WorkerEpoch = registration.WorkerEpoch,
            ControlPlaneInstanceId = manager.ControlPlaneInstanceId,
            CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            RegistrationResult = new RegistrationResult
            {
                Accepted = decision.Accepted,
                ReasonCode = decision.ReasonCode,
                NegotiatedProtocolVersion = decision.Accepted ? StructuredIpcProtocol.CurrentVersion : 0,
                RuntimeIntegrationVersion = RuntimeBaseline.CloudEmueraIntegrationVersion,
                UpstreamCommit = RuntimeBaseline.UpstreamCommit,
                ControlPlaneInstanceId = manager.ControlPlaneInstanceId,
                CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            },
        };
        await responseStream.WriteAsync(response).ConfigureAwait(false);
        if (!decision.Accepted || decision.Connection is null || decision.Session is null)
            return;

        ApiWorkerConnection connection = decision.Connection;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, connection.CancellationToken);
        Task writer = decision.Session.WriteCommandsAsync(responseStream, lifetime.Token);
        try
        {
            while (true)
            {
                if (!await requestStream.MoveNext(lifetime.Token).ConfigureAwait(false))
                    break;

                await manager.ReceiveAsync(connection, requestStream.Current).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (IOException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            connection.Cancel();
            WorkerManager.Disconnect(connection);
            try { await writer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            connection.Dispose();
        }
    }
}

internal sealed class WorkerSocketLifecycle(WorkerManagerOptions options) : IDisposable
{
    public string SocketPath => options.ControlSocketPath;
    private bool sealedSocket;

    public void Prepare() => UnixSocketSecurity.PreparePrivateTree(options);

    public void SealSocket()
    {
        // WebApplicationFactory/TestServer does not create a Kestrel socket;
        // the production Kestrel listener always does.
        if (!File.Exists(options.ControlSocketPath))
            return;
        UnixSocketSecurity.SealSocket(options.ControlSocketPath);
        sealedSocket = true;
    }

    public void Dispose()
    {
        if (sealedSocket)
            UnixSocketSecurity.RemoveOwnedSocket(options.ControlSocketPath);
    }
}

public sealed class WorkerRuntimeReadiness
{
    private readonly ConcurrentDictionary<string, byte> unconfirmedFences = new(StringComparer.Ordinal);
    private int ready;
    private string reason = "worker_reconciliation_pending";

    public bool IsReady => Volatile.Read(ref ready) != 0;
    public string Reason => Volatile.Read(ref reason) ?? "worker_reconciliation_pending";

    public IReadOnlySet<string> WriteFenceUnconfirmedSessionIds => unconfirmedFences.Keys.ToHashSet(StringComparer.Ordinal);

    public void MarkWriteFenceUnconfirmed(string sessionId) =>
        unconfirmedFences.TryAdd(sessionId, 0);

    public void ClearWriteFenceUnconfirmed(string sessionId) =>
        unconfirmedFences.TryRemove(sessionId, out _);

    public void MarkReady()
    {
        Volatile.Write(ref reason, "ready");
        Volatile.Write(ref ready, 1);
    }

    public void MarkFailed(string failureReason)
    {
        Volatile.Write(ref ready, 0);
        Volatile.Write(ref reason, string.IsNullOrWhiteSpace(failureReason) ? "worker_reconciliation_failed" : failureReason);
    }
}

public sealed class WorkerRuntimeHealthCheck(WorkerRuntimeReadiness readiness) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(readiness.IsReady
            ? HealthCheckResult.Healthy("READY")
            : HealthCheckResult.Unhealthy(readiness.Reason));
}

internal sealed class WorkerManagerHostedService(
    WorkerManager manager,
    WorkerSocketLifecycle socketLifecycle,
    ISessionRuntimeStore runtimeStore,
    SessionRuntimeCoordinator coordinator,
    WorkerManagerOptions managerOptions,
    WorkerRuntimeReadiness readiness,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime,
    ILogger<WorkerManagerHostedService> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource monitorCancellation = new();
    private readonly ConcurrentDictionary<string, Task> workerMonitors = new(StringComparer.Ordinal);
    private Task? monitorTask;
    private int stopped;
    private CancellationTokenRegistration applicationStoppingRegistration;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        applicationStoppingRegistration = applicationLifetime.ApplicationStopping.Register(BeginDrainingImmediately);
        try
        {
            socketLifecycle.SealSocket();
            IReadOnlyList<PersistedWorkerLease> leases = await runtimeStore.ListPersistedLeasesAsync(cancellationToken).ConfigureAwait(false);
            foreach (PersistedWorkerLease lease in leases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (lease.ProcessIdentity is null)
                {
                    // A legacy lease without an exact process identity is
                    // intentionally left in place. It fences only this
                    // Session's open/save mutations; it must not make the
                    // entire trusted instance fail readiness.
                    AmbiguousLeaseLog(logger, lease.Binding.SessionId, lease.Binding.WorkerId, "process_identity_missing", null);
                    readiness.MarkWriteFenceUnconfirmed(lease.Binding.SessionId);
                    continue;
                }

                if (!await ProcessIdentityProbe.TerminateExactAsync(
                    lease.ProcessIdentity,
                    TimeSpan.FromSeconds(5),
                    cancellationToken).ConfigureAwait(false))
                {
                    AmbiguousLeaseLog(logger, lease.Binding.SessionId, lease.Binding.WorkerId, "exact_process_exit_unconfirmed", null);
                    readiness.MarkWriteFenceUnconfirmed(lease.Binding.SessionId);
                    continue;
                }

                if (!await runtimeStore.ReconcileAsync(lease, "control_plane_restarted", DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false))
                {
                    AmbiguousLeaseLog(logger, lease.Binding.SessionId, lease.Binding.WorkerId, "reconcile_not_applied", null);
                    readiness.MarkWriteFenceUnconfirmed(lease.Binding.SessionId);
                }
                else
                    readiness.ClearWriteFenceUnconfirmed(lease.Binding.SessionId);
            }

            readiness.MarkReady();
            monitorTask = MonitorWorkersAsync(monitorCancellation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            readiness.MarkFailed("worker_reconciliation_cancelled");
            coordinator.BeginDraining();
        }
        catch (Exception)
        {
            ReconciliationFailedLog(logger, SessionRuntimeResultCodes.ControlPlaneReconciliationFailed, null);
            readiness.MarkFailed(SessionRuntimeResultCodes.ControlPlaneReconciliationFailed);
            coordinator.BeginDraining();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
            return;

        coordinator.BeginDraining();
        manager.BeginDraining();
        monitorCancellation.Cancel();
        if (monitorTask is not null)
        {
            try
            {
                await monitorTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
            }
        }
        ApiWorkerSession[] workers = manager.Workers.ToArray();
        await manager.ShutdownAsync("control_plane_stopped").ConfigureAwait(false);
        using var persistenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        persistenceCancellation.CancelAfter(WorkerShutdownDefaults.PersistenceTimeout);
        await Task.WhenAll(workers.Select(worker => PersistShutdownStateAsync(worker, persistenceCancellation.Token))).ConfigureAwait(false);
        await manager.DisposeAsync().ConfigureAwait(false);
        readiness.MarkFailed("control_plane_stopped");
        socketLifecycle.Dispose();
    }

    private void BeginDrainingImmediately()
    {
        // ApplicationStopping is intentionally a synchronous, no-I/O barrier.
        // Host shutdown invokes hosted services in reverse registration order;
        // this callback makes all control-plane gates fail closed first.
        coordinator.BeginDraining();
        manager.BeginDraining();
        readiness.MarkFailed("control_plane_stopping");
    }

    private async Task PersistShutdownStateAsync(ApiWorkerSession worker, CancellationToken cancellationToken)
    {
        if (!worker.HasExited)
        {
            readiness.MarkFailed(SessionRuntimeResultCodes.WorkerExitUnconfirmed);
            worker.LogLifecycle("shutdown_exit_unconfirmed", SessionRuntimeResultCodes.WorkerExitUnconfirmed, LogLevel.Error);
            return;
        }

        try
        {
            await runtimeStore.CompleteAsync(
                worker.RuntimeBinding,
                SessionRuntimeTerminalState.Crashed,
                "control_plane_stopped",
                worker.LastOutputSequence,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ShutdownPersistFailedLog(logger, worker.Binding.SessionId, exception);
        }
    }

    private async Task MonitorWorkersAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (ApiWorkerSession worker in manager.Workers)
                {
                    _ = workerMonitors.GetOrAdd(worker.Binding.WorkerId, _ =>
                    {
                        Task monitor = ObserveWorkerAsync(worker, cancellationToken);
                        return monitor;
                    });
                }

                foreach ((string workerId, Task task) in workerMonitors)
                {
                    if (task.IsCompleted)
                        workerMonitors.TryRemove(workerId, out _);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ObserveWorkerAsync(ApiWorkerSession worker, CancellationToken cancellationToken)
    {
        using var observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<int> exitTask = worker.WaitForExitAsync(Timeout.InfiniteTimeSpan, observationCancellation.Token);
        Task heartbeatTimeoutTask = WaitForHeartbeatTimeoutAsync(worker, observationCancellation.Token);
        try
        {
            Task completed = await Task.WhenAny(exitTask, heartbeatTimeoutTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                return;

            if (completed == heartbeatTimeoutTask && !exitTask.IsCompleted)
            {
                worker.LogLifecycle("heartbeat_timeout", "lease_expired", LogLevel.Warning);
                if (!await worker.TryTerminateProcessAsync(managerOptions.WorkerShutdownTimeout, "heartbeat-timeout", CancellationToken.None).ConfigureAwait(false))
                {
                    worker.LogLifecycle("heartbeat_termination_unconfirmed", SessionRuntimeResultCodes.WorkerExitUnconfirmed, LogLevel.Error);
                    return;
                }

                await runtimeStore.CompleteAsync(
                    worker.RuntimeBinding,
                    SessionRuntimeTerminalState.Crashed,
                    "heartbeat_timeout",
                    worker.LastOutputSequence,
                    timeProvider.GetUtcNow(),
                    CancellationToken.None).ConfigureAwait(false);
                manager.RemoveWorker(worker, "heartbeat-timeout");
                return;
            }

            int exitCode = await exitTask.ConfigureAwait(false);
            worker.LogLifecycle(
                "worker_process_exit_observed",
                $"exit={exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
                $"ready={worker.ReadyConfirmed};graceful={worker.GracefulTerminationObserved};diagnostics_present={worker.ProcessDiagnostics.Length > 0}",
                exitCode == 0 ? LogLevel.Information : LogLevel.Warning);

            // A zero exit code is not sufficient: when the UDS control stream
            // disappears the Worker deliberately shuts itself down with the
            // same process code, but the durable Session must be fenced as
            // CRASHED until the user explicitly reopens it. Only a terminal
            // Worker envelope observed by this API proves graceful completion.
            bool controlPlaneStopping = manager.IsDraining;
            bool graceful = !controlPlaneStopping && !worker.ForceStopRequested && worker.ReadyConfirmed && exitCode == 0 && worker.GracefulTerminationObserved;
            string reasonCode = controlPlaneStopping
                ? "control_plane_stopped"
                : worker.ForceStopRequested ? "admin_force_stopped" : graceful ? "runtime_completed" : "worker_exit";
            await runtimeStore.CompleteAsync(
                worker.RuntimeBinding,
                graceful ? SessionRuntimeTerminalState.Closed : SessionRuntimeTerminalState.Crashed,
                reasonCode,
                worker.LastOutputSequence,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            manager.RemoveWorker(worker, graceful ? "runtime-completed" : "worker-exited");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ReconciliationFailedLog(logger, SessionRuntimeResultCodes.ControlPlaneReconciliationFailed, null);
        }
        finally
        {
            observationCancellation.Cancel();
            try { await heartbeatTimeoutTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (observationCancellation.IsCancellationRequested) { }
        }
    }

    private async Task WaitForHeartbeatTimeoutAsync(ApiWorkerSession worker, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(managerOptions.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            if (!worker.ReadyConfirmed)
                continue;

            if (worker.TryClaimHeartbeatTimeout(
                timeProvider.GetUtcNow(),
                managerOptions.LeaseDuration,
                managerOptions.WorkerShutdownTimeout))
                return;
        }
    }

    public void Dispose()
    {
        applicationStoppingRegistration.Dispose();
        monitorCancellation.Dispose();
    }

    private static readonly Action<ILogger, string, Exception?> ReconciliationFailedLog =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2105, "WorkerReconciliationFailed"),
            "worker_event=reconciliation_failed reason={Reason}");

    private static readonly Action<ILogger, string, Exception?> ShutdownPersistFailedLog =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2106, "WorkerShutdownPersistFailed"),
            "worker_event=shutdown_persist_failed sessionId={SessionId}");

    private static readonly Action<ILogger, string, string, string, Exception?> AmbiguousLeaseLog =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2107, "WorkerAmbiguousLease"),
            "worker_event=ambiguous_lease sessionId={SessionId} workerId={WorkerId} reason={Reason}");
}
