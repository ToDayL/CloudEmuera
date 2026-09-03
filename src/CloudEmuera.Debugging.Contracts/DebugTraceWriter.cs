using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CloudEmuera.Debugging.Contracts;

public sealed class DebugTraceWriter : IDisposable
{
    private const int ReservedTailBytes = 4 * 1024;
    private readonly object sync = new();
    private readonly FileStream stream;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly long hardLimit;
    private long index;
    private bool truncated;
    private bool disposed;

    public DebugTraceWriter(string path, DebugTraceHeader header, long maxBytes = DebugTraceContract.DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(header);
        if (maxBytes < 16 * 1024 || maxBytes > DebugTraceContract.DefaultMaxBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        hardLimit = maxBytes;
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read, 16 * 1024, FileOptions.WriteThrough);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        WriteCore(DebugTraceEventTypes.Header, header, flush: true, terminal: true);
    }

    public bool IsTruncated { get { lock (sync) return truncated; } }

    public void Write(string type, object data, bool flush = false, bool terminal = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(data);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (truncated && !terminal) return;
            WriteCore(type, data, flush, terminal);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            stream.Flush(flushToDisk: true);
            stream.Dispose();
        }
    }

    private void WriteCore(string type, object data, bool flush, bool terminal)
    {
        JsonElement element = JsonSerializer.SerializeToElement(data, DebugTraceJson.CompactOptions);
        var traceEvent = new DebugTraceEvent(DebugTraceContract.Version, index + 1, type, elapsed.ElapsedMilliseconds, element);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(traceEvent, DebugTraceJson.CompactOptions);
        // An individual oversized diagnostic event is non-essential. Treat it
        // exactly like the aggregate limit: retain a bounded truncation marker
        // and let the game continue running.
        if (json.Length > DebugTraceContract.MaxLineBytes)
        {
            if (!terminal && !truncated)
            {
                truncated = true;
                WriteCore(DebugTraceEventTypes.TraceTruncated,
                    new { reason = "event_size_limit", hardLimitBytes = hardLimit },
                    flush: true, terminal: true);
            }
            return;
        }
        long limit = terminal ? hardLimit : hardLimit - ReservedTailBytes;
        if (stream.Position + json.Length + 1 > limit)
        {
            if (!terminal && !truncated)
            {
                truncated = true;
                WriteCore(DebugTraceEventTypes.TraceTruncated, new { reason = "size_limit", hardLimitBytes = hardLimit }, flush: true, terminal: true);
            }
            return;
        }
        stream.Write(json);
        stream.WriteByte((byte)'\n');
        index++;
        if (flush) stream.Flush(flushToDisk: true);
    }
}
