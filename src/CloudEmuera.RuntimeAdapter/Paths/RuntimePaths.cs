namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Immutable, session-scoped physical roots used by the runtime adapter.
///
/// A SessionRoot is a complete, private copy of one published GameContent. It
/// is the actual Emuera GameRoot: configuration, game files, temporary data
/// and native saves all live below this one ordinary directory. The
/// GameContentRoot property is retained for control-plane/diagnostic binding and
/// is never used as a runtime content fallback.
/// </summary>
public sealed class RuntimePaths
{
    /// <summary>
    /// Creates the adapter view for a SessionRoot that was materialized by a
    /// API Worker Manager before the Worker process started. The source GameContent is
    /// deliberately represented by a non-existent sibling sentinel; no
    /// source path is supplied to the Worker and no content fallback exists.
    /// </summary>
    public static RuntimePaths ForExistingSessionRoot(
        string sessionRoot,
        RuntimeSaveLayout saveLayout)
    {
        string normalizedSessionRoot = RuntimePathUtilities.NormalizeAbsolutePath(sessionRoot, nameof(sessionRoot));
        string parent = Directory.GetParent(normalizedSessionRoot)?.FullName
            ?? throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "An existing SessionRoot must have a parent directory.");
        if (string.Equals(normalizedSessionRoot, Path.GetPathRoot(normalizedSessionRoot), RuntimePathUtilities.PathComparison))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "The filesystem root cannot be used as a SessionRoot.");
        }

        string suffix = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedSessionRoot)))
            [..16]
            .ToLowerInvariant();
        return new RuntimePaths(
            normalizedSessionRoot,
            Path.Combine(parent, $".cloudemuera-unavailable-game-content-{suffix}"),
            Path.Combine(parent, $".cloudemuera-worker-workspace-{suffix}"),
            saveLayout);
    }

    public RuntimePaths(
        string sessionRoot,
        string gameContentRoot,
        string sessionWorkspaceRoot,
        RuntimeSaveLayout saveLayout)
        : this(
            sessionRoot,
            gameContentRoot,
            sessionWorkspaceRoot,
            saveLayout,
            csvRoot: null,
            erbRoot: null,
            resourceRoot: null,
            soundRoot: null,
            fontRoot: null,
            configurationRoot: null,
            temporaryRoot: null,
            rootSaveRoot: null,
            savDirectoryRoot: null,
            otherSessionWorkspaceRoots: null)
    {
    }

    public RuntimePaths(
        string sessionRoot,
        string gameContentRoot,
        string sessionWorkspaceRoot,
        RuntimeSaveLayout saveLayout,
        string? csvRoot,
        string? erbRoot,
        string? resourceRoot,
        string? soundRoot = null,
        string? fontRoot = null,
        string? configurationRoot = null,
        string? temporaryRoot = null,
        string? rootSaveRoot = null,
        string? savDirectoryRoot = null)
        : this(
            sessionRoot,
            gameContentRoot,
            sessionWorkspaceRoot,
            saveLayout,
            csvRoot,
            erbRoot,
            resourceRoot,
            soundRoot,
            fontRoot,
            configurationRoot,
            temporaryRoot,
            rootSaveRoot,
            savDirectoryRoot,
            otherSessionWorkspaceRoots: null)
    {
    }

    public RuntimePaths(
        string sessionRoot,
        string gameContentRoot,
        string sessionWorkspaceRoot,
        RuntimeSaveLayout saveLayout,
        IEnumerable<string> otherSessionWorkspaceRoots)
        : this(
            sessionRoot,
            gameContentRoot,
            sessionWorkspaceRoot,
            saveLayout,
            csvRoot: null,
            erbRoot: null,
            resourceRoot: null,
            soundRoot: null,
            fontRoot: null,
            configurationRoot: null,
            temporaryRoot: null,
            rootSaveRoot: null,
            savDirectoryRoot: null,
            otherSessionWorkspaceRoots: otherSessionWorkspaceRoots)
    {
    }

    internal RuntimePaths(
        string sessionRoot,
        string gameContentRoot,
        string sessionWorkspaceRoot,
        RuntimeSaveLayout saveLayout,
        string? csvRoot,
        string? erbRoot,
        string? resourceRoot,
        string? soundRoot,
        string? fontRoot,
        string? configurationRoot,
        string? temporaryRoot,
        string? rootSaveRoot,
        string? savDirectoryRoot,
        IEnumerable<string>? otherSessionWorkspaceRoots)
    {
        if (!Enum.IsDefined(saveLayout))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "The runtime save layout is invalid.");
        }

        SessionRoot = RuntimePathUtilities.NormalizeAbsolutePath(sessionRoot, nameof(sessionRoot));
        GameContentRoot = RuntimePathUtilities.NormalizeAbsolutePath(gameContentRoot, nameof(gameContentRoot));
        SessionWorkspaceRoot = RuntimePathUtilities.NormalizeAbsolutePath(
            sessionWorkspaceRoot,
            nameof(sessionWorkspaceRoot));
        SaveLayout = saveLayout;

        ValidateRootRelationship();
        ValidateOtherSessionRoots(otherSessionWorkspaceRoots);

        CsvRoot = ResolveCanonicalContentRoot(csvRoot, "CSV");
        ErbRoot = ResolveCanonicalContentRoot(erbRoot, "ERB");
        ResourceRoot = ResolveCanonicalContentRoot(resourceRoot, "resources");
        SoundRoot = ResolveOptionalCanonicalContentRoot(soundRoot, "sound");
        FontRoot = ResolveOptionalCanonicalContentRoot(fontRoot, "font");

        WritableRoot = SessionRoot;
        ConfigurationRoot = NormalizeSessionRootPath(
            configurationRoot ?? SessionRoot,
            SessionRoot,
            nameof(ConfigurationRoot));
        TemporaryRoot = NormalizeSessionRootPath(
            temporaryRoot ?? Path.Combine(SessionRoot, "tmp"),
            Path.Combine(SessionRoot, "tmp"),
            nameof(TemporaryRoot));
        RootSaveRoot = NormalizeSessionRootPath(
            rootSaveRoot ?? SessionRoot,
            SessionRoot,
            nameof(RootSaveRoot));
        SavDirectoryRoot = NormalizeSessionRootPath(
            savDirectoryRoot ?? Path.Combine(SessionRoot, "sav"),
            Path.Combine(SessionRoot, "sav"),
            nameof(SavDirectoryRoot));
    }

    public string SessionRoot { get; }

    /// <summary>
    /// The immutable source root used to construct this SessionRoot. The
    /// runtime itself must not be given access to this path.
    /// </summary>
    public string GameContentRoot { get; }

    /// <summary>
    /// Metadata/workspace parent used for overlap checks. It is not an
    /// independent writable runtime root.
    /// </summary>
    public string SessionWorkspaceRoot { get; }

    public string WritableRoot { get; }

    public string CsvRoot { get; }

    public string ErbRoot { get; }

    public string ResourceRoot { get; }

    public string? SoundRoot { get; }

    public string? FontRoot { get; }

    /// <summary>The SessionRoot directory containing the private config file.</summary>
    public string ConfigurationRoot { get; }

    public string TemporaryRoot { get; }

    /// <summary>Exactly SessionRoot; retained as a P0-02 compatibility property.</summary>
    public string RootSaveRoot { get; }

    /// <summary>Exactly SessionRoot/sav; retained as a P0-02 compatibility property.</summary>
    public string SavDirectoryRoot { get; }

    public RuntimeSaveLayout SaveLayout { get; }

    public string ResolveSavePath(RuntimeRelativePath logicalPath)
    {
        string[] segments = logicalPath.Segments.ToArray();
        if (segments.Length == 0)
        {
            throw SavePathError(logicalPath);
        }

        if (SaveLayout == RuntimeSaveLayout.Root)
        {
            if (segments.Length != 1)
            {
                throw SavePathError(logicalPath);
            }
        }
        else
        {
            ValidateSaveDirectorySegments(segments[..^1], logicalPath);
        }

        if (!IsAllowedSaveFileName(segments[^1]))
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.UnsupportedRuntimeFile,
                "A save file name is outside the fixed runtime contract.",
                logicalPath.Value,
                RuntimeFileArea.Save);
        }

        _ = EmueraSavePathPolicy.Parse(
            SaveLayout,
            logicalPath.Value,
            allowPhysicalSavPrefix: true,
            allowAuxiliaryInRoot: true);

        return ResolveSaveEntryCandidate(
            logicalPath,
            SaveLayout == RuntimeSaveLayout.Root ? RootSaveRoot : SavDirectoryRoot);
    }

    public string ResolveSavePath(string logicalPath) => ResolveSavePath(RuntimeRelativePath.Parse(logicalPath));

    public string ResolveSaveDirectoryPath(RuntimeRelativePath logicalPath)
    {
        if (SaveLayout != RuntimeSaveLayout.SavDirectory || logicalPath.Segments.Count == 0)
        {
            throw SaveDirectoryPathError(logicalPath);
        }

        ValidateSaveDirectorySegments(logicalPath.Segments, logicalPath);
        return ResolveSaveEntryCandidate(logicalPath, SavDirectoryRoot);
    }

    public string ResolveSaveDirectoryPath(string logicalPath) =>
        ResolveSaveDirectoryPath(RuntimeRelativePath.Parse(logicalPath));

    internal string ResolveSaveEntryPath(RuntimeRelativePath logicalPath)
    {
        if (logicalPath.Segments.Count == 0)
        {
            throw SavePathError(logicalPath);
        }

        if (SaveLayout == RuntimeSaveLayout.Root)
        {
            return ResolveSavePath(logicalPath);
        }

        string[] segments = logicalPath.Segments.ToArray();
        ValidateSaveDirectorySegments(segments[..^1], logicalPath);
        if (!IsAllowedSaveFileName(segments[^1]) && !IsAllowedSaveDirectorySegment(segments[^1]))
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.UnsupportedRuntimeFile,
                "A save entry name is outside the fixed runtime contract.",
                logicalPath.Value,
                RuntimeFileArea.Save);
        }

        return ResolveSaveEntryCandidate(logicalPath, SavDirectoryRoot);
    }

    /// <summary>
    /// Resolves a logical path under the selected SessionRoot area. The
    /// physical guard repeats the no-reparse check immediately before I/O.
    /// </summary>
    public string ResolvePhysicalPath(RuntimeFilePath path)
    {
        if (path.Area == RuntimeFileArea.Save)
        {
            return ResolveSavePath(path.RelativePath);
        }

        string root = GetAreaRoot(path.Area);
        string candidate = RuntimePathUtilities.Combine(root, path.RelativePath);
        RuntimePathUtilities.ThrowIfOutside(candidate, root, path.LogicalPath, path.Area);
        candidate = RuntimePathUtilities.ResolveCaseInsensitivePath(candidate, root, path.LogicalPath, path.Area);
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(candidate, path.LogicalPath, path.Area);
        return candidate;
    }

    public string Resolve(RuntimeFilePath path) => ResolvePhysicalPath(path);

    internal string GetAreaRoot(RuntimeFileArea area)
    {
        string root = area switch
        {
            RuntimeFileArea.GameContent => SessionRoot,
            RuntimeFileArea.Configuration => ConfigurationRoot,
            RuntimeFileArea.Save => SaveLayout == RuntimeSaveLayout.Root ? RootSaveRoot : SavDirectoryRoot,
            RuntimeFileArea.Temporary => TemporaryRoot,
            _ => throw new RuntimePathException(
                RuntimePathReasonCodes.PathOutsideArea,
                "The runtime file area is invalid.",
                area: area)
        };
        return root.Equals(SessionRoot, RuntimePathUtilities.PathComparison)
            ? root
            : RuntimePathUtilities.ResolveCaseInsensitivePath(root, SessionRoot, $"<{area}-root>", area);
    }

    /// <summary>
    /// Revalidates the actual directory before it is handed to the fixed
    /// upstream interpreter. Every entry must remain an ordinary file or
    /// directory; there is no content-link exception.
    /// </summary>
    public void ValidateSessionRoot()
    {
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(SessionRoot, "<session-root>");
        ValidateDirectory(SessionRoot, "<session-root>", required: true);
        ValidateDirectory(CsvRoot, "CSV", required: true);
        ValidateDirectory(ErbRoot, "ERB", required: true);
        ValidateRegularFile(Path.Combine(SessionRoot, "emuera.config"), "emuera.config", required: true);
        ValidateDirectory(TemporaryRoot, "tmp", required: true);
        if (SaveLayout == RuntimeSaveLayout.SavDirectory)
        {
            ValidateDirectory(SavDirectoryRoot, "sav", required: true);
        }

        ValidateTree(SessionRoot, "<session-root>");
    }

    internal static bool IsAllowedSaveFileName(string filename)
        => EmueraSavePathPolicy.IsAllowedSaveFileName(filename);

    internal static bool IsAllowedSaveDirectorySegment(string segment) =>
        EmueraSavePathPolicy.IsAllowedSaveDirectorySegment(segment);

    private RuntimeFileAccessException SavePathError(RuntimeRelativePath logicalPath) =>
        new(
            RuntimePathReasonCodes.PathOutsideArea,
            SaveLayout == RuntimeSaveLayout.Root
                ? "Root-layout save paths must not contain a directory segment."
                : "The save path is invalid.",
            logicalPath.Value,
            RuntimeFileArea.Save);

    private static RuntimeFileAccessException SaveDirectoryPathError(RuntimeRelativePath logicalPath) =>
        new(
            RuntimePathReasonCodes.UnsupportedRuntimeFile,
            "Only the nested sav-directory layout supports runtime save directories.",
            logicalPath.Value,
            RuntimeFileArea.Save);

    private static void ValidateSaveDirectorySegments(
        IReadOnlyList<string> segments,
        RuntimeRelativePath logicalPath)
    {
        foreach (string segment in segments)
        {
            if (!IsAllowedSaveDirectorySegment(segment))
            {
                throw new RuntimeFileAccessException(
                    RuntimePathReasonCodes.UnsupportedRuntimeFile,
                    "A save directory segment is outside the fixed runtime directory contract.",
                    logicalPath.Value,
                    RuntimeFileArea.Save);
            }
        }
    }

    private static string ResolveSaveEntryCandidate(RuntimeRelativePath logicalPath, string root)
    {
        if (Path.GetFileName(root).Equals("sav", StringComparison.OrdinalIgnoreCase))
        {
            string parent = Directory.GetParent(root)?.FullName ?? root;
            root = RuntimePathUtilities.ResolveCaseInsensitivePath(root, parent, "sav", RuntimeFileArea.Save);
        }
        string candidate = RuntimePathUtilities.Combine(root, logicalPath);
        RuntimePathUtilities.ThrowIfOutside(candidate, root, logicalPath.Value, RuntimeFileArea.Save);
        return RuntimePathUtilities.ResolveCaseInsensitivePath(candidate, root, logicalPath.Value, RuntimeFileArea.Save);
    }

    private string ResolveCanonicalContentRoot(string? explicitRoot, string name)
    {
        string canonical = Path.Combine(SessionRoot, name);
        if (explicitRoot is null)
        {
            return canonical;
        }

        string normalized = RuntimePathUtilities.NormalizeAbsolutePath(explicitRoot, name);
        if (!string.Equals(normalized, RuntimePathUtilities.NormalizeForComparison(canonical), RuntimePathUtilities.PathComparison))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.CrossSessionPath,
                "A content root must be an entry in the bound SessionRoot.",
                name,
                RuntimeFileArea.GameContent);
        }

        return canonical;
    }

    private string? ResolveOptionalCanonicalContentRoot(string? explicitRoot, string name)
    {
        string canonical = Path.Combine(SessionRoot, name);
        if (explicitRoot is null)
        {
            return Directory.Exists(canonical) ? canonical : null;
        }

        return ResolveCanonicalContentRoot(explicitRoot, name);
    }

    private void ValidateRootRelationship()
    {
        if (string.Equals(SessionRoot, SessionWorkspaceRoot, RuntimePathUtilities.PathComparison))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "SessionRoot and SessionWorkspaceRoot must be different.");
        }

        if (RuntimePathUtilities.PathsOverlap(GameContentRoot, SessionRoot) ||
            RuntimePathUtilities.PathsOverlap(GameContentRoot, SessionWorkspaceRoot))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.CrossSessionPath,
                "Session roots must be physically separate from the game version root.");
        }

        if (RuntimePathUtilities.IsStrictlyWithin(SessionWorkspaceRoot, SessionRoot))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.CrossSessionPath,
                "The session workspace cannot be inside the interpreter root.");
        }

        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(SessionRoot, "<session-root>");
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(GameContentRoot, "<game-content-root>");
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(SessionWorkspaceRoot, "<session-workspace>");
    }

    private void ValidateOtherSessionRoots(IEnumerable<string>? otherSessionWorkspaceRoots)
    {
        if (otherSessionWorkspaceRoots is null)
        {
            return;
        }

        foreach (string otherRoot in otherSessionWorkspaceRoots)
        {
            string normalized = RuntimePathUtilities.NormalizeAbsolutePath(otherRoot, "other session root");
            if (RuntimePathUtilities.PathsOverlap(SessionRoot, normalized) ||
                RuntimePathUtilities.PathsOverlap(SessionWorkspaceRoot, normalized))
            {
                throw new RuntimePathException(
                    RuntimePathReasonCodes.CrossSessionPath,
                    "The SessionRoot overlaps another allocated session root.");
            }

            RuntimePathUtilities.ValidateNoReparsePointsAlongPath(
                normalized,
                "<other-session-root>");
        }
    }

    private static string NormalizeSessionRootPath(string candidate, string expected, string name)
    {
        string normalized = RuntimePathUtilities.NormalizeAbsolutePath(candidate, name);
        if (!string.Equals(normalized, RuntimePathUtilities.NormalizeForComparison(expected), RuntimePathUtilities.PathComparison))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.CrossSessionPath,
                "A runtime private root must be inside the bound SessionRoot.",
                name);
        }

        return normalized;
    }

    private static void ValidateDirectory(string path, string logicalPath, bool required)
    {
        string? parent = Directory.GetParent(path)?.FullName;
        if (parent is not null && Directory.Exists(parent))
            path = RuntimePathUtilities.ResolveCaseInsensitivePath(path, parent, logicalPath);
        RuntimePathUtilities.ThrowIfReparsePoint(path, logicalPath, missingIsAllowed: !required);
        if (required && !Directory.Exists(path))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.EntryNotFound,
                "A required SessionRoot directory is missing.",
                logicalPath);
        }

        if (File.Exists(path))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "A SessionRoot directory is occupied by a file.",
                logicalPath);
        }
    }

    private static void ValidateRegularFile(string path, string logicalPath, bool required)
    {
        string? parent = Directory.GetParent(path)?.FullName;
        if (parent is not null && Directory.Exists(parent))
            path = RuntimePathUtilities.ResolveCaseInsensitivePath(path, parent, logicalPath);
        RuntimePathUtilities.ThrowIfReparsePoint(path, logicalPath, missingIsAllowed: !required);
        if (required && !File.Exists(path))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.EntryNotFound,
                "A required SessionRoot file is missing.",
                logicalPath);
        }

        if (Directory.Exists(path))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "A SessionRoot file is occupied by a directory.",
                logicalPath);
        }

        if (File.Exists(path))
        {
            RuntimePathUtilities.ThrowIfHardLink(path, logicalPath);
        }
    }

    private static void ValidateTree(string root, string logicalPath)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            string childLogicalPath = logicalPath == "<session-root>"
                ? entry.Name
                : $"{logicalPath}/{entry.Name}";
            RuntimePathUtilities.ThrowIfReparsePoint(
                entry.FullName,
                childLogicalPath,
                missingIsAllowed: false);

            if (entry is DirectoryInfo)
            {
                ValidateTree(entry.FullName, childLogicalPath);
            }
            else if (entry is FileInfo)
            {
                RuntimePathUtilities.ThrowIfHardLink(entry.FullName, childLogicalPath);
            }
            else
            {
                throw new RuntimeFileAccessException(
                    RuntimePathReasonCodes.UnsupportedRuntimeFile,
                    "A SessionRoot contains a non-regular filesystem entry.",
                    childLogicalPath);
            }
        }
    }
}
