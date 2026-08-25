using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Materializes one published GameContent into a persistent SessionRoot.
/// Session management remains the caller's responsibility: this component
/// only applies the supplied manifest, limits and already-authorized roots.
/// </summary>
public sealed class SessionRootLayoutBuilder
{
    public const string BindingMetadataFileName = ".cloudemuera-binding.json";

    private readonly string gameContentRootInput;
    private readonly string sessionRootInput;
    private readonly string? sessionWorkspaceRootInput;
    private readonly bool deriveSessionRoot;
    private readonly RuntimeSaveLayout? expectedSaveLayout;
    private readonly List<string> otherSessionRoots = [];
    private SessionRootPublishedManifest? publishedManifest;
    private SessionRootCopyLimits copyLimits = new();
    private string? rootOnlyContentIdentity;

    public SessionRootLayoutBuilder(string gameContentRoot, string sessionWorkspaceRoot)
    {
        gameContentRootInput = gameContentRoot;
        sessionWorkspaceRootInput = sessionWorkspaceRoot;
        sessionRootInput = string.Empty;
        deriveSessionRoot = true;
    }

    public SessionRootLayoutBuilder(
        string gameContentRoot,
        string sessionWorkspaceRoot,
        RuntimeSaveLayout saveLayout)
        : this(gameContentRoot, sessionWorkspaceRoot)
    {
        expectedSaveLayout = saveLayout;
    }

    public SessionRootLayoutBuilder(
        string gameContentRoot,
        string sessionWorkspaceRoot,
        IEnumerable<string> otherSessionWorkspaceRoots)
        : this(gameContentRoot, sessionWorkspaceRoot)
    {
        WithOtherSessionWorkspaceRoots(otherSessionWorkspaceRoots);
    }

    public SessionRootLayoutBuilder(
        string gameContentRoot,
        string sessionRoot,
        string sessionWorkspaceRoot)
    {
        gameContentRootInput = gameContentRoot;
        sessionRootInput = sessionRoot;
        sessionWorkspaceRootInput = sessionWorkspaceRoot;
        deriveSessionRoot = false;
    }

    public SessionRootLayoutBuilder(
        string gameContentRoot,
        string sessionRoot,
        string sessionWorkspaceRoot,
        RuntimeSaveLayout saveLayout)
        : this(gameContentRoot, sessionRoot, sessionWorkspaceRoot)
    {
        expectedSaveLayout = saveLayout;
    }

    public SessionRootLayoutBuilder(
        string gameContentRoot,
        string sessionRoot,
        string sessionWorkspaceRoot,
        IEnumerable<string> otherSessionWorkspaceRoots)
        : this(gameContentRoot, sessionRoot, sessionWorkspaceRoot)
    {
        WithOtherSessionWorkspaceRoots(otherSessionWorkspaceRoots);
    }

    public static SessionRootLayoutBuilder ForSessionRoot(
        string gameContentRoot,
        string sessionRoot,
        IEnumerable<string>? allocatedOtherSessionRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        string fullSessionRoot = Path.GetFullPath(sessionRoot);
        string workspace = Directory.GetParent(fullSessionRoot)?.FullName
            ?? throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "A SessionRoot must have a workspace parent.");
        var builder = new SessionRootLayoutBuilder(gameContentRoot, fullSessionRoot, workspace);
        if (allocatedOtherSessionRoots is not null)
        {
            builder.WithOtherSessionWorkspaceRoots(allocatedOtherSessionRoots);
        }

        return builder;
    }

    public RuntimeSaveLayout SaveLayout => expectedSaveLayout ?? RuntimeSaveLayout.Root;

    public RuntimeSaveLayout? ExpectedSaveLayout => expectedSaveLayout;

    public IReadOnlyList<string> OtherSessionWorkspaceRoots =>
        new ReadOnlyCollection<string>(otherSessionRoots);

    public SessionRootPublishedManifest? PublishedManifest => publishedManifest;

    public SessionRootCopyLimits CopyLimits => copyLimits;

    public SessionRootLayoutBuilder WithOtherSessionWorkspaceRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        otherSessionRoots.Clear();
        otherSessionRoots.AddRange(roots);
        return this;
    }

    public SessionRootLayoutBuilder WithAllocatedSessionWorkspaceRoots(IEnumerable<string> roots) =>
        WithOtherSessionWorkspaceRoots(roots);

    public SessionRootLayoutBuilder WithPublishedManifest(SessionRootPublishedManifest manifest)
    {
        publishedManifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        return this;
    }

    public SessionRootLayoutBuilder WithCopyLimits(SessionRootCopyLimits limits)
    {
        copyLimits = limits ?? throw new ArgumentNullException(nameof(limits));
        return this;
    }

    /// <summary>
    /// Binds the root-only materialization path to a stable GameId/revision
    /// identity. This value is metadata only; it is never derived from file
    /// bytes.
    /// </summary>
    public SessionRootLayoutBuilder WithRootOnlyContentIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            throw new ArgumentException("A root-only content identity is required.", nameof(identity));
        rootOnlyContentIdentity = identity;
        return this;
    }

    public SessionRootLayout Build() => BuildInternal(publishedManifest ??
        SessionRootPublishedManifest.FromDirectory(gameContentRootInput));

    public SessionRootLayout Build(
        SessionRootPublishedManifest manifest,
        SessionRootCopyLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return BuildInternal(manifest, limits ?? copyLimits);
    }

    /// <summary>
    /// Copies the complete GameContent tree without constructing or persisting
    /// a per-file manifest. The existing explicit-manifest APIs remain the
    /// compatibility path for old SessionRoots and fixtures.
    /// </summary>
    public SessionRootLayout BuildRootOnly(
        string? contentIdentity = null,
        SessionRootCopyLimits? limits = null) =>
        BuildRootOnlyInternal(
            contentIdentity ?? rootOnlyContentIdentity ?? "path-v2",
            limits ?? copyLimits);

    /// <summary>
    /// Explicit production-style entry point. The caller supplies the
    /// published manifest and limits; no Session manager state is inferred.
    /// </summary>
    public static SessionRootLayout Build(
        string gameContentRoot,
        string sessionRoot,
        SessionRootPublishedManifest publishedManifest,
        SessionRootCopyLimits copyLimits,
        IEnumerable<string>? allocatedOtherSessionRoots = null)
    {
        ArgumentNullException.ThrowIfNull(publishedManifest);
        ArgumentNullException.ThrowIfNull(copyLimits);
        return ForSessionRoot(gameContentRoot, sessionRoot, allocatedOtherSessionRoots)
            .WithPublishedManifest(publishedManifest)
            .WithCopyLimits(copyLimits)
            .Build();
    }

    public SessionRootLayout BuildLayout() => Build();

    public static SessionRootLayout Create(
        string gameContentRoot,
        string sessionWorkspaceRoot,
        RuntimeSaveLayout saveLayout = RuntimeSaveLayout.Root) =>
        new SessionRootLayoutBuilder(gameContentRoot, sessionWorkspaceRoot, saveLayout).Build();

    public static SessionRootLayout CreateForSessionRoot(
        string gameContentRoot,
        string sessionRoot,
        IEnumerable<string>? allocatedOtherSessionRoots = null) =>
        ForSessionRoot(gameContentRoot, sessionRoot, allocatedOtherSessionRoots).Build();

    private SessionRootLayout BuildInternal(
        SessionRootPublishedManifest manifest,
        SessionRootCopyLimits? requestedLimits = null)
    {
        SessionRootCopyLimits limits = requestedLimits ?? copyLimits;
        string gameContentRoot = RuntimePathUtilities.NormalizeAbsolutePath(
            gameContentRootInput,
            nameof(gameContentRootInput));
        string sessionRoot = deriveSessionRoot
            ? Path.Combine(
                RuntimePathUtilities.NormalizeAbsolutePath(
                    sessionWorkspaceRootInput!,
                    nameof(sessionWorkspaceRootInput)),
                "root")
            : RuntimePathUtilities.NormalizeAbsolutePath(sessionRootInput, nameof(sessionRootInput));
        string sessionWorkspaceRoot = RuntimePathUtilities.NormalizeAbsolutePath(
            sessionWorkspaceRootInput ?? Directory.GetParent(sessionRoot)!.FullName,
            nameof(sessionWorkspaceRootInput));

        ValidatePublishedGameContent(gameContentRoot, manifest);
        RuntimeSaveLayout saveLayout = InspectSaveLayout(Path.Combine(gameContentRoot, "emuera.config"));
        if (expectedSaveLayout is RuntimeSaveLayout expected && expected != saveLayout)
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "The requested runtime save layout conflicts with emuera.config.",
                "emuera.config",
                RuntimeFileArea.Configuration);
        }

        var paths = new RuntimePaths(
            sessionRoot,
            gameContentRoot,
            sessionWorkspaceRoot,
            saveLayout,
            csvRoot: Path.Combine(sessionRoot, "CSV"),
            erbRoot: Path.Combine(sessionRoot, "ERB"),
            resourceRoot: Path.Combine(sessionRoot, "resources"),
            soundRoot: Path.Combine(sessionRoot, "sound"),
            fontRoot: Path.Combine(sessionRoot, "font"),
            configurationRoot: sessionRoot,
            temporaryRoot: Path.Combine(sessionRoot, "tmp"),
            rootSaveRoot: sessionRoot,
            savDirectoryRoot: Path.Combine(sessionRoot, "sav"),
            otherSessionWorkspaceRoots: otherSessionRoots);

        EnsureDirectory(sessionWorkspaceRoot, "<session-workspace>");
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(sessionRoot, "<session-root>");

        if (Directory.Exists(sessionRoot) || File.Exists(sessionRoot) || RuntimePathUtilities.IsReparsePoint(sessionRoot))
        {
            return BuildExisting(paths, manifest, saveLayout);
        }

        return BuildFresh(paths, manifest, limits);
    }

    private SessionRootLayout BuildRootOnlyInternal(
        string contentIdentity,
        SessionRootCopyLimits limits)
    {
        if (string.IsNullOrWhiteSpace(contentIdentity))
            throw new ArgumentException("A root-only content identity is required.", nameof(contentIdentity));

        string gameContentRoot = RuntimePathUtilities.NormalizeAbsolutePath(
            gameContentRootInput,
            nameof(gameContentRootInput));
        string sessionRoot = deriveSessionRoot
            ? Path.Combine(
                RuntimePathUtilities.NormalizeAbsolutePath(
                    sessionWorkspaceRootInput!,
                    nameof(sessionWorkspaceRootInput)),
                "root")
            : RuntimePathUtilities.NormalizeAbsolutePath(sessionRootInput, nameof(sessionRootInput));
        string sessionWorkspaceRoot = RuntimePathUtilities.NormalizeAbsolutePath(
            sessionWorkspaceRootInput ?? Directory.GetParent(sessionRoot)!.FullName,
            nameof(sessionWorkspaceRootInput));

        ValidateRootOnlyGameContent(gameContentRoot);
        RuntimeSaveLayout saveLayout = InspectSaveLayout(Path.Combine(gameContentRoot, "emuera.config"));
        if (expectedSaveLayout is RuntimeSaveLayout expected && expected != saveLayout)
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "The requested runtime save layout conflicts with emuera.config.",
                "emuera.config",
                RuntimeFileArea.Configuration);
        }

        var paths = new RuntimePaths(
            sessionRoot,
            gameContentRoot,
            sessionWorkspaceRoot,
            saveLayout,
            csvRoot: Path.Combine(sessionRoot, "CSV"),
            erbRoot: Path.Combine(sessionRoot, "ERB"),
            resourceRoot: Path.Combine(sessionRoot, "resources"),
            soundRoot: Path.Combine(sessionRoot, "sound"),
            fontRoot: Path.Combine(sessionRoot, "font"),
            configurationRoot: sessionRoot,
            temporaryRoot: Path.Combine(sessionRoot, "tmp"),
            rootSaveRoot: sessionRoot,
            savDirectoryRoot: Path.Combine(sessionRoot, "sav"),
            otherSessionWorkspaceRoots: otherSessionRoots);

        EnsureDirectory(sessionWorkspaceRoot, "<session-workspace>");
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(sessionRoot, "<session-root>");
        if (Directory.Exists(sessionRoot) || File.Exists(sessionRoot) || RuntimePathUtilities.IsReparsePoint(sessionRoot))
            return BuildRootOnlyExisting(paths, contentIdentity, saveLayout);

        return BuildRootOnlyFresh(paths, contentIdentity, limits);
    }

    private static SessionRootLayout BuildRootOnlyExisting(
        RuntimePaths paths,
        string contentIdentity,
        RuntimeSaveLayout saveLayout)
    {
        RuntimePathUtilities.ThrowIfReparsePoint(paths.SessionRoot, "<session-root>", missingIsAllowed: false);
        if (!Directory.Exists(paths.SessionRoot))
            throw LayoutConflict("An existing SessionRoot is not a directory.", "<session-root>");

        string metadataPath = Path.Combine(paths.SessionRoot, BindingMetadataFileName);
        RuntimePathUtilities.ThrowIfReparsePoint(metadataPath, BindingMetadataFileName, missingIsAllowed: true);
        if (!File.Exists(metadataPath))
            throw LayoutConflict("An existing SessionRoot is missing its binding metadata.", BindingMetadataFileName);
        RuntimePathUtilities.ThrowIfHardLink(metadataPath, BindingMetadataFileName);

        SessionRootBindingMetadata metadata = ReadBindingMetadata(metadataPath);
        if (metadata.SchemaVersion != 2 ||
            !string.Equals(metadata.GameContentIdentity, contentIdentity, StringComparison.Ordinal) ||
            !string.Equals(metadata.ManifestDigest, contentIdentity, StringComparison.Ordinal) ||
            metadata.SaveLayout != saveLayout)
        {
            throw LayoutConflict(
                "The existing SessionRoot is bound to a different root-only GameContent identity.",
                BindingMetadataFileName);
        }

        paths.ValidateSessionRoot();
        RuntimeSaveLayout currentLayout = InspectSaveLayout(Path.Combine(paths.SessionRoot, "emuera.config"));
        if (currentLayout != saveLayout)
            throw LayoutConflict("The existing SessionRoot configuration no longer matches its binding.", "emuera.config");

        return CreateLayout(
            paths,
            contentIdentity,
            Array.Empty<SessionRootManifestEntry>(),
            saveLayout,
            "root-only; manifest=none");
    }

    private static SessionRootLayout BuildRootOnlyFresh(
        RuntimePaths paths,
        string contentIdentity,
        SessionRootCopyLimits limits)
    {
        string parent = Directory.GetParent(paths.SessionRoot)?.FullName
            ?? throw LayoutConflict("A SessionRoot must have a parent directory.", "<session-root>");
        EnsureDirectory(parent, "<session-parent>");
        string staging = Path.Combine(parent, $".cloudemuera-staging-{Guid.NewGuid():N}");
        EnsureStagingPath(staging, parent);
        var state = new CopyState(limits);

        try
        {
            Directory.CreateDirectory(staging);
            RuntimePathUtilities.ThrowIfReparsePoint(staging, "<staging-root>", missingIsAllowed: false);
            CopyDirectoryDirect(paths.GameContentRoot, paths.GameContentRoot, staging, state);
            MaterializeFixedCaseAliasesDirect(paths.GameContentRoot, staging, state);

            EnsureDirectory(Path.Combine(staging, "tmp"), "tmp");
            if (paths.SaveLayout == RuntimeSaveLayout.SavDirectory)
            {
                string savRoot = Path.Combine(staging, "sav");
                EnsureDirectory(savRoot, "sav");
                SetPrivateDirectoryMode(savRoot);
            }

            var stagingPaths = new RuntimePaths(
                staging,
                paths.GameContentRoot,
                paths.SessionWorkspaceRoot,
                paths.SaveLayout,
                csvRoot: Path.Combine(staging, "CSV"),
                erbRoot: Path.Combine(staging, "ERB"),
                resourceRoot: Path.Combine(staging, "resources"),
                soundRoot: Path.Combine(staging, "sound"),
                fontRoot: Path.Combine(staging, "font"),
                configurationRoot: staging,
                temporaryRoot: Path.Combine(staging, "tmp"),
                rootSaveRoot: staging,
                savDirectoryRoot: Path.Combine(staging, "sav"),
                otherSessionWorkspaceRoots: null);
            WriteBindingMetadata(staging, 2, contentIdentity, paths.SaveLayout);
            stagingPaths.ValidateSessionRoot();

            if (Directory.Exists(paths.SessionRoot) || File.Exists(paths.SessionRoot) || RuntimePathUtilities.IsReparsePoint(paths.SessionRoot))
                throw LayoutConflict("The final SessionRoot appeared while the copy was in progress.", "<session-root>");

            Directory.Move(staging, paths.SessionRoot);
            return CreateLayout(
                paths,
                contentIdentity,
                Array.Empty<SessionRootManifestEntry>(),
                paths.SaveLayout,
                "root-only; manifest=none");
        }
        catch
        {
            CleanupStaging(staging, parent);
            throw;
        }
    }

    private static void CopyDirectoryDirect(
        string sourceRoot,
        string currentSource,
        string stagingRoot,
        CopyState state)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(currentSource).EnumerateFileSystemInfos()
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(sourceRoot, entry.FullName).Replace('\\', '/');
            string target = CombineRelative(stagingRoot, relative);
            RuntimePathUtilities.ThrowIfReparsePoint(
                entry.FullName,
                relative,
                RuntimeFileArea.GameContent,
                missingIsAllowed: false);
            if (entry is DirectoryInfo)
            {
                state.AddDirectory();
                EnsureDirectory(target, relative);
                CopyDirectoryDirect(sourceRoot, entry.FullName, stagingRoot, state);
            }
            else if (entry is FileInfo)
            {
                CopyFileDirect(entry.FullName, target, relative, state);
            }
            else
            {
                throw new RuntimeFileAccessException(
                    RuntimePathReasonCodes.UnsupportedRuntimeFile,
                    "The GameContent contains a non-regular filesystem entry.",
                    relative,
                    RuntimeFileArea.GameContent);
            }
        }
    }

    private static void CopyFileDirect(
        string source,
        string target,
        string logicalPath,
        CopyState state)
    {
        RuntimePathUtilities.ThrowIfReparsePoint(source, logicalPath, RuntimeFileArea.GameContent, false);
        RuntimePathUtilities.ThrowIfHardLink(source, logicalPath, RuntimeFileArea.GameContent);
        var sourceInfo = new FileInfo(source);
        if (!sourceInfo.Exists)
            throw LayoutConflict("A GameContent file disappeared while it was being copied.", logicalPath);
        long expectedLength = sourceInfo.Length;
        state.AddFile(expectedLength, logicalPath);
        string? parent = Directory.GetParent(target)?.FullName;
        if (parent is null)
            throw LayoutConflict("A GameContent file has no target parent.", logicalPath);
        EnsureDirectory(parent, Path.GetRelativePath(Path.GetDirectoryName(target) ?? parent, parent).Replace('\\', '/'));

        byte[] buffer = new byte[64 * 1024];
        using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
        using var targetStream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan);
        long copiedLength = 0;
        int read;
        while ((read = sourceStream.Read(buffer, 0, buffer.Length)) != 0)
        {
            copiedLength = checked(copiedLength + read);
            if (copiedLength > expectedLength || copiedLength > state.Limits.MaxSingleFileBytes)
                throw LayoutConflict("A GameContent file exceeded its copy limit.", logicalPath);
            targetStream.Write(buffer, 0, read);
        }

        targetStream.Flush(flushToDisk: false);
        if (copiedLength != expectedLength || new FileInfo(source).Length != expectedLength)
            throw LayoutConflict("A GameContent file changed size while it was being copied.", logicalPath);

        RuntimePathUtilities.ThrowIfReparsePoint(target, logicalPath, RuntimeFileArea.GameContent, false);
        RuntimePathUtilities.ThrowIfHardLink(target, logicalPath, RuntimeFileArea.GameContent);
        if (new FileInfo(target).Length != expectedLength)
            throw LayoutConflict("A copied GameContent file has an unexpected length.", logicalPath);
        SetSafeFileMode(target);
    }

    private static SessionRootLayout BuildExisting(
        RuntimePaths paths,
        SessionRootPublishedManifest manifest,
        RuntimeSaveLayout saveLayout)
    {
        RuntimePathUtilities.ThrowIfReparsePoint(paths.SessionRoot, "<session-root>", missingIsAllowed: false);
        if (!Directory.Exists(paths.SessionRoot))
        {
            throw LayoutConflict("An existing SessionRoot is not a directory.", "<session-root>");
        }

        string metadataPath = Path.Combine(paths.SessionRoot, BindingMetadataFileName);
        RuntimePathUtilities.ThrowIfReparsePoint(metadataPath, BindingMetadataFileName, missingIsAllowed: true);
        if (!File.Exists(metadataPath))
        {
            throw LayoutConflict(
                "An existing SessionRoot is missing its binding metadata.",
                BindingMetadataFileName);
        }

        RuntimePathUtilities.ThrowIfHardLink(metadataPath, BindingMetadataFileName);

        SessionRootBindingMetadata metadata = ReadBindingMetadata(metadataPath);
        if (metadata.SchemaVersion != 1 ||
            !string.Equals(metadata.GameContentIdentity, manifest.GameContentIdentity, StringComparison.Ordinal) ||
            !string.Equals(metadata.ManifestDigest, manifest.ManifestDigest, StringComparison.Ordinal) ||
            metadata.SaveLayout != saveLayout)
        {
            throw LayoutConflict(
                "The existing SessionRoot is bound to a different GameContent or manifest.",
                BindingMetadataFileName);
        }

        paths.ValidateSessionRoot();
        ValidateManifestEntries(paths.SessionRoot, manifest, verifyLengths: false);
        RuntimeSaveLayout currentLayout = InspectSaveLayout(Path.Combine(paths.SessionRoot, "emuera.config"));
        if (currentLayout != saveLayout)
        {
            throw LayoutConflict(
                "The existing SessionRoot configuration no longer matches its binding.",
                "emuera.config");
        }

        MaterializeFixedCaseAliases(paths.GameContentRoot, paths.SessionRoot, manifest, state: null);
        return CreateLayout(paths, manifest, saveLayout);
    }

    private static SessionRootLayout BuildFresh(
        RuntimePaths paths,
        SessionRootPublishedManifest manifest,
        SessionRootCopyLimits limits)
    {
        string parent = Directory.GetParent(paths.SessionRoot)?.FullName
            ?? throw LayoutConflict("A SessionRoot must have a parent directory.", "<session-root>");
        EnsureDirectory(parent, "<session-parent>");
        string staging = Path.Combine(parent, $".cloudemuera-staging-{Guid.NewGuid():N}");
        EnsureStagingPath(staging, parent);
        var state = new CopyState(limits);

        try
        {
            Directory.CreateDirectory(staging);
            RuntimePathUtilities.ThrowIfReparsePoint(staging, "<staging-root>", missingIsAllowed: false);

            foreach (SessionRootManifestEntry entry in manifest.Entries
                         .Where(item => item.Kind == SessionRootManifestEntryKind.Directory)
                         .OrderBy(item => item.RelativePath.Count(static character => character == '/'))
                         .ThenBy(item => item.RelativePath, StringComparer.Ordinal))
            {
                state.AddDirectory();
                string target = CombineRelative(staging, entry.RelativePath);
                EnsureDirectory(target, entry.RelativePath);
            }

            foreach (SessionRootManifestEntry entry in manifest.Entries
                         .Where(item => item.Kind == SessionRootManifestEntryKind.File)
                         .OrderBy(item => item.RelativePath, StringComparer.Ordinal))
            {
                CopyFile(
                    paths.GameContentRoot,
                    staging,
                    entry,
                    state);
            }

            MaterializeFixedCaseAliases(paths.GameContentRoot, staging, manifest, state);

            EnsureDirectory(Path.Combine(staging, "tmp"), "tmp");
            if (paths.SaveLayout == RuntimeSaveLayout.SavDirectory)
            {
                string savRoot = Path.Combine(staging, "sav");
                EnsureDirectory(savRoot, "sav");
                SetPrivateDirectoryMode(savRoot);
            }

            var stagingPaths = new RuntimePaths(
                staging,
                paths.GameContentRoot,
                paths.SessionWorkspaceRoot,
                paths.SaveLayout,
                csvRoot: Path.Combine(staging, "CSV"),
                erbRoot: Path.Combine(staging, "ERB"),
                resourceRoot: Path.Combine(staging, "resources"),
                soundRoot: Path.Combine(staging, "sound"),
                fontRoot: Path.Combine(staging, "font"),
                configurationRoot: staging,
                temporaryRoot: Path.Combine(staging, "tmp"),
                rootSaveRoot: staging,
                savDirectoryRoot: Path.Combine(staging, "sav"),
                otherSessionWorkspaceRoots: null);
            stagingPaths.ValidateSessionRoot();
            ValidateManifestEntries(staging, manifest, verifyLengths: true);
            WriteBindingMetadata(staging, manifest, paths.SaveLayout);
            stagingPaths.ValidateSessionRoot();

            if (Directory.Exists(paths.SessionRoot) || File.Exists(paths.SessionRoot) || RuntimePathUtilities.IsReparsePoint(paths.SessionRoot))
            {
                throw LayoutConflict(
                    "The final SessionRoot appeared while the copy was in progress.",
                    "<session-root>");
            }

            Directory.Move(staging, paths.SessionRoot);
            return CreateLayout(paths, manifest, paths.SaveLayout);
        }
        catch
        {
            CleanupStaging(staging, parent);
            throw;
        }
    }

    private static void CopyFile(
        string sourceRoot,
        string stagingRoot,
        SessionRootManifestEntry manifestEntry,
        CopyState state)
    {
        string source = CombineRelative(sourceRoot, manifestEntry.RelativePath);
        string target = CombineRelative(stagingRoot, manifestEntry.RelativePath);
        RuntimePathUtilities.ThrowIfReparsePoint(source, manifestEntry.RelativePath, RuntimeFileArea.GameContent, false);
        RuntimePathUtilities.ThrowIfHardLink(source, manifestEntry.RelativePath, RuntimeFileArea.GameContent);
        if (!File.Exists(source))
        {
            throw LayoutConflict("A manifest file is missing from the GameContent.", manifestEntry.RelativePath);
        }

        var sourceInfo = new FileInfo(source);
        if (sourceInfo.Length != manifestEntry.Length)
        {
            throw LayoutConflict("A manifest file changed size before copying.", manifestEntry.RelativePath);
        }

        state.AddFile(sourceInfo.Length, manifestEntry.RelativePath);
        string? parent = Directory.GetParent(target)?.FullName;
        if (parent is null)
        {
            throw LayoutConflict("A manifest file has no target parent.", manifestEntry.RelativePath);
        }

        EnsureDirectory(parent, Path.GetRelativePath(stagingRoot, parent).Replace('\\', '/'));
        byte[] buffer = new byte[64 * 1024];
        using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
        using var targetStream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan);
        long copiedLength = 0;
        int read;
        while ((read = sourceStream.Read(buffer, 0, buffer.Length)) != 0)
        {
            copiedLength = checked(copiedLength + read);
            if (copiedLength > manifestEntry.Length || copiedLength > state.Limits.MaxSingleFileBytes)
            {
                throw LayoutConflict("A manifest file exceeded its reserved copy limit.", manifestEntry.RelativePath);
            }

            targetStream.Write(buffer, 0, read);
        }

        targetStream.Flush(flushToDisk: false);
        if (copiedLength != manifestEntry.Length)
        {
            throw LayoutConflict("A copied file did not retain its manifest length.", manifestEntry.RelativePath);
        }

        RuntimePathUtilities.ThrowIfReparsePoint(target, manifestEntry.RelativePath, RuntimeFileArea.GameContent, false);
        RuntimePathUtilities.ThrowIfHardLink(target, manifestEntry.RelativePath, RuntimeFileArea.GameContent);
        FileInfo targetInfo = new(target);
        if (targetInfo.Length != manifestEntry.Length)
        {
            throw LayoutConflict("A copied file did not retain its manifest length.", manifestEntry.RelativePath);
        }

        if (new FileInfo(source).Length != manifestEntry.Length)
        {
            throw LayoutConflict("The GameContent changed size while it was being copied.", manifestEntry.RelativePath);
        }

        SetSafeFileMode(target);
    }

    private static SessionRootLayout CreateLayout(
        RuntimePaths paths,
        SessionRootPublishedManifest manifest,
        RuntimeSaveLayout saveLayout)
        => CreateLayout(
            paths,
            manifest.ManifestDigest,
            manifest.Entries,
            saveLayout,
            $"manifest={manifest.ManifestDigest}");

    private static SessionRootLayout CreateLayout(
        RuntimePaths paths,
        string contentIdentity,
        IReadOnlyList<SessionRootManifestEntry> copiedEntries,
        RuntimeSaveLayout saveLayout,
        string materializationDescription)
    {
        var mappings = new List<RuntimePathMapping>
        {
            new(
                "root",
                null,
                paths.SessionRoot,
                RuntimeAccessMode.ReadWrite,
                RuntimeFileArea.GameContent),
            new(
                "root/emuera.config",
                null,
                Path.Combine(paths.SessionRoot, "emuera.config"),
                RuntimeAccessMode.ReadWrite,
                RuntimeFileArea.Configuration),
            new(
                "root/tmp",
                null,
                paths.TemporaryRoot,
                RuntimeAccessMode.ReadWrite,
                RuntimeFileArea.Temporary),
            new(
                saveLayout == RuntimeSaveLayout.Root ? "root/saves" : "root/sav",
                null,
                saveLayout == RuntimeSaveLayout.Root ? paths.RootSaveRoot : paths.SavDirectoryRoot,
                RuntimeAccessMode.ReadWrite,
                RuntimeFileArea.Save)
        };

        return new SessionRootLayout(
            paths,
            new ReadOnlyCollection<RuntimePathMapping>(mappings),
            contentIdentity,
            copiedEntries,
            $"saveLayout={saveLayout}; content=complete-copy; {materializationDescription}; atomicPublish=true");
    }

    private static void ValidateRootOnlyGameContent(string gameContentRoot)
    {
        RuntimePathUtilities.ThrowIfReparsePoint(
            gameContentRoot,
            "<game-content-root>",
            RuntimeFileArea.GameContent,
            missingIsAllowed: false);
        if (!Directory.Exists(gameContentRoot))
            throw LayoutConflict("The GameContent root does not exist.", "<game-content-root>");

        ValidateRequiredSourceEntry(gameContentRoot, "CSV", directory: true);
        ValidateRequiredSourceEntry(gameContentRoot, "ERB", directory: true);
        ValidateRequiredSourceEntry(gameContentRoot, "emuera.config", directory: false);
    }

    private static void ValidatePublishedGameContent(
        string gameContentRoot,
        SessionRootPublishedManifest manifest)
    {
        RuntimePathUtilities.ThrowIfReparsePoint(
            gameContentRoot,
            "<game-content-root>",
            RuntimeFileArea.GameContent,
            missingIsAllowed: false);
        if (!Directory.Exists(gameContentRoot))
        {
            throw LayoutConflict("The GameContent root does not exist.", "<game-content-root>");
        }

        Dictionary<string, SessionRootManifestEntry> actual = ScanTree(gameContentRoot);
        var expected = manifest.Entries.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        if (actual.Count != expected.Count || actual.Keys.Except(expected.Keys, StringComparer.Ordinal).Any() ||
            expected.Keys.Except(actual.Keys, StringComparer.Ordinal).Any())
        {
            throw LayoutConflict("The GameContent entries do not exactly match the published manifest.", "<game-content-root>");
        }

        foreach ((string path, SessionRootManifestEntry expectedEntry) in expected)
        {
            SessionRootManifestEntry actualEntry = actual[path];
            if (actualEntry.Kind != expectedEntry.Kind ||
                actualEntry.Length != expectedEntry.Length)
            {
                throw LayoutConflict("A GameContent entry does not match the published manifest.", path);
            }
        }

        ValidateRequiredSourceEntry(gameContentRoot, "CSV", directory: true);
        ValidateRequiredSourceEntry(gameContentRoot, "ERB", directory: true);
        ValidateRequiredSourceEntry(gameContentRoot, "emuera.config", directory: false);
    }

    private static Dictionary<string, SessionRootManifestEntry> ScanTree(string root)
    {
        var result = new Dictionary<string, SessionRootManifestEntry>(StringComparer.Ordinal);
        ScanDirectory(root, root, result);
        return result;
    }

    private static void ScanDirectory(
        string root,
        string current,
        IDictionary<string, SessionRootManifestEntry> result)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(current).EnumerateFileSystemInfos()
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
            RuntimePathUtilities.ThrowIfReparsePoint(
                entry.FullName,
                relative,
                RuntimeFileArea.GameContent,
                missingIsAllowed: false);
            if (entry is DirectoryInfo)
            {
                result.Add(relative, new SessionRootManifestEntry(
                    relative,
                    SessionRootManifestEntryKind.Directory,
                    0,
                    string.Empty));
                ScanDirectory(root, entry.FullName, result);
            }
            else if (entry is FileInfo file)
            {
                RuntimePathUtilities.ThrowIfHardLink(file.FullName, relative, RuntimeFileArea.GameContent);
                result.Add(relative, new SessionRootManifestEntry(
                    relative,
                    SessionRootManifestEntryKind.File,
                    file.Length));
            }
            else
            {
                throw new RuntimeFileAccessException(
                    RuntimePathReasonCodes.UnsupportedRuntimeFile,
                    "The GameContent contains a non-regular filesystem entry.",
                    relative,
                    RuntimeFileArea.GameContent);
            }
        }
    }

    private static void ValidateRequiredSourceEntry(string root, string name, bool directory)
    {
        string path = Path.Combine(root, name);
        if (!directory)
        {
            path = ResolveFixedCaseFile(path) ?? path;
        }
        RuntimePathUtilities.ThrowIfReparsePoint(path, name, RuntimeFileArea.GameContent, missingIsAllowed: false);
        bool exists = directory ? Directory.Exists(path) : File.Exists(path);
        if (!exists)
        {
            throw LayoutConflict(
                directory ? "A required GameContent directory is missing." : "The GameContent configuration is missing.",
                name);
        }
    }

    private static RuntimeSaveLayout InspectSaveLayout(string configurationFile)
    {
        string resolved = ResolveFixedCaseFile(configurationFile) ?? configurationFile;
        try
        {
            return EmueraSaveLayoutInspector.InspectFile(resolved);
        }
        catch (RuntimeSaveLayoutInspectionException exception)
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "The GameContent emuera.config does not contain a valid save layout.",
                "emuera.config",
                RuntimeFileArea.Configuration,
                exception);
        }
    }

    /// <summary>
    /// Fixed-name files the pinned upstream loader reads by exact case on Linux.
    /// The session copy is private and disposable, so a missing exact name with a
    /// unique case-insensitive match is materialized as an alias; ambiguous case
    /// variants never create an alias.
    /// </summary>
    private static readonly (string FixedPath, string DisplayName)[] FixedCaseNames =
    [
        ("CSV/GAMEBASE.CSV", "GAMEBASE.CSV"),
        ("CSV/_Rename.csv", "_Rename.csv"),
        ("CSV/_Replace.csv", "_Replace.csv"),
        ("emuera.config", "emuera.config"),
    ];

    private static void MaterializeFixedCaseAliases(
        string sourceRoot,
        string stagingRoot,
        SessionRootPublishedManifest manifest,
        CopyState? state)
    {
        HashSet<string> exactFiles = new(
            manifest.Entries.Where(entry => entry.Kind == SessionRootManifestEntryKind.File)
                .Select(entry => entry.RelativePath),
            StringComparer.Ordinal);
        foreach ((string fixedPath, string displayName) in FixedCaseNames)
        {
            if (exactFiles.Contains(fixedPath)) continue;
            string? match = null;
            foreach (SessionRootManifestEntry entry in manifest.Entries)
            {
                if (entry.Kind != SessionRootManifestEntryKind.File
                    || !string.Equals(entry.RelativePath, fixedPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (match is not null) { match = null; break; } // ambiguous case variants
                match = entry.RelativePath;
            }
            if (match is null) continue;
            string target = CombineRelative(stagingRoot, fixedPath);
            if (File.Exists(target)) continue;
            CopyAliasFile(sourceRoot, stagingRoot, match, fixedPath, state);
        }
    }

    private static void MaterializeFixedCaseAliasesDirect(
        string sourceRoot,
        string stagingRoot,
        CopyState state)
    {
        foreach ((string fixedPath, _) in FixedCaseNames)
        {
            string exactSource = CombineRelative(sourceRoot, fixedPath);
            if (File.Exists(exactSource))
                continue;

            string? directory = Path.GetDirectoryName(exactSource);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                continue;
            string filename = Path.GetFileName(exactSource);
            string? match = null;
            foreach (FileSystemInfo candidate in new DirectoryInfo(directory).EnumerateFileSystemInfos()
                         .Where(item => string.Equals(item.Name, filename, StringComparison.OrdinalIgnoreCase)))
            {
                RuntimePathUtilities.ThrowIfReparsePoint(
                    candidate.FullName,
                    fixedPath,
                    RuntimeFileArea.GameContent,
                    missingIsAllowed: false);
                if (candidate is not FileInfo)
                    continue;
                RuntimePathUtilities.ThrowIfHardLink(candidate.FullName, fixedPath, RuntimeFileArea.GameContent);
                if (match is not null)
                {
                    match = null;
                    break;
                }

                match = candidate.Name;
            }

            if (match is null)
                continue;
            string directoryRelative = Path.GetDirectoryName(fixedPath)?.Replace('\\', '/') ?? string.Empty;
            string sourceRelative = string.IsNullOrEmpty(directoryRelative)
                ? match
                : $"{directoryRelative}/{match}";
            CopyAliasFile(sourceRoot, stagingRoot, sourceRelative, fixedPath, state);
        }
    }

    private static void CopyAliasFile(
        string sourceRoot,
        string stagingRoot,
        string sourceRelative,
        string targetRelative,
        CopyState? state)
    {
        string source = CombineRelative(sourceRoot, sourceRelative);
        string target = CombineRelative(stagingRoot, targetRelative);
        RuntimePathUtilities.ThrowIfReparsePoint(source, sourceRelative, RuntimeFileArea.GameContent, missingIsAllowed: false);
        RuntimePathUtilities.ThrowIfHardLink(source, sourceRelative, RuntimeFileArea.GameContent);
        var sourceInfo = new FileInfo(source);
        if (!sourceInfo.Exists)
        {
            throw LayoutConflict("A fixed-case alias source is missing from the GameContent.", sourceRelative);
        }
        state?.AddFile(sourceInfo.Length, targetRelative);
        string? parent = Directory.GetParent(target)?.FullName;
        if (parent is null)
        {
            throw LayoutConflict("A fixed-case alias has no target parent.", targetRelative);
        }
        EnsureDirectory(parent, Path.GetRelativePath(stagingRoot, parent).Replace('\\', '/'));
        byte[] buffer = new byte[64 * 1024];
        using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
        using var targetStream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan);
        sourceStream.CopyTo(targetStream, buffer.Length);
        targetStream.Flush(flushToDisk: false);
        SetSafeFileMode(target);
    }

    /// <summary>
    /// Resolves a fixed-name file case-insensitively when the exact name is absent
    /// and a unique case-insensitive match exists on the (Linux) filesystem.
    /// </summary>
    private static string? ResolveFixedCaseFile(string path)
    {
        if (File.Exists(path)) return path;
        string? directory = Path.GetDirectoryName(path);
        string filename = Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;
        string? match = null;
        foreach (string candidate in Directory.EnumerateFiles(directory))
        {
            if (!string.Equals(Path.GetFileName(candidate), filename, StringComparison.OrdinalIgnoreCase)) continue;
            if (match is not null) return null; // ambiguous
            match = candidate;
        }
        return match;
    }

    private static void ValidateManifestEntries(
        string root,
        SessionRootPublishedManifest manifest,
        bool verifyLengths)
    {
        foreach (SessionRootManifestEntry entry in manifest.Entries)
        {
            string path = CombineRelative(root, entry.RelativePath);
            RuntimePathUtilities.ThrowIfReparsePoint(path, entry.RelativePath, missingIsAllowed: false);
            if (entry.Kind == SessionRootManifestEntryKind.Directory)
            {
                if (!Directory.Exists(path))
                {
                    throw LayoutConflict("A copied manifest directory is missing.", entry.RelativePath);
                }

                continue;
            }

            if (!File.Exists(path))
            {
                throw LayoutConflict("A copied manifest file is missing.", entry.RelativePath);
            }

            RuntimePathUtilities.ThrowIfHardLink(path, entry.RelativePath, RuntimeFileArea.GameContent);
            FileInfo file = new(path);
            if (verifyLengths && file.Length != entry.Length)
            {
                throw LayoutConflict("A copied manifest file has an unexpected length.", entry.RelativePath);
            }
        }
    }

    private static SessionRootBindingMetadata ReadBindingMetadata(string path)
    {
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            SessionRootBindingMetadata? metadata = JsonSerializer.Deserialize<SessionRootBindingMetadata>(json);
            return metadata ?? throw new InvalidDataException("Binding metadata is empty.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "The SessionRoot binding metadata is damaged.",
                BindingMetadataFileName,
                innerException: exception);
        }
    }

    private static void WriteBindingMetadata(
        string root,
        SessionRootPublishedManifest manifest,
        RuntimeSaveLayout saveLayout)
        => WriteBindingMetadata(root, 1, manifest.ManifestDigest, saveLayout);

    private static void WriteBindingMetadata(
        string root,
        int schemaVersion,
        string contentIdentity,
        RuntimeSaveLayout saveLayout)
    {
        var metadata = new SessionRootBindingMetadata
        {
            SchemaVersion = schemaVersion,
            GameContentIdentity = contentIdentity,
            ManifestDigest = contentIdentity,
            SaveLayout = saveLayout
        };
        string path = Path.Combine(root, BindingMetadataFileName);
        string json = JsonSerializer.Serialize(metadata) + "\n";
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
        writer.Write(json);
        writer.Flush();
        stream.Flush(flushToDisk: false);
        SetSafeFileMode(path);
    }


    private static string CombineRelative(string root, string relativePath)
    {
        RuntimeRelativePath logical = RuntimeRelativePath.Parse(relativePath);
        return RuntimePathUtilities.Combine(root, logical);
    }

    private static void EnsureDirectory(string path, string logicalPath)
    {
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(path, logicalPath);
        RuntimePathUtilities.ThrowIfReparsePoint(path, logicalPath, missingIsAllowed: true);
        if (File.Exists(path))
        {
            throw LayoutConflict("A directory target is occupied by a file.", logicalPath);
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                "A runtime directory could not be created.",
                logicalPath,
                innerException: exception);
        }

        RuntimePathUtilities.ThrowIfReparsePoint(path, logicalPath, missingIsAllowed: false);
        if (!Directory.Exists(path))
        {
            throw LayoutConflict("A runtime directory is not available after creation.", logicalPath);
        }
    }

    private static void EnsureStagingPath(string staging, string parent)
    {
        string normalizedParent = RuntimePathUtilities.NormalizeForComparison(parent);
        string normalizedStaging = RuntimePathUtilities.NormalizeForComparison(staging);
        if (!RuntimePathUtilities.IsStrictlyWithin(normalizedStaging, normalizedParent) ||
            !Path.GetFileName(normalizedStaging).StartsWith(".cloudemuera-staging-", StringComparison.Ordinal))
        {
            throw LayoutConflict("The staging path is outside its assigned parent.", "<staging-root>");
        }

        if (File.Exists(staging) || Directory.Exists(staging) || RuntimePathUtilities.IsReparsePoint(staging))
        {
            throw LayoutConflict("The random staging path is already occupied.", "<staging-root>");
        }
    }

    private static void CleanupStaging(string staging, string parent)
    {
        try
        {
            string normalizedParent = RuntimePathUtilities.NormalizeForComparison(parent);
            string normalizedStaging = RuntimePathUtilities.NormalizeForComparison(staging);
            if (!RuntimePathUtilities.IsStrictlyWithin(normalizedStaging, normalizedParent) ||
                !Path.GetFileName(normalizedStaging).StartsWith(".cloudemuera-staging-", StringComparison.Ordinal))
            {
                return;
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            else if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
        catch
        {
            // The original validation/copy failure is the useful diagnostic.
        }
    }

    private static void SetSafeFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static RuntimePathException LayoutConflict(string message, string logicalPath) =>
        new(RuntimePathReasonCodes.LayoutConflict, message, logicalPath);

    private sealed class CopyState(SessionRootCopyLimits limits)
    {
        public SessionRootCopyLimits Limits { get; } = limits;

        public long FileCount { get; private set; }

        public long DirectoryCount { get; private set; }

        public long TotalBytes { get; private set; }

        public void AddDirectory()
        {
            DirectoryCount++;
            if (DirectoryCount > Limits.MaxDirectoryCount)
            {
                throw LayoutConflict("The GameContent exceeds the directory-count copy limit.", "<manifest>");
            }
        }

        public void AddFile(long length, string logicalPath)
        {
            FileCount++;
            if (FileCount > Limits.MaxFileCount || length > Limits.MaxSingleFileBytes)
            {
                throw LayoutConflict("The GameContent exceeds the file-count or single-file copy limit.", logicalPath);
            }

            TotalBytes = checked(TotalBytes + length);
            if (TotalBytes > Limits.MaxTotalBytes)
            {
                throw LayoutConflict("The GameContent exceeds the total-byte copy limit.", logicalPath);
            }
        }
    }

    private sealed class SessionRootBindingMetadata
    {
        public int SchemaVersion { get; set; }

        public string? GameContentIdentity { get; set; }

        public string? ManifestDigest { get; set; }

        public RuntimeSaveLayout SaveLayout { get; set; }
    }
}
