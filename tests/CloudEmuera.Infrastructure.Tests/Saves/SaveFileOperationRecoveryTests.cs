using System.Security.Cryptography;
using System.Text;
using CloudEmuera.Application.Saves;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Domain.Sessions;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Saves;
using CloudEmuera.Infrastructure.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Tests.Saves;

[Trait("Category", "SaveOperation")]
public sealed class SaveFileOperationRecoveryTests
{
    [Fact]
    public async Task OrphanedMutationLeaseIsReportedAndNotReleased()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);

        await using (DbContextScope seed = database.OpenContext())
        {
            seed.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
            seed.Context.Users.Add(PersistenceFixtures.CreateUser());
            seed.Context.Games.Add(PersistenceFixtures.CreateGame());
            SessionRow session = PersistenceFixtures.CreateSession();
            session.State = SessionState.Closed;
            session.StateVersion = 1;
            session.ClosedAt = PersistenceFixtures.CreatedAt;
            seed.Context.Sessions.Add(session);
            seed.Context.SessionRootMutationLeases.Add(new SessionRootMutationLeaseRow
            {
                SessionId = session.Id,
                OperationId = "mut_orphan",
                ActorUserId = "usr_fixture",
                Purpose = "SAVE_IMPORT",
                AcquiredAt = PersistenceFixtures.CreatedAt.AddHours(-2),
                ExpiresAt = PersistenceFixtures.CreatedAt.AddHours(-1),
            });
            await seed.Context.SaveChangesAsync();
        }

        await using DbContextScope scope = database.OpenContext();
        SaveFileOperationRecovery recovery = new(
            scope.Context,
            new SqliteSaveFileOperationStore(scope.Context, TimeProvider.System),
            new FakeSaveRootAccessor(),
            new FakeMutationLeases(),
            TimeProvider.System);

        await Assert.ThrowsAsync<SessionSaveException>(() => recovery.RecoverAsync());

        await using DbContextScope verify = database.OpenContext();
        Assert.True(await verify.Context.SessionRootMutationLeases.AnyAsync(row => row.OperationId == "mut_orphan"));
    }

    [Fact]
    public async Task ActiveLeasedOperationIsSkippedUntilItsOwnerStopsRenewing()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string key = "recovery-active";
        await SeedSessionAndOperationAsync(database, new SaveFileOperationRow
        {
            Id = "sfop_active",
            IdempotencyScope = "SAVE_IMPORT",
            IdempotencyKeyHash = string.Empty,
            Type = SaveFileOperationType.Import,
            Status = SaveFileOperationStatus.Prepared,
            TargetPath = "global.sav",
            CreatedAt = PersistenceFixtures.CreatedAt,
            UpdatedAt = PersistenceFixtures.CreatedAt,
        }, key);

        await using (DbContextScope seed = database.OpenContext())
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            seed.Context.SessionRootMutationLeases.Add(new SessionRootMutationLeaseRow
            {
                SessionId = "sess_fixture",
                OperationId = "sfop_active",
                ActorUserId = "usr_fixture",
                Purpose = "SAVE_IMPORT",
                AcquiredAt = now,
                ExpiresAt = now.AddMinutes(1),
            });
            await seed.Context.SaveChangesAsync();
        }

        await using DbContextScope scope = database.OpenContext();
        SaveFileOperationRecovery recovery = new(
            scope.Context,
            new SqliteSaveFileOperationStore(scope.Context, TimeProvider.System),
            new FakeSaveRootAccessor(),
            new FakeMutationLeases(),
            TimeProvider.System);

        await recovery.RecoverAsync();

        await using DbContextScope verify = database.OpenContext();
        Assert.Equal(SaveFileOperationStatus.Prepared, (await verify.Context.SaveFileOperations.SingleAsync(row => row.Id == "sfop_active")).Status);
        Assert.True(await verify.Context.SessionRootMutationLeases.AnyAsync(row => row.OperationId == "sfop_active"));
    }

    [Fact]
    public async Task ExpiredTerminalOperationAndSaveIdempotencyAreReapedWithoutTouchingRoot()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string key = "recovery-reap";
        await SeedSessionAndOperationAsync(database, new SaveFileOperationRow
        {
            Id = "sfop_reap",
            IdempotencyScope = "SAVE_IMPORT",
            IdempotencyKeyHash = string.Empty,
            Type = SaveFileOperationType.Import,
            Status = SaveFileOperationStatus.Committed,
            TargetPath = "global.sav",
            ResultJson = "{}",
            ErrorCode = null,
            CreatedAt = PersistenceFixtures.CreatedAt,
            UpdatedAt = PersistenceFixtures.CreatedAt,
            CompletedAt = PersistenceFixtures.CreatedAt.AddMinutes(1),
        }, key);

        await using (DbContextScope seed = database.OpenContext())
        {
            IdempotencyRecordRow idempotency = await seed.Context.IdempotencyRecords.SingleAsync(row => row.ResourceId == "sess_fixture");
            idempotency.Status = IdempotencyRecordStatus.Succeeded;
            idempotency.ResponseStatus = 201;
            idempotency.ResponseJson = "{}";
            idempotency.CompletedAt = PersistenceFixtures.CreatedAt.AddMinutes(1);
            await seed.Context.SaveChangesAsync();
        }

        await using DbContextScope scope = database.OpenContext();
        SaveFileOperationRecovery recovery = new(
            scope.Context,
            new SqliteSaveFileOperationStore(scope.Context, TimeProvider.System),
            new FakeSaveRootAccessor(),
            new FakeMutationLeases(),
            TimeProvider.System);

        await recovery.RecoverAsync();

        await using DbContextScope verify = database.OpenContext();
        Assert.False(await verify.Context.SaveFileOperations.AnyAsync(row => row.Id == "sfop_reap"));
        Assert.False(await verify.Context.IdempotencyRecords.AnyAsync(row => row.ResourceId == "sess_fixture" && row.Scope == "SAVE_IMPORT"));
    }

    [Fact]
    public async Task PreparedImportIsFailedAndItsIdempotencyRecordIsClosed()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string key = "recovery-prepared";
        await SeedSessionAndOperationAsync(database, new SaveFileOperationRow
        {
            Id = "sfop_prepared",
            IdempotencyScope = "SAVE_IMPORT",
            IdempotencyKeyHash = HashText(key),
            Type = SaveFileOperationType.Import,
            Status = SaveFileOperationStatus.Prepared,
            TargetPath = "global.sav",
            CreatedAt = PersistenceFixtures.CreatedAt.AddHours(-1),
            UpdatedAt = PersistenceFixtures.CreatedAt.AddHours(-1),
        }, key);

        await using DbContextScope scope = database.OpenContext();
        FakeSaveRootAccessor root = new();
        SaveFileOperationRecovery recovery = new(
            scope.Context,
            new SqliteSaveFileOperationStore(scope.Context, TimeProvider.System),
            root,
            new FakeMutationLeases(),
            TimeProvider.System);

        await recovery.RecoverAsync();

        await using DbContextScope verify = database.OpenContext();
        SaveFileOperationRow operation = await verify.Context.SaveFileOperations.SingleAsync(row => row.Id == "sfop_prepared");
        IdempotencyRecordRow idempotency = await verify.Context.IdempotencyRecords.SingleAsync(row => row.IdempotencyKey == StorageKey("sess_fixture", key));
        Assert.Equal(SaveFileOperationStatus.Failed, operation.Status);
        Assert.Equal(SaveErrorCodes.RecoveryRequired, operation.ErrorCode);
        Assert.Equal(IdempotencyRecordStatus.Failed, idempotency.Status);
        Assert.Equal(SaveErrorCodes.RecoveryRequired, idempotency.ErrorCode);
        Assert.Equal(1, root.CleanupCount);
    }

    [Fact]
    public async Task StagedImportIsPublishedAndCommittedFromMatchingPayload()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        byte[] payload = Encoding.UTF8.GetBytes("0\n0\n");
        string key = "recovery-staged";
        await SeedSessionAndOperationAsync(database, new SaveFileOperationRow
        {
            Id = "sfop_staged",
            IdempotencyScope = "SAVE_IMPORT",
            IdempotencyKeyHash = HashText(key),
            Type = SaveFileOperationType.Import,
            Status = SaveFileOperationStatus.Staged,
            TargetPath = "global.sav",
            PayloadPath = "metadata/save-operations/sfop_staged/payload.tmp",
            PayloadSize = payload.Length,
            PayloadDigest = HashBytes(payload),
            ExpectedTargetCaptured = true,
            ExpectedTargetExists = false,
            CreatedAt = PersistenceFixtures.CreatedAt.AddHours(-1),
            UpdatedAt = PersistenceFixtures.CreatedAt.AddHours(-1),
        }, key);

        await using DbContextScope scope = database.OpenContext();
        FakeSaveRootAccessor root = new(payload);
        SaveFileOperationRecovery recovery = new(
            scope.Context,
            new SqliteSaveFileOperationStore(scope.Context, TimeProvider.System),
            root,
            new FakeMutationLeases(),
            TimeProvider.System);

        await recovery.RecoverAsync();

        await using DbContextScope verify = database.OpenContext();
        SaveFileOperationRow operation = await verify.Context.SaveFileOperations.SingleAsync(row => row.Id == "sfop_staged");
        IdempotencyRecordRow idempotency = await verify.Context.IdempotencyRecords.SingleAsync(row => row.IdempotencyKey == StorageKey("sess_fixture", key));
        Assert.Equal(SaveFileOperationStatus.Committed, operation.Status);
        Assert.Equal(IdempotencyRecordStatus.Succeeded, idempotency.Status);
        Assert.Equal(1, root.PublishCount);
        Assert.True(root.CleanupCount >= 1);
        Assert.Contains(await verify.Context.AuditEvents.Where(row => row.ResourceId == "sess_fixture").ToListAsync(), row => row.Action == "SESSION_SAVE_IMPORTED");
    }

    [Fact]
    public async Task StagedImportWithChangedTargetFailsClosedWithoutPublishing()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        byte[] payload = Encoding.UTF8.GetBytes("0\n0\n");
        string key = "recovery-target-conflict";
        await SeedSessionAndOperationAsync(database, new SaveFileOperationRow
        {
            Id = "sfop_target_conflict",
            IdempotencyScope = "SAVE_IMPORT",
            IdempotencyKeyHash = string.Empty,
            Type = SaveFileOperationType.Import,
            Status = SaveFileOperationStatus.Staged,
            TargetPath = "global.sav",
            PayloadPath = "metadata/save-operations/sfop_target_conflict/payload.tmp",
            PayloadSize = payload.Length,
            PayloadDigest = HashBytes(payload),
            ExpectedTargetCaptured = true,
            ExpectedTargetExists = false,
            CreatedAt = PersistenceFixtures.CreatedAt.AddHours(-1),
            UpdatedAt = PersistenceFixtures.CreatedAt.AddHours(-1),
        }, key);

        await using DbContextScope scope = database.OpenContext();
        FakeSaveRootAccessor root = new(payload, Encoding.UTF8.GetBytes("newer target"));
        SaveFileOperationRecovery recovery = new(
            scope.Context,
            new SqliteSaveFileOperationStore(scope.Context, TimeProvider.System),
            root,
            new FakeMutationLeases(),
            TimeProvider.System);

        await Assert.ThrowsAsync<SessionSaveException>(() => recovery.RecoverAsync());

        Assert.Equal(0, root.PublishCount);
        await using DbContextScope verify = database.OpenContext();
        Assert.Equal(SaveFileOperationStatus.Staged, (await verify.Context.SaveFileOperations.SingleAsync(row => row.Id == "sfop_target_conflict")).Status);
    }

    [Fact]
    public async Task StagedReplacementUsesOriginalNoContentResponse()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        byte[] payload = Encoding.UTF8.GetBytes("0\n0\n");
        byte[] original = Encoding.UTF8.GetBytes("old");
        string key = "recovery-replace";
        await SeedSessionAndOperationAsync(database, new SaveFileOperationRow
        {
            Id = "sfop_replace",
            IdempotencyScope = "SAVE_IMPORT",
            IdempotencyKeyHash = string.Empty,
            Type = SaveFileOperationType.Import,
            Status = SaveFileOperationStatus.Staged,
            TargetPath = "global.sav",
            PayloadPath = "metadata/save-operations/sfop_replace/payload.tmp",
            PayloadSize = payload.Length,
            PayloadDigest = HashBytes(payload),
            ExpectedTargetCaptured = true,
            ExpectedTargetExists = true,
            ExpectedTargetIdentityJson = IdentityJson(original.Length),
            CreatedAt = PersistenceFixtures.CreatedAt.AddHours(-1),
            UpdatedAt = PersistenceFixtures.CreatedAt.AddHours(-1),
        }, key);

        await using DbContextScope scope = database.OpenContext();
        FakeSaveRootAccessor root = new(payload, original);
        SaveFileOperationRecovery recovery = new(
            scope.Context,
            new SqliteSaveFileOperationStore(scope.Context, TimeProvider.System),
            root,
            new FakeMutationLeases(),
            TimeProvider.System);

        await recovery.RecoverAsync();

        await using DbContextScope verify = database.OpenContext();
        SaveFileOperationRow operation = await verify.Context.SaveFileOperations.SingleAsync(row => row.Id == "sfop_replace");
        IdempotencyRecordRow idempotency = await verify.Context.IdempotencyRecords.SingleAsync(row => row.ResourceId == "sess_fixture" && row.Scope == "SAVE_IMPORT");
        Assert.Equal(SaveFileOperationStatus.Committed, operation.Status);
        Assert.Equal(IdempotencyRecordStatus.Succeeded, idempotency.Status);
        Assert.Equal(204, idempotency.ResponseStatus);
        Assert.Equal(1, root.PublishCount);
    }

    private static async Task SeedSessionAndOperationAsync(TemporarySqliteDatabase database, SaveFileOperationRow operation, string key)
    {
        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        scope.Context.Games.Add(PersistenceFixtures.CreateGame());
        SessionRow session = PersistenceFixtures.CreateSession();
        session.State = SessionState.Closed;
        session.StateVersion = 1;
        session.ClosedAt = PersistenceFixtures.CreatedAt;
        scope.Context.Sessions.Add(session);
        operation.SessionId = session.Id;
        operation.ActorUserId = "usr_fixture";
        operation.IdempotencyKeyHash = HashText(StorageKey(session.Id, key));
        operation.ResultJson = "{}";
        operation.StateVersion = 0;
        scope.Context.SaveFileOperations.Add(operation);
        scope.Context.IdempotencyRecords.Add(new IdempotencyRecordRow
        {
            ActorUserId = "usr_fixture",
            ResourceId = session.Id,
            Scope = operation.IdempotencyScope,
            IdempotencyKey = StorageKey(session.Id, key),
            RequestDigest = operation.Type == SaveFileOperationType.Import && operation.PayloadDigest is not null
                ? RequestDigest(operation.TargetPath, operation.PayloadSize!.Value, operation.PayloadDigest)
                : RequestDigest("request"),
            Status = IdempotencyRecordStatus.InProgress,
            ResponseStatus = 201,
            ResponseJson = "{}",
            CreatedAt = operation.CreatedAt,
            UpdatedAt = operation.UpdatedAt,
            ExpiresAt = operation.UpdatedAt.AddHours(24),
        });
        await scope.Context.SaveChangesAsync();
    }

    private static string HashBytes(byte[] bytes) => $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

    private static string HashText(string value) => HashBytes(Encoding.UTF8.GetBytes(value));

    private static string IdentityJson(long size) => $"{{\"deviceMajor\":1,\"deviceMinor\":1,\"inode\":1,\"size\":{size}}}";

    private static string StorageKey(string sessionId, string key) => $"save:{sessionId}:{HashText(key)}";

    private static string RequestDigest(string targetPath, long size, string digest) =>
        HashText($"{{\"targetPath\":\"{targetPath}\",\"size\":{size},\"digest\":\"{digest}\"}}");

    private static string RequestDigest(string value) => HashText(value);

    private sealed class FakeMutationLeases : ISessionRootMutationLeaseStore
    {
        public Task<SessionRootMutationAcquireResult> TryAcquireAsync(string sessionId, string actorUserId, string operationId, SessionRootMutationPurpose purpose, TimeSpan duration, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionRootMutationAcquireResult(SessionRootMutationAcquireFailure.None,
                new SessionRootMutationLease(sessionId, operationId, actorUserId, purpose, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.Add(duration))));

        public Task<bool> RenewAsync(SessionRootMutationLease lease, TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> ReleaseAsync(SessionRootMutationLease lease, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> ReleaseExpiredAsync(string sessionId, string operationId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeSaveRootAccessor(byte[]? staged = null, byte[]? existing = null) : ISessionSaveRootAccessor
    {
        private readonly byte[]? staged = staged;
        private byte[]? published = existing;

        public int CleanupCount { get; private set; }
        public int PublishCount { get; private set; }

        public Task<SessionSaveRootSnapshot> ListAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SessionSaveFileRead?> OpenReadAsync(string sessionId, string path, CancellationToken cancellationToken = default)
        {
            if (published is null)
                return Task.FromResult<SessionSaveFileRead?>(null);
            return Task.FromResult<SessionSaveFileRead?>(new SessionSaveFileRead(path, SessionSaveFileKind.Global, published.Length, PersistenceFixtures.CreatedAt, new MemoryStream(published, writable: false)));
        }

        public Task<SessionSaveFileObservation?> InspectFileAsync(string sessionId, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionSaveFileObservation?>(published is null ? null : Observation(path, published.Length));

        public Task<SessionSaveStaging> CreateStagingAsync(string sessionId, string operationId, string targetPath, string actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task FinalizeStagingAsync(string sessionId, string operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Stream> OpenStagingReadAsync(string sessionId, string operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(staged is null ? throw new FileNotFoundException() : new MemoryStream(staged, writable: false));

        public Task<SessionSavePublishResult> PublishAsync(string sessionId, string operationId, string targetPath, bool replace, CancellationToken cancellationToken = default)
        {
            if (staged is null)
                throw new FileNotFoundException();
            bool created = published is null;
            published = staged.ToArray();
            PublishCount++;
            return Task.FromResult(new SessionSavePublishResult(created, Observation(targetPath, published.Length)));
        }

        public Task<SessionSaveRenameResult> RenameAsync(string sessionId, string sourcePath, string targetPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string sessionId, string sourcePath, string expectedIdentityJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CleanupStagingAsync(string sessionId, string operationId, CancellationToken cancellationToken = default)
        {
            CleanupCount++;
            return Task.CompletedTask;
        }

        private static SessionSaveFileObservation Observation(string path, long size) =>
            new(path, SessionSaveFileKind.Global, size, PersistenceFixtures.CreatedAt,
                IdentityJson(size));
    }
}
