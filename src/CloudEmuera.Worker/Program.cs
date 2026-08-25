using System.Runtime.InteropServices;
using System.Text.Json;
using CloudEmuera.Ipc;
using CloudEmuera.Worker;
using Grpc.Core;
using Microsoft.Extensions.Logging;

return await WorkerProcess.RunAsync(args).ConfigureAwait(false);

internal static class WorkerProcess
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? bootstrapPath = null;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] != "--bootstrap-file" || index + 1 >= args.Length || bootstrapPath is not null)
            {
                Console.Error.WriteLine($"worker_error code={IpcReasonCodes.BootstrapInvalid}");
                return 10;
            }

            bootstrapPath = args[++index];
        }

        if (bootstrapPath is null)
        {
            Console.Error.WriteLine($"worker_error code={IpcReasonCodes.BootstrapInvalid}");
            return 10;
        }

        WorkerBootstrapDocument bootstrap;
        try
        {
            bootstrap = WorkerBootstrapFile.Read(bootstrapPath);
            ParentDeathGuard.Install(bootstrap.ExpectedParentProcessId);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"worker_error code={IpcReasonCodes.BootstrapInvalid}");
            return 10;
        }

        using var processCancellation = new CancellationTokenSource();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.ClearProviders();
            logging.AddJsonConsole(options => options.IncludeScopes = false);
            logging.SetMinimumLevel(LogLevel.Information);
        });
        ILogger connectionLogger = loggerFactory.CreateLogger<CloudEmuera.Worker.WorkerConnectionLoop>();
        ILogger runtimeLogger = loggerFactory.CreateLogger<CloudEmuera.Worker.WorkerRuntimeController>();
        WorkerLifecycleLog.Write(
            loggerFactory.CreateLogger("CloudEmuera.Worker"),
            bootstrap.Binding,
            "bootstrap_accepted",
            string.Empty,
            LogLevel.Information);
        using PosixSignalRegistration sigterm = RegisterSignal(PosixSignal.SIGTERM, processCancellation);
        using PosixSignalRegistration sigint = RegisterSignal(PosixSignal.SIGINT, processCancellation);
        CloudEmuera.Worker.WorkerRuntimeController? controller = null;
        await using var connection = new CloudEmuera.Worker.WorkerConnectionLoop(
            bootstrap,
            connectionLogger,
            (command, cancellationToken) => controller is null
                ? Task.CompletedTask
                : controller.HandleCommandAsync(command, cancellationToken));
        controller = new CloudEmuera.Worker.WorkerRuntimeController(bootstrap, connection, runtimeLogger);

        Task connectionTask = connection.RunAsync();
        try
        {
            await connection.RegistrationAccepted.WaitAsync(
                    TimeSpan.FromMilliseconds(Math.Max(
                        1,
                        bootstrap.ConnectDeadlineUnixMilliseconds - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            WorkerErrorLog.Append(
                bootstrap,
                bootstrap.Binding,
                "registration_wait_failed",
                "registration_wait_failed",
                "registration",
                "The Worker did not complete registration before the deadline.",
                LogLevel.Error,
                fatal: true);
            await connection.StopAsync().ConfigureAwait(false);
            return 11;
        }

        int exitCode;
        try
        {
            Task completed = await Task.WhenAny(controller.Completion, connectionTask).ConfigureAwait(false);
            if (completed == connectionTask)
            {
                try { await connectionTask.ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or RpcException or OperationCanceledException)
                {
                    WorkerLifecycleLog.Write(
                        connectionLogger,
                        bootstrap.Binding,
                        "control_stream_closed",
                        exception.GetType().Name,
                        LogLevel.Warning,
                        bootstrap);
                }
                await controller.RequestShutdownAsync().ConfigureAwait(false);
            }
            exitCode = await controller.Completion.WaitAsync(processCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await controller.RequestShutdownAsync().ConfigureAwait(false);
            exitCode = await controller.Completion.ConfigureAwait(false);
        }

        await connection.StopAsync().ConfigureAwait(false);
        await controller.DisposeAsync().ConfigureAwait(false);
        try { await connectionTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or RpcException or OperationCanceledException)
        {
            WorkerLifecycleLog.Write(
                connectionLogger,
                bootstrap.Binding,
                "control_stream_closed",
                exception.GetType().Name,
                LogLevel.Warning,
                bootstrap);
        }
        return exitCode;
    }

    private static PosixSignalRegistration RegisterSignal(PosixSignal signal, CancellationTokenSource cancellation)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null!;
        }

        return PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
    }

}
