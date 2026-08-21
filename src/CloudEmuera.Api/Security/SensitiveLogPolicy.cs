namespace CloudEmuera.Api.Security;

/// <summary>
/// Logging is an allow-list boundary. Stable lower-case reason codes may be
/// emitted; arbitrary diagnostic text is represented only by a presence
/// marker. This prevents game output, input, paths and exception messages from
/// becoming accidental structured-log fields.
/// </summary>
public static class SensitiveLogPolicy
{
    public const string DiagnosticPresent = "diagnostic_present";

    public static string SafeReasonCode(string? value)
    {
        string candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
            return string.Empty;
        if (candidate.Length > 128)
            return DiagnosticPresent;
        foreach (char character in candidate)
        {
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or ':' or '.'))
                return DiagnosticPresent;
        }
        return candidate;
    }

    public static string SafeDiagnosticText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : DiagnosticPresent;
}
