using CloudEmuera.Domain.Sessions;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Tests.Persistence;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707", Justification = "P1-01 scenario names use separators for requirement mapping.")]
public sealed class InitialMigrationTests
{
    [Fact]
    [Trait("Category", "Migration")]
    public async Task EmptyDatabase_MigratesToLatestSchema()
    {
        using TemporarySqliteDatabase database = new();

        MigrationResult result = await database.MigrateAsync();

        Assert.True(result.Succeeded, result.ErrorCode);
        await using DbContextScope scope = database.OpenContext();
        HashSet<string> tables = await ReadObjectsAsync(scope.Connection, "table");
        HashSet<string> indexes = await ReadObjectsAsync(scope.Connection, "index");
        HashSet<string> triggers = await ReadObjectsAsync(scope.Connection, "trigger");

        Assert.Equal(
            [
                "__EFMigrationsLock",
                "audit_events",
                "auth_sessions",
                "compatibility_diagnostics",
                "game_content_copy_leases",
                "game_content_operations",
                "game_files",
                "game_package_ingestions",
                "games",
                "idempotency_records",
                "instance_state",
                "quota_profiles",
                "save_file_operations",
                "schema_migrations",
                "session_creation_operations",
                "session_root_mutation_leases",
                "sessions",
                "users",
                "worker_leases",
            ],
            tables.Order(StringComparer.Ordinal));
        Assert.Contains("trg_audit_events_append_only_update", triggers);
        Assert.Contains("trg_audit_events_append_only_delete", triggers);
        Assert.Contains("ux_games_current_content_path", indexes);
        Assert.Contains("ux_worker_leases_worker_id", indexes);
        Assert.DoesNotContain(tables, name => name.StartsWith("AspNet", StringComparison.Ordinal));
        Assert.DoesNotContain("game_versions", tables);
        Assert.DoesNotContain(tables, name => name == "__EFMigrationsHistory");
        HashSet<string> sessionColumns = await ReadColumnsAsync(scope.Connection, "sessions");
        Assert.DoesNotContain("runtime_manifest_json", sessionColumns);
        Assert.Contains("session_root_manifest_digest", sessionColumns);
        Assert.Contains("save_layout", sessionColumns);
        Assert.Equal(17, await ScalarIntAsync(scope.Connection, "SELECT COUNT(*) FROM schema_migrations;"));
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task LatestDatabase_MigrateAgain_IsNoOpAndPreservesRows()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);

        await using (DbContextScope scope = database.OpenContext(SqliteConnectionAccess.ReadWriteCreate))
        {
            scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
            await scope.Context.SaveChangesAsync();
        }

        int backupsBefore = CountBackups(database);
        MigrationResult result = await database.MigrateAsync();
        int backupsAfter = CountBackups(database);

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal(backupsBefore, backupsAfter);
        await using DbContextScope verify = database.OpenContext();
        Assert.Equal("Fixture qtp_fixture", await verify.Context.QuotaProfiles.Select(profile => profile.Name).SingleAsync());
        Assert.Equal(17, await ScalarIntAsync(verify.Connection, "SELECT COUNT(*) FROM schema_migrations;"));
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task PopulatedP101Database_UpgradesToIdentitySchemaAndPreservesLegacyUser()
    {
        using TemporarySqliteDatabase database = new();
        await using (DbContextScope initial = database.OpenContext(SqliteConnectionAccess.ReadWriteCreate))
        {
            await initial.Context.Database.MigrateAsync("20260807071428_InitialMetadata");
            await ExecuteAsync(initial.Connection, "INSERT INTO quota_profiles (id, name, max_active_sessions, max_game_package_bytes, max_session_bytes, max_output_bytes_per_second, created_at, updated_at, state_version) VALUES ('qtp_legacy', 'Legacy', 1, 1024, 2048, 512, 1, 1, 0);");
            await ExecuteAsync(initial.Connection, "INSERT INTO users (id, login_name, normalized_login_name, role, status, quota_profile_id, preferences_json, created_at, updated_at, state_version, password_hash, security_stamp, lockout_end, access_failed_count) VALUES ('usr_legacy', 'legacy', 'LEGACY', 'PLAYER', 'ACTIVE', 'qtp_legacy', '{}', 1, 1, 0, NULL, 'legacy-security-stamp', NULL, 0);");
        }

        MigrationResult result = await database.MigrateAsync();

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.NotNull(result.BackupPath);
        await using DbContextScope verify = database.OpenContext();
        CloudEmueraUser user = await verify.Context.Users.SingleAsync(value => value.Id == "usr_legacy");
        Assert.Null(user.Email);
        Assert.Null(user.PasswordChangedAt);
        Assert.False(user.MustChangePassword);
        Assert.Equal(InstanceStateRow.Required, (await verify.Context.InstanceStates.SingleAsync()).BootstrapStatus);
        Assert.Equal(17, await ScalarIntAsync(verify.Connection, "SELECT COUNT(*) FROM schema_migrations;"));
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task DetachedSession_UpgradesToRunning_AndLegacyStateIsRejected()
    {
        using TemporarySqliteDatabase database = new();
        await using (DbContextScope initial = database.OpenContext(SqliteConnectionAccess.ReadWriteCreate))
        {
            await initial.Context.Database.MigrateAsync("20260810110000_AddGameNameReuseAfterDelete");
            initial.Context.AddRange(
                PersistenceFixtures.CreateQuotaProfile(),
                PersistenceFixtures.CreateUser(),
                PersistenceFixtures.CreateGame());
            await initial.Context.SaveChangesAsync();
            await ExecuteAsync(initial.Connection, "INSERT INTO sessions (id, owner_user_id, game_id, source_content_digest, source_content_revision, runtime_manifest_json, runtime_version, session_root_path, name, state, state_version, worker_epoch, waiting_for_input, current_prompt_id, last_output_sequence, close_reason, created_at, started_at, last_activity_at, closed_at) VALUES ('sess_fixture', 'usr_fixture', 'game_fixture', 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 1, '{}', 'headless-test', 'sessions/sess_fixture/root', 'Fixture Session', 'CREATING', 0, 0, 0, NULL, 0, NULL, 1, NULL, 1, NULL);");
            await ExecuteAsync(initial.Connection, "UPDATE sessions SET state = 'DETACHED' WHERE id = 'sess_fixture';");
        }

        MigrationResult result = await database.MigrateAsync();

        Assert.True(result.Succeeded, result.ErrorCode);
        await using DbContextScope verify = database.OpenContext();
        Assert.Equal(SessionState.Running, (await verify.Context.Sessions.SingleAsync()).State);
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(verify.Connection, "UPDATE sessions SET state = 'DETACHED' WHERE id = 'sess_fixture';"));
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task LegacyPublishedContentAndSession_AreCollapsedIntoGameAndSnapshotMetadata()
    {
        using TemporarySqliteDatabase database = new();
        string digest = "sha256:" + new string('d', 64);
        await using (DbContextScope initial = database.OpenContext(SqliteConnectionAccess.ReadWriteCreate))
        {
            await initial.Context.Database.MigrateAsync("20260807071428_InitialMetadata");
            await ExecuteAsync(initial.Connection, "INSERT INTO quota_profiles (id,name,max_active_sessions,max_game_package_bytes,max_session_bytes,max_output_bytes_per_second,created_at,updated_at,state_version) VALUES ('qtp_legacy','Legacy',1,1024,2048,512,1,1,0);");
            await ExecuteAsync(initial.Connection, "INSERT INTO users (id,login_name,normalized_login_name,role,status,quota_profile_id,preferences_json,created_at,updated_at,state_version,password_hash,security_stamp,access_failed_count) VALUES ('usr_legacy','legacy','LEGACY','PLAYER','ACTIVE','qtp_legacy','{}',1,1,0,NULL,'stamp',0);");
            await ExecuteAsync(initial.Connection, "INSERT INTO games (id,owner_user_id,name,visibility,status,created_at,updated_at,state_version) VALUES ('game_legacy','usr_legacy','Legacy Game','PRIVATE','ACTIVE',1,1,0);");
            await ExecuteAsync(initial.Connection, $"INSERT INTO game_versions (id,game_id,version_label,status,content_digest,content_path,manifest_json,runtime_config_json,compatibility_summary_json,created_by,created_at,published_at,state_version) VALUES ('gver_legacy','game_legacy','current','PUBLISHED','{digest}','games/game_legacy/content','{{}}','{{}}','{{}}','usr_legacy',1,2,0);");
            await ExecuteAsync(initial.Connection, "INSERT INTO sessions (id,owner_user_id,game_id,game_version_id,runtime_version,session_root_path,name,state,state_version,worker_epoch,waiting_for_input,last_output_sequence,created_at,last_activity_at) VALUES ('sess_legacy','usr_legacy','game_legacy','gver_legacy','runtime','sessions/sess_legacy/root','Legacy Session','CREATING',0,0,0,0,2,2);");
        }

        await using (DbContextScope upgrade = database.OpenContext())
            await upgrade.Context.Database.MigrateAsync();
        await using DbContextScope verify = database.OpenContext();
        GameRow game = await verify.Context.Games.SingleAsync();
        SessionRow session = await verify.Context.Sessions.SingleAsync();
        Assert.Equal(digest, game.ContentDigest);
        Assert.Equal(1, game.ContentRevision);
        Assert.Equal("games/game_legacy/content", game.CurrentContentPath);
        Assert.Equal(digest, session.SourceContentDigest);
        Assert.Equal(1, session.SourceContentRevision);
        Assert.Equal(digest, session.SessionRootManifestDigest);
        Assert.Equal(0, session.SaveLayout);
        Assert.DoesNotContain("game_versions", await ReadObjectsAsync(verify.Connection, "table"));
        Assert.Equal(0, await ScalarIntAsync(verify.Connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task PreexistingDatabase_InitialMigration_PreservesUnrelatedData()
    {
        using TemporarySqliteDatabase database = new();
        await CreateProbeDatabaseAsync(database.DatabasePath);

        MigrationResult result = await database.MigrateAsync();

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.NotNull(result.BackupPath);
        string backupPath = result.BackupPath!;
        Assert.True(File.Exists(backupPath));
        string backupFileName = Path.GetFileName(backupPath);
        Assert.StartsWith("cloudemuera.db.before-", backupFileName, StringComparison.Ordinal);
        Assert.EndsWith("-20260807071428_InitialMetadata.sqlite", backupFileName, StringComparison.Ordinal);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(backupPath) & (UnixFileMode)0x1FF);
        }
        await using DbContextScope scope = database.OpenContext();
        Assert.Equal("probe-value", await ReadStringAsync(scope.Connection, "SELECT value FROM probe WHERE id = 1;"));
        Assert.Equal(1, CountBackups(database));
        Assert.Empty(Directory.EnumerateFiles(database.BackupDirectoryPath, ".*.tmp-*", SearchOption.TopDirectoryOnly));
        await using SqliteConnection backup = OpenReadOnly(backupPath);
        Assert.Equal("probe-value", await ReadStringAsync(backup, "SELECT value FROM probe WHERE id = 1;"));
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task InitialMigration_Down_RemovesOwnedSchemaOnly()
    {
        using TemporarySqliteDatabase database = new();
        await using (DbContextScope scope = database.OpenContext(SqliteConnectionAccess.ReadWriteCreate))
        {
            await scope.Context.Database.MigrateAsync("20260809141320_AddGamePackageIngestions");
            await ExecuteAsync(scope.Connection, "CREATE TABLE probe (id INTEGER PRIMARY KEY, value TEXT NOT NULL);");
            await ExecuteAsync(scope.Connection, "INSERT INTO probe (id, value) VALUES (1, 'keep');");
            await scope.Context.Database.MigrateAsync("0");
        }

        await using DbContextScope verify = database.OpenContext();
        HashSet<string> tables = await ReadObjectsAsync(verify.Connection, "table");
        HashSet<string> triggers = await ReadObjectsAsync(verify.Connection, "trigger");
        Assert.Contains("probe", tables);
        Assert.Contains("schema_migrations", tables);
        Assert.DoesNotContain("users", tables);
        Assert.DoesNotContain("sessions", tables);
        Assert.Empty(triggers);
        Assert.Equal("keep", await ReadStringAsync(verify.Connection, "SELECT value FROM probe WHERE id = 1;"));
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task NewDatabase_DoesNotCreateBackup()
    {
        using TemporarySqliteDatabase database = new();

        MigrationResult result = await database.MigrateAsync();

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.False(Directory.Exists(database.BackupDirectoryPath));
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task DatabaseNewerThanBinary_CheckFailsWithoutMutation()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await using (DbContextScope scope = database.OpenContext())
        {
            await ExecuteAsync(scope.Connection, "INSERT INTO schema_migrations (MigrationId, ProductVersion) VALUES ('99999999999999_Future', '99.0.0');");
        }

        MigrationResult result = await database.CheckAsync();

        Assert.Equal(MigrationExitCodes.DatabaseNewerThanBinary, result.ExitCode);
        Assert.Equal(18, await CountHistoryRowsAsync(database));
    }

    private static int CountBackups(TemporarySqliteDatabase database) =>
        Directory.Exists(database.BackupDirectoryPath)
            ? Directory.EnumerateFiles(database.BackupDirectoryPath, "*.sqlite", SearchOption.TopDirectoryOnly).Count()
            : 0;

    private static async Task CreateProbeDatabaseAsync(string databasePath)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE probe (id INTEGER PRIMARY KEY, value TEXT NOT NULL);");
        await ExecuteAsync(connection, "INSERT INTO probe (id, value) VALUES (1, 'probe-value');");
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task<HashSet<string>> ReadObjectsAsync(SqliteConnection connection, string type)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        command.Parameters.AddWithValue("$type", type);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        HashSet<string> values = new(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        HashSet<string> values = new(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountHistoryRowsAsync(TemporarySqliteDatabase database)
    {
        await using DbContextScope scope = database.OpenContext(SqliteConnectionAccess.ReadOnly);
        return await ScalarIntAsync(scope.Connection, "SELECT COUNT(*) FROM schema_migrations;");
    }

    private static async Task<string> ReadStringAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected a value."));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
