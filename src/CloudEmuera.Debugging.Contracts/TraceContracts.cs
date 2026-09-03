using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudEmuera.Debugging.Contracts;

public static class DebugTraceContract
{
    public const int Version = 1;
    public const long DefaultMaxBytes = 32L * 1024 * 1024;
    public const int MaxLineBytes = 256 * 1024;
    public const int MaxEvents = 131_072;
    public const int MaxStringLength = 32 * 1024;
}

public static class DebugTraceEventTypes
{
    public const string Header = "header";
    public const string RuntimeConfig = "runtime_config";
    public const string PromptOpen = "prompt_open";
    public const string PromptResponse = "prompt_response";
    public const string ClockValue = "clock_value";
    public const string FailureMarker = "failure_marker";
    public const string RuntimeFailure = "runtime_failure";
    public const string Terminal = "terminal";
    public const string TraceTruncated = "trace_truncated";

    public static bool IsKnown(string value) => value is Header or RuntimeConfig or PromptOpen or
        PromptResponse or ClockValue or FailureMarker or RuntimeFailure or Terminal or TraceTruncated;
}

public sealed record DebugTraceEvent(
    int Version,
    long Index,
    string Type,
    long ElapsedMilliseconds,
    JsonElement Data);

public sealed record DebugTraceHeader
{
    public string CaptureId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public ulong OriginalWorkerEpoch { get; init; }
    public string CloudEmueraVersion { get; init; } = string.Empty;
    public string RuntimeIntegrationVersion { get; init; } = string.Empty;
    public string UpstreamCommit { get; init; } = string.Empty;
    public uint StructuredIpcVersion { get; init; }
    public uint RealtimeVersion { get; init; }
    public string CompatibilityProfile { get; init; } = string.Empty;
    public string SaveLayout { get; init; } = string.Empty;
    public string? SessionRootManifestDigest { get; init; }
    public int BrowserWidth { get; init; }
    public string WidthMode { get; init; } = "ADAPTIVE";
    public int? CustomWidth { get; init; }
    public string FontFaceId { get; init; } = string.Empty;
    public string? FontCatalogDigest { get; init; }
    public int FontSize { get; init; } = 18;
    public int LineHeight { get; init; } = 19;
    public string FontSizeLineHeightMode { get; init; } = "OVERRIDE";
    public bool ConvertBackslashToYen { get; init; }
    public int RuntimeInitializationTimeoutMilliseconds { get; init; } = 30_000;
    public int RuntimeExecutionTimeoutMilliseconds { get; init; } = -1;
    public string RandomAlgorithm { get; init; } = string.Empty;
    public long RandomSeed { get; init; }
    public string Timezone { get; init; } = string.Empty;
    public string Locale { get; init; } = string.Empty;
    public DateTimeOffset StartupWallClock { get; init; }
    public bool SaveSnapshotComplete { get; init; }
}

public sealed record DebugPromptOpen
{
    public long Ordinal { get; init; }
    public string PromptId { get; init; } = string.Empty;
    public string InputType { get; init; } = string.Empty;
    public string[] AllowedSources { get; init; } = [];
    public string? DefaultValue { get; init; }
    public bool OneInput { get; init; }
    public long? TimeoutMilliseconds { get; init; }
    public long ButtonGeneration { get; init; }
    public string? SourceFile { get; init; }
    public int? SourceLine { get; init; }
}

public static class DebugPromptResolutionKinds
{
    public const string Accepted = "ACCEPTED";
    public const string Defaulted = "DEFAULTED";
    public const string Timeout = "TIMEOUT";
    public const string WaitCompleted = "WAIT_COMPLETED";
    public const string Cancelled = "CANCELLED";

    public static bool IsKnown(string value) => value is Accepted or Defaulted or Timeout or WaitCompleted or Cancelled;
}

public sealed record DebugPromptResponse
{
    public long Ordinal { get; init; }
    public string Result { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string? Value { get; init; }
    public string? NormalizedValue { get; init; }
    public string Source { get; init; } = string.Empty;
    public long ResponseDelayMilliseconds { get; init; }
    [JsonPropertyName("pointer")]
    public JsonElement? PointerData { get; init; }
    [JsonPropertyName("key")]
    public JsonElement? KeyData { get; init; }
    public JsonElement? Frontend { get; init; }
}

public sealed record DebugRuntimeFailure
{
    public string StableCode { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;
    public string? ExceptionType { get; init; }
    public string? Message { get; init; }
    public string? SourceFile { get; init; }
    public int? SourceLine { get; init; }
    public long LastOutputSequence { get; init; }
    public long LastPromptOrdinal { get; init; }
}

public sealed record DebugTraceDocument(
    DebugTraceHeader Header,
    IReadOnlyList<DebugTraceEvent> Events,
    IReadOnlyList<DebugPromptStep> Prompts,
    DebugTraceTarget DefaultTarget,
    bool IsTruncated);

public sealed record DebugPromptStep(
    DebugTraceEvent OpenEvent,
    DebugPromptOpen Open,
    DebugTraceEvent ResponseEvent,
    DebugPromptResponse Response);

public sealed record DebugTraceTarget(string Type, long? MarkerOrdinal = null, [property: JsonIgnore] DebugTraceEvent? Event = null);

public sealed class DebugTraceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class DebugTraceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
    };

    public static readonly JsonSerializerOptions CompactOptions = new(Options)
    {
        WriteIndented = false,
    };

    public static T ReadData<T>(DebugTraceEvent traceEvent) where T : class =>
        traceEvent.Data.Deserialize<T>(Options) ??
        throw new DebugTraceException("TRACE_INVALID", $"Trace event {traceEvent.Index} has invalid {traceEvent.Type} data.");
}
