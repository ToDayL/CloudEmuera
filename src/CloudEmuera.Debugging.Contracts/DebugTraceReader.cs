using System.Text;
using System.Text.Json;

namespace CloudEmuera.Debugging.Contracts;

public static class DebugTraceReader
{
    public static DebugTraceDocument Read(string path, string target = "auto", bool allowTruncated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return Read(stream, target, allowTruncated);
    }

    public static DebugTraceDocument Read(Stream stream, string target = "auto", bool allowTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("Trace stream must be readable.", nameof(stream));
        byte[] content = ReadBounded(stream);

        var events = new List<DebugTraceEvent>();
        long expectedIndex = 1;
        int offset = 0;
        while (offset < content.Length)
        {
            int newline = Array.IndexOf(content, (byte)'\n', offset);
            bool completeLine = newline >= 0;
            int end = completeLine ? newline : content.Length;
            int length = end - offset;
            if (length > 0 && content[end - 1] == (byte)'\r') length--;
            if (length == 0)
                throw Invalid("Trace contains an empty line.");
            if (length > DebugTraceContract.MaxLineBytes)
                throw Invalid("Trace event exceeds the v1 line limit.");
            if (events.Count >= DebugTraceContract.MaxEvents)
                throw Invalid("Trace contains too many events.");
            DebugTraceEvent? value;
            try
            {
                string line = new UTF8Encoding(false, true).GetString(content, offset, length);
                value = JsonSerializer.Deserialize<DebugTraceEvent>(line, DebugTraceJson.Options);
            }
            catch (Exception exception) when (!completeLine && exception is JsonException or DecoderFallbackException)
            {
                // A Worker can die between writing bytes and its terminating newline.
                break;
            }
            catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
            {
                throw new DebugTraceException("TRACE_INVALID", "Trace contains malformed JSON before EOF: " + exception.Message);
            }
            if (value is null || value.Version != DebugTraceContract.Version || value.Index != expectedIndex ||
                value.ElapsedMilliseconds < 0 || string.IsNullOrWhiteSpace(value.Type))
                throw Invalid($"Trace event {expectedIndex} violates the v1 envelope contract.");
            if (!DebugTraceEventTypes.IsKnown(value.Type))
                throw new DebugTraceException("TRACE_UNSUPPORTED_EVENT", $"Trace event {value.Index} has unsupported type '{value.Type}'.");
            events.Add(value);
            expectedIndex++;
            offset = end + 1;
        }

        if (events.Count == 0 || events[0].Type != DebugTraceEventTypes.Header)
            throw Invalid("The first complete trace event must be header.");
        DebugTraceHeader header = DebugTraceJson.ReadData<DebugTraceHeader>(events[0]);
        ValidateHeader(header);
        bool truncated = events.Any(value => value.Type == DebugTraceEventTypes.TraceTruncated);
        if (truncated && !allowTruncated)
            throw new DebugTraceException("TRACE_TRUNCATED", "The trace is truncated; complete replay cannot be claimed.");

        var prompts = new List<DebugPromptStep>();
        DebugTraceEvent? openEvent = null;
        DebugPromptOpen? open = null;
        long ordinal = 0;
        foreach (DebugTraceEvent traceEvent in events.Skip(1))
        {
            if (traceEvent.Type == DebugTraceEventTypes.PromptOpen)
            {
                if (open is not null)
                    throw Invalid($"Prompt {open.Ordinal} has no final response.");
                open = DebugTraceJson.ReadData<DebugPromptOpen>(traceEvent);
                if (open.Ordinal != ++ordinal || string.IsNullOrWhiteSpace(open.InputType) || open.AllowedSources.Length == 0)
                    throw Invalid($"Prompt event {traceEvent.Index} has invalid ordering or constraints.");
                openEvent = traceEvent;
            }
            else if (traceEvent.Type == DebugTraceEventTypes.PromptResponse)
            {
                DebugPromptResponse response = DebugTraceJson.ReadData<DebugPromptResponse>(traceEvent);
                if (open is null || openEvent is null || response.Ordinal != open.Ordinal ||
                    !DebugPromptResolutionKinds.IsKnown(response.Result) || response.ResponseDelayMilliseconds < 0)
                    throw Invalid($"Prompt response event {traceEvent.Index} does not resolve the current prompt.");
                prompts.Add(new DebugPromptStep(openEvent, open, traceEvent, response));
                open = null;
                openEvent = null;
            }
        }
        if (open is not null)
            throw new DebugTraceException("TRACE_EXHAUSTED", $"Trace ended while prompt {open.Ordinal} was open.");

        DebugTraceTarget selectedTarget = SelectTarget(events, target);
        return new DebugTraceDocument(header, events, prompts, selectedTarget, truncated);
    }

    private static byte[] ReadBounded(Stream stream)
    {
        if (stream.CanSeek && stream.Length > DebugTraceContract.DefaultMaxBytes)
            throw Invalid("Trace exceeds the v1 hard size limit.");
        using var copy = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (copy.Length + read > DebugTraceContract.DefaultMaxBytes)
                throw Invalid("Trace exceeds the v1 hard size limit.");
            copy.Write(buffer, 0, read);
        }
        return copy.ToArray();
    }

    private static void ValidateHeader(DebugTraceHeader header)
    {
        if (string.IsNullOrWhiteSpace(header.CaptureId) || string.IsNullOrWhiteSpace(header.SessionId) ||
            header.OriginalWorkerEpoch == 0 || header.CompatibilityProfile is not ("v18-compatible" or "em-ee-current") ||
            header.SaveLayout is not ("root" or "sav") || header.FontSize is < 8 or > 72 ||
            header.LineHeight < header.FontSize || header.LineHeight > 128 ||
            header.StartupWallClock.ToUnixTimeMilliseconds() <= 0 || !header.SaveSnapshotComplete)
            throw Invalid("Trace header is missing required replay provenance or has an incomplete reset snapshot.");
    }

    private static DebugTraceTarget SelectTarget(IReadOnlyList<DebugTraceEvent> events, string target)
    {
        DebugTraceEvent? runtimeFailure = events.LastOrDefault(value => value.Type == DebugTraceEventTypes.RuntimeFailure);
        DebugTraceEvent[] markers = events.Where(value => value.Type == DebugTraceEventTypes.FailureMarker).ToArray();
        DebugTraceEvent? terminal = events.LastOrDefault(value => value.Type == DebugTraceEventTypes.Terminal);
        if (target == "auto")
        {
            if (runtimeFailure is not null) return new("runtime_failure", null, runtimeFailure);
            if (markers.Length != 0) return new("marker", markers.Length, markers[^1]);
            if (terminal is not null) return new("terminal", null, terminal);
            throw Invalid("Trace has no runtime failure, failure marker, or terminal target.");
        }
        if (target == "runtime-failure" && runtimeFailure is not null) return new("runtime_failure", null, runtimeFailure);
        if (target == "terminal" && terminal is not null) return new("terminal", null, terminal);
        if (target.StartsWith("marker:", StringComparison.Ordinal) &&
            long.TryParse(target.AsSpan("marker:".Length), out long markerOrdinal) && markerOrdinal > 0 && markerOrdinal <= markers.Length)
            return new("marker", markerOrdinal, markers[markerOrdinal - 1]);
        throw Invalid($"Requested replay target '{target}' is absent or invalid.");
    }

    private static DebugTraceException Invalid(string message) => new("TRACE_INVALID", message);
}
