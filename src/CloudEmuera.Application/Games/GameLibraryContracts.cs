using CloudEmuera.Application.Identity;

namespace CloudEmuera.Application.Games;

public sealed record GameLibraryItem(
    string Id,
    string Name,
    string Visibility,
    string Status,
    string WorkspaceStatus,
    bool HasCurrentContent,
    string? ContentDigest,
    long ContentRevision,
    int StateVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record GameFileItem(string Path, bool IsDirectory, long Bytes, string? ETag = null);
public sealed record GameTextFile(string Path, string Content, string Encoding, bool HasBom, long Bytes, string? ETag, int StateVersion);
public sealed record GameFileDownload(string FileName, long Bytes, string? ETag, Stream Content);
public sealed record GameContentOperationItem(string Id, string Type, string Status, string Stage, string? CurrentItem, string? ContentDigest, string? ErrorCode, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt);
public sealed record GameUploadProgressItem(string GameId, string OperationId, string Status, string Stage, string? CurrentItem, string? ErrorCode, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt);
public sealed record GameDiagnosticItem(string Id, string Code, string Severity, string? Path, string Message, string MessageKey, bool ActivationBlocking, string OverridePolicy, string? OverriddenBy, DateTimeOffset? OverriddenAt);

public sealed record GameValidationDiagnostic(
    string Code,
    string Severity,
    string? Path,
    string Message,
    bool ActivationBlocking);

public sealed record GameValidationResult(
    bool CanActivate,
    string? ContentDigest,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<GameValidationDiagnostic> Diagnostics,
    int StateVersion);

public sealed record GameParserValidationResult(
    bool CanActivate,
    IReadOnlyList<GameValidationDiagnostic> Diagnostics);

/// <summary>
/// Runs the pinned Emuera parser in an isolated, one-shot process. Implementations
/// must bound execution time and protocol output and must never execute the game loop.
/// </summary>
public interface IGameContentValidator
{
    Task<GameParserValidationResult> ValidateAsync(string snapshotRoot, CancellationToken cancellationToken = default);
}

public abstract class GameContentCopyLease : IAsyncDisposable
{
    public abstract string LeaseId { get; }
    public abstract string GameId { get; }
    public abstract long ContentRevision { get; }
    public abstract string? ContentDigest { get; }
    public abstract string ContentRootPath { get; }
    public abstract ValueTask RenewAsync(CancellationToken cancellationToken = default);
    public abstract ValueTask DisposeAsync();
}

public interface IGameContentCopyLeaseStore
{
    Task<GameContentCopyLease> AcquireAsync(
        string gameId,
        long contentRevision,
        string? contentDigest,
        string consumerType,
        string consumerId,
        CancellationToken cancellationToken = default);
}

public interface IGameContentOperationMaintenance
{
    Task<int> ReconcileAsync(int maxItems = 32, CancellationToken cancellationToken = default);
}

public interface IGameLibraryService
{
    Task<IReadOnlyList<GameLibraryItem>> ListAsync(CurrentActor actor, CancellationToken cancellationToken = default);
    Task<GameLibraryItem> UploadAsync(CurrentActor actor, string name, string visibility, Stream content, string requestId, CancellationToken cancellationToken = default);
    Task<GameLibraryItem> CreateAsync(CurrentActor actor, string name, string visibility, CancellationToken cancellationToken = default);
    Task<GameLibraryItem?> GetAsync(CurrentActor actor, string gameId, CancellationToken cancellationToken = default);
    Task<GameLibraryItem> UpdateAsync(CurrentActor actor, string gameId, string? name, string? visibility, int expectedStateVersion, CancellationToken cancellationToken = default);
    Task DeleteAsync(CurrentActor actor, string gameId, int expectedStateVersion, CancellationToken cancellationToken = default);
    Task<GameLibraryItem> SetBlockedAsync(CurrentActor actor, string gameId, bool blocked, int expectedStateVersion, CancellationToken cancellationToken = default);
    Task<GameLibraryItem> BindPackageAsync(CurrentActor actor, string gameId, string ingestionId, string? contentDigest, int expectedStateVersion, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GameFileItem>> ListFilesAsync(CurrentActor actor, string gameId, string? scope, string? directory, CancellationToken cancellationToken = default);
    Task<GameTextFile> ReadTextFileAsync(CurrentActor actor, string gameId, string? scope, string path, CancellationToken cancellationToken = default);
    Task<GameFileDownload> OpenDownloadAsync(CurrentActor actor, string gameId, string? scope, string path, CancellationToken cancellationToken = default);
    Task<GameContentOperationItem?> GetOperationAsync(CurrentActor actor, string gameId, string operationId, CancellationToken cancellationToken = default);
    Task<GameUploadProgressItem?> GetUploadProgressAsync(CurrentActor actor, string requestId, CancellationToken cancellationToken = default);
    Task<GameDiagnosticItem> OverrideDiagnosticAsync(CurrentActor actor, string gameId, string diagnosticId, int expectedStateVersion, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GameDiagnosticItem>> ListDiagnosticsAsync(CurrentActor actor, string gameId, CancellationToken cancellationToken = default);
    Task<GameValidationResult> ValidateAsync(CurrentActor actor, string gameId, int expectedStateVersion, CancellationToken cancellationToken = default);
    Task<GameLibraryItem> ActivateAsync(CurrentActor actor, string gameId, int expectedStateVersion, CancellationToken cancellationToken = default);
}

public sealed class GameLibraryException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class GameLibraryErrorCodes
{
    public const string NotFound = "GAME_NOT_FOUND";
    public const string NameConflict = "GAME_NAME_CONFLICT";
    public const string InUse = "GAME_IN_USE";
    public const string HasNoCurrentContent = "GAME_HAS_NO_CURRENT_CONTENT";
    public const string Conflict = "GAME_CONFLICT";
    public const string StateVersionConflict = "GAME_STATE_CONFLICT";
    public const string InvalidInput = "GAME_INPUT_INVALID";
    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string FileTooLargeToRead = "FILE_TOO_LARGE_TO_READ";
    public const string TextEncodingUnsupported = "TEXT_ENCODING_UNSUPPORTED";
    public const string ValidationFailed = "GAME_VALIDATION_FAILED";
    public const string ValidationInProgress = "VALIDATION_IN_PROGRESS";
    public const string ActivationInProgress = "ACTIVATION_IN_PROGRESS";
    public const string ActivationValidationFailed = "ACTIVATION_VALIDATION_FAILED";
    public const string UnsafePath = "GAME_PATH_UNSAFE";
    public const string IdempotencyConflict = "IDEMPOTENCY_KEY_REUSED";
    public const string DiagnosticOverrideNotAllowed = "DIAGNOSTIC_OVERRIDE_NOT_ALLOWED";
}
