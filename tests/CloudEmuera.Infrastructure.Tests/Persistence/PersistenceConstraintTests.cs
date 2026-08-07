using CloudEmuera.Domain.Sessions;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Tests.Persistence;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707", Justification = "P1-01 scenario names use separators for requirement mapping.")]
public sealed class PersistenceConstraintTests
{
    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public async Task DuplicateNormalizedLoginName_IsRejectedByDatabase()
    {
        using TemporarySqliteDatabase database = await CreateSeededDatabaseAsync();
        await using DbContextScope scope = database.OpenContext();
        scope.Context.Users.Add(PersistenceFixtures.CreateUser("usr_second", "FIXTURE"));

        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());
    }

    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public async Task DuplicateGameNameAndVersionLabel_AreRejected()
    {
        using TemporarySqliteDatabase database = await CreateSeededDatabaseAsync();
        await using DbContextScope scope = database.OpenContext();
        scope.Context.Games.Add(PersistenceFixtures.CreateGame("game_second", name: "Fixture Game"));
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());

        scope.Context.ChangeTracker.Clear();
        GameVersionRow duplicateVersion = PersistenceFixtures.CreateVersion("gver_second");
        duplicateVersion.VersionLabel = "gver_fixture";
        scope.Context.GameVersions.Add(duplicateVersion);
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());
    }

    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public async Task NullDraftDigestsAreAllowed_ButPublishedDigestIsUnique()
    {
        using TemporarySqliteDatabase database = await CreateSeededDatabaseAsync(includeVersion: false, includeSession: false);
        await using DbContextScope scope = database.OpenContext();
        scope.Context.GameVersions.AddRange(
            PersistenceFixtures.CreateVersion("gver_draft_1"),
            PersistenceFixtures.CreateVersion("gver_draft_2"));
        await scope.Context.SaveChangesAsync();

        string digest = "sha256:" + new string('b', 64);
        scope.Context.GameVersions.Add(PersistenceFixtures.CreateVersion("gver_published_1", digest: digest, status: GameVersionStatus.Published));
        await scope.Context.SaveChangesAsync();
        scope.Context.GameVersions.Add(PersistenceFixtures.CreateVersion("gver_published_2", digest: digest, status: GameVersionStatus.Published));

        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());
    }

    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public async Task InvalidDigestJsonEnumBooleanAndCounterValues_AreRejected()
    {
        using TemporarySqliteDatabase database = await CreateSeededDatabaseAsync();
        await using DbContextScope scope = database.OpenContext();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(scope.Connection, "UPDATE users SET role = 'UNKNOWN' WHERE id = 'usr_fixture';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(scope.Connection, "UPDATE users SET preferences_json = 'not-json' WHERE id = 'usr_fixture';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(scope.Connection, "UPDATE sessions SET waiting_for_input = 2 WHERE id = 'sess_fixture';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(scope.Connection, "UPDATE sessions SET state_version = -1 WHERE id = 'sess_fixture';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(scope.Connection, "UPDATE sessions SET last_output_sequence = -1 WHERE id = 'sess_fixture';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(scope.Connection, "INSERT INTO game_versions (id, game_id, version_label, status, content_digest, content_path, manifest_json, runtime_config_json, compatibility_summary_json, created_by, created_at, state_version) VALUES ('gver_bad', 'game_fixture', 'bad', 'DRAFT', 'sha256:bad', 'games/game_fixture/gver_bad/content', '{}', '{}', '{}', 'usr_fixture', 0, 0);"));
    }

    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public async Task SessionCompositeForeignKey_RejectsCrossGameVersionReference()
    {
        using TemporarySqliteDatabase database = await CreateSeededDatabaseAsync(includeSession: false);
        await using DbContextScope scope = database.OpenContext();
        scope.Context.Games.Add(PersistenceFixtures.CreateGame("game_other", name: "Other Game"));
        scope.Context.GameVersions.Add(PersistenceFixtures.CreateVersion("gver_other", "game_other"));
        await scope.Context.SaveChangesAsync();
        scope.Context.Sessions.Add(PersistenceFixtures.CreateSession("sess_cross", "game_other", "gver_fixture"));

        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());
    }

    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public async Task ReferencedGameVersionAndSessionRoot_CannotBeDeletedOrReused()
    {
        using TemporarySqliteDatabase database = await CreateSeededDatabaseAsync();
        await using DbContextScope scope = database.OpenContext();
        GameVersionRow version = await scope.Context.GameVersions.SingleAsync(row => row.Id == "gver_fixture");
        scope.Context.GameVersions.Remove(version);
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());

        scope.Context.ChangeTracker.Clear();
        GameVersionRow duplicatePath = PersistenceFixtures.CreateVersion("gver_duplicate_path");
        duplicatePath.ContentPath = version.ContentPath;
        scope.Context.GameVersions.Add(duplicatePath);
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());
    }

    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public async Task WorkerLease_IsSinglePerSession_UsesCurrentEpochAndUniqueWorkerId()
    {
        using TemporarySqliteDatabase database = await CreateSeededDatabaseAsync(includeSession: false);
        await using DbContextScope scope = database.OpenContext();
        scope.Context.Sessions.Add(PersistenceFixtures.CreateSession(workerEpoch: 1));
        scope.Context.Sessions.Add(PersistenceFixtures.CreateSession("sess_other", workerEpoch: 1));
        await scope.Context.SaveChangesAsync();

        scope.Context.WorkerLeases.Add(PersistenceFixtures.CreateLease());
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();
        scope.Context.WorkerLeases.Add(PersistenceFixtures.CreateLease(workerId: "wrk_second"));
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());

        scope.Context.ChangeTracker.Clear();
        scope.Context.WorkerLeases.Add(PersistenceFixtures.CreateLease("sess_other", 1, "wrk_fixture"));
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());

        scope.Context.ChangeTracker.Clear();
        scope.Context.WorkerLeases.Add(PersistenceFixtures.CreateLease("sess_other", 0, "wrk_zero"));
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(scope.Connection, "UPDATE sessions SET worker_epoch = 2 WHERE id = 'sess_fixture';"));
        scope.Context.ChangeTracker.Clear();
        scope.Context.WorkerLeases.Add(PersistenceFixtures.CreateLease("sess_other", 2, "wrk_epoch_mismatch"));
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());
    }

    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public async Task IdempotencyKeyIsUniquePerActorAndScope_ButCanVaryAcrossThem()
    {
        using TemporarySqliteDatabase database = await CreateSeededDatabaseAsync();
        await using DbContextScope scope = database.OpenContext();
        scope.Context.IdempotencyRecords.Add(PersistenceFixtures.CreateIdempotency());
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();
        scope.Context.IdempotencyRecords.Add(PersistenceFixtures.CreateIdempotency());
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());

        scope.Context.ChangeTracker.Clear();
        scope.Context.IdempotencyRecords.Add(PersistenceFixtures.CreateIdempotency("request-2"));
        await scope.Context.SaveChangesAsync();
        scope.Context.Users.Add(PersistenceFixtures.CreateUser("usr_second", "SECOND"));
        await scope.Context.SaveChangesAsync();
        scope.Context.IdempotencyRecords.Add(new IdempotencyRecordRow
        {
            ActorUserId = "usr_second",
            Scope = "SESSION_CREATE",
            IdempotencyKey = "request-1",
            RequestDigest = "sha256:" + new string('c', 64),
            ResponseStatus = 201,
            ResponseJson = "{}",
            CreatedAt = PersistenceFixtures.CreatedAt,
            ExpiresAt = PersistenceFixtures.CreatedAt.AddHours(1),
        });
        await scope.Context.SaveChangesAsync();
    }

    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public async Task AuditEvents_AreAppendOnlyAtDatabaseBoundary()
    {
        using TemporarySqliteDatabase database = await CreateSeededDatabaseAsync();
        await using DbContextScope scope = database.OpenContext();
        scope.Context.AuditEvents.Add(PersistenceFixtures.CreateAudit());
        await scope.Context.SaveChangesAsync();

        await using SqliteConnection independentConnection = new SqliteConnectionFactory(database.Options, createDataRoot: false).OpenConnection(SqliteConnectionAccess.ReadWrite);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(independentConnection, "UPDATE audit_events SET action = 'CHANGED' WHERE id = 'audit_fixture';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(independentConnection, "DELETE FROM audit_events WHERE id = 'audit_fixture';"));
        Assert.Equal(1, await CountAsync(independentConnection, "SELECT COUNT(*) FROM audit_events;"));
    }

    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public async Task ClosedAndWaitingSessionFields_MustBeConsistent()
    {
        using TemporarySqliteDatabase database = await CreateSeededDatabaseAsync();
        await using DbContextScope scope = database.OpenContext();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(scope.Connection, "UPDATE sessions SET state = 'CLOSED' WHERE id = 'sess_fixture';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(scope.Connection, "UPDATE sessions SET waiting_for_input = 1 WHERE id = 'sess_fixture';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(scope.Connection, "UPDATE sessions SET last_activity_at = created_at - 1 WHERE id = 'sess_fixture';"));
    }

    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public async Task OwnerDeletionDoesNotCascadeUserData()
    {
        using TemporarySqliteDatabase database = await CreateSeededDatabaseAsync();
        await using DbContextScope scope = database.OpenContext();
        CloudEmueraUser user = await scope.Context.Users.SingleAsync(row => row.Id == "usr_fixture");
        scope.Context.Users.Remove(user);

        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());
        Assert.Equal(1, await CountAsync(scope.Connection, "SELECT COUNT(*) FROM games WHERE owner_user_id = 'usr_fixture';"));
        Assert.Equal(1, await CountAsync(scope.Connection, "SELECT COUNT(*) FROM sessions WHERE owner_user_id = 'usr_fixture';"));
    }

    private static async Task<TemporarySqliteDatabase> CreateSeededDatabaseAsync(bool includeVersion = true, bool includeSession = true)
    {
        TemporarySqliteDatabase database = new();
        MigrationResult result = await database.MigrateAsync();
        Assert.True(result.Succeeded, result.ErrorCode);
        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        scope.Context.Games.Add(PersistenceFixtures.CreateGame());
        if (includeVersion)
        {
            scope.Context.GameVersions.Add(PersistenceFixtures.CreateVersion());
        }

        if (includeSession)
        {
            scope.Context.Sessions.Add(PersistenceFixtures.CreateSession());
        }

        await scope.Context.SaveChangesAsync();
        return database;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
