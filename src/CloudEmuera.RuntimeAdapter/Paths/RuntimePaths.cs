namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Immutable, session-scoped physical roots used by the runtime adapter.
/// Game-provided paths are resolved by logical area and never by the current
/// process directory. The roots are trusted host inputs and are normalized at
/// construction; file operations still need to run through a physical guard.
/// </summary>
public sealed class RuntimePaths
{
    public RuntimePaths(
        string sessionRoot,
        string gameVersionRoot,
        string sessionWorkspaceRoot,
        RuntimeSaveLayout saveLayout)
        : this(
            sessionRoot,
            gameVersionRoot,
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

    /// <summary>
    /// Creates paths with explicitly selected content and writable roots. The
    /// optional roots remain trusted host paths and must satisfy the same
    /// containment rules as the derived roots.
    /// </summary>
    public RuntimePaths(
        string sessionRoot,
        string gameVersionRoot,
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
            gameVersionRoot,
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

    /// <summary>
    /// Creates paths and rejects overlap with workspace roots already allocated
    /// to other sessions.
    /// </summary>
    public RuntimePaths(
        string sessionRoot,
        string gameVersionRoot,
        string sessionWorkspaceRoot,
        RuntimeSaveLayout saveLayout,
        IEnumerable<string> otherSessionWorkspaceRoots)
        : this(
            sessionRoot,
            gameVersionRoot,
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
            otherSessionWorkspaceRoots)
    {
    }

    internal RuntimePaths(
        string sessionRoot,
        string gameVersionRoot,
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
        GameVersionRoot = RuntimePathUtilities.NormalizeAbsolutePath(gameVersionRoot, nameof(gameVersionRoot));
        SessionWorkspaceRoot = RuntimePathUtilities.NormalizeAbsolutePath(
            sessionWorkspaceRoot,
            nameof(sessionWorkspaceRoot));

        SaveLayout = saveLayout;

        ValidateRootRelationship();
        ValidateOtherSessionRoots(otherSessionWorkspaceRoots);

        CsvRoot = ResolveContentRoot(csvRoot, "CSV", "csv");
        ErbRoot = ResolveContentRoot(erbRoot, "ERB", "erb");
        ResourceRoot = ResolveContentRoot(resourceRoot, "resources");
        SoundRoot = ResolveOptionalContentRoot(soundRoot, "sound");
        FontRoot = ResolveOptionalContentRoot(fontRoot, "font");

        WritableRoot = Path.Combine(SessionWorkspaceRoot, "writable");
        ConfigurationRoot = NormalizeWritableRoot(
            configurationRoot ?? Path.Combine(WritableRoot, "config"),
            nameof(ConfigurationRoot));
        TemporaryRoot = NormalizeWritableRoot(
            temporaryRoot ?? Path.Combine(WritableRoot, "tmp"),
            nameof(TemporaryRoot));
        RootSaveRoot = NormalizeWritableRoot(
            rootSaveRoot ?? Path.Combine(WritableRoot, "root-saves"),
            nameof(RootSaveRoot));
        SavDirectoryRoot = NormalizeWritableRoot(
            savDirectoryRoot ?? Path.Combine(WritableRoot, "sav"),
            nameof(SavDirectoryRoot));
    }

    public string SessionRoot { get; }

    public string GameVersionRoot { get; }

    public string SessionWorkspaceRoot { get; }

    public string WritableRoot { get; }

    public string CsvRoot { get; }

    public string ErbRoot { get; }

    public string ResourceRoot { get; }

    public string? SoundRoot { get; }

    public string? FontRoot { get; }

    public string ConfigurationRoot { get; }

    public string TemporaryRoot { get; }

    public string RootSaveRoot { get; }

    public string SavDirectoryRoot { get; }

    public RuntimeSaveLayout SaveLayout { get; }

    public string ResolveSavePath(RuntimeRelativePath logicalPath)
    {
        string value = logicalPath.Value;
        string[] segments = logicalPath.Segments.ToArray();

        if (segments.Length == 0 || (SaveLayout == RuntimeSaveLayout.Root && segments.Length != 1))
        {
            throw SavePathError(logicalPath);
        }

        if (!IsAllowedSaveFileName(segments[^1]))
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.UnsupportedRuntimeFile,
                "The save file name is not part of the fixed runtime save contract.",
                value,
                RuntimeFileArea.Save);
        }

        if (SaveLayout == RuntimeSaveLayout.SavDirectory)
        {
            ValidateSaveDirectorySegments(segments[..^1], logicalPath);
        }

        return ResolveSaveEntryCandidate(logicalPath, SaveLayout == RuntimeSaveLayout.Root ? RootSaveRoot : SavDirectoryRoot);
    }

    public string ResolveSavePath(string logicalPath) => ResolveSavePath(RuntimeRelativePath.Parse(logicalPath));

    /// <summary>
    /// Resolves a directory in the nested <c>sav/</c> layout. Directory names
    /// have their own conservative contract; save-file names are not accepted
    /// as directory segments so a path cannot be ambiguous at the boundary.
    /// </summary>
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

    /// <summary>
    /// Resolves either a file or a directory in the nested <c>sav/</c> layout.
    /// Callers must validate the entry kind and, for files, the save filename
    /// before performing the corresponding operation.
    /// </summary>
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
    /// Resolves a controlled logical path lexically. Actual I/O must use
    /// <see cref="PhysicalPathGuard"/> so that link and reparse-point checks are
    /// repeated immediately before the operation.
    /// </summary>
    public string ResolvePhysicalPath(RuntimeFilePath path)
    {
        if (path.Area == RuntimeFileArea.Save)
        {
            return ResolveSavePath(path.RelativePath);
        }

        string root;
        string candidate;
        if (path.Area == RuntimeFileArea.GameContent)
        {
            string[] segments = path.RelativePath.Segments.ToArray();
            string? contentRoot = segments.Length == 0 ? null : ResolveContentName(segments[0]);
            root = contentRoot ?? GameVersionRoot;
            candidate = root;
            int firstChild = contentRoot is null ? 0 : 1;
            for (int index = firstChild; index < segments.Length; index++)
            {
                candidate = Path.Combine(candidate, segments[index]);
            }

            if (!RuntimePathUtilities.IsSameOrWithin(candidate, root))
            {
                throw new RuntimeFileAccessException(
                    RuntimePathReasonCodes.PathOutsideArea,
                    "The logical path is outside its runtime area.",
                    path.LogicalPath,
                    path.Area);
            }

            RuntimePathUtilities.ThrowIfOutside(candidate, GameVersionRoot, path.LogicalPath, path.Area);
        }
        else
        {
            root = GetAreaRoot(path.Area);
            candidate = RuntimePathUtilities.Combine(root, path.RelativePath);
            RuntimePathUtilities.ThrowIfOutside(candidate, root, path.LogicalPath, path.Area);
        }

        return candidate;
    }

    public string Resolve(RuntimeFilePath path) => ResolvePhysicalPath(path);

    internal string GetAreaRoot(RuntimeFileArea area) => area switch
    {
        RuntimeFileArea.GameContent => GameVersionRoot,
        RuntimeFileArea.Configuration => ConfigurationRoot,
        RuntimeFileArea.Save => SaveLayout == RuntimeSaveLayout.Root ? RootSaveRoot : SavDirectoryRoot,
        RuntimeFileArea.Temporary => TemporaryRoot,
        _ => throw new RuntimePathException(
            RuntimePathReasonCodes.PathOutsideArea,
            "The runtime file area is invalid.",
            area: area)
    };

    internal static bool IsAllowedSaveFileName(string filename)
    {
        if (filename.Equals("global.sav", StringComparison.Ordinal))
        {
            return true;
        }

        return IsNumberedFile(filename, "save", ".sav") ||
            IsNumberedFile(filename, "txt", ".txt") ||
            IsNumberedFile(filename, "img", ".png");
    }

    internal static bool IsAllowedSaveDirectorySegment(string segment) =>
        !IsAllowedSaveFileName(segment) &&
        segment.Length is > 0 and <= 64 &&
        segment.All(static character =>
            character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '-' or '_');

    private static bool IsNumberedFile(string value, string prefix, string suffix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            !value.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        int digitsLength = value.Length - prefix.Length - suffix.Length;
        // The fixed upstream formats an integer with a minimum width of two;
        // accepting up to Int32's decimal width preserves that contract without
        // turning a save area into an arbitrary filename sink.
        if (digitsLength is < 1 or > 10)
        {
            return false;
        }

        for (int index = prefix.Length; index < prefix.Length + digitsLength; index++)
        {
            if (value[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private RuntimeFileAccessException SavePathError(RuntimeRelativePath logicalPath) =>
        new RuntimeFileAccessException(
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
        string candidate = RuntimePathUtilities.Combine(root, logicalPath);
        RuntimePathUtilities.ThrowIfOutside(candidate, root, logicalPath.Value, RuntimeFileArea.Save);
        return candidate;
    }

    private string ResolveContentRoot(string? explicitRoot, params string[] names)
    {
        string? existingRoot = explicitRoot is null
            ? FindExistingContentRoot(GameVersionRoot, names)
            : null;
        string root = explicitRoot is null
            ? existingRoot ?? Path.Combine(GameVersionRoot, names[0])
            : RuntimePathUtilities.NormalizeAbsolutePath(explicitRoot, nameof(explicitRoot));
        EnsureContentRoot(root, names[0], required: explicitRoot is not null || existingRoot is not null);
        RuntimePathUtilities.ThrowIfOutside(root, GameVersionRoot, names[0], RuntimeFileArea.GameContent);
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(root, names[0], RuntimeFileArea.GameContent);
        return root;
    }

    private string? ResolveOptionalContentRoot(string? explicitRoot, params string[] names)
    {
        if (explicitRoot is null)
        {
            string? existing = FindExistingContentRoot(GameVersionRoot, names);
            if (existing is null)
            {
                return null;
            }

            RuntimePathUtilities.ValidateNoReparsePointsAlongPath(existing, names[0], RuntimeFileArea.GameContent);
            return existing;
        }

        string root = RuntimePathUtilities.NormalizeAbsolutePath(explicitRoot, nameof(explicitRoot));
        EnsureContentRoot(root, names[0], required: true);
        RuntimePathUtilities.ThrowIfOutside(root, GameVersionRoot, names[0], RuntimeFileArea.GameContent);
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(root, names[0], RuntimeFileArea.GameContent);
        return root;
    }

    private string? ResolveContentName(string name) =>
        name.Equals("CSV", StringComparison.OrdinalIgnoreCase) ? CsvRoot :
        name.Equals("ERB", StringComparison.OrdinalIgnoreCase) ? ErbRoot :
        name.Equals("resources", StringComparison.OrdinalIgnoreCase) ? ResourceRoot :
        name.Equals("sound", StringComparison.OrdinalIgnoreCase) ? SoundRoot :
        name.Equals("font", StringComparison.OrdinalIgnoreCase) ? FontRoot :
        null;

    private void ValidateRootRelationship()
    {
        if (string.Equals(SessionRoot, SessionWorkspaceRoot, RuntimePathUtilities.PathComparison))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "SessionRoot and SessionWorkspaceRoot must be different.");
        }

        if (RuntimePathUtilities.PathsOverlap(GameVersionRoot, SessionRoot) ||
            RuntimePathUtilities.PathsOverlap(GameVersionRoot, SessionWorkspaceRoot))
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
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(GameVersionRoot, "<game-version-root>");
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
            string normalized = RuntimePathUtilities.NormalizeAbsolutePath(otherRoot, "other session workspace");
            if (RuntimePathUtilities.PathsOverlap(SessionWorkspaceRoot, normalized))
            {
                throw new RuntimePathException(
                    RuntimePathReasonCodes.CrossSessionPath,
                    "The session workspace overlaps another session workspace.");
            }
        }
    }

    private string NormalizeWritableRoot(string root, string name)
    {
        string normalized = RuntimePathUtilities.NormalizeAbsolutePath(root, name);
        if (!RuntimePathUtilities.IsStrictlyWithin(normalized, SessionWorkspaceRoot))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.CrossSessionPath,
                "A writable runtime root must be strictly inside SessionWorkspaceRoot.");
        }

        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(normalized, name);
        return normalized;
    }

    private static string? FindExistingContentRoot(string root, IReadOnlyList<string> names)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        string? found = null;
        foreach (FileSystemInfo entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if (!names.Any(name => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (found is not null)
            {
                throw new RuntimePathException(
                    RuntimePathReasonCodes.LayoutConflict,
                    "The game version contains colliding content directory names.");
            }

            found = entry.FullName;
        }

        return found;
    }

    private static void EnsureContentRoot(string root, string logicalName, bool required)
    {
        if (!Directory.Exists(root))
        {
            if (!required && !File.Exists(root))
            {
                return;
            }

            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "A required game content directory is missing.",
                logicalName,
                RuntimeFileArea.GameContent);
        }

        RuntimePathUtilities.ThrowIfReparsePoint(root, logicalName, RuntimeFileArea.GameContent, missingIsAllowed: false);
    }
}
