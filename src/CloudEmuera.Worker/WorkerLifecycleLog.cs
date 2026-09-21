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

    private static readonly Action<ILogger, string, string, ulong, int, Exception?> RuntimeWidth =
        LoggerMessage.Define<string, string, ulong, int>(
            LogLevel.Information,
            new EventId(1004, "WorkerRuntimeWidth"),
            "worker_event=runtime_width_received sessionId={SessionId} workerId={WorkerId} workerEpoch={WorkerEpoch} browserWidth={BrowserWidth}");

    private static readonly Action<ILogger, string, ulong, string, long, long, long, Exception?> StartupTiming =
        LoggerMessage.Define<string, ulong, string, long, long, long>(
            LogLevel.Information,
            new EventId(1005, "WorkerStartupTiming"),
            "worker_event=startup_timing sessionId={SessionId} workerEpoch={WorkerEpoch} phase={Phase} durationMs={DurationMs} framesSent={FramesSent} snapshotsSent={SnapshotsSent}");

    public static void Write(
        ILogger logger,
        WorkerBinding binding,
        string eventName,
        string reason,
        LogLevel level,
        WorkerBootstrapDocument? bootstrap = null)
    {
        Action<ILogger, string, string, string, ulong, string, Exception?> log = level switch
        {
            LogLevel.Error or LogLevel.Critical => Error,
            LogLevel.Warning => Warning,
            _ => Information
        };
        log(logger, eventName, binding.SessionId, binding.WorkerId, binding.WorkerEpoch, SafeReasonCode(reason), null);

        if (bootstrap is not null &&
            level is (LogLevel.Warning or LogLevel.Error or LogLevel.Critical) &&
            !string.Equals(eventName, "runtime_failed", StringComparison.Ordinal))
        {
            WorkerErrorLog.Append(
                bootstrap,
                binding,
                eventName,
                eventName,
                "worker",
                reason,
                level,
                fatal: level is LogLevel.Error or LogLevel.Critical);
        }
    }

    public static void WriteRuntimeWidth(ILogger logger, WorkerBinding binding, int browserWidth) =>
        RuntimeWidth(logger, binding.SessionId, binding.WorkerId, binding.WorkerEpoch, browserWidth, null);

    public static void WriteStartupTiming(
        ILogger logger,
        WorkerBinding binding,
        WorkerBootstrapDocument bootstrap,
        string phase,
        long durationMilliseconds,
        long framesSent,
        long snapshotsSent)
    {
        StartupTiming(
            logger,
            binding.SessionId,
            binding.WorkerEpoch,
            phase,
            durationMilliseconds,
            framesSent,
            snapshotsSent,
            null);
        WorkerStartupTimingLog.Append(
            bootstrap,
            binding,
            phase,
            durationMilliseconds,
            framesSent,
            snapshotsSent);
    }

    private static string SafeReasonCode(string? value)
    {
        string candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
            return string.Empty;
        if (candidate.Length > 128)
            return "diagnostic_present";
        foreach (char character in candidate)
        {
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or ':' or '.'))
                return "diagnostic_present";
        }
        return candidate;
    }
}
