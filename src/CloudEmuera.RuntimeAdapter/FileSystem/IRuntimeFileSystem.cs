using System.Diagnostics.CodeAnalysis;

namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Explicit write modes accepted by the local runtime file adapter.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "CreateNew is the explicit runtime file operation name.")]
public enum RuntimeFileOpenMode
{
    CreateNew = 0,
    Create = 1,
    Open = 2,
    OpenOrCreate = 3,
    Truncate = 4,
    Append = 5
}

public enum RuntimeFileEntryKind
{
    File = 0,
    Directory = 1
}

public sealed record RuntimeFileMetadata(
    RuntimeFileEntryKind Kind,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

public sealed record RuntimeFileEntry(
    RuntimeFilePath Path,
    RuntimeFileEntryKind Kind,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

/// <summary>
/// The only file-system surface available to runtime code. Every path is a
/// controlled logical path; no method accepts a host absolute path or glob.
/// Synchronous calls honor cancellation before starting I/O. Once a blocking
/// OS call has started, cancellation cannot interrupt that OS call.
/// </summary>
public interface IRuntimeFileSystem
{
    bool FileExists(RuntimeFilePath path, CancellationToken cancellationToken = default);

    bool DirectoryExists(RuntimeFilePath path, CancellationToken cancellationToken = default);

    Stream OpenRead(RuntimeFilePath path, CancellationToken cancellationToken = default);

    Stream OpenWrite(
        RuntimeFilePath path,
        RuntimeFileOpenMode mode,
        CancellationToken cancellationToken = default);

    void CreateDirectory(RuntimeFilePath path, CancellationToken cancellationToken = default);

    IReadOnlyList<RuntimeFileEntry> Enumerate(
        RuntimeFilePath directory,
        CancellationToken cancellationToken = default);

    IReadOnlyList<RuntimeFileEntry> Enumerate(
        RuntimeFileArea area,
        CancellationToken cancellationToken = default);

    RuntimeFileMetadata GetMetadata(RuntimeFilePath path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an existing entry to the canonical relative casing stored by
    /// the runtime filesystem. Case-insensitive lookup is part of the game
    /// compatibility contract; presentation asset IDs must use the same path
    /// that the SessionRoot manifest exposes.
    /// </summary>
    RuntimeFilePath ResolveExistingPath(RuntimeFilePath path, CancellationToken cancellationToken = default);

    void Move(
        RuntimeFilePath source,
        RuntimeFilePath destination,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    void Replace(
        RuntimeFilePath source,
        RuntimeFilePath destination,
        RuntimeFilePath? backupPath = null,
        CancellationToken cancellationToken = default);

    void Delete(
        RuntimeFilePath path,
        bool recursive = false,
        CancellationToken cancellationToken = default);
}
