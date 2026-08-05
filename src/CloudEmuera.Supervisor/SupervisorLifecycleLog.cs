using CloudEmuera.Ipc;
using Microsoft.Extensions.Logging;

namespace CloudEmuera.Supervisor;

internal static class SupervisorLifecycleLog
{
    private static readonly Action<ILogger, string, string, string, ulong, string, Exception?> Information =
        LoggerMessage.Define<string, string, string, ulong, string>(
            LogLevel.Information,
            new EventId(1101, "SupervisorWorkerLifecycle"),
            "worker_event={WorkerEvent} sessionId={SessionId} workerId={WorkerId} workerEpoch={WorkerEpoch} reason={Reason}");

    private static readonly Action<ILogger, string, string, string, ulong, string, Exception?> Warning =
        LoggerMessage.Define<string, string, string, ulong, string>(
            LogLevel.Warning,
            new EventId(1102, "SupervisorWorkerLifecycleWarning"),
            "worker_event={WorkerEvent} sessionId={SessionId} workerId={WorkerId} workerEpoch={WorkerEpoch} reason={Reason}");

    public static void Write(
        ILogger logger,
        WorkerBinding binding,
        string eventName,
        string reason,
        LogLevel level = LogLevel.Information)
    {
        Action<ILogger, string, string, string, ulong, string, Exception?> log =
            level == LogLevel.Warning ? Warning : Information;
        log(logger, eventName, binding.SessionId, binding.WorkerId, binding.WorkerEpoch, reason, null);
    }

    public static void Rejected(ILogger logger, string reason)
    {
        Warning(logger, "registration_rejected", string.Empty, string.Empty, 0, reason, null);
    }
}
