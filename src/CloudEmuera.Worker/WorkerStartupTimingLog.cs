using System.Text.Json;
using CloudEmuera.Ipc;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.Worker;

/// <summary>
/// Persists a small, bounded startup timeline beside the SessionRoot. This is
/// internal Session metadata and never enters the player Console.
/// </summary>
internal static class WorkerStartupTimingLog
{
    public const string FileName = "worker-startup.jsonl";
    internal const int MaxFileBytes = 256 * 1024;
    private static readonly object Sync = new();

    public static void Append(
        WorkerBootstrapDocument bootstrap,
        WorkerBinding binding,
        string phase,
        long durationMilliseconds,
        long framesSent,
        long snapshotsSent)
    {
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
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                timestamp = DateTimeOffset.UtcNow,
                sessionId = binding.SessionId,
                workerId = binding.WorkerId,
                workerEpoch = binding.WorkerEpoch,
                phase = NormalizePhase(phase),
                durationMilliseconds = Math.Max(0, durationMilliseconds),
                framesSent = Math.Max(0, framesSent),
                snapshotsSent = Math.Max(0, snapshotsSent)
            });
            byte[] line = new byte[json.Length + 1];
            json.CopyTo(line, 0);
            line[^1] = (byte)'\n';

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
                    stream.SetLength(0);
                stream.Position = stream.Length;
                stream.Write(line);
                stream.Flush(flushToDisk: true);
                SetPrivateMode(metadataDirectory, path);
            }
        }
        catch
        {
            // Observability must never change Session startup behavior.
        }
    }

    private static string NormalizePhase(string value)
    {
        string normalized = new((value ?? string.Empty).Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.').ToArray());
        return normalized.Length == 0 ? "unknown" : normalized[..Math.Min(normalized.Length, 128)];
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
}
