namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Access mode requested by a session layout mapping. Applying a mapping is a
/// future control-plane concern; this object only describes and validates it.
/// </summary>
public enum RuntimeAccessMode
{
    ReadOnly = 0,
    ReadWrite = 1
}

/// <summary>
/// One logical interpreter target and its trusted physical source.
/// </summary>
public sealed record RuntimePathMapping
{
    public RuntimePathMapping(
        string logicalTarget,
        string? physicalSource,
        string physicalTarget,
        RuntimeAccessMode accessMode,
        RuntimeFileArea area)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalTarget);
        if (!Enum.IsDefined(accessMode) || !Enum.IsDefined(area))
        {
            throw new ArgumentException("The mapping enum value is invalid.");
        }

        LogicalTarget = logicalTarget;
        PhysicalSource = physicalSource;
        PhysicalTarget = physicalTarget;
        AccessMode = accessMode;
        Area = area;
    }

    public string LogicalTarget { get; }

    public string? PhysicalSource { get; }

    public string PhysicalTarget { get; }

    public RuntimeAccessMode AccessMode { get; }

    public RuntimeFileArea Area { get; }

    public bool ReadOnly => AccessMode == RuntimeAccessMode.ReadOnly;
}

/// <summary>
/// A validated SessionRoot mapping plan and its immutable runtime paths.
/// Physical paths are available to the API Worker Manager and adapter, while
/// <see cref="DiagnosticDescription"/> intentionally contains only stable
/// layout facts and no host directory names.
/// </summary>
public sealed class SessionRootLayout
{
    internal SessionRootLayout(
        RuntimePaths runtimePaths,
        IReadOnlyList<RuntimePathMapping> mappings,
        string copiedManifestDigest,
        IReadOnlyList<SessionRootManifestEntry> copiedManifestEntries,
        string diagnosticDescription)
    {
        RuntimePaths = runtimePaths;
        Mappings = mappings;
        CopiedManifestDigest = copiedManifestDigest;
        CopiedManifestEntries = copiedManifestEntries;
        DiagnosticDescription = diagnosticDescription;
    }

    public RuntimePaths RuntimePaths { get; }

    public RuntimePaths Paths => RuntimePaths;

    public string SessionRoot => RuntimePaths.SessionRoot;

    public string SessionWorkspaceRoot => RuntimePaths.SessionWorkspaceRoot;

    public string GameContentRoot => RuntimePaths.GameContentRoot;

    public RuntimeSaveLayout SaveLayout => RuntimePaths.SaveLayout;

    public IReadOnlyList<RuntimePathMapping> Mappings { get; }

    public IReadOnlyList<RuntimePathMapping> Entries => Mappings;

    /// <summary>
    /// Retained for callers compiled against the P0-02 layout shape. Complete
    /// copy deliberately creates no content links, so this collection is
    /// always empty in P0-05.
    /// </summary>
    public IReadOnlyList<RuntimePathMapping> ContentLinks { get; } = Array.Empty<RuntimePathMapping>();

    public string CopiedManifestDigest { get; }

    public IReadOnlyList<SessionRootManifestEntry> CopiedManifestEntries { get; }

    public string DiagnosticDescription { get; }
}
