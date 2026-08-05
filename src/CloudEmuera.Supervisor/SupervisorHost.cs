using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CloudEmuera.Ipc;
using CloudEmuera.Ipc.V1;
using CloudEmuera.RuntimeAdapter;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudEmuera.Supervisor;

public sealed class SupervisorHost : IAsyncDisposable
{
    private readonly WebApplication application;
    private readonly SupervisorCoordinator coordinator;
    private readonly UnixSocketLifecycle socketLifecycle;
    private int disposed;

    private SupervisorHost(
        WebApplication application,
        SupervisorCoordinator coordinator,
        UnixSocketLifecycle socketLifecycle)
    {
        this.application = application;
        this.coordinator = coordinator;
        this.socketLifecycle = socketLifecycle;
    }

    public string SocketPath => socketLifecycle.SocketPath;

    public IReadOnlyCollection<SupervisorWorkerSession> Workers => coordinator.Workers;

    public static async Task<SupervisorHost> StartAsync(
        SupervisorOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var lifecycle = new UnixSocketLifecycle(options);
        lifecycle.Prepare();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(SupervisorHost).Assembly.GetName().Name
        });
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.AddServerHeader = false;
            serverOptions.ListenUnixSocket(options.SocketPath, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
        });
        builder.Services.AddGrpc(grpcOptions =>
        {
            grpcOptions.MaxReceiveMessageSize = IpcLimits.MaxEnvelopeBytes;
            grpcOptions.MaxSendMessageSize = IpcLimits.MaxEnvelopeBytes;
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(loggingOptions => loggingOptions.IncludeScopes = false);
        builder.Logging.AddFilter(
            (category, level) =>
                category is not null &&
                category.StartsWith("CloudEmuera.Supervisor", StringComparison.Ordinal) &&
                level >= LogLevel.Information);
        builder.Services.AddSingleton<SupervisorCoordinator>(services =>
            new SupervisorCoordinator(options, services.GetRequiredService<ILoggerFactory>()));
        WebApplication application = builder.Build();
        SupervisorCoordinator coordinator = application.Services.GetRequiredService<SupervisorCoordinator>();
        application.MapGrpcService<WorkerControlGrpcService>();
        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            lifecycle.SealSocket();
            return new SupervisorHost(application, coordinator, lifecycle);
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            lifecycle.Dispose();
            await coordinator.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<SupervisorWorkerSession> LaunchWorkerAsync(
        WorkerLaunchRequest request,
        CancellationToken cancellationToken = default) =>
        coordinator.LaunchWorkerAsync(request, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        await coordinator.DisposeAsync().ConfigureAwait(false);
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

internal sealed class UnixSocketLifecycle
{
    private readonly SupervisorOptions options;
    private bool sealedSocket;

    public UnixSocketLifecycle(SupervisorOptions options) => this.options = options;

    public string SocketPath => options.SocketPath;

    public void Prepare()
    {
        UnixSocketSecurity.EnsurePrivateDirectory(options.RuntimeDirectory, "runtime directory");
        UnixSocketSecurity.EnsurePrivateDirectory(options.BootstrapDirectory, "bootstrap directory");

        if (!TryReadSocketMetadata(options.SocketPath, out UnixSocketSecurity.UnixMetadata metadata))
            return;

        if (metadata.Kind != UnixSocketSecurity.UnixEntryKind.Socket)
            throw new IOException("The Supervisor UDS endpoint already exists as a non-socket entry.");
        UnixSocketSecurity.RequirePrivateSocket(options.SocketPath, "UDS endpoint");

        switch (UnixSocketSecurity.ProbeStaleSocket(options.SocketPath, metadata))
        {
            case UnixSocketSecurity.StaleSocketProbe.Active:
                throw new IOException("The Supervisor UDS endpoint is already active.");
            case UnixSocketSecurity.StaleSocketProbe.Stale:
                if (!UnixSocketSecurity.RemoveOwnedSocket(options.SocketPath, metadata, "stale UDS endpoint"))
                    throw new IOException("The Supervisor UDS endpoint changed during stale cleanup.");
                break;
            case UnixSocketSecurity.StaleSocketProbe.Missing:
                break;
            default:
                throw new IOException("The Supervisor UDS endpoint could not be proven stale safely.");
        }
    }

    public void SealSocket()
    {
        if (!TryReadSocketMetadata(options.SocketPath, out _))
            throw new IOException("Kestrel did not create the Supervisor UDS endpoint.");
        UnixSocketSecurity.SetPrivateSocketMode(options.SocketPath, "UDS endpoint");
        sealedSocket = true;
    }

    public void Dispose()
    {
        if (!sealedSocket || !TryReadSocketMetadata(options.SocketPath, out UnixSocketSecurity.UnixMetadata metadata))
            return;
        if (metadata.Kind != UnixSocketSecurity.UnixEntryKind.Socket)
            return;
        try
        {
            UnixSocketSecurity.RequirePrivateSocket(options.SocketPath, "owned UDS endpoint");
            _ = UnixSocketSecurity.RemoveOwnedSocket(options.SocketPath, metadata, "owned UDS endpoint");
        }
        catch (IOException)
        {
            // Fail closed if the path was replaced or its ownership changed.
        }
        catch (UnauthorizedAccessException)
        {
            // Fail closed if the path was replaced or its ownership changed.
        }
    }

    private static bool TryReadSocketMetadata(string path, out UnixSocketSecurity.UnixMetadata metadata)
    {
        metadata = default;
        if (!OperatingSystem.IsLinux())
            return File.Exists(path) || Directory.Exists(path);

        return UnixSocketSecurity.TryReadMetadataForLifecycle(path, out metadata);
    }
}

internal sealed class SupervisorCoordinator : IAsyncDisposable
{
    private readonly SupervisorOptions options;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly object sync = new();
    private readonly Dictionary<string, SupervisorWorkerSession> sessions = new(StringComparer.Ordinal);
    private int disposed;

    public SupervisorCoordinator(SupervisorOptions options, ILoggerFactory loggerFactory)
    {
        this.options = options;
        this.loggerFactory = loggerFactory;
        logger = loggerFactory.CreateLogger<SupervisorCoordinator>();
    }

    public IReadOnlyCollection<SupervisorWorkerSession> Workers
    {
        get
        {
            lock (sync)
            {
                return sessions.Values.ToArray();
            }
        }
    }

    public async Task<SupervisorWorkerSession> LaunchWorkerAsync(
        WorkerLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, nameof(SupervisorHost));
        ArgumentNullException.ThrowIfNull(request);
        SupervisorWorkerSession session;
        lock (sync)
        {
            if (sessions.Count >= options.MaxConcurrentWorkers)
                throw new InvalidOperationException("The Supervisor worker limit has been reached.");
            if (sessions.ContainsKey(request.Binding.WorkerId))
                throw new InvalidOperationException("The WorkerId is already registered with this Supervisor.");
            session = new SupervisorWorkerSession(
                request,
                loggerFactory.CreateLogger<SupervisorWorkerSession>(),
                options.SocketPath);
            sessions.Add(request.Binding.WorkerId, session);
        }

        string bootstrapPath = Path.Combine(options.BootstrapDirectory, $"{request.Binding.WorkerId}-{Guid.NewGuid():N}.json");
        session.SetBootstrapPath(bootstrapPath);
        var bootstrap = new WorkerBootstrapDocument
        {
            SessionId = request.Binding.SessionId,
            WorkerId = request.Binding.WorkerId,
            WorkerEpoch = request.Binding.WorkerEpoch,
            SessionRoot = request.SessionRoot,
            CompatibilityProfile = request.CompatibilityProfile,
            SupervisorSocketPath = options.SocketPath,
            BootstrapToken = IpcProtocol.CreateBootstrapToken(),
            ConnectDeadlineUnixMilliseconds = DateTimeOffset.UtcNow.Add(options.RegistrationTimeout).ToUnixTimeMilliseconds(),
            HeartbeatIntervalMilliseconds = 500,
            ShutdownGracePeriodMilliseconds = checked((int)options.WorkerShutdownTimeout.TotalMilliseconds),
            SaveLayout = (int)request.SaveLayout,
            SessionRootManifestDigest = request.SessionRootManifestDigest
        };
        session.SetBootstrapToken(bootstrap.BootstrapToken);
        WorkerBootstrapDocument bootstrapToWrite = options.BootstrapTransformForTest?.Invoke(bootstrap) ?? bootstrap;
        WorkerBootstrapFile.Write(bootstrapPath, bootstrapToWrite);

        try
        {
            Process process = StartProcess(request.SessionRoot, bootstrapPath, session);
            session.AttachProcess(process);
            await session.WaitForRegistrationAsync(options.RegistrationTimeout, cancellationToken).ConfigureAwait(false);
            session.LogLifecycle("worker_registered");
            WorkerBootstrapFile.DeleteIfOwned(bootstrapPath);
            return session;
        }
        catch
        {
            session.LogLifecycle("worker_launch_failed", "launch_failed");
            WorkerBootstrapFile.DeleteIfOwned(bootstrapPath);
            await session.TerminateProcessAsync().ConfigureAwait(false);
            lock (sync)
            {
                sessions.Remove(request.Binding.WorkerId);
            }
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public RegistrationDecision Register(WorkerEnvelope registration)
    {
        IpcValidationResult validation = IpcValidator.ValidateWorkerEnvelope(registration, registered: false);
        if (!validation.IsValid)
        {
            SupervisorLifecycleLog.Rejected(logger, validation.ReasonCode);
            return RegistrationDecision.Rejected(validation.ReasonCode);
        }

        SupervisorWorkerSession? session;
        lock (sync)
        {
            sessions.TryGetValue(registration.WorkerId, out session);
        }

        if (session is null || !session.Binding.Matches(registration.SessionId, registration.WorkerId, registration.WorkerEpoch))
        {
            SupervisorLifecycleLog.Write(
                logger,
                new WorkerBinding(registration.SessionId, registration.WorkerId, registration.WorkerEpoch),
                "registration_rejected",
                IpcReasonCodes.BindingMismatch,
                LogLevel.Warning);
            return RegistrationDecision.Rejected(IpcReasonCodes.BindingMismatch);
        }
        if (session.ProcessId != registration.Registration.ProcessId)
        {
            session.LogLifecycle("registration_rejected", IpcReasonCodes.BindingMismatch);
            return RegistrationDecision.Rejected(IpcReasonCodes.BindingMismatch);
        }
        if (!FixedTimeEquals(session.BootstrapToken, registration.Registration.StartupToken))
        {
            session.LogLifecycle("registration_rejected", IpcReasonCodes.InvalidToken);
            return RegistrationDecision.Rejected(IpcReasonCodes.InvalidToken);
        }
        if (!string.Equals(registration.Registration.RuntimeIntegrationVersion,
                RuntimeBaseline.CloudEmueraIntegrationVersion, StringComparison.Ordinal) ||
            !string.Equals(registration.Registration.UpstreamCommit, RuntimeBaseline.UpstreamCommit, StringComparison.Ordinal))
        {
            session.LogLifecycle("registration_rejected", IpcReasonCodes.RuntimeVersionMismatch);
            return RegistrationDecision.Rejected(IpcReasonCodes.RuntimeVersionMismatch);
        }

        var connection = new SupervisorWorkerConnection(session);
        session.AttachConnection(connection, registration.Registration.ProcessId);
        return RegistrationDecision.Accept(session, connection);
    }

    public static async Task ReceiveAsync(SupervisorWorkerConnection connection, WorkerEnvelope message)
    {
        IpcValidationResult validation = IpcValidator.ValidateWorkerEnvelope(message, registered: true, connection.Session.Binding);
        if (!validation.IsValid)
        {
            connection.Session.RecordProtocolError(validation.ReasonCode);
            connection.Cancel();
            return;
        }

        if (message.PayloadCase == WorkerEnvelope.PayloadOneofCase.DisplayBatch)
        {
            if (!connection.Session.AcceptDisplayBatch(message.DisplayBatch))
                return;
        }

        await connection.Session.PublishAsync(message).ConfigureAwait(false);
    }

    public static void Disconnect(SupervisorWorkerConnection connection) => connection.Session.DetachConnection(connection);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        SupervisorWorkerSession[] workers = Workers.ToArray();
        foreach (SupervisorWorkerSession worker in workers)
        {
            try
            {
                await worker.StopAsync(options.WorkerShutdownTimeout).ConfigureAwait(false);
            }
            catch
            {
                await worker.TerminateProcessAsync().ConfigureAwait(false);
            }

            await worker.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Process StartProcess(string workingDirectory, string bootstrapPath, SupervisorWorkerSession session)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.DotnetPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
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

    private static bool FixedTimeEquals(string expected, string actual)
    {
        byte[] left = Encoding.UTF8.GetBytes(expected);
        byte[] right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

internal sealed record RegistrationDecision(
    bool Accepted,
    string ReasonCode,
    SupervisorWorkerSession? Session,
    SupervisorWorkerConnection? Connection)
{
    public static RegistrationDecision Accept(SupervisorWorkerSession session, SupervisorWorkerConnection connection) =>
        new(true, IpcReasonCodes.Accepted, session, connection);

    public static RegistrationDecision Rejected(string reasonCode) =>
        new(false, reasonCode, null, null);
}

public sealed class SupervisorWorkerSession : IAsyncDisposable
{
    private readonly WorkerLaunchRequest request;
    private readonly ILogger logger;
    private readonly object sync = new();
    private readonly Channel<SupervisorEnvelope> commands = Channel.CreateBounded<SupervisorEnvelope>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly List<WorkerEnvelope> pendingEvents = [];
    private TaskCompletionSource<bool> eventSignal = NewEventSignal();
    private bool eventsClosed;
    private readonly TaskCompletionSource<bool> registration =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> processExit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WorkerEnvelope> stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<DisplayBatch> displayBatches = [];
    private readonly StringBuilder processDiagnostics = new();
    private readonly List<string> sensitiveLogValues = [];
    private SupervisorWorkerConnection? connection;
    private Process? process;
    private string bootstrapPath = string.Empty;
    private string bootstrapToken = string.Empty;
    private long lastDisplaySequence;
    private int connectionCount;
    private int disposed;

    internal SupervisorWorkerSession(WorkerLaunchRequest request, ILogger logger, string supervisorSocketPath)
    {
        this.request = request;
        this.logger = logger;
        sensitiveLogValues.Add(request.SessionRoot);
        sensitiveLogValues.Add(Path.GetFullPath(supervisorSocketPath));
    }

    public WorkerBinding Binding => request.Binding;

    public string SessionRoot => request.SessionRoot;

    public int ProcessId => process?.Id ?? 0;

    public bool HasExited => process?.HasExited ?? true;

    public DateTimeOffset? ProcessStartTimeUtc
    {
        get
        {
            if (process is null)
                return null;
            try
            {
                return process.StartTime.ToUniversalTime();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            if (process is null || !process.HasExited)
                return null;
            return process.ExitCode;
        }
    }

    public int ConnectionCount
    {
        get
        {
            lock (sync)
            {
                return connectionCount;
            }
        }
    }

    public string ProcessDiagnostics
    {
        get
        {
            lock (sync)
            {
                return processDiagnostics.ToString();
            }
        }
    }

    public Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        processExit.Task.WaitAsync(timeout, cancellationToken);

    public IReadOnlyList<DisplayBatch> DisplayBatches
    {
        get
        {
            lock (sync)
            {
                return displayBatches.Select(batch => batch.Clone()).ToArray();
            }
        }
    }

    public async Task<WorkerEnvelope> WaitForAsync(
        Func<WorkerEnvelope, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
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

                    if (eventsClosed)
                        throw new TimeoutException(
                            $"The Worker event channel closed. exited={HasExited}; diagnostics={ProcessDiagnostics}");

                    if (eventSignal.Task.IsCompleted)
                        eventSignal = NewEventSignal();
                    signal = eventSignal.Task;
                }

                await signal.WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The Worker did not produce the expected IPC event. exited={HasExited}; diagnostics={ProcessDiagnostics}");
        }

    }

    public async Task SendStartRuntimeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(10));
        string messageId = IpcProtocol.NewMessageId("start");
        await commands.Writer.WriteAsync(CreateEnvelope(messageId, new StartRuntime
        {
            ExpectedSessionId = Binding.SessionId,
            ExpectedWorkerId = Binding.WorkerId,
            ExpectedWorkerEpoch = Binding.WorkerEpoch,
            ExpectedCompatibilityProfile = request.CompatibilityProfile,
            DeadlineUnixMilliseconds = deadline.ToUnixTimeMilliseconds()
        }), cancellationToken).ConfigureAwait(false);
    }

    public async Task SendInputAsync(
        string promptId,
        string clientMessageId,
        string value,
        CancellationToken cancellationToken = default)
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

        await commands.Writer.WriteAsync(CreateEnvelope(
            IpcProtocol.NewMessageId("input"),
            new SubmitInput
            {
                PromptId = promptId,
                ClientMessageId = clientMessageId,
                Value = value,
                DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds()
            }), cancellationToken).ConfigureAwait(false);
    }

    public Task SendRawAsync(SupervisorEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return commands.Writer.WriteAsync(envelope, cancellationToken).AsTask();
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await commands.Writer.WriteAsync(CreateEnvelope(
            IpcProtocol.NewMessageId("stop"),
            new StopWorker
            {
                ReasonCode = "requested",
                DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.Add(timeout).ToUnixTimeMilliseconds()
            }), cancellationToken).ConfigureAwait(false);
        await stopped.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        await processExit.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    public Task DisconnectCurrentConnectionForTestAsync()
    {
        lock (sync)
        {
            connection?.Cancel();
        }

        return Task.CompletedTask;
    }

    public async Task WaitForConnectionCountAsync(
        int expectedCount,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedCount, nameof(expectedCount));
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

    internal string BootstrapToken => bootstrapToken;

    internal void SetBootstrapPath(string path)
    {
        bootstrapPath = path;
        lock (sync)
        {
            sensitiveLogValues.Add(Path.GetFullPath(path));
        }
    }

    internal void SetBootstrapToken(string token)
    {
        bootstrapToken = token;
        lock (sync)
        {
            sensitiveLogValues.Add(token);
        }
    }

    internal void LogLifecycle(string eventName, string reason = "")
        => SupervisorLifecycleLog.Write(logger, Binding, eventName, reason);

    internal void AttachProcess(Process value)
    {
        process = value;
        LogLifecycle("worker_process_started");
        if (value.HasExited)
            processExit.TrySetResult(value.ExitCode);
        value.Exited += (_, _) =>
        {
            processExit.TrySetResult(value.ExitCode);
            registration.TrySetException(new InvalidOperationException("The Worker exited before registration."));
            LogLifecycle("worker_process_exited", value.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        };
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
    }

    internal void AttachConnection(SupervisorWorkerConnection value, long processId)
    {
        lock (sync)
        {
            connection?.Cancel();
            connection = value;
            connectionCount++;
        }

        LogLifecycle("worker_connection_attached", connectionCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        registration.TrySetResult(true);
    }

    internal void DetachConnection(SupervisorWorkerConnection value)
    {
        lock (sync)
        {
            if (ReferenceEquals(connection, value))
            {
                connection = null;
                LogLifecycle("worker_connection_detached");
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
            RuntimeFailed = new RuntimeFailed
            {
                StableCode = reasonCode,
                Phase = "ipc",
                SafeMessage = "The Worker IPC message was rejected.",
                Fatal = true,
                LastOutputSequence = lastDisplaySequence
            }
        });

    internal Task PublishAsync(WorkerEnvelope value) =>
        PublishCoreAsync(value);

    internal async Task WriteCommandsAsync(
        IServerStreamWriter<SupervisorEnvelope> writer,
        CancellationToken cancellationToken)
    {
        while (await commands.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (commands.Reader.TryRead(out SupervisorEnvelope? command))
            {
                await writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task TerminateProcessAsync()
    {
        if (process is null)
            return;
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        lock (sync)
        {
            connection?.Cancel();
            connection = null;
        }

        commands.Writer.TryComplete();
        lock (sync)
        {
            eventsClosed = true;
            eventSignal.TrySetResult(true);
        }
        await TerminateProcessAsync().ConfigureAwait(false);
        LogLifecycle("worker_session_disposed");
        WorkerBootstrapFile.DeleteIfOwned(bootstrapPath);
    }

    private Task PublishCoreAsync(WorkerEnvelope value)
    {
        lock (sync)
        {
            if (eventsClosed)
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

    private static TaskCompletionSource<bool> NewEventSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SupervisorEnvelope CreateEnvelope(string messageId, StartRuntime payload) => new()
    {
        ProtocolVersion = IpcProtocol.CurrentVersion,
        MessageId = messageId,
        SessionId = Binding.SessionId,
        WorkerId = Binding.WorkerId,
        WorkerEpoch = Binding.WorkerEpoch,
        StartRuntime = payload
    };

    private SupervisorEnvelope CreateEnvelope(string messageId, SubmitInput payload) => new()
    {
        ProtocolVersion = IpcProtocol.CurrentVersion,
        MessageId = messageId,
        SessionId = Binding.SessionId,
        WorkerId = Binding.WorkerId,
        WorkerEpoch = Binding.WorkerEpoch,
        SubmitInput = payload
    };

    private SupervisorEnvelope CreateEnvelope(string messageId, StopWorker payload) => new()
    {
        ProtocolVersion = IpcProtocol.CurrentVersion,
        MessageId = messageId,
        SessionId = Binding.SessionId,
        WorkerId = Binding.WorkerId,
        WorkerEpoch = Binding.WorkerEpoch,
        Stop = payload
    };
}

internal sealed class SupervisorWorkerConnection : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();

    public SupervisorWorkerConnection(SupervisorWorkerSession session) => Session = session;

    public SupervisorWorkerSession Session { get; }

    public CancellationToken CancellationToken => cancellation.Token;

    public void Cancel() => cancellation.Cancel();

    public void Dispose() => cancellation.Dispose();
}

internal sealed class WorkerControlGrpcService(SupervisorCoordinator coordinator) : WorkerControl.WorkerControlBase
{
    public override async Task Connect(
        IAsyncStreamReader<WorkerEnvelope> requestStream,
        IServerStreamWriter<SupervisorEnvelope> responseStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            return;
        WorkerEnvelope registration = requestStream.Current;
        RegistrationDecision decision = coordinator.Register(registration);
        var response = new SupervisorEnvelope
        {
            ProtocolVersion = IpcProtocol.CurrentVersion,
            MessageId = IpcProtocol.NewMessageId("registration"),
            CorrelationId = registration.MessageId,
            SessionId = registration.SessionId,
            WorkerId = registration.WorkerId,
            WorkerEpoch = registration.WorkerEpoch,
            RegistrationResult = new RegistrationResult
            {
                Accepted = decision.Accepted,
                ReasonCode = decision.ReasonCode,
                NegotiatedProtocolVersion = decision.Accepted ? IpcProtocol.CurrentVersion : 0,
                RuntimeIntegrationVersion = RuntimeBaseline.CloudEmueraIntegrationVersion,
                UpstreamCommit = RuntimeBaseline.UpstreamCommit
            }
        };
        await responseStream.WriteAsync(response).ConfigureAwait(false);
        if (!decision.Accepted || decision.Connection is null || decision.Session is null)
            return;

        SupervisorWorkerConnection connection = decision.Connection;
        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken,
            connection.CancellationToken);
        Task writer = decision.Session.WriteCommandsAsync(responseStream, connection.CancellationToken);
        try
        {
            while (await requestStream.MoveNext(connectionLifetime.Token).ConfigureAwait(false))
            {
                await SupervisorCoordinator.ReceiveAsync(connection, requestStream.Current).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (connectionLifetime.IsCancellationRequested)
        {
        }
        catch (IOException) when (connectionLifetime.IsCancellationRequested)
        {
        }
        finally
        {
            connection.Cancel();
            SupervisorCoordinator.Disconnect(connection);
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            connection.Dispose();
        }
    }
}
