using CloudEmuera.Ipc;
using Microsoft.Extensions.Logging;

namespace CloudEmuera.Worker;

internal static class WorkerLifecycleLog
{
    private static readonly Action<ILogger, string, string, string, ulong, string, Exception?> Information =
        LoggerMessage.Define<string, string, string, ulong, string>(
            LogLevel.Information,
            new EventId(1001, "WorkerLifecycle"),
            "worker_event={WorkerEvent} sessionId={SessionId} workerId={WorkerId} workerEpoch={WorkerEpoch} reason={Reason}");

    private static readonly Action<ILogger, string, string, string, ulong, string, Exception?> Warning =
        LoggerMessage.Define<string, string, string, ulong, string>(
            LogLevel.Warning,
            new EventId(1002, "WorkerLifecycleWarning"),
            "worker_event={WorkerEvent} sessionId={SessionId} workerId={WorkerId} workerEpoch={WorkerEpoch} reason={Reason}");

    private static readonly Action<ILogger, string, string, string, ulong, string, Exception?> Error =
        LoggerMessage.Define<string, string, string, ulong, string>(
            LogLevel.Error,
            new EventId(1003, "WorkerLifecycleError"),
            "worker_event={WorkerEvent} sessionId={SessionId} workerId={WorkerId} workerEpoch={WorkerEpoch} reason={Reason}");

    public static void Write(
        ILogger logger,
        WorkerBinding binding,
        string eventName,
        string reason,
        LogLevel level)
    {
        Action<ILogger, string, string, string, ulong, string, Exception?> log = level switch
        {
            LogLevel.Error or LogLevel.Critical => Error,
            LogLevel.Warning => Warning,
            _ => Information
        };
        log(logger, eventName, binding.SessionId, binding.WorkerId, binding.WorkerEpoch, reason, null);
    }
}
