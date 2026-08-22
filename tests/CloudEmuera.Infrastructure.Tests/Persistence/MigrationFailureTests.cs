using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Tests.Support;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace CloudEmuera.Infrastructure.Tests.Persistence;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707", Justification = "P1-01 scenario names use separators for requirement mapping.")]
public sealed class MigrationFailureTests
{
    [Fact]
    [Trait("Category", "Migration")]
    public async Task FailingMigration_RollsBackAllChanges()
    {
        using TemporarySqliteDatabase database = new();
        SqliteDatabaseOptions options = new()
        {
            DataRoot = database.RootPath,
            MigrationsAssembly = typeof(FailingMigration).Assembly.GetName().Name!,
        };
        DatabaseMigrationRunner runner = new(options);

        MigrationResult result = await runner.MigrateAsync();

        Assert.Equal(MigrationExitCodes.MigrationFailed, result.ExitCode);
        Assert.False(await TableExistsAsync(database.DatabasePath, "failing_partial"));
        Assert.True(File.Exists(database.DatabasePath));
        if (await TableExistsAsync(database.DatabasePath, SqliteStorageConventions.MigrationHistoryTable))
        {
            Assert.Equal(0, await CountAsync(database.DatabasePath, $"SELECT COUNT(*) FROM {SqliteStorageConventions.MigrationHistoryTable};"));
        }
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task BackupFailure_PreventsMigration()
    {
        using TemporarySqliteDatabase database = new();
        await CreateProbeDatabaseAsync(database.DatabasePath);
        MigrationResult result = await database.MigrateAsync(new FailingBackup());

        Assert.Equal(MigrationExitCodes.BackupFailed, result.ExitCode);
        Assert.False(await TableExistsAsync(database.DatabasePath, "users"));
        Assert.False(await TableExistsAsync(database.DatabasePath, SqliteStorageConventions.MigrationHistoryTable));
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task CancellationBeforeMigration_LeavesDatabaseUncreated()
    {
        using TemporarySqliteDatabase database = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        MigrationResult result = await database.MigrateAsync(cancellationToken: cancellation.Token);

        Assert.Equal(MigrationExitCodes.MigrationFailed, result.ExitCode);
        Assert.False(File.Exists(database.DatabasePath));
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task CorruptDatabase_CheckFailsClosedWithoutOverwrite()
    {
        using TemporarySqliteDatabase database = new();
        File.WriteAllText(database.DatabasePath, "not an sqlite database");

        MigrationResult result = await database.CheckAsync();

        Assert.Equal(MigrationExitCodes.IntegrityCheckFailed, result.ExitCode);
        Assert.Equal("not an sqlite database", File.ReadAllText(database.DatabasePath));
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task RepairIndexes_RechecksExistingDatabaseWithoutCreatingBackup()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        int backupsBefore = Directory.EnumerateFiles(database.BackupDirectoryPath, "*.sqlite").Count();

        MigrationResult result = await new DatabaseMigrationRunner(database.Options).RepairIndexesAsync();

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal(backupsBefore, Directory.EnumerateFiles(database.BackupDirectoryPath, "*.sqlite").Count());
        Assert.True((await database.CheckAsync()).Succeeded);
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task BusyDatabaseWriteFailsWithinConfiguredTimeout()
    {
        using TemporarySqliteDatabase database = new();
        SqliteDatabaseOptions options = new()
        {
            DataRoot = database.RootPath,
            BusyTimeoutMilliseconds = 100,
        };
        Assert.True((await new DatabaseMigrationRunner(options).MigrateAsync()).Succeeded);

        await using SqliteConnection blocker = new(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString());
        await blocker.OpenAsync();
        await using (SqliteCommand begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        SqliteException? failure = null;
        try
        {
            await using SqliteConnection contender = new SqliteConnectionFactory(options, createDataRoot: false).OpenConnection(SqliteConnectionAccess.ReadWrite);
            await using SqliteCommand write = contender.CreateCommand();
            write.CommandText = "INSERT INTO quota_profiles (id, name, max_active_sessions, max_game_package_bytes, max_session_bytes, max_output_bytes_per_second, created_at, updated_at, state_version) VALUES ('qtp_busy', 'busy', 1, 1, 1, 1, 0, 0, 0);";
            await write.ExecuteNonQueryAsync();
        }
        catch (SqliteException exception)
        {
            failure = exception;
        }

        stopwatch.Stop();
        Assert.NotNull(failure);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    private static async Task CreateProbeDatabaseAsync(string path)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE probe (id INTEGER PRIMARY KEY);";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(string path, string name)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<int> CountAsync(string path, string sql)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FailingBackup : ISqliteOnlineBackup
    {
        public Task<string> CreateAsync(SqliteDatabaseOptions options, SqliteDatabasePaths paths, string migrationId, TimeProvider timeProvider, CancellationToken cancellationToken) =>
            throw new SqliteBackupException("injected backup failure");
    }
}
