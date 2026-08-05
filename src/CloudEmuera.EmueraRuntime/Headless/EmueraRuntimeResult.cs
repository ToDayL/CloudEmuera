using CloudEmuera.RuntimeAdapter;
using System.Diagnostics.CodeAnalysis;

namespace CloudEmuera.EmueraRuntime.Headless;

public enum EmueraRuntimeStatus
{
    Completed,
    Cancelled,
    InitializationFailed,
    ScriptFailed,
    UnsupportedCapability,
    DeadlineExceeded
}

public sealed record EmueraRuntimeResult
{
    public EmueraRuntimeResult(
        EmueraRuntimeStatus status,
        IReadOnlyList<EmueraRuntimeDiagnostic>? diagnostics = null,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        Status = status;
        Diagnostics = diagnostics ?? Array.Empty<EmueraRuntimeDiagnostic>();
        Variables = variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public EmueraRuntimeStatus Status { get; }

    public IReadOnlyList<EmueraRuntimeDiagnostic> Diagnostics { get; }

    public IReadOnlyDictionary<string, string> Variables { get; }

    [SuppressMessage("Performance", "CA1822", Justification = "Report fields intentionally travel with each runtime result.")]
    public string UpstreamCommit => RuntimeBaseline.UpstreamCommit;

    [SuppressMessage("Performance", "CA1822", Justification = "Report fields intentionally travel with each runtime result.")]
    public string IntegrationVersion => RuntimeBaseline.CloudEmueraIntegrationVersion;
}
