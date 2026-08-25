using CloudEmuera.Application.Games;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Games;

public sealed class GameContentCopyLeaseStore(
    CloudEmueraDbContext db,
    SqliteDatabaseOptions databaseOptions,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IGameContentCopyLeaseStore
{
    public async Task<GameContentCopyLease> AcquireAsync(
        string gameId,
        long contentRevision,
        string? contentDigest,
        string consumerType,
        string consumerId,
        CancellationToken cancellationToken = default)
    {
        string normalizedConsumer = consumerType.ToUpperInvariant();
        if (normalizedConsumer is not ("SESSION_CREATE" or "VALIDATION") || string.IsNullOrWhiteSpace(consumerId) || consumerId.Length > 64)
            throw new GameLibraryException(GameLibraryErrorCodes.InvalidInput, "The content-copy lease consumer is invalid.");
        await using FileStream mutationLock = AcquireMutationLock(gameId);
        var snapshot = await db.Games.AsNoTracking()
            .Where(game => game.Id == gameId && game.Status == GameStatus.Active)
            .Select(game => new { game.CurrentContentPath, game.ContentRevision })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new GameLibraryException(GameLibraryErrorCodes.NotFound, "The game was not found.");
        if (snapshot.CurrentContentPath is null || snapshot.ContentRevision != contentRevision)
            throw new GameLibraryException(GameLibraryErrorCodes.StateVersionConflict, "The requested game content is no longer current.");

        string path = Path.GetFullPath(Path.Combine(databaseOptions.DataRoot, snapshot.CurrentContentPath));
        GameStorageOwnerMarker.Validate(Path.GetDirectoryName(path)!, gameId);
        SafeFileHandle handle;
        try { handle = LinuxFileOperations.OpenDirectory(path); }
        catch (IOException exception) { throw new GameLibraryException(GameLibraryErrorCodes.UnsafePath, exception.Message); }
        string leaseId = $"gcl_{Guid.CreateVersion7():N}";
        DateTimeOffset now = timeProvider.GetUtcNow();
        db.GameContentCopyLeases.Add(new GameContentCopyLeaseRow
        {
            Id = leaseId,
            GameId = gameId,
            ContentRevision = contentRevision,
            ContentDigest = contentDigest,
            SourceContentPath = snapshot.CurrentContentPath,
            ConsumerType = normalizedConsumer,
            ConsumerId = consumerId,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(5),
        });
        try { await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateException exception)
        {
            handle.Dispose();
            throw new GameLibraryException(GameLibraryErrorCodes.Conflict, $"A content-copy lease already exists for this consumer. {exception.Message}");
        }
        return new Lease(leaseId, gameId, contentRevision, contentDigest, snapshot.CurrentContentPath, handle, scopeFactory, timeProvider);
    }

    private FileStream AcquireMutationLock(string gameId)
    {
        if (gameId.Length is < 6 or > 64 || !gameId.StartsWith("game_", StringComparison.Ordinal)
            || gameId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new GameLibraryException(GameLibraryErrorCodes.NotFound, "The game was not found.");
        string dataRoot = Path.GetFullPath(databaseOptions.DataRoot);
        string directory = Path.GetFullPath(Path.Combine(dataRoot, "games", gameId));
        if (!directory.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new GameLibraryException(GameLibraryErrorCodes.UnsafePath, "The game path is unsafe.");
        try { GameStorageOwnerMarker.Validate(directory, gameId); }
        catch (DirectoryNotFoundException) { throw new GameLibraryException(GameLibraryErrorCodes.NotFound, "The game was not found."); }
        catch (FileNotFoundException) { throw new GameLibraryException(GameLibraryErrorCodes.NotFound, "The game was not found."); }
        try
        {
            return new FileStream(Path.Combine(directory, ".mutation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new GameLibraryException(GameLibraryErrorCodes.Conflict, $"Another game content operation is in progress. {exception.Message}");
        }
    }

    private sealed class Lease(
        string leaseId,
        string gameId,
        long revision,
        string? digest,
        string sourceContentPath,
        SafeFileHandle handle,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider) : GameContentCopyLease
    {
        private int disposed;
        public override string LeaseId => leaseId;
        public override string GameId => gameId;
        public override long ContentRevision => revision;
        public override string? ContentDigest => digest;
        public override string ContentRootPath => LinuxFileOperations.GetProcFileDescriptorPath(handle);

        public override async ValueTask RenewAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, nameof(GameContentCopyLease));
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            CloudEmueraDbContext scopedDb = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
            DateTimeOffset now = timeProvider.GetUtcNow();
            int changed = await scopedDb.GameContentCopyLeases
                .Where(row => row.Id == leaseId && row.GameId == gameId && row.ContentRevision == revision
                    && row.SourceContentPath == sourceContentPath && row.ExpiresAt > now)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ExpiresAt, now.AddMinutes(5)), cancellationToken)
                .ConfigureAwait(false);
            if (changed != 1) throw new GameLibraryException(GameLibraryErrorCodes.Conflict, "The content-copy lease expired.");
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            handle.Dispose();
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            CloudEmueraDbContext scopedDb = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
            await scopedDb.GameContentCopyLeases.Where(row => row.Id == leaseId).ExecuteDeleteAsync().ConfigureAwait(false);
        }
    }
}
