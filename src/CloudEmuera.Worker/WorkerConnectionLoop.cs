using System.Net.Sockets;
using System.Threading.Channels;
using CloudEmuera.Ipc;
using CloudEmuera.Ipc.V6;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace CloudEmuera.Worker;

internal sealed class WorkerRegistrationRejectedException(string reasonCode) : Exception(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;
}

/// <summary>
/// Owns the single Worker-side gRPC writer. Runtime code only enqueues bounded
/// messages; it never waits on a response stream write. The UDS stream is the
/// Worker lifetime boundary: once it closes, this process exits and does not
/// establish another control stream.
/// </summary>
internal sealed class WorkerConnectionLoop : IAsyncDisposable
{
    private readonly WorkerBootstrapDocument bootstrap;
    private readonly WorkerBinding binding;
    private readonly ILogger logger;
    private readonly Func<WorkerCommandEnvelope, CancellationToken, Task> commandHandler;
    private readonly Channel<PendingWorkerMessage> controlMessages = Channel.CreateBounded<PendingWorkerMessage>(
        new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Channel<PendingWorkerMessage> displayMessages = Channel.CreateBounded<PendingWorkerMessage>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly SemaphoreSlim outputAvailable = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource<bool> registrationAccepted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? loopTask;
    private long lastOutputSequence;
    private int registrationAttempted;
    private int controlStreamClosed;

    public WorkerConnectionLoop(
        WorkerBootstrapDocument bootstrap,
        ILogger logger,
        Func<WorkerCommandEnvelope, CancellationToken, Task> commandHandler)
    {
        this.bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        binding = bootstrap.Binding;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
        lastOutputSequence = bootstrap.InitialOutputSequence;
    }

    public Task RegistrationAccepted => registrationAccepted.Task;

    public bool ControlStreamClosed => Volatile.Read(ref controlStreamClosed) != 0;

    public void SetLastOutputSequence(long sequence) => Interlocked.Exchange(ref lastOutputSequence, sequence);

    public Task RunAsync()
    {
        loopTask ??= Task.Run(() => RunLoopAsync(lifetime.Token), CancellationToken.None);
        return loopTask;
    }

    public async Task SendControlAsync(WorkerEnvelope message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var pending = new PendingWorkerMessage(message);
        await controlMessages.Writer.WriteAsync(pending, sendCancellation.Token).ConfigureAwait(false);
        outputAvailable.Release();
        await pending.Written.Task.WaitAsync(sendCancellation.Token).ConfigureAwait(false);
    }

    public async Task SendDisplayAsync(WorkerEnvelope message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var pending = new PendingWorkerMessage(message);
        await displayMessages.Writer.WriteAsync(pending, sendCancellation.Token).ConfigureAwait(false);
        outputAvailable.Release();
        await pending.Written.Task.WaitAsync(sendCancellation.Token).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (!lifetime.IsCancellationRequested)
        {
            lifetime.Cancel();
            controlMessages.Writer.TryComplete();
            displayMessages.Writer.TryComplete();
            CancelPending(controlMessages.Reader);
            CancelPending(displayMessages.Reader);
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lifetime.Dispose();
        outputAvailable.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ConnectAndServeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerRegistrationRejectedException exception)
        {
            LogLifecycle("registration_rejected", exception.ReasonCode, LogLevel.Warning);
            registrationAccepted.TrySetException(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            string reason = exception is RpcException rpcException
                ? $"connection_closed:{rpcException.StatusCode}"
                : $"connection_closed:{exception.GetType().Name}";
            LogLifecycle("connection_closed", reason, LogLevel.Warning);
            if (!registrationAccepted.Task.IsCompleted)
                registrationAccepted.TrySetException(exception);
        }
    }

    private async Task ConnectAndServeAsync(CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = ConnectUnixSocketAsync
        };
        using GrpcChannel channel = GrpcChannel.ForAddress(
            "http://cloudemuera-uds",
            new GrpcChannelOptions
            {
                HttpHandler = handler,
                MaxReceiveMessageSize = StructuredIpcLimits.MaxEnvelopeBytes,
                MaxSendMessageSize = StructuredIpcLimits.MaxEnvelopeBytes
            });
        var client = new WorkerControl.WorkerControlClient(channel);
        using AsyncDuplexStreamingCall<WorkerEnvelope, WorkerCommandEnvelope> call = client.Connect(
            cancellationToken: cancellationToken);

        LogLifecycle("registration_sent", string.Empty, LogLevel.Information);
        await call.RequestStream.WriteAsync(CreateRegistration(), cancellationToken).ConfigureAwait(false);
        if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The API control stream closed during registration.");
        }

        WorkerCommandEnvelope registrationEnvelope = call.ResponseStream.Current;
        IpcValidationResult validation = StructuredIpcValidator.ValidateCommandEnvelope(
            registrationEnvelope,
            binding,
            bootstrap.ControlPlaneInstanceId,
            bootstrap.CapabilitySetDigest);
        if (!validation.IsValid || registrationEnvelope.PayloadCase != WorkerCommandEnvelope.PayloadOneofCase.RegistrationResult)
        {
            throw new WorkerRegistrationRejectedException(validation.ReasonCode);
        }

        if (!registrationEnvelope.RegistrationResult.Accepted)
        {
            throw new WorkerRegistrationRejectedException(registrationEnvelope.RegistrationResult.ReasonCode);
        }

        if (registrationEnvelope.RegistrationResult.NegotiatedProtocolVersion != StructuredIpcProtocol.CurrentVersion ||
            !string.Equals(
                registrationEnvelope.RegistrationResult.RuntimeIntegrationVersion,
                CloudEmuera.RuntimeAdapter.RuntimeBaseline.CloudEmueraIntegrationVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                registrationEnvelope.RegistrationResult.UpstreamCommit,
                CloudEmuera.RuntimeAdapter.RuntimeBaseline.UpstreamCommit,
                StringComparison.Ordinal))
        {
            throw new WorkerRegistrationRejectedException(IpcReasonCodes.RuntimeVersionMismatch);
        }

        LogLifecycle("registration_accepted", string.Empty, LogLevel.Information);
        registrationAccepted.TrySetResult(true);
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task writer = WriteLoopAsync(call.RequestStream, connectionCancellation.Token);
        try
        {
            while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                WorkerCommandEnvelope command = call.ResponseStream.Current;
                IpcValidationResult commandValidation = StructuredIpcValidator.ValidateCommandEnvelope(
                    command,
                    binding,
                    bootstrap.ControlPlaneInstanceId,
                    bootstrap.CapabilitySetDigest);
                if (!commandValidation.IsValid)
                {
                    throw new InvalidDataException(commandValidation.ReasonCode);
                }

                await commandHandler(command, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            connectionCancellation.Cancel();
            LogLifecycle("connection_closed", string.Empty, LogLevel.Information);
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                LogLifecycle("writer_closed", exception.GetType().Name, LogLevel.Warning);
            }
            finally
            {
                Interlocked.Exchange(ref controlStreamClosed, 1);
                lifetime.Cancel();
            }
        }
    }

    private async Task WriteLoopAsync(
        IClientStreamWriter<WorkerEnvelope> writer,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (controlMessages.Reader.TryRead(out PendingWorkerMessage? control))
            {
                await WritePendingAsync(writer, control)
                    .ConfigureAwait(false);
                continue;
            }

            if (displayMessages.Reader.TryRead(out PendingWorkerMessage? display))
            {
                await WritePendingAsync(writer, display)
                    .ConfigureAwait(false);
                continue;
            }

            await outputAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WritePendingAsync(
        IClientStreamWriter<WorkerEnvelope> writer,
        PendingWorkerMessage pending)
    {
        try
        {
            await writer.WriteAsync(pending.Message).ConfigureAwait(false);
            pending.Written.TrySetResult(true);
        }
        catch
        {
            pending.Written.TrySetCanceled(lifetime.Token);
            throw;
        }
    }

    private static void CancelPending(ChannelReader<PendingWorkerMessage> reader)
    {
        while (reader.TryRead(out PendingWorkerMessage? pending))
            pending.Written.TrySetCanceled();
    }

    private sealed class PendingWorkerMessage(WorkerEnvelope message)
    {
        public WorkerEnvelope Message { get; } = message;

        public TaskCompletionSource<bool> Written { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private WorkerEnvelope CreateRegistration() => new()
    {
        ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
        MessageId = IpcProtocol.NewMessageId("reg"),
        SessionId = binding.SessionId,
        WorkerId = binding.WorkerId,
        WorkerEpoch = binding.WorkerEpoch,
        ControlPlaneInstanceId = bootstrap.ControlPlaneInstanceId,
        CapabilitySetDigest = bootstrap.CapabilitySetDigest,
        Registration = new WorkerRegistration
        {
            StartupToken = Interlocked.Exchange(ref registrationAttempted, 1) == 0 ? bootstrap.BootstrapToken : string.Empty,
            RuntimeIntegrationVersion = CloudEmuera.RuntimeAdapter.RuntimeBaseline.CloudEmueraIntegrationVersion,
            UpstreamCommit = CloudEmuera.RuntimeAdapter.RuntimeBaseline.UpstreamCommit,
            ProcessId = Environment.ProcessId,
            ProcessBootId = WorkerProcessIdentityProbe.ReadBootId(),
            ProcessStartTicks = WorkerProcessIdentityProbe.ReadStartTicks(Environment.ProcessId),
            LastOutputSequence = Interlocked.Read(ref lastOutputSequence),
            CapabilitySetDigest = bootstrap.CapabilitySetDigest
        }
    };

    private void LogLifecycle(string eventName, string reason, LogLevel level)
        => WorkerLifecycleLog.Write(logger, binding, eventName, reason, level, bootstrap);

    private async ValueTask<Stream> ConnectUnixSocketAsync(
        SocketsHttpConnectionContext _,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(
                new UnixDomainSocketEndPoint(bootstrap.ControlSocketPath),
                cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
