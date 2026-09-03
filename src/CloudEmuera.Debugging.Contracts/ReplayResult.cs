using System.Text.Json;

namespace CloudEmuera.Debugging.Contracts;

public static class DebugReplayStatuses
{
    public const string RuntimeFailureReproduced = "RUNTIME_FAILURE_REPRODUCED";
    public const string FailureMarkerReached = "FAILURE_MARKER_REACHED";
    public const string TerminalReached = "TERMINAL_REACHED";
    public const string ExpectedFailureNotReproduced = "EXPECTED_FAILURE_NOT_REPRODUCED";
    public const string FailureMismatch = "FAILURE_MISMATCH";
    public const string PromptMismatch = "PROMPT_MISMATCH";
    public const string InputResultMismatch = "INPUT_RESULT_MISMATCH";
    public const string TraceExhausted = "TRACE_EXHAUSTED";
    public const string TraceTruncated = "TRACE_TRUNCATED";
    public const string TraceInvalid = "TRACE_INVALID";
    public const string CaptureMismatch = "CAPTURE_MISMATCH";
    public const string ReplayTimeout = "REPLAY_TIMEOUT";
    public const string WorkspaceInvalid = "WORKSPACE_INVALID";
    public const string WorkerFailed = "WORKER_FAILED";
    public const string RendererFailed = "RENDERER_FAILED";
    public const string DebuggerFailed = "DEBUGGER_FAILED";
}

public sealed record DebugFirstDivergence(
    string Kind,
    long? TraceIndex,
    long? PromptOrdinal,
    object? Expected,
    object? Actual,
    bool SourceModified,
    long LastCommittedFrameId,
    long LastCommittedSequence);

public sealed record DebugReplayResult
{
    public int Version { get; init; } = 1;
    public string Status { get; init; } = DebugReplayStatuses.DebuggerFailed;
    public DebugTraceTarget? Target { get; init; }
    public bool SourceModified { get; init; }
    public long LastMatchedPromptOrdinal { get; init; }
    public long LastFrameId { get; init; }
    public long LastSequence { get; init; }
    public DebugRuntimeFailure? Failure { get; init; }
    public DebugFirstDivergence? FirstDivergence { get; init; }
    public string? Diagnostic { get; init; }

    public static void Write(string path, DebugReplayResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(result);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, DebugTraceJson.Options));
        File.Move(temporary, path, overwrite: true);
    }
}
