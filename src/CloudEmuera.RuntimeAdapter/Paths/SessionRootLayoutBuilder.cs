using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;

namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Builds the directory portion of a session layout without applying mounts.
/// The caller supplies trusted, session-assigned roots; game content cannot
/// supply or redefine them. Build is idempotent and never removes unknown data.
/// </summary>
public sealed class SessionRootLayoutBuilder
{
    private readonly string gameVersionRootInput;
    private readonly string sessionRootInput;
    private readonly string sessionWorkspaceRootInput;
    private readonly bool deriveSessionRoot;
    private readonly List<string> otherSessionWorkspaceRoots = [];

    public SessionRootLayoutBuilder(
        string gameVersionRoot,
        string sessionWorkspaceRoot,
        RuntimeSaveLayout saveLayout = RuntimeSaveLayout.Root)
    {
        gameVersionRootInput = gameVersionRoot;
        sessionWorkspaceRootInput = sessionWorkspaceRoot;
        sessionRootInput = string.Empty;
        deriveSessionRoot = true;
        SaveLayout = saveLayout;
    }

    public SessionRootLayoutBuilder(
        string gameVersionRoot,
        string sessionWorkspaceRoot,
        IEnumerable<string> otherSessionWorkspaceRoots,
        RuntimeSaveLayout saveLayout = RuntimeSaveLayout.Root)
        : this(gameVersionRoot, sessionWorkspaceRoot, saveLayout)
    {
        WithOtherSessionWorkspaceRoots(otherSessionWorkspaceRoots);
    }

    public SessionRootLayoutBuilder(
        string gameVersionRoot,
        string sessionRoot,
        string sessionWorkspaceRoot,
        RuntimeSaveLayout saveLayout = RuntimeSaveLayout.Root)
    {
        gameVersionRootInput = gameVersionRoot;
        sessionRootInput = sessionRoot;
        sessionWorkspaceRootInput = sessionWorkspaceRoot;
        deriveSessionRoot = false;
        SaveLayout = saveLayout;
    }

    public SessionRootLayoutBuilder(
        string gameVersionRoot,
        string sessionRoot,
        string sessionWorkspaceRoot,
        IEnumerable<string> otherSessionWorkspaceRoots,
        RuntimeSaveLayout saveLayout = RuntimeSaveLayout.Root)
        : this(gameVersionRoot, sessionRoot, sessionWorkspaceRoot, saveLayout)
    {
        WithOtherSessionWorkspaceRoots(otherSessionWorkspaceRoots);
    }

    public RuntimeSaveLayout SaveLayout { get; }

    public IReadOnlyList<string> OtherSessionWorkspaceRoots =>
        new ReadOnlyCollection<string>(otherSessionWorkspaceRoots);

    public SessionRootLayoutBuilder WithOtherSessionWorkspaceRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        otherSessionWorkspaceRoots.Clear();
        otherSessionWorkspaceRoots.AddRange(roots);
        return this;
    }

    public SessionRootLayoutBuilder WithAllocatedSessionWorkspaceRoots(IEnumerable<string> roots) =>
        WithOtherSessionWorkspaceRoots(roots);

    public SessionRootLayout Build()
    {
        if (!Enum.IsDefined(SaveLayout))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "The runtime save layout is invalid.");
        }

        string gameVersionRoot = RuntimePathUtilities.NormalizeAbsolutePath(
            gameVersionRootInput,
            nameof(gameVersionRootInput));
        string sessionWorkspaceRoot = RuntimePathUtilities.NormalizeAbsolutePath(
            sessionWorkspaceRootInput,
            nameof(sessionWorkspaceRootInput));
        string sessionRoot = deriveSessionRoot
            ? Path.Combine(sessionWorkspaceRoot, "root")
            : RuntimePathUtilities.NormalizeAbsolutePath(sessionRootInput, nameof(sessionRootInput));

        GameVersionSources sources = ValidateGameVersion(gameVersionRoot);

        // Validate before creating anything so a bad source cannot leave a
        // partially initialized session tree behind.
        var preliminaryPaths = new RuntimePaths(
            sessionRoot,
            gameVersionRoot,
            sessionWorkspaceRoot,
            SaveLayout,
            sources.CsvRoot,
            sources.ErbRoot,
            sources.ResourceRoot,
            sources.SoundRoot,
            sources.FontRoot,
            configurationRoot: null,
            temporaryRoot: null,
            rootSaveRoot: null,
            savDirectoryRoot: null,
            otherSessionWorkspaceRoots: otherSessionWorkspaceRoots);

        EnsureDirectory(preliminaryPaths.SessionWorkspaceRoot, "<session-workspace>");
        EnsureDirectory(preliminaryPaths.SessionRoot, "<session-root>");
        EnsureDirectory(preliminaryPaths.WritableRoot, "writable");
        EnsureDirectory(preliminaryPaths.ConfigurationRoot, "configuration");
        EnsureDirectory(preliminaryPaths.TemporaryRoot, "temporary");
        EnsureDirectory(preliminaryPaths.RootSaveRoot, "root-saves");
        EnsureDirectory(preliminaryPaths.SavDirectoryRoot, "sav");
        EnsureWritableDirectory(preliminaryPaths.ConfigurationRoot, "configuration");
        EnsureWritableDirectory(preliminaryPaths.TemporaryRoot, "temporary");
        EnsureWritableDirectory(preliminaryPaths.RootSaveRoot, "root-saves");
        EnsureWritableDirectory(preliminaryPaths.SavDirectoryRoot, "sav");

        // The target directories are placeholders for the future supervisor's
        // read-only bind mappings. They are never populated from game content.
        EnsureDirectory(Path.Combine(preliminaryPaths.SessionRoot, "CSV"), "CSV target");
        EnsureDirectory(Path.Combine(preliminaryPaths.SessionRoot, "ERB"), "ERB target");
        if (sources.ResourceRoot is not null)
        {
            EnsureDirectory(Path.Combine(preliminaryPaths.SessionRoot, "resources"), "resources target");
        }

        if (sources.SoundRoot is not null)
        {
            EnsureDirectory(Path.Combine(preliminaryPaths.SessionRoot, "sound"), "sound target");
        }

        if (sources.FontRoot is not null)
        {
            EnsureDirectory(Path.Combine(preliminaryPaths.SessionRoot, "font"), "font target");
        }

        EnsureDirectory(Path.Combine(preliminaryPaths.SessionRoot, "sav"), "sav target");

        string privateConfig = Path.Combine(preliminaryPaths.ConfigurationRoot, "emuera.config");
        EnsurePrivateConfigCopy(sources.ConfigurationFile, privateConfig);

        // Recreate the immutable object after directory creation. This is the
        // second physical validation pass required by the layout contract.
        var runtimePaths = new RuntimePaths(
            preliminaryPaths.SessionRoot,
            preliminaryPaths.GameVersionRoot,
            preliminaryPaths.SessionWorkspaceRoot,
            SaveLayout,
            sources.CsvRoot,
            sources.ErbRoot,
            sources.ResourceRoot,
            sources.SoundRoot,
            sources.FontRoot,
            preliminaryPaths.ConfigurationRoot,
            preliminaryPaths.TemporaryRoot,
            preliminaryPaths.RootSaveRoot,
            preliminaryPaths.SavDirectoryRoot,
            otherSessionWorkspaceRoots);

        var mappings = new List<RuntimePathMapping>
        {
            Mapping("root/CSV", sources.CsvRoot, Path.Combine(runtimePaths.SessionRoot, "CSV"), RuntimeAccessMode.ReadOnly, RuntimeFileArea.GameContent),
            Mapping("root/ERB", sources.ErbRoot, Path.Combine(runtimePaths.SessionRoot, "ERB"), RuntimeAccessMode.ReadOnly, RuntimeFileArea.GameContent),
            Mapping("root/sav", runtimePaths.SavDirectoryRoot, Path.Combine(runtimePaths.SessionRoot, "sav"), RuntimeAccessMode.ReadWrite, RuntimeFileArea.Save),
            Mapping("root/emuera.config", privateConfig, Path.Combine(runtimePaths.SessionRoot, "emuera.config"), RuntimeAccessMode.ReadWrite, RuntimeFileArea.Configuration),
            Mapping("writable/config", null, runtimePaths.ConfigurationRoot, RuntimeAccessMode.ReadWrite, RuntimeFileArea.Configuration),
            Mapping("writable/tmp", null, runtimePaths.TemporaryRoot, RuntimeAccessMode.ReadWrite, RuntimeFileArea.Temporary),
            Mapping("writable/root-saves", null, runtimePaths.RootSaveRoot, RuntimeAccessMode.ReadWrite, RuntimeFileArea.Save),
            Mapping("writable/sav", null, runtimePaths.SavDirectoryRoot, RuntimeAccessMode.ReadWrite, RuntimeFileArea.Save)
        };

        if (sources.ResourceRoot is not null)
        {
            mappings.Add(Mapping(
                "root/resources",
                sources.ResourceRoot,
                Path.Combine(runtimePaths.SessionRoot, "resources"),
                RuntimeAccessMode.ReadOnly,
                RuntimeFileArea.GameContent));
        }

        if (sources.SoundRoot is not null)
        {
            mappings.Add(Mapping(
                "root/sound",
                sources.SoundRoot,
                Path.Combine(runtimePaths.SessionRoot, "sound"),
                RuntimeAccessMode.ReadOnly,
                RuntimeFileArea.GameContent));
        }

        if (sources.FontRoot is not null)
        {
            mappings.Add(Mapping(
                "root/font",
                sources.FontRoot,
                Path.Combine(runtimePaths.SessionRoot, "font"),
                RuntimeAccessMode.ReadOnly,
                RuntimeFileArea.GameContent));
        }

        return new SessionRootLayout(
            runtimePaths,
            new ReadOnlyCollection<RuntimePathMapping>(mappings),
            $"saveLayout={SaveLayout}; content=read-only; configuration/save/temp=session-private; mountPlan=not-applied");
    }

    public SessionRootLayout BuildLayout() => Build();

    public static SessionRootLayout Create(
        string gameVersionRoot,
        string sessionWorkspaceRoot,
        RuntimeSaveLayout saveLayout = RuntimeSaveLayout.Root) =>
        new SessionRootLayoutBuilder(gameVersionRoot, sessionWorkspaceRoot, saveLayout).Build();

    private static RuntimePathMapping Mapping(
        string logicalTarget,
        string? source,
        string target,
        RuntimeAccessMode accessMode,
        RuntimeFileArea area) =>
        new(logicalTarget, source, target, accessMode, area);

    private static GameVersionSources ValidateGameVersion(string gameVersionRoot)
    {
        if (!Directory.Exists(gameVersionRoot))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "The game version root does not exist.");
        }

        RuntimePathUtilities.ThrowIfReparsePoint(
            gameVersionRoot,
            "<game-version-root>",
            RuntimeFileArea.GameContent,
            missingIsAllowed: false);
        ValidateTree(gameVersionRoot, "<game-version-root>");

        string csvRoot = FindRequiredDirectory(gameVersionRoot, "CSV", "csv");
        string erbRoot = FindRequiredDirectory(gameVersionRoot, "ERB", "erb");
        string? resourceRoot = FindOptionalDirectory(gameVersionRoot, "resources", "RESOURCES");
        string? soundRoot = FindOptionalDirectory(gameVersionRoot, "sound", "SOUND");
        string? fontRoot = FindOptionalDirectory(gameVersionRoot, "font", "FONT");

        string configurationFile = FindRequiredFile(gameVersionRoot, "emuera.config");
        return new GameVersionSources(csvRoot, erbRoot, resourceRoot, soundRoot, fontRoot, configurationFile);
    }

    private static string FindRequiredDirectory(string root, params string[] names)
    {
        string? path = FindEntry(root, names, expectDirectory: true);
        if (path is null)
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "A required game version directory is missing.",
                names[0],
                RuntimeFileArea.GameContent);
        }

        return path;
    }

    private static string? FindOptionalDirectory(string root, params string[] names) =>
        FindEntry(root, names, expectDirectory: true);

    private static string FindRequiredFile(string root, params string[] names)
    {
        string? path = FindEntry(root, names, expectDirectory: false);
        if (path is null)
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "The game version configuration source is missing.",
                names[0],
                RuntimeFileArea.GameContent);
        }

        return path;
    }

    private static string? FindEntry(string root, IReadOnlyList<string> names, bool expectDirectory)
    {
        string? found = null;
        foreach (FileSystemInfo entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if (!names.Any(name => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            RuntimePathUtilities.ThrowIfReparsePoint(
                entry.FullName,
                entry.Name,
                RuntimeFileArea.GameContent,
                missingIsAllowed: false);

            bool isExpectedType = expectDirectory ? entry is DirectoryInfo : entry is FileInfo;
            if (!isExpectedType)
            {
                throw new RuntimePathException(
                    RuntimePathReasonCodes.LayoutConflict,
                    "A game version entry has the wrong type.",
                    entry.Name,
                    RuntimeFileArea.GameContent);
            }

            if (found is not null)
            {
                throw new RuntimePathException(
                    RuntimePathReasonCodes.LayoutConflict,
                    "The game version contains colliding case variants.",
                    entry.Name,
                    RuntimeFileArea.GameContent);
            }

            found = entry.FullName;
        }

        return found;
    }

    private static void ValidateTree(string root, string logicalPath)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            RuntimePathUtilities.ThrowIfReparsePoint(entry.FullName, logicalPath, RuntimeFileArea.GameContent);
            if (entry is DirectoryInfo)
            {
                ValidateTree(entry.FullName, logicalPath);
            }
            else if (entry is FileInfo file)
            {
                try
                {
                    _ = file.Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new RuntimePathException(
                        RuntimePathReasonCodes.UnsupportedRuntimeFile,
                        "The game version contains an unsupported non-regular entry.",
                        logicalPath,
                        RuntimeFileArea.GameContent,
                        exception);
                }
            }
        }
    }

    private static void EnsureDirectory(string path, string logicalPath)
    {
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(path, logicalPath);
        if (File.Exists(path) ||
            RuntimePathUtilities.IsReparsePoint(path) && !Directory.Exists(path))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "A layout directory conflicts with an existing non-directory entry.",
                logicalPath);
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "A layout directory could not be created.",
                logicalPath,
                innerException: exception);
        }

        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(path, logicalPath);
        if (!Directory.Exists(path))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "A layout directory is not available after creation.",
                logicalPath);
        }
    }

    private static void EnsureWritableDirectory(string path, string logicalPath)
    {
        string probe = Path.Combine(path, $".cloudemuera-layout-probe-{Guid.NewGuid():N}");
        Exception? operationFailure = null;
        try
        {
            RuntimePathUtilities.ValidateNoReparsePointsAlongPath(probe, logicalPath);
            using (FileStream stream = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            RuntimePathUtilities.ValidateNoReparsePointsAlongPath(probe, logicalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RuntimePathException)
        {
            operationFailure = exception is RuntimePathException
                ? exception
                : new RuntimePathException(
                    RuntimePathReasonCodes.LayoutConflict,
                    "A writable runtime directory does not have the required access.",
                    logicalPath,
                    innerException: exception);
        }

        RuntimePathException? cleanupFailure = null;
        try
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            cleanupFailure = new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "A writable runtime directory probe could not be removed.",
                logicalPath,
                innerException: exception);
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            throw cleanupFailure;
        }
    }

    private static void EnsurePrivateConfigCopy(string source, string target)
    {
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(source, "game-configuration");
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(target, "private-configuration");

        if (Directory.Exists(target))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "The private configuration target is a directory.",
                "emuera.config",
                RuntimeFileArea.Configuration);
        }

        if (!File.Exists(target))
        {
            try
            {
                File.Copy(source, target, overwrite: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new RuntimePathException(
                    RuntimePathReasonCodes.LayoutConflict,
                    "The private configuration copy could not be created.",
                    "emuera.config",
                    RuntimeFileArea.Configuration,
                    exception);
            }
        }

        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(target, "private-configuration");
    }

    private sealed record GameVersionSources(
        string CsvRoot,
        string ErbRoot,
        string? ResourceRoot,
        string? SoundRoot,
        string? FontRoot,
        string ConfigurationFile);
}
