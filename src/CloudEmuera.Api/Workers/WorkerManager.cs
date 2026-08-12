using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Ipc;
using CloudEmuera.Ipc.V2;
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
            grpcOptions.MaxReceiveMessageSize = IpcLimits.MaxEnvelopeBytes;
            grpcOptions.MaxSendMessageSize = IpcLimits.MaxEnvelopeBytes;
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

public sealed class WorkerManager : IAsyncDisposable, ISessionWorkerControl, ICurrentWorkerRouter
{
    private readonly WorkerManagerOptions options;
    private readonly ILoggerFactory loggerFactory;
    private readonly ISessionRuntimeStore? runtimeStore;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly object sync = new();
    private readonly Dictionary<string, ApiWorkerSession> sessions = new(StringComparer.Ordinal);
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

    public IReadOnlyCollection<ApiWorkerSession> Workers
    {
        get
        {
            lock (sync)
                return sessions.Values.ToArray();
        }
    }

    public void BeginDraining() => Interlocked.Exchange(ref draining, 1);

    internal void RemoveWorker(ApiWorkerSession worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        lock (sync)
        {
            if (sessions.TryGetValue(worker.Binding.WorkerId, out ApiWorkerSession? current) && ReferenceEquals(current, worker))
                sessions.Remove(worker.Binding.WorkerId);
        }
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
                spec.Binding.InitialOutputSequence),
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
            if (sessions.Count >= options.MaxConcurrentWorkers)
                throw new SessionRuntimeException(SessionRuntimeResultCodes.WorkerLimitExceeded, "The API Worker limit has been reached.");
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
            DisconnectGracePeriodMilliseconds = checked((int)options.DisconnectGracePeriod.TotalMilliseconds),
            SaveLayout = (int)request.SaveLayout,
            SessionRootManifestDigest = request.SessionRootManifestDigest,
            InitialOutputSequence = request.InitialOutputSequence,
        };
        session.SetBootstrapToken(bootstrap.BootstrapToken);
        WorkerBootstrapDocument bootstrapToWrite = options.BootstrapTransformForTest?.Invoke(bootstrap) ?? bootstrap;
        WorkerBootstrapFile.Write(bootstrapPath, bootstrapToWrite);

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
            bool exited = await session.TerminateProcessAsync(options.WorkerShutdownTimeout, CancellationToken.None).ConfigureAwait(false);
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
        IpcValidationResult validation = IpcValidator.ValidateWorkerEnvelope(
            registration,
            registered,
            registered ? session!.Binding : null,
            options.ControlPlaneInstanceId);
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
        IpcValidationResult validation = IpcValidator.ValidateWorkerEnvelope(
            message,
            registered: true,
            connection.Session.Binding,
            connection.Session.ControlPlaneInstanceId);
        if (!validation.IsValid)
        {
            connection.Session.RecordProtocolError(validation.ReasonCode);
            connection.Cancel();
            return;
        }
        if (message.PayloadCase == WorkerEnvelope.PayloadOneofCase.DisplayBatch && !connection.Session.AcceptDisplayBatch(message.DisplayBatch))
            return;
        if (message.PayloadCase == WorkerEnvelope.PayloadOneofCase.Heartbeat && runtimeStore is not null)
        {
            try
            {
                if (connection.Session.RuntimePersistenceReady)
                {
                    SessionRuntimeWriteResult heartbeatResult = await runtimeStore.RecordHeartbeatAsync(
                        connection.Session.RuntimeBinding,
                        new WorkerHeartbeatInfo(
                            connection.Session.ProcessIdentity,
                            message.Heartbeat.OutputSequence,
                            message.Heartbeat.WaitingForInput,
                            string.IsNullOrEmpty(message.Heartbeat.CurrentPromptId) ? null : message.Heartbeat.CurrentPromptId,
                            message.Heartbeat.ResidentMemoryBytes,
                            timeProvider.GetUtcNow()),
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
            catch (Exception exception) when (exception is ArgumentException or DbUpdateException or SqliteException or IOException or UnauthorizedAccessException)
            {
                connection.Session.LogLifecycle("heartbeat_persist_failed", "database_unavailable", LogLevel.Error);
                connection.Cancel();
                return;
            }
        }
        if (message.PayloadCase == WorkerEnvelope.PayloadOneofCase.Heartbeat)
            connection.Session.MarkHeartbeatReceived(timeProvider.GetUtcNow());
        await connection.Session.PublishAsync(message).ConfigureAwait(false);
    }

    public static void Disconnect(ApiWorkerConnection connection) => connection.Session.DetachConnection(connection);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        BeginDraining();
        ApiWorkerSession[] workers = Workers.ToArray();
        foreach (ApiWorkerSession worker in workers)
        {
            try
            {
                await worker.StopAsync(options.WorkerShutdownTimeout).ConfigureAwait(false);
            }
            catch
            {
                await worker.TerminateProcessAsync(options.WorkerShutdownTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            await worker.DisposeAsync().ConfigureAwait(false);
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
        startInfo.ArgumentList.Add(options.WorkerAssemblyPath);
        startInfo.ArgumentList.Add("--bootstrap-file");
        startInfo.ArgumentList.Add(bootstrapPath);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("The Worker process could not be started.");
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
    private readonly List<DisplayBatch> displayBatches = [];
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
    private bool tokenConsumed;
    private bool registered;
    private DateTimeOffset reconnectUntil;
    private long lastHeartbeatUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
    private long lastDisplaySequence;
    private int connectionCount;
    private int disposed;
    private int runtimePersistenceReady;

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
    public IReadOnlyList<DisplayBatch> DisplayBatches { get { lock (sync) return displayBatches.Select(item => item.Clone()).ToArray(); } }
    public long LastOutputSequence { get { lock (sync) return lastDisplaySequence; } }
    public DateTimeOffset LastHeartbeatAt => new(Interlocked.Read(ref lastHeartbeatUtcTicks), TimeSpan.Zero);
    internal bool RuntimePersistenceReady => Volatile.Read(ref runtimePersistenceReady) != 0;

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
            throw new TimeoutException($"The Worker did not produce the expected IPC event. exited={HasExited}; diagnostics={ProcessDiagnostics}");
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
            DeadlineUnixMilliseconds = deadline.ToUnixTimeMilliseconds(),
        }), cancellationToken).ConfigureAwait(false);
    }

    public async Task SendInputAsync(string promptId, string clientMessageId, string value, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (!string.IsNullOrEmpty(value) && !sensitiveLogValues.Contains(value, StringComparer.Ordinal))
            {
                if (sensitiveLogValues.Count >= 128)
                    sensitiveLogValues.RemoveAt(2);
                sensitiveLogValues.Add(value);
            }
        }
        await commands.Writer.WriteAsync(CreateEnvelope(IpcProtocol.NewMessageId("input"), new SubmitInput
        {
            PromptId = promptId,
            ClientMessageId = clientMessageId,
            Value = value,
            DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds(),
        }), cancellationToken).ConfigureAwait(false);
    }

    public Task SendRawAsync(WorkerCommandEnvelope envelope, CancellationToken cancellationToken = default) =>
        commands.Writer.WriteAsync(envelope, cancellationToken).AsTask();

    public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        StopAsync("requested", timeout, cancellationToken);

    internal async Task StopAsync(string reasonCode, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        using var stopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stopCancellation.CancelAfter(timeout);
        await commands.Writer.WriteAsync(CreateEnvelope(IpcProtocol.NewMessageId("stop"), new StopWorker
        {
            ReasonCode = reasonCode,
            DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.Add(timeout).ToUnixTimeMilliseconds(),
        }), stopCancellation.Token).ConfigureAwait(false);
        await stopped.Task.WaitAsync(timeout, stopCancellation.Token).ConfigureAwait(false);
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
            processExit.TrySetResult(value.ExitCode);
            if (!IsRegistered)
                registration.TrySetException(new InvalidOperationException("The Worker exited before registration."));
            LogLifecycle("worker_process_exited", value.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                    LastOutputSequence,
                    "{}");
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

    internal void MarkHeartbeatReceived(DateTimeOffset observedAt) =>
        Interlocked.Exchange(ref lastHeartbeatUtcTicks, observedAt.UtcTicks);

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
                tokenConsumed = true;
                bootstrapToken = string.Empty;
                registered = true;
                registration.TrySetResult(true);
                return true;
            }
            if (!tokenConsumed || !string.IsNullOrEmpty(token) || DateTimeOffset.UtcNow > reconnectUntil)
                return false;
            return true;
        }
    }

    internal void AttachConnection(ApiWorkerConnection value)
    {
        lock (sync)
        {
            connection?.Cancel();
            connection = value;
            connectionCount++;
            reconnectUntil = DateTimeOffset.UtcNow.Add(options.DisconnectGracePeriod);
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
                reconnectUntil = DateTimeOffset.UtcNow.Add(options.DisconnectGracePeriod);
                LogLifecycle("worker_connection_disconnected");
            }
        }
    }

    internal bool AcceptDisplayBatch(DisplayBatch batch)
    {
        lock (sync)
        {
            if (batch.LastSequence <= lastDisplaySequence)
                return false;
            if (!batch.IsSnapshot && batch.FirstSequence != lastDisplaySequence + 1)
            {
                RecordProtocolError(IpcReasonCodes.OutputResumeGap);
                return false;
            }
            lastDisplaySequence = batch.LastSequence;
            displayBatches.Add(batch.Clone());
            return true;
        }
    }

    internal void RecordProtocolError(string reasonCode) =>
        _ = PublishAsync(new WorkerEnvelope
        {
            ProtocolVersion = IpcProtocol.CurrentVersion,
            MessageId = IpcProtocol.NewMessageId("protocol"),
            SessionId = Binding.SessionId,
            WorkerId = Binding.WorkerId,
            WorkerEpoch = Binding.WorkerEpoch,
            ControlPlaneInstanceId = ControlPlaneInstanceId,
            RuntimeFailed = new RuntimeFailed
            {
                StableCode = reasonCode,
                Phase = "ipc",
                SafeMessage = "The Worker IPC message was rejected.",
                Fatal = true,
                LastOutputSequence = lastDisplaySequence,
            },
        });

    internal Task PublishAsync(WorkerEnvelope value)
    {
        lock (sync)
        {
            if (Volatile.Read(ref disposed) != 0)
                return Task.CompletedTask;
            pendingEvents.Add(value.Clone());
            if (pendingEvents.Count > 4096)
                pendingEvents.RemoveRange(0, pendingEvents.Count - 4096);
            eventSignal.TrySetResult(true);
            if (value.PayloadCase == WorkerEnvelope.PayloadOneofCase.WorkerStopped)
                stopped.TrySetResult(value);
        }
        return Task.CompletedTask;
    }

    internal async Task WriteCommandsAsync(IServerStreamWriter<WorkerCommandEnvelope> writer, CancellationToken cancellationToken)
    {
        while (await commands.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (commands.Reader.TryRead(out WorkerCommandEnvelope? command))
                await writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<bool> TryTerminateProcessAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (process is null)
            return true;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
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
    }

    internal Task<bool> TerminateProcessAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        TryTerminateProcessAsync(timeout, cancellationToken);

    internal void LogLifecycle(string eventName, string reason = "", LogLevel level = LogLevel.Information)
    {
        Action<ILogger, string, string, string, ulong, string, Exception?> log = level switch
        {
            LogLevel.Warning => LifecycleWarningLog,
            LogLevel.Error or LogLevel.Critical => LifecycleErrorLog,
            _ => LifecycleInfoLog,
        };
        log(logger, eventName, ControlPlaneInstanceId, Binding.SessionId, Binding.WorkerEpoch, reason, null);
    }

    private static readonly Action<ILogger, string, string, string, ulong, string, Exception?> LifecycleInfoLog =
        LoggerMessage.Define<string, string, string, ulong, string>(
            LogLevel.Information,
            new EventId(2102, "WorkerLifecycle"),
            "worker_event={WorkerEvent} controlPlaneInstanceId={ControlPlaneInstanceId} sessionId={SessionId} workerEpoch={WorkerEpoch} reason={Reason}");

    private static readonly Action<ILogger, string, string, string, ulong, string, Exception?> LifecycleWarningLog =
        LoggerMessage.Define<string, string, string, ulong, string>(
            LogLevel.Warning,
            new EventId(2103, "WorkerLifecycleWarning"),
            "worker_event={WorkerEvent} controlPlaneInstanceId={ControlPlaneInstanceId} sessionId={SessionId} workerEpoch={WorkerEpoch} reason={Reason}");

    private static readonly Action<ILogger, string, string, string, ulong, string, Exception?> LifecycleErrorLog =
        LoggerMessage.Define<string, string, string, ulong, string>(
            LogLevel.Error,
            new EventId(2104, "WorkerLifecycleError"),
            "worker_event={WorkerEvent} controlPlaneInstanceId={ControlPlaneInstanceId} sessionId={SessionId} workerEpoch={WorkerEpoch} reason={Reason}");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        lock (sync)
        {
            connection?.Cancel();
            connection = null;
            eventSignal.TrySetResult(true);
        }
        commands.Writer.TryComplete();
        bool exited = await TerminateProcessAsync(options.WorkerShutdownTimeout, CancellationToken.None).ConfigureAwait(false);
        if (!exited)
            LogLifecycle("worker_exit_unconfirmed", SessionRuntimeResultCodes.WorkerExitUnconfirmed, LogLevel.Error);
        WorkerBootstrapFile.DeleteIfOwned(bootstrapPath);
        LogLifecycle("worker_session_disposed");
    }

    private WorkerCommandEnvelope CreateEnvelope(string messageId, StartRuntime payload) => new()
    {
        ProtocolVersion = IpcProtocol.CurrentVersion,
        MessageId = messageId,
        SessionId = Binding.SessionId,
        WorkerId = Binding.WorkerId,
        WorkerEpoch = Binding.WorkerEpoch,
        ControlPlaneInstanceId = ControlPlaneInstanceId,
        StartRuntime = payload,
    };

    private WorkerCommandEnvelope CreateEnvelope(string messageId, SubmitInput payload) => new()
    {
        ProtocolVersion = IpcProtocol.CurrentVersion,
        MessageId = messageId,
        SessionId = Binding.SessionId,
        WorkerId = Binding.WorkerId,
        WorkerEpoch = Binding.WorkerEpoch,
        ControlPlaneInstanceId = ControlPlaneInstanceId,
        SubmitInput = payload,
    };

    private WorkerCommandEnvelope CreateEnvelope(string messageId, StopWorker payload) => new()
    {
        ProtocolVersion = IpcProtocol.CurrentVersion,
        MessageId = messageId,
        SessionId = Binding.SessionId,
        WorkerId = Binding.WorkerId,
        WorkerEpoch = Binding.WorkerEpoch,
        ControlPlaneInstanceId = ControlPlaneInstanceId,
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
        if (!await session.TerminateProcessAsync(session.ShutdownTimeout, cancellationToken).ConfigureAwait(false))
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
    public void Cancel() => cancellation.Cancel();
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
            ProtocolVersion = IpcProtocol.CurrentVersion,
            MessageId = IpcProtocol.NewMessageId("registration"),
            CorrelationId = registration.MessageId,
            SessionId = registration.SessionId,
            WorkerId = registration.WorkerId,
            WorkerEpoch = registration.WorkerEpoch,
            ControlPlaneInstanceId = manager.ControlPlaneInstanceId,
            RegistrationResult = new RegistrationResult
            {
                Accepted = decision.Accepted,
                ReasonCode = decision.ReasonCode,
                NegotiatedProtocolVersion = decision.Accepted ? IpcProtocol.CurrentVersion : 0,
                RuntimeIntegrationVersion = RuntimeBaseline.CloudEmueraIntegrationVersion,
                UpstreamCommit = RuntimeBaseline.UpstreamCommit,
                ControlPlaneInstanceId = manager.ControlPlaneInstanceId,
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
            while (await requestStream.MoveNext(lifetime.Token).ConfigureAwait(false))
                await manager.ReceiveAsync(connection, requestStream.Current).ConfigureAwait(false);
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
    private int ready;
    private string reason = "worker_reconciliation_pending";

    public bool IsReady => Volatile.Read(ref ready) != 0;
    public string Reason => Volatile.Read(ref reason) ?? "worker_reconciliation_pending";

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
    ILogger<WorkerManagerHostedService> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource monitorCancellation = new();
    private readonly ConcurrentDictionary<string, Task> workerMonitors = new(StringComparer.Ordinal);
    private Task? monitorTask;
    private int stopped;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            socketLifecycle.SealSocket();
            IReadOnlyList<PersistedWorkerLease> leases = await runtimeStore.ListPersistedLeasesAsync(cancellationToken).ConfigureAwait(false);
            foreach (PersistedWorkerLease lease in leases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (lease.ProcessIdentity is null)
                {
                    if (!string.Equals(lease.Status, WorkerLeaseStatus.Starting.ToString().ToUpperInvariant(), StringComparison.Ordinal))
                    {
                        readiness.MarkFailed(SessionRuntimeResultCodes.WorkerExitUnconfirmed);
                        coordinator.BeginDraining();
                        return;
                    }

                    if (!await runtimeStore.ReconcileAsync(lease, "control_plane_restarted", timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
                    {
                        readiness.MarkFailed(SessionRuntimeResultCodes.ControlPlaneReconciliationFailed);
                        coordinator.BeginDraining();
                        return;
                    }

                    continue;
                }

                if (!await ProcessIdentityProbe.TerminateExactAsync(lease.ProcessIdentity, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
                {
                    readiness.MarkFailed(SessionRuntimeResultCodes.WorkerExitUnconfirmed);
                    coordinator.BeginDraining();
                    return;
                }

                if (!await runtimeStore.ReconcileAsync(lease, "control_plane_restarted", DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false))
                {
                    readiness.MarkFailed(SessionRuntimeResultCodes.ControlPlaneReconciliationFailed);
                    coordinator.BeginDraining();
                    return;
                }
            }

            readiness.MarkReady();
            monitorTask = MonitorWorkersAsync(monitorCancellation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            readiness.MarkFailed("worker_reconciliation_cancelled");
            coordinator.BeginDraining();
        }
        catch (Exception exception)
        {
            ReconciliationFailedLog(logger, SessionRuntimeResultCodes.ControlPlaneReconciliationFailed, exception);
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
        await manager.DisposeAsync().ConfigureAwait(false);
        foreach (ApiWorkerSession worker in workers)
        {
            if (!worker.HasExited)
            {
                readiness.MarkFailed(SessionRuntimeResultCodes.WorkerExitUnconfirmed);
                worker.LogLifecycle("shutdown_exit_unconfirmed", SessionRuntimeResultCodes.WorkerExitUnconfirmed, LogLevel.Error);
                continue;
            }

            try
            {
                await runtimeStore.CompleteAsync(
                    worker.RuntimeBinding,
                    SessionRuntimeTerminalState.Crashed,
                    "control_plane_stopped",
                    worker.LastOutputSequence,
                    timeProvider.GetUtcNow(),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ShutdownPersistFailedLog(logger, worker.Binding.SessionId, exception);
            }
        }
        readiness.MarkFailed("control_plane_stopped");
        socketLifecycle.Dispose();
        monitorCancellation.Dispose();
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
                if (!await worker.TryTerminateProcessAsync(managerOptions.WorkerShutdownTimeout, CancellationToken.None).ConfigureAwait(false))
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
                manager.RemoveWorker(worker);
                return;
            }

            int exitCode = await exitTask.ConfigureAwait(false);

            bool graceful = worker.ReadyConfirmed && exitCode == 0;
            await runtimeStore.CompleteAsync(
                worker.RuntimeBinding,
                graceful ? SessionRuntimeTerminalState.Closed : SessionRuntimeTerminalState.Crashed,
                graceful ? "runtime_completed" : "worker_exit",
                worker.LastOutputSequence,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            manager.RemoveWorker(worker);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReconciliationFailedLog(logger, SessionRuntimeResultCodes.ControlPlaneReconciliationFailed, exception);
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

            if (timeProvider.GetUtcNow() - worker.LastHeartbeatAt >= managerOptions.LeaseDuration)
                return;
        }
    }

    public void Dispose() => monitorCancellation.Dispose();

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
}
