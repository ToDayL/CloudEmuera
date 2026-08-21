using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Infrastructure.Games;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Sessions;

/// <summary>
/// Rebinds the filesystem identity stored in protected SessionRoot markers
/// after an operator restores a complete DataRoot into a new directory tree.
/// This is an explicit offline operation; normal API startup never changes a
/// marker identity.
/// </summary>
public static class SessionRootRestoreRebinder
{
    public static async Task<int> RunAsync(
        SqliteDatabaseOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        SqliteDatabasePaths paths;
        try
        {
            paths = options.ResolvePaths(createDataRoot: false);
        }
        catch (Exception exception) when (exception is SqlitePathException or SqliteConfigurationException or UnauthorizedAccessException)
        {
            log?.Invoke("operation=rebind-session-roots result=failed error=invalid_configuration");
            return MigrationExitCodes.InvalidConfiguration;
        }

        MigrationLockStatus lockStatus = MigrationLock.TryAcquire(paths.MigrationLockPath, out MigrationLock? migrationLock);
        if (lockStatus == MigrationLockStatus.Busy)
        {
            log?.Invoke("operation=rebind-session-roots result=failed error=migration_lock_busy");
            return MigrationExitCodes.LockBusy;
        }

        if (lockStatus != MigrationLockStatus.Acquired || migrationLock is null)
        {
            log?.Invoke("operation=rebind-session-roots result=failed error=migration_lock_invalid");
            return MigrationExitCodes.InvalidConfiguration;
        }

        using (migrationLock)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<GameRow> games = await ReadGamesAsync(options, cancellationToken).ConfigureAwait(false);
                List<SessionRow> sessions = await ReadSessionsAsync(options, cancellationToken).ConfigureAwait(false);
                foreach (GameRow game in games)
                {
                    ValidateRestoredGame(options, game);
                }
                foreach (SessionRow session in sessions)
                {
                    ValidateRestoredSession(options, session);
                }

                foreach (GameRow game in games)
                {
                    string gamePath = Path.Combine(Path.GetFullPath(options.DataRoot), "games", game.Id);
                    GameStorageOwnerMarker.RebindDirectoryIdentity(gamePath, game.Id, game.OwnerUserId);
                }
                foreach (SessionRow session in sessions)
                {
                    string rootPath = Path.Combine(Path.GetFullPath(options.DataRoot), "sessions", session.Id, "root");
                    SessionRootProtectedMarkerStore.RebindRootIdentity(options, session.Id, rootPath);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                log?.Invoke($"operation=rebind-session-roots games={games.Count} sessions={sessions.Count} result=succeeded");
                return MigrationExitCodes.Success;
            }
            catch (OperationCanceledException)
            {
                log?.Invoke("operation=rebind-session-roots result=cancelled");
                return MigrationExitCodes.MigrationFailed;
            }
            catch (SessionRuntimeException exception)
            {
                log?.Invoke($"operation=rebind-session-roots result=failed error={exception.Code}");
                return MigrationExitCodes.MigrationFailed;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                log?.Invoke("operation=rebind-session-roots result=failed error=invalid_restored_data");
                return MigrationExitCodes.MigrationFailed;
            }
            catch (Exception)
            {
                log?.Invoke("operation=rebind-session-roots result=failed error=rebind_failed");
                return MigrationExitCodes.MigrationFailed;
            }
        }
    }

    private static async Task<List<SessionRow>> ReadSessionsAsync(
        SqliteDatabaseOptions options,
        CancellationToken cancellationToken)
    {
        SqliteConnectionFactory connectionFactory = new(options, createDataRoot: false);
        await using SqliteConnection connection = connectionFactory.OpenConnection(SqliteConnectionAccess.ReadWrite);
        DbContextOptions<CloudEmueraDbContext> contextOptions = new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
            .Options;
        await using CloudEmueraDbContext context = new(contextOptions);
        return await context.Sessions.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<GameRow>> ReadGamesAsync(
        SqliteDatabaseOptions options,
        CancellationToken cancellationToken)
    {
        SqliteConnectionFactory connectionFactory = new(options, createDataRoot: false);
        await using SqliteConnection connection = connectionFactory.OpenConnection(SqliteConnectionAccess.ReadWrite);
        DbContextOptions<CloudEmueraDbContext> contextOptions = new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
            .Options;
        await using CloudEmueraDbContext context = new(contextOptions);
        return await context.Games.AsNoTracking()
            .Where(game => game.Status != GameStatus.Deleted)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateRestoredGame(SqliteDatabaseOptions options, GameRow game)
    {
        if (game.Id.Length is < 6 or > 64 || !game.Id.StartsWith("game_", StringComparison.Ordinal) ||
            game.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) ||
            string.IsNullOrWhiteSpace(game.OwnerUserId))
            throw new SessionRuntimeException(SessionRuntimeResultCodes.SessionRootInvalid, "The restored Game identity is invalid.");
        GameStorageOwnerMarker.ValidateForRestore(
            Path.Combine(Path.GetFullPath(options.DataRoot), "games", game.Id),
            game.Id,
            game.OwnerUserId);
    }

    private static void ValidateRestoredSession(SqliteDatabaseOptions options, SessionRow session)
    {
        if (!string.Equals(session.SessionRootPath, $"sessions/{session.Id}/root", StringComparison.Ordinal))
            throw new SessionRuntimeException(SessionRuntimeResultCodes.SessionRootInvalid, "The restored SessionRoot path is not the canonical path.");

        SessionRootProtectedMarker marker = SessionRootProtectedMarkerStore.Read(options, session.Id);
        if (!string.Equals(marker.SessionId, session.Id, StringComparison.Ordinal) ||
            !string.Equals(marker.OwnerUserId, session.OwnerUserId, StringComparison.Ordinal) ||
            !string.Equals(marker.GameId, session.GameId, StringComparison.Ordinal) ||
            marker.SourceContentRevision != session.SourceContentRevision ||
            !string.Equals(marker.SourceContentDigest, session.SourceContentDigest, StringComparison.Ordinal) ||
            !string.Equals(marker.SourceManifestDigest, session.SessionRootManifestDigest, StringComparison.Ordinal) ||
            !string.Equals(marker.MaterializedManifestDigest, session.SessionRootManifestDigest, StringComparison.Ordinal) ||
            (int)marker.SaveLayout != session.SaveLayout ||
            !string.Equals(marker.RuntimeVersion, session.RuntimeVersion, StringComparison.Ordinal))
        {
            throw new SessionRuntimeException(SessionRuntimeResultCodes.SessionRootInvalid, "The restored SessionRoot marker does not match the durable Session row.");
        }
    }
}
