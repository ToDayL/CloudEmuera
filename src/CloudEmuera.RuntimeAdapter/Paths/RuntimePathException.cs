namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Stable reason codes used by the runtime path boundary.
/// </summary>
public static class RuntimePathReasonCodes
{
    public const string InvalidRelativePath = "invalid_relative_path";
    public const string PathOutsideArea = "path_outside_area";
    public const string ReadOnlyArea = "read_only_area";
    public const string SymbolicLinkRejected = "symbolic_link_rejected";
    public const string CrossSessionPath = "cross_session_path";
    public const string LayoutConflict = "layout_conflict";
    public const string EntryNotFound = "entry_not_found";
    public const string UnsupportedRuntimeFile = "unsupported_runtime_file";
}

/// <summary>
/// Indicates that a logical runtime path or a trusted runtime layout is invalid.
/// The exception deliberately reports logical data only; physical host paths are
/// not included in the public diagnostic contract.
/// </summary>
public class RuntimePathException : Exception
{
    public RuntimePathException(
        string reasonCode,
        string message,
        string? logicalPath = null,
        RuntimeFileArea? area = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrEmpty(reasonCode);
        ReasonCode = reasonCode;
        LogicalPath = logicalPath;
        Area = area;
    }

    public string ReasonCode { get; }

    public string? LogicalPath { get; }

    public RuntimeFileArea? Area { get; }
}

/// <summary>
/// Indicates that a file operation violates the runtime area policy.
/// Underlying I/O failures are intentionally not converted to this exception.
/// </summary>
public sealed class RuntimeFileAccessException : RuntimePathException
{
    public RuntimeFileAccessException(
        string reasonCode,
        string message,
        string? logicalPath = null,
        RuntimeFileArea? area = null,
        Exception? innerException = null)
        : base(reasonCode, message, logicalPath, area, innerException)
    {
    }
}
