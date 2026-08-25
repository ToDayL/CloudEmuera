using System.Text.Json;
using System.Text.Json.Serialization;
using CloudEmuera.Ipc;
using CloudEmuera.RuntimeAdapter;
using Microsoft.Extensions.Logging;

namespace CloudEmuera.Worker;

/// <summary>
/// Persists Worker-side failures beside the SessionRoot. The file is private
/// Session metadata, not game content and not the runtime debug trace.
/// </summary>
internal static class WorkerErrorLog
{
    public const string FileName = "worker-error.jsonl";

    internal const int MaxFileBytes = 256 * 1024;

    private const int MaxMessageLength = 4 * 1024;
    private const int MaxTokenLength = 128;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Append(
        WorkerBootstrapDocument bootstrap,
        WorkerBinding binding,
        string eventName,
        string code,
        string phase,
        string? message,
        LogLevel level,
        bool fatal = false,
        long? lastOutputSequence = null,
        string? exceptionType = null)
    {
        if (level is not (LogLevel.Warning or LogLevel.Error or LogLevel.Critical))
            return;

        try
        {
            string root = Path.GetFullPath(bootstrap.SessionRoot);
            string? container = Directory.GetParent(root)?.FullName;
            if (container is null)
                return;

            string metadataDirectory = Path.Combine(container, "metadata");
            RuntimePathUtilities.ValidateNoReparsePointsAlongPath(metadataDirectory, "session-metadata");
            Directory.CreateDirectory(metadataDirectory);
            RuntimePathUtilities.ValidateNoReparsePointsAlongPath(metadataDirectory, "session-metadata");
            RuntimePathUtilities.ThrowIfReparsePoint(metadataDirectory, "session-metadata", missingIsAllowed: false);

            string path = Path.Combine(metadataDirectory, FileName);
            RuntimePathUtilities.ThrowIfReparsePoint(path, FileName);
            RuntimePathUtilities.ThrowIfHardLink(path, FileName);

            WorkerErrorRecord record = new(
                SchemaVersion: 1,
                Timestamp: DateTimeOffset.UtcNow,
                Severity: level is LogLevel.Error or LogLevel.Critical ? "error" : "warning",
                EventName: NormalizeToken(eventName, "worker_error"),
                SessionId: NormalizeToken(binding.SessionId, "unknown"),
                WorkerId: NormalizeToken(binding.WorkerId, "unknown"),
                WorkerEpoch: binding.WorkerEpoch,
                Code: NormalizeToken(code, "worker_error"),
                Phase: NormalizeToken(phase, "worker"),
                Fatal: fatal,
                LastOutputSequence: lastOutputSequence is >= 0 ? lastOutputSequence : null,
                ExceptionType: NormalizeOptionalToken(exceptionType),
                Message: SanitizeMessage(bootstrap, message));
            byte[] line = SerializeLine(record);
            if (line.Length > MaxFileBytes)
                return;

            lock (Sync)
            {
                using FileStream stream = new(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 4096,
                    options: FileOptions.WriteThrough);

                if (stream.Length + line.Length > MaxFileBytes)
                {
                    stream.SetLength(0);
                    stream.Position = 0;
                    byte[] marker = SerializeLine(record with
                    {
                        EventName = "log_truncated",
                        Code = "worker_error_log_truncated",
                        Phase = "worker",
                        Severity = "warning",
                        Fatal = false,
                        LastOutputSequence = null,
                        ExceptionType = null,
                        Message = "Previous Worker error entries were truncated at the size limit."
                    });
                    if (marker.Length + line.Length <= MaxFileBytes)
                        stream.Write(marker);
                }

                stream.Position = stream.Length;
                stream.Write(line);
                stream.Flush(flushToDisk: true);
                SetPrivateMode(metadataDirectory, path);
            }
        }
        catch (Exception)
        {
            // Diagnostics must never turn a Worker failure into a different
            // failure, especially when the SessionRoot is already damaged.
        }
    }

    private static byte[] SerializeLine(WorkerErrorRecord record)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        byte[] line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    private static string SanitizeMessage(WorkerBootstrapDocument bootstrap, string? value)
    {
        string safe = value?.Trim() ?? string.Empty;
        foreach (string secret in new[]
                 {
                     bootstrap.BootstrapToken,
                     bootstrap.SessionRoot,
                     bootstrap.ControlSocketPath
                 })
        {
            if (!string.IsNullOrEmpty(secret))
                safe = safe.Replace(secret, "<redacted>", StringComparison.Ordinal);
        }

        return safe.Length <= MaxMessageLength ? safe : safe[..MaxMessageLength];
    }

    private static string NormalizeOptionalToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return NormalizeToken(value, "unknown");
    }

    private static string NormalizeToken(string? value, string fallback)
    {
        string candidate = value?.Trim() ?? string.Empty;
        string normalized = new(candidate.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or ':' or '.' or '~').ToArray());
        if (normalized.Length == 0)
            return fallback;
        return normalized[..Math.Min(normalized.Length, MaxTokenLength)];
    }

    private static void SetPrivateMode(string metadataDirectory, string path)
    {
        if (!OperatingSystem.IsLinux())
            return;

        File.SetUnixFileMode(
            metadataDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed record WorkerErrorRecord(
        int SchemaVersion,
        DateTimeOffset Timestamp,
        string Severity,
        string EventName,
        string SessionId,
        string WorkerId,
        ulong WorkerEpoch,
        string Code,
        string Phase,
        bool Fatal,
        long? LastOutputSequence,
        string? ExceptionType,
        string Message);
}
