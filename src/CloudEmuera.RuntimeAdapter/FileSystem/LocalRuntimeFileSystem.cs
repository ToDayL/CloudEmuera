using System.Collections.ObjectModel;

namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Local adapter for one <see cref="RuntimePaths"/> instance. It performs the
/// physical guard on every operation and deliberately does not expose the
/// process working directory or arbitrary host paths.
/// </summary>
public sealed class LocalRuntimeFileSystem : IRuntimeFileSystem
{
    private readonly RuntimePaths paths;
    private readonly PhysicalPathGuard guard;

    public LocalRuntimeFileSystem(RuntimePaths paths)
        : this(paths, new PhysicalPathGuard(paths))
    {
    }

    public LocalRuntimeFileSystem(RuntimePaths paths, PhysicalPathGuard guard)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    public bool FileExists(RuntimeFilePath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeFileOperation operation = path.Area == RuntimeFileArea.Save
            ? RuntimeFileOperation.ReadEntry
            : RuntimeFileOperation.Read;
        string physicalPath = guard.Resolve(path, operation, requireExisting: false);
        if (File.Exists(physicalPath))
        {
            EnsureAllowedSaveFile(path);
            return true;
        }

        if (Directory.Exists(physicalPath))
        {
            EnsureAllowedSaveDirectory(path);
        }

        return false;
    }

    public bool DirectoryExists(RuntimeFilePath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeFileOperation operation = path.Area == RuntimeFileArea.Save
            ? RuntimeFileOperation.ReadEntry
            : RuntimeFileOperation.ReadDirectory;
        string physicalPath = guard.Resolve(path, operation, requireExisting: false);
        if (Directory.Exists(physicalPath))
        {
            EnsureAllowedSaveDirectory(path);
            return true;
        }

        if (File.Exists(physicalPath))
        {
            EnsureAllowedSaveFile(path);
        }

        return false;
    }

    public Stream OpenRead(RuntimeFilePath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string physicalPath = guard.Resolve(path, RuntimeFileOperation.Read, requireExisting: true);
        EnsureFile(physicalPath, path);

        FileStream stream = new(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        try
        {
            guard.ValidateOpenedPath(path, RuntimeFileOperation.Read);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public Stream OpenWrite(
        RuntimeFilePath path,
        RuntimeFileOpenMode mode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileMode fileMode = ToFileMode(mode);
        RuntimeFileOperation operation = ToOperation(mode);
        string physicalPath = guard.Resolve(path, operation, requireExisting: false);
        if (Directory.Exists(physicalPath))
        {
            throw EntryKindError(path, RuntimeFileEntryKind.File);
        }

        FileStream stream = new(physicalPath, fileMode, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
        try
        {
            guard.ValidateOpenedPath(path, operation);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void CreateDirectory(RuntimeFilePath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string physicalPath = guard.Resolve(path, RuntimeFileOperation.CreateDirectory, requireExisting: false);
        if (File.Exists(physicalPath))
        {
            throw EntryKindError(path, RuntimeFileEntryKind.Directory);
        }

        Directory.CreateDirectory(physicalPath);
        guard.ValidateOpenedPath(path, RuntimeFileOperation.CreateDirectory);
    }

    public IReadOnlyList<RuntimeFileEntry> Enumerate(
        RuntimeFilePath directory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string physicalDirectory = guard.Resolve(directory, RuntimeFileOperation.Enumerate, requireExisting: true);
        EnsureDirectory(physicalDirectory, directory);
        return EnumerateDirectory(directory.Area, directory.RelativePath, physicalDirectory, cancellationToken);
    }

    public IReadOnlyList<RuntimeFileEntry> Enumerate(
        RuntimeFileArea area,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string physicalDirectory = guard.ResolveAreaRoot(area, RuntimeFileOperation.Enumerate, requireExisting: true);
        EnsureDirectory(physicalDirectory, area);
        return EnumerateDirectory(area, relativeDirectory: null, physicalDirectory, cancellationToken);
    }

    public RuntimeFileMetadata GetMetadata(RuntimeFilePath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string physicalPath = guard.Resolve(path, RuntimeFileOperation.ReadEntry, requireExisting: true);
        RuntimePathUtilities.ThrowIfReparsePoint(physicalPath, path.LogicalPath, path.Area, missingIsAllowed: false);

        if (File.Exists(physicalPath))
        {
            EnsureAllowedSaveFile(path);
            var info = new FileInfo(physicalPath);
            return new RuntimeFileMetadata(
                RuntimeFileEntryKind.File,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }

        if (Directory.Exists(physicalPath))
        {
            EnsureAllowedSaveDirectory(path);
            var info = new DirectoryInfo(physicalPath);
            return new RuntimeFileMetadata(
                RuntimeFileEntryKind.Directory,
                0,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }

        throw EntryNotFound(path);
    }

    public RuntimeFilePath ResolveExistingPath(RuntimeFilePath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string physicalPath = guard.Resolve(path, RuntimeFileOperation.ReadEntry, requireExisting: true);
        EnsureFile(physicalPath, path);
        string relativePath = Path.GetRelativePath(paths.GetAreaRoot(path.Area), physicalPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        return new RuntimeFilePath(path.Area, RuntimeRelativePath.Parse(relativePath));
    }

    public void Move(
        RuntimeFilePath source,
        RuntimeFilePath destination,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSameArea(source, destination);
        string physicalSource = guard.Resolve(source, RuntimeFileOperation.Move, requireExisting: true);
        EnsureExistingEntry(physicalSource, source);

        if (Directory.Exists(physicalSource))
        {
            EnsureAllowedSaveDirectory(source);
            string physicalDestination = guard.Resolve(
                destination,
                RuntimeFileOperation.MoveDirectory,
                requireExisting: false);
            if (overwrite)
            {
                throw new IOException("Directory move overwrite is not supported by the runtime port.");
            }

            Directory.Move(physicalSource, physicalDestination);
        }
        else
        {
            EnsureAllowedSaveFile(source);
            string physicalDestination = guard.Resolve(destination, RuntimeFileOperation.Move, requireExisting: false);
            File.Move(physicalSource, physicalDestination, overwrite);
        }

        guard.ValidateOpenedPath(destination, RuntimeFileOperation.Move);
    }

    public void Replace(
        RuntimeFilePath source,
        RuntimeFilePath destination,
        RuntimeFilePath? backupPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSameArea(source, destination);
        if (backupPath is RuntimeFilePath backup)
        {
            EnsureSameArea(source, backup);
        }

        string physicalSource = guard.Resolve(source, RuntimeFileOperation.Replace, requireExisting: true);
        string physicalDestination = guard.Resolve(destination, RuntimeFileOperation.Replace, requireExisting: true);
        string? physicalBackup = backupPath is RuntimeFilePath backupValue
            ? guard.Resolve(backupValue, RuntimeFileOperation.Replace, requireExisting: false)
            : null;
        EnsureFile(physicalSource, source);
        EnsureFile(physicalDestination, destination);
        File.Replace(physicalSource, physicalDestination, physicalBackup, ignoreMetadataErrors: false);
        guard.ValidateOpenedPath(destination, RuntimeFileOperation.Replace);
    }

    public void Delete(
        RuntimeFilePath path,
        bool recursive = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string physicalPath = guard.Resolve(path, RuntimeFileOperation.Delete, requireExisting: true);
        EnsureExistingEntry(physicalPath, path);

        if (Directory.Exists(physicalPath))
        {
            EnsureAllowedSaveDirectory(path);
            Directory.Delete(physicalPath, recursive);
        }
        else
        {
            EnsureAllowedSaveFile(path);
            File.Delete(physicalPath);
        }
    }

    private ReadOnlyCollection<RuntimeFileEntry> EnumerateDirectory(
        RuntimeFileArea area,
        RuntimeRelativePath? relativeDirectory,
        string physicalDirectory,
        CancellationToken cancellationToken)
    {
        var entries = new List<RuntimeFileEntry>();
        foreach (FileSystemInfo entry in new DirectoryInfo(physicalDirectory).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimePathUtilities.ThrowIfReparsePoint(entry.FullName, entry.Name, area, missingIsAllowed: false);

            if (area == RuntimeFileArea.Save &&
                ((entry is FileInfo && !RuntimePaths.IsAllowedSaveFileName(entry.Name)) ||
                 (entry is DirectoryInfo &&
                  (paths.SaveLayout == RuntimeSaveLayout.Root ||
                   !RuntimePaths.IsAllowedSaveDirectorySegment(entry.Name)))))
            {
                throw new RuntimeFileAccessException(
                    RuntimePathReasonCodes.UnsupportedRuntimeFile,
                    "A save directory contains an entry outside the fixed runtime save contract.",
                    "<enumeration>",
                    area);
            }

            string logicalValue = relativeDirectory is RuntimeRelativePath relative
                ? $"{relative.Value}/{entry.Name}"
                : entry.Name;
            if (!RuntimeRelativePath.TryParse(logicalValue, out RuntimeRelativePath logicalPath))
            {
                throw new RuntimeFileAccessException(
                    RuntimePathReasonCodes.UnsupportedRuntimeFile,
                    "A directory entry cannot be represented as a runtime logical path.",
                    "<enumeration>",
                    area);
            }

            RuntimeFileEntryKind kind = entry is DirectoryInfo
                ? RuntimeFileEntryKind.Directory
                : RuntimeFileEntryKind.File;
            long length = 0;
            DateTimeOffset lastWriteTimeUtc = DateTimeOffset.UnixEpoch;
            if (entry is FileInfo file)
            {
                length = file.Length;
                lastWriteTimeUtc = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            }
            else if (entry is DirectoryInfo directory)
            {
                lastWriteTimeUtc = new DateTimeOffset(directory.LastWriteTimeUtc, TimeSpan.Zero);
            }

            entries.Add(new RuntimeFileEntry(
                new RuntimeFilePath(area, logicalPath),
                kind,
                length,
                lastWriteTimeUtc));
        }

        entries.Sort(static (left, right) =>
            string.Compare(left.Path.RelativePath.Value, right.Path.RelativePath.Value, StringComparison.Ordinal));
        return new ReadOnlyCollection<RuntimeFileEntry>(entries);
    }

    private static FileMode ToFileMode(RuntimeFileOpenMode mode) => mode switch
    {
        RuntimeFileOpenMode.CreateNew => FileMode.CreateNew,
        RuntimeFileOpenMode.Create => FileMode.Create,
        RuntimeFileOpenMode.Open => FileMode.Open,
        RuntimeFileOpenMode.OpenOrCreate => FileMode.OpenOrCreate,
        RuntimeFileOpenMode.Truncate => FileMode.Truncate,
        RuntimeFileOpenMode.Append => FileMode.Append,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static RuntimeFileOperation ToOperation(RuntimeFileOpenMode mode) => mode switch
    {
        RuntimeFileOpenMode.CreateNew => RuntimeFileOperation.CreateNew,
        RuntimeFileOpenMode.Create => RuntimeFileOperation.Create,
        RuntimeFileOpenMode.Open => RuntimeFileOperation.Create,
        RuntimeFileOpenMode.OpenOrCreate => RuntimeFileOperation.OpenOrCreate,
        RuntimeFileOpenMode.Truncate => RuntimeFileOperation.Truncate,
        RuntimeFileOpenMode.Append => RuntimeFileOperation.Append,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static void EnsureFile(string physicalPath, RuntimeFilePath path)
    {
        if (!File.Exists(physicalPath) || Directory.Exists(physicalPath))
        {
            throw EntryKindError(path, RuntimeFileEntryKind.File);
        }
    }

    private static void EnsureDirectory(string physicalPath, RuntimeFilePath path)
    {
        if (!Directory.Exists(physicalPath))
        {
            throw EntryKindError(path, RuntimeFileEntryKind.Directory);
        }
    }

    private static void EnsureDirectory(string physicalPath, RuntimeFileArea area)
    {
        if (!Directory.Exists(physicalPath))
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.EntryNotFound,
                "The runtime area directory does not exist.",
                "<area-root>",
                area);
        }
    }

    private static void EnsureExistingEntry(string physicalPath, RuntimeFilePath path)
    {
        if (!File.Exists(physicalPath) && !Directory.Exists(physicalPath))
        {
            throw EntryNotFound(path);
        }
    }

    private static void EnsureAllowedSaveFile(RuntimeFilePath path)
    {
        if (path.Area == RuntimeFileArea.Save &&
            !RuntimePaths.IsAllowedSaveFileName(path.RelativePath.Segments[^1]))
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.UnsupportedRuntimeFile,
                "The save file name is not part of the fixed runtime save contract.",
                path.LogicalPath,
                path.Area);
        }
    }

    private void EnsureAllowedSaveDirectory(RuntimeFilePath path)
    {
        if (path.Area != RuntimeFileArea.Save)
        {
            return;
        }

        if (paths.SaveLayout != RuntimeSaveLayout.SavDirectory ||
            path.RelativePath.Segments.Any(segment => !RuntimePaths.IsAllowedSaveDirectorySegment(segment)))
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.UnsupportedRuntimeFile,
                "The runtime save directory is outside the fixed directory contract.",
                path.LogicalPath,
                path.Area);
        }
    }

    private static void EnsureSameArea(RuntimeFilePath source, RuntimeFilePath destination)
    {
        if (source.Area != destination.Area)
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.PathOutsideArea,
                "Cross-area move and replace operations are not allowed.",
                source.LogicalPath,
                source.Area);
        }
    }

    private static RuntimeFileAccessException EntryNotFound(RuntimeFilePath path) =>
        new(
            RuntimePathReasonCodes.EntryNotFound,
            "The runtime entry does not exist.",
            path.LogicalPath,
            path.Area);

    private static RuntimeFileAccessException EntryKindError(RuntimeFilePath path, RuntimeFileEntryKind expected) =>
        new(
            RuntimePathReasonCodes.LayoutConflict,
            expected == RuntimeFileEntryKind.File
                ? "The runtime path is not a file."
                : "The runtime path is not a directory.",
            path.LogicalPath,
            path.Area);
}
