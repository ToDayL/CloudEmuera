using System.Collections.ObjectModel;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Application.GamePackages;

public sealed record GamePackageIngestionRequest(
    string OwnerUserId,
    Stream Content,
    string? RequestId = null,
    Func<GamePackageProgressUpdate, CancellationToken, Task>? ProgressAsync = null);

public enum GamePackageProgressStage
{
    Receiving,
    InspectingArchive,
    Extracting,
    NormalizingEncoding,
    Analyzing,
    Ready,
}

public sealed record GamePackageProgressUpdate(
    GamePackageProgressStage Stage,
    string? CurrentItem = null);

public sealed record GamePackageIngestionLimits
{
    public const int AbsoluteMaxEntryCount = 1_000_000;

    public long MaxArchiveBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public long MaxExpandedBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    public long MaxSingleFileBytes { get; init; } = 1L * 1024 * 1024 * 1024;
    public int MaxEntryCount { get; init; } = AbsoluteMaxEntryCount;
    public long MaxCentralDirectoryBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxDirectoryDepth { get; init; } = 32;
    public double MaxCompressionRatio { get; init; } = 200;
    public int MaxPathUtf8Bytes { get; init; } = 1024;
    public int MaxSegmentUtf8Bytes { get; init; } = 255;
    public int MaxDiagnostics { get; init; } = 1_000;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(15);

    public void Validate()
    {
        if (MaxArchiveBytes <= 0 || MaxExpandedBytes <= 0 || MaxSingleFileBytes <= 0
            || MaxSingleFileBytes > MaxExpandedBytes || MaxEntryCount is <= 0 or > AbsoluteMaxEntryCount
            || MaxCentralDirectoryBytes <= 0
            || MaxDirectoryDepth <= 0 || MaxCompressionRatio < 1
            || MaxPathUtf8Bytes <= 0 || MaxSegmentUtf8Bytes <= 0
            || MaxSegmentUtf8Bytes > MaxPathUtf8Bytes || MaxDiagnostics <= 0
            || MaxDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(GamePackageIngestionLimits), "Game package ingestion limits are inconsistent.");
        }
    }
}

public enum GamePackageDiagnosticSeverity { Info, Warning, Error }

public sealed record GamePackageDiagnostic(
    string Code,
    GamePackageDiagnosticSeverity Severity,
    string Stage,
    string? LogicalPath,
    string MessageKey,
    IReadOnlyDictionary<string, string> Arguments,
    bool PublishBlocking,
    int SuppressedCount = 0);

public enum GamePackageFileKind { Binary, Text }

public enum GamePackageTextEncoding { None, Utf8, Utf8Bom, ShiftJis, Unknown }

public sealed record GamePackageFileManifest(
    string Path,
    long Bytes,
    string? Digest,
    GamePackageFileKind Kind,
    GamePackageTextEncoding Encoding,
    bool HasBom);

public sealed record GamePackageManifest(
    int SchemaVersion,
    long ArchiveBytes,
    string? ArchiveDigest,
    long ContentBytes,
    int FileCount,
    int DirectoryCount,
    string? ContentDigest,
    IReadOnlyList<GamePackageFileManifest> Files,
    IReadOnlyList<string> Directories,
    IReadOnlyList<GamePackageDiagnostic> Diagnostics)
{
    public static GamePackageManifest Create(
        long archiveBytes,
        string? archiveDigest,
        long contentBytes,
        string? contentDigest,
        IEnumerable<GamePackageFileManifest> files,
        IEnumerable<string> directories,
        IEnumerable<GamePackageDiagnostic> diagnostics)
    {
        GamePackageFileManifest[] fileArray = files.ToArray();
        string[] directoryArray = directories.ToArray();
        return new(1, archiveBytes, archiveDigest, contentBytes, fileArray.Length, directoryArray.Length,
            contentDigest, new ReadOnlyCollection<GamePackageFileManifest>(fileArray),
            new ReadOnlyCollection<string>(directoryArray),
            new ReadOnlyCollection<GamePackageDiagnostic>(diagnostics.ToArray()));
    }
}

public sealed record IngestedGamePackage(
    string IngestionId,
    string OwnerUserId,
    DateTimeOffset ExpiresAt,
    GamePackageManifest Manifest);

public sealed class GamePackageConsumption : IAsyncDisposable
{
    public GamePackageConsumption(
        string ingestionId,
        string ownerUserId,
        string? contentDigest,
        SafeFileHandle contentDirectoryHandle,
        string manifestJson)
    {
        IngestionId = ingestionId;
        OwnerUserId = ownerUserId;
        ContentDigest = contentDigest;
        ContentDirectoryHandle = contentDirectoryHandle;
        ManifestJson = manifestJson;
    }

    public string IngestionId { get; }
    public string OwnerUserId { get; }
    public string? ContentDigest { get; }
    public SafeFileHandle ContentDirectoryHandle { get; }
    public string ManifestJson { get; }

    public ValueTask DisposeAsync()
    {
        ContentDirectoryHandle.Dispose();
        return ValueTask.CompletedTask;
    }
}

public interface IGamePackageIngestionService
{
    Task<IngestedGamePackage> IngestAsync(
        GamePackageIngestionRequest request,
        GamePackageIngestionLimits? requestedLimits = null,
        CancellationToken cancellationToken = default);

    Task<GamePackageConsumption> BeginConsumeAsync(
        string ingestionId,
        string ownerUserId,
        string? expectedContentDigest,
        CancellationToken cancellationToken = default);

    Task<DateTimeOffset> RenewConsumeAsync(
        string ingestionId,
        string ownerUserId,
        string? expectedContentDigest,
        CancellationToken cancellationToken = default);

    Task CompleteConsumeAsync(string ingestionId, string ownerUserId, CancellationToken cancellationToken = default);

    Task AbandonAsync(string ingestionId, string ownerUserId, CancellationToken cancellationToken = default);
}

public interface IGamePackageIngestionMaintenance
{
    Task<int> ReapExpiredAsync(int maxItems = 32, CancellationToken cancellationToken = default);
}

public sealed class GamePackageIngestionException(string code, string message, string? logicalPath = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public string? LogicalPath { get; } = logicalPath;
}

public static class GamePackageRejectionCodes
{
    public const string ArchiveTooLarge = "ARCHIVE_TOO_LARGE";
    public const string ArchiveFormatUnsupported = "ARCHIVE_FORMAT_UNSUPPORTED";
    public const string ArchiveCorrupt = "ARCHIVE_CORRUPT";
    public const string ArchiveEncrypted = "ARCHIVE_ENCRYPTED";
    public const string Zip64Unsupported = "ZIP64_UNSUPPORTED";
    public const string ZipMethodUnsupported = "ZIP_METHOD_UNSUPPORTED";
    public const string EntryCountExceeded = "ENTRY_COUNT_EXCEEDED";
    public const string CentralDirectoryTooLarge = "CENTRAL_DIRECTORY_TOO_LARGE";
    public const string EntryTooLarge = "ENTRY_TOO_LARGE";
    public const string ExpandedSizeExceeded = "EXPANDED_SIZE_EXCEEDED";
    public const string CompressionRatioExceeded = "COMPRESSION_RATIO_EXCEEDED";
    public const string PathDepthExceeded = "PATH_DEPTH_EXCEEDED";
    public const string PathInvalid = "PATH_INVALID";
    public const string PathReservedName = "PATH_RESERVED_NAME";
    public const string PathCollision = "PATH_COLLISION";
    public const string PathTypeConflict = "PATH_TYPE_CONFLICT";
    public const string LinkEntryForbidden = "LINK_ENTRY_FORBIDDEN";
    public const string SpecialEntryForbidden = "SPECIAL_ENTRY_FORBIDDEN";
    public const string StagingBudgetExhausted = "STAGING_BUDGET_EXHAUSTED";
    public const string DataRootSpaceLow = "DATA_ROOT_SPACE_LOW";
    public const string StagingIoFailed = "STAGING_IO_FAILED";
    public const string IngestionCancelled = "INGESTION_CANCELLED";
    public const string IngestionDeadlineExceeded = "INGESTION_DEADLINE_EXCEEDED";
    public const string StagedContentChanged = "STAGED_CONTENT_CHANGED";
    public const string IngestionCommitFailed = "INGESTION_COMMIT_FAILED";
}
