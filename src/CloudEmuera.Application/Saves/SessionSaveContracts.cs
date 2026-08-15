using CloudEmuera.Application.Identity;

namespace CloudEmuera.Application.Saves;

public static class SaveErrorCodes
{
    public const string PathInvalid = "SAVE_PATH_INVALID";
    public const string NotFound = "SAVE_NOT_FOUND";
    public const string SessionNotFound = "SESSION_NOT_FOUND";
    public const string SessionNotQuiescent = "SESSION_NOT_QUIESCENT";
    public const string SessionHasActiveWorker = "SESSION_HAS_ACTIVE_WORKER";
    public const string MutationInProgress = "SESSION_MUTATION_IN_PROGRESS";
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string FileTooLarge = "SAVE_FILE_TOO_LARGE";
    public const string FormatInvalid = "SAVE_FORMAT_INVALID";
    public const string DeleteConfirmationRequired = "SAVE_DELETE_CONFIRMATION_REQUIRED";
    public const string TargetExists = "SAVE_TARGET_EXISTS";
    public const string SessionRootInvalid = "SESSION_ROOT_INVALID";
    public const string DataRootSpaceLow = "DATA_ROOT_SPACE_LOW";
    public const string RecoveryRequired = "SAVE_OPERATION_RECOVERY_REQUIRED";
    public const string StorageFailure = "SAVE_STORAGE_FAILURE";
}

public sealed class SessionSaveException(
    string code,
    string message,
    int statusCode,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public enum SessionSaveLayout
{
    Root,
    SavDirectory,
}

public enum SessionSaveFileKind
{
    Normal,
    Global,
    AuxiliaryText,
    AuxiliaryImage,
}

public sealed record SessionSaveItem(
    string Path,
    SessionSaveFileKind Kind,
    long SizeBytes,
    DateTimeOffset ModifiedAt);

public sealed record SessionSaveList(
    int SchemaVersion,
    SessionSaveLayout Layout,
    IReadOnlyList<SessionSaveItem> Items);

public sealed record SessionSaveDownload(
    string Path,
    SessionSaveFileKind Kind,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    Stream Content);

public sealed record SessionSaveMutationResult(
    int StatusCode,
    bool Replayed,
    SessionSaveItem? Item = null);

public sealed record SaveImportCommand(
    CurrentActor Actor,
    string SessionId,
    string TargetPath,
    Stream Content,
    long? DeclaredLength,
    string IdempotencyKey);

public sealed record SaveRenameCommand(
    CurrentActor Actor,
    string SessionId,
    string SourcePath,
    string TargetPath,
    string IdempotencyKey);

public sealed record SaveDeleteCommand(
    CurrentActor Actor,
    string SessionId,
    string SourcePath,
    string IdempotencyKey,
    bool Confirmed);

public sealed record SessionSaveRootSnapshot(
    SessionSaveLayout Layout,
    IReadOnlyList<SessionSaveItem> Items);

public sealed record SessionSaveFileObservation(
    string Path,
    SessionSaveFileKind Kind,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    string IdentityJson);

public sealed record SessionSaveFileRead(
    string Path,
    SessionSaveFileKind Kind,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    Stream Content);

public sealed record SessionSaveStaging(
    string OperationId,
    string PayloadPath,
    Stream Content);

public sealed record SessionSavePublishResult(
    bool Created,
    SessionSaveFileObservation Target);

public sealed record SessionSaveRenameResult(SessionSaveFileObservation Target);

public interface ISessionSaveRootAccessor
{
    Task<SessionSaveRootSnapshot> ListAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<SessionSaveFileRead?> OpenReadAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    Task<SessionSaveFileObservation?> InspectFileAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    Task<SessionSaveStaging> CreateStagingAsync(string sessionId, string operationId, string targetPath, string actorUserId, CancellationToken cancellationToken = default);

    Task FinalizeStagingAsync(string sessionId, string operationId, CancellationToken cancellationToken = default);

    Task<Stream> OpenStagingReadAsync(string sessionId, string operationId, CancellationToken cancellationToken = default);

    Task<SessionSavePublishResult> PublishAsync(string sessionId, string operationId, string targetPath, bool replace, CancellationToken cancellationToken = default);

    Task<SessionSaveRenameResult> RenameAsync(string sessionId, string sourcePath, string targetPath, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string sessionId, string sourcePath, string expectedIdentityJson, CancellationToken cancellationToken = default);

    Task CleanupStagingAsync(string sessionId, string operationId, CancellationToken cancellationToken = default);
}

public sealed record SaveFileOperationRecord(
    string Id,
    string SessionId,
    string ActorUserId,
    string IdempotencyScope,
    string IdempotencyKeyHash,
    string Type,
    string Status,
    string? SourcePath,
    string TargetPath,
    string? PayloadPath,
    long? PayloadSize,
    string? PayloadDigest,
    string? ExpectedSourceIdentityJson,
    string ResultJson,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    int StateVersion)
{
    public bool ExpectedTargetCaptured { get; init; }

    public bool ExpectedTargetExists { get; init; }

    public string? ExpectedTargetIdentityJson { get; init; }

    public bool IsTerminal => Status is "COMMITTED" or "FAILED";
}

public interface ISaveFileOperationStore
{
    Task<SaveFileOperationRecord?> FindByIdempotencyAsync(string actorUserId, string scope, string keyHash, CancellationToken cancellationToken = default);

    Task<SaveFileOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default);

    Task<SaveFileOperationRecord> CreatePreparedAsync(SaveFileOperationRecord operation, CancellationToken cancellationToken = default);

    Task<bool> MarkStagedAsync(
        string operationId,
        long payloadSize,
        string payloadDigest,
        string payloadPath,
        bool expectedTargetCaptured,
        bool expectedTargetExists,
        string? expectedTargetIdentityJson,
        CancellationToken cancellationToken = default);

    Task<bool> MarkPublishedAsync(string operationId, CancellationToken cancellationToken = default);

    Task<bool> MarkCommittedAsync(string operationId, string resultJson, CancellationToken cancellationToken = default);

    Task<bool> MarkFailedAsync(string operationId, string errorCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaveFileOperationRecord>> ListIncompleteAsync(CancellationToken cancellationToken = default);
}

public sealed record SaveFormatValidationResult(long SizeBytes, string Digest);

public interface ISaveFileFormatValidator
{
    Task<SaveFormatValidationResult> ValidateAsync(Stream content, SessionSaveFileKind kind, string fileName, long sizeBytes, CancellationToken cancellationToken = default);
}

public interface ISessionSaveApplicationService
{
    Task<SessionSaveList> ListAsync(CurrentActor actor, string sessionId, CancellationToken cancellationToken = default);

    Task<SessionSaveDownload> OpenReadAsync(CurrentActor actor, string sessionId, string path, CancellationToken cancellationToken = default);

    Task<SessionSaveMutationResult> ImportAsync(SaveImportCommand command, CancellationToken cancellationToken = default);

    Task<SessionSaveMutationResult> RenameAsync(SaveRenameCommand command, CancellationToken cancellationToken = default);

    Task<SessionSaveMutationResult> DeleteAsync(SaveDeleteCommand command, CancellationToken cancellationToken = default);
}

public interface ISaveFileOperationRecovery
{
    Task RecoverAsync(CancellationToken cancellationToken = default);
}
