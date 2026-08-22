using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudEmuera.Infrastructure.Persistence;

public sealed class DatabaseMigrationRunner
{
    private readonly SqliteDatabaseOptions _options;
    private readonly ISqliteOnlineBackup _backup;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _log;

    public DatabaseMigrationRunner(
        SqliteDatabaseOptions options,
        ISqliteOnlineBackup? backup = null,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _backup = backup ?? new SqliteOnlineBackup();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log;
    }

    public async Task<MigrationResult> MigrateAsync(CancellationToken cancellationToken = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        const string operation = "migrate";
        SqliteDatabasePaths? paths = null;
        try
        {
            paths = _options.ResolvePaths(createDataRoot: true);
            MigrationLockStatus lockStatus = MigrationLock.TryAcquire(paths.MigrationLockPath, out MigrationLock? migrationLock);
            if (lockStatus == MigrationLockStatus.Busy)
            {
                return Failure(operation, MigrationExitCodes.LockBusy, "migration_lock_busy", stopwatch);
            }

            if (lockStatus != MigrationLockStatus.Acquired || migrationLock is null)
            {
                return Failure(operation, MigrationExitCodes.InvalidConfiguration, "migration_lock_invalid", stopwatch);
            }

            using (migrationLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool existedBeforeOpen = File.Exists(paths.DatabasePath);
                MigrationState state = await ReadMigrationStateAsync(paths, existedBeforeOpen, readOnly: false, cancellationToken).ConfigureAwait(false);
                if (state.PendingMigrations.Count == 0)
                {
                    if (state.AppliedMigrations.Contains("20260809150000_CollapseGameVersionsIntoGames", StringComparer.Ordinal))
                    {
                        await LegacyGameDataRootMigrator.FinalizeAsync(_options, "20260809150000_CollapseGameVersionsIntoGames", cancellationToken).ConfigureAwait(false);
                    }
                    await VerifyDatabaseAsync(paths, readOnly: false, cancellationToken).ConfigureAwait(false);
                    Log(operation, "up_to_date", null, stopwatch);
                    return MigrationResult.SuccessResult(operation, state.AppliedMigrations, state.PendingMigrations);
                }

                string firstPendingMigration = state.PendingMigrations[0];
                const string collapseMigrationId = "20260809150000_CollapseGameVersionsIntoGames";
                LegacyGameCollapseReport? collapseReport = null;
                IReadOnlyList<LegacyGameCollapseSelection>? collapseSelections = null;
                if (state.PendingMigrations.Contains(collapseMigrationId, StringComparer.Ordinal))
                {
                    collapseReport = await LegacyGameCollapsePlanner.PlanAsync(_options, cancellationToken).ConfigureAwait(false);
                    collapseSelections = _options.GameCollapsePlanPath is null
                        ? LegacyGameCollapsePlanner.AutomaticSelections(collapseReport)
                        : await LegacyGameCollapsePlanner.LoadAndValidateSelectionsAsync(_options.GameCollapsePlanPath, collapseReport, cancellationToken).ConfigureAwait(false);
                }
                string? backupPath = null;
                if (existedBeforeOpen)
                {
                    try
                    {
                        backupPath = await _backup.CreateAsync(_options, paths, firstPendingMigration, _timeProvider, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is SqliteBackupException or IOException or UnauthorizedAccessException)
                    {
                        Log(operation, "backup_failed", firstPendingMigration, stopwatch);
                        return new MigrationResult(MigrationExitCodes.BackupFailed, operation, "failed", state.AppliedMigrations, state.PendingMigrations, ErrorCode: "backup_failed");
                    }
                }

                try
                {
                    if (collapseReport is not null)
                    {
                        await LegacyGameDataRootMigrator.PrepareAsync(_options, collapseReport, collapseMigrationId, cancellationToken).ConfigureAwait(false);
                    }
                    await ApplyMigrationsAsync(paths, collapseSelections, cancellationToken).ConfigureAwait(false);
                    if (collapseReport is not null)
                    {
                        await LegacyGameDataRootMigrator.FinalizeAsync(_options, collapseMigrationId, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (LegacyGameCollapseException exception)
                {
                    Log(operation, exception.Code, firstPendingMigration, stopwatch);
                    return new MigrationResult(MigrationExitCodes.MigrationFailed, operation, "failed", state.AppliedMigrations, state.PendingMigrations, backupPath, exception.Code);
                }
                catch (Exception)
                {
                    Log(operation, "migration_failed", firstPendingMigration, stopwatch);
                    return new MigrationResult(MigrationExitCodes.MigrationFailed, operation, "failed", state.AppliedMigrations, state.PendingMigrations, backupPath, "migration_failed");
                }

                try
                {
                    await VerifyDatabaseAsync(paths, readOnly: false, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    Log(operation, "integrity_check_failed", firstPendingMigration, stopwatch);
                    return new MigrationResult(MigrationExitCodes.IntegrityCheckFailed, operation, "failed", state.AppliedMigrations, state.PendingMigrations, backupPath, "integrity_check_failed");
                }

                MigrationState finalState = await ReadMigrationStateAsync(paths, existedBeforeOpen: true, readOnly: true, cancellationToken).ConfigureAwait(false);
                Log(operation, "succeeded", firstPendingMigration, stopwatch);
                return MigrationResult.SuccessResult(operation, finalState.AppliedMigrations, finalState.PendingMigrations, backupPath);
            }
        }
        catch (OperationCanceledException)
        {
            Log(operation, "cancelled", null, stopwatch);
            return new MigrationResult(MigrationExitCodes.MigrationFailed, operation, "cancelled", [], [], ErrorCode: "cancelled");
        }
        catch (DatabaseNewerThanBinaryException)
        {
            Log(operation, "database_newer_than_binary", null, stopwatch);
            return Failure(operation, MigrationExitCodes.DatabaseNewerThanBinary, "database_newer_than_binary", stopwatch);
        }
        catch (LegacyGameCollapseException exception)
        {
            Log(operation, exception.Code, null, stopwatch);
            return Failure(operation, MigrationExitCodes.MigrationFailed, exception.Code, stopwatch);
        }
        catch (Exception exception) when (exception is SqlitePathException or SqliteConfigurationException or UnauthorizedAccessException)
        {
            Log(operation, "invalid_configuration", null, stopwatch);
            return Failure(operation, MigrationExitCodes.InvalidConfiguration, "invalid_configuration", stopwatch);
        }
        catch (SqliteIntegrityException)
        {
            Log(operation, "integrity_check_failed", null, stopwatch);
            return Failure(operation, MigrationExitCodes.IntegrityCheckFailed, "integrity_check_failed", stopwatch);
        }
        catch (Exception)
        {
            Log(operation, "migration_failed", null, stopwatch);
            return Failure(operation, MigrationExitCodes.MigrationFailed, "migration_failed", stopwatch);
        }
    }

    public async Task<MigrationResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        const string operation = "check";
        try
        {
            SqliteDatabasePaths paths = _options.ResolvePaths(createDataRoot: false);
            if (!File.Exists(paths.DatabasePath))
            {
                return Failure(operation, MigrationExitCodes.IntegrityCheckFailed, "database_missing", stopwatch);
            }

            MigrationState state = await ReadMigrationStateAsync(paths, existedBeforeOpen: true, readOnly: true, cancellationToken).ConfigureAwait(false);
            if (state.PendingMigrations.Count != 0)
            {
                return new MigrationResult(MigrationExitCodes.IntegrityCheckFailed, operation, "failed", state.AppliedMigrations, state.PendingMigrations, ErrorCode: "pending_migrations");
            }

            await VerifyDatabaseAsync(paths, readOnly: true, cancellationToken).ConfigureAwait(false);
            Log(operation, "succeeded", null, stopwatch);
            return MigrationResult.SuccessResult(operation, state.AppliedMigrations, state.PendingMigrations);
        }
        catch (DatabaseNewerThanBinaryException)
        {
            Log(operation, "database_newer_than_binary", null, stopwatch);
            return Failure(operation, MigrationExitCodes.DatabaseNewerThanBinary, "database_newer_than_binary", stopwatch);
        }
        catch (Exception exception) when (exception is SqlitePathException or SqliteConfigurationException or UnauthorizedAccessException)
        {
            Log(operation, "invalid_configuration", null, stopwatch);
            return Failure(operation, MigrationExitCodes.InvalidConfiguration, "invalid_configuration", stopwatch);
        }
        catch (OperationCanceledException)
        {
            Log(operation, "cancelled", null, stopwatch);
            return Failure(operation, MigrationExitCodes.IntegrityCheckFailed, "cancelled", stopwatch);
        }
        catch (Exception)
        {
            Log(operation, "integrity_check_failed", null, stopwatch);
            return Failure(operation, MigrationExitCodes.IntegrityCheckFailed, "integrity_check_failed", stopwatch);
        }
    }

    /// <summary>
    /// Rebuilds SQLite indexes after an operator has made a cold copy of the
    /// complete DataRoot and stopped every API/Worker process. This is an
    /// explicit recovery operation, not part of normal startup: silently
    /// repairing a database would hide broader storage corruption.
    /// </summary>
    public async Task<MigrationResult> RepairIndexesAsync(CancellationToken cancellationToken = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        const string operation = "repair-indexes";
        try
        {
            SqliteDatabasePaths paths = _options.ResolvePaths(createDataRoot: false);
            if (!File.Exists(paths.DatabasePath))
            {
                return Failure(operation, MigrationExitCodes.IntegrityCheckFailed, "database_missing", stopwatch);
            }

            MigrationLockStatus lockStatus = MigrationLock.TryAcquire(paths.MigrationLockPath, out MigrationLock? migrationLock);
            if (lockStatus == MigrationLockStatus.Busy)
            {
                return Failure(operation, MigrationExitCodes.LockBusy, "migration_lock_busy", stopwatch);
            }

            if (lockStatus != MigrationLockStatus.Acquired || migrationLock is null)
            {
                return Failure(operation, MigrationExitCodes.InvalidConfiguration, "migration_lock_invalid", stopwatch);
            }

            using (migrationLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MigrationState state = await ReadMigrationStateAsync(paths, existedBeforeOpen: true, readOnly: true, cancellationToken).ConfigureAwait(false);
                SqliteConnectionFactory connectionFactory = new(_options, createDataRoot: false);
                await using SqliteConnection connection = connectionFactory.OpenConnection(SqliteConnectionAccess.ReadWrite);
                await using SqliteCommand reindex = connection.CreateCommand();
                reindex.CommandText = "REINDEX;";
                await reindex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await SqliteIntegrityChecker.VerifyAsync(connection, cancellationToken).ConfigureAwait(false);
                Log(operation, "succeeded", null, stopwatch);
                return MigrationResult.SuccessResult(operation, state.AppliedMigrations, state.PendingMigrations);
            }
        }
        catch (OperationCanceledException)
        {
            Log(operation, "cancelled", null, stopwatch);
            return new MigrationResult(MigrationExitCodes.MigrationFailed, operation, "cancelled", [], [], ErrorCode: "cancelled");
        }
        catch (DatabaseNewerThanBinaryException)
        {
            Log(operation, "database_newer_than_binary", null, stopwatch);
            return Failure(operation, MigrationExitCodes.DatabaseNewerThanBinary, "database_newer_than_binary", stopwatch);
        }
        catch (SqliteIntegrityException)
        {
            Log(operation, "integrity_check_failed", null, stopwatch);
            return Failure(operation, MigrationExitCodes.IntegrityCheckFailed, "integrity_check_failed", stopwatch);
        }
        catch (Exception exception) when (exception is SqlitePathException or SqliteConfigurationException or UnauthorizedAccessException)
        {
            Log(operation, "invalid_configuration", null, stopwatch);
            return Failure(operation, MigrationExitCodes.InvalidConfiguration, "invalid_configuration", stopwatch);
        }
        catch (Exception)
        {
            Log(operation, "repair_failed", null, stopwatch);
            return Failure(operation, MigrationExitCodes.MigrationFailed, "repair_failed", stopwatch);
        }
    }

    private async Task<MigrationState> ReadMigrationStateAsync(
        SqliteDatabasePaths paths,
        bool existedBeforeOpen,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        SqliteConnectionAccess access = readOnly
            ? SqliteConnectionAccess.ReadOnly
            : existedBeforeOpen ? SqliteConnectionAccess.ReadWrite : SqliteConnectionAccess.ReadWriteCreate;
        SqliteConnectionFactory connectionFactory = new(_options, createDataRoot: !readOnly);
        await using SqliteConnection connection = connectionFactory.OpenConnection(access);
        await using CloudEmueraDbContext context = CreateContext(connection);
        string[] known = context.Database.GetMigrations().ToArray();
        string[] applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        ValidateMigrationHistory(known, applied);
        HashSet<string> appliedSet = applied.ToHashSet(StringComparer.Ordinal);
        string[] pending = known.Where(id => !appliedSet.Contains(id)).ToArray();
        return new MigrationState(known, applied, pending);
    }

    private async Task ApplyMigrationsAsync(SqliteDatabasePaths paths, IReadOnlyList<LegacyGameCollapseSelection>? collapseSelections, CancellationToken cancellationToken)
    {
        bool existed = File.Exists(paths.DatabasePath);
        SqliteConnectionFactory connectionFactory = new(_options, createDataRoot: true);
        await using SqliteConnection connection = connectionFactory.OpenConnection(existed ? SqliteConnectionAccess.ReadWrite : SqliteConnectionAccess.ReadWriteCreate);
        try
        {
            if (collapseSelections is not null)
            {
                await using SqliteCommand create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE IF NOT EXISTS __cloudemuera_game_collapse_selection (game_id TEXT PRIMARY KEY NOT NULL, current_version_id TEXT NULL, workspace_version_id TEXT NULL); DELETE FROM __cloudemuera_game_collapse_selection;";
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                foreach (LegacyGameCollapseSelection selection in collapseSelections)
                {
                    await using SqliteCommand insert = connection.CreateCommand();
                    insert.CommandText = "INSERT INTO __cloudemuera_game_collapse_selection (game_id,current_version_id,workspace_version_id) VALUES ($game,$current,$workspace);";
                    insert.Parameters.AddWithValue("$game", selection.GameId);
                    insert.Parameters.AddWithValue("$current", (object?)selection.CurrentVersionId ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$workspace", (object?)selection.WorkspaceVersionId ?? DBNull.Value);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            await using CloudEmueraDbContext context = CreateContext(connection);
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (collapseSelections is not null)
            {
                await using SqliteCommand cleanup = connection.CreateCommand();
                cleanup.CommandText = "DROP TABLE IF EXISTS __cloudemuera_game_collapse_selection;";
                await cleanup.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task VerifyDatabaseAsync(SqliteDatabasePaths paths, bool readOnly, CancellationToken cancellationToken)
    {
        SqliteConnectionFactory connectionFactory = new(_options, createDataRoot: !readOnly);
        await using SqliteConnection connection = connectionFactory.OpenConnection(readOnly ? SqliteConnectionAccess.ReadOnly : SqliteConnectionAccess.ReadWrite);
        await SqliteIntegrityChecker.VerifyAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private CloudEmueraDbContext CreateContext(SqliteConnection connection)
    {
        DbContextOptions<CloudEmueraDbContext> options = new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(connection, sqlite =>
            {
                sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable);
                sqlite.MigrationsAssembly(_options.MigrationsAssembly);
            })
            .Options;
        return new CloudEmueraDbContext(options);
    }

    private static void ValidateMigrationHistory(string[] known, string[] applied)
    {
        for (int index = 0; index < applied.Length; index++)
        {
            if (index >= known.Length || !string.Equals(known[index], applied[index], StringComparison.Ordinal))
            {
                throw new DatabaseNewerThanBinaryException();
            }
        }
    }

    private MigrationResult Failure(string operation, int exitCode, string errorCode, Stopwatch stopwatch)
    {
        Log(operation, "failed", null, stopwatch);
        return new MigrationResult(exitCode, operation, "failed", [], [], ErrorCode: errorCode);
    }

    private void Log(string operation, string result, string? migrationId, Stopwatch stopwatch)
    {
        if (_log is null)
        {
            return;
        }

        string migrationPart = migrationId is null ? string.Empty : $" migration_id={migrationId}";
        _log($"operation={operation}{migrationPart} elapsed_ms={stopwatch.ElapsedMilliseconds} result={result}");
    }

    private sealed record MigrationState(
        IReadOnlyList<string> KnownMigrations,
        IReadOnlyList<string> AppliedMigrations,
        IReadOnlyList<string> PendingMigrations);
}

public sealed class DatabaseNewerThanBinaryException : Exception;
