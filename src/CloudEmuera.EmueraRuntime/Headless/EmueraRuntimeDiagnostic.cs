namespace CloudEmuera.EmueraRuntime.Headless;

public enum EmueraRuntimePhase
{
    Initialization,
    Loading,
    Execution,
    Input,
    Media
}

public sealed record EmueraRuntimeDiagnostic(
    string Code,
    EmueraRuntimePhase Phase,
    string Message,
    bool IsFatal = false,
    string? SourcePath = null,
    int? LineNumber = null);
