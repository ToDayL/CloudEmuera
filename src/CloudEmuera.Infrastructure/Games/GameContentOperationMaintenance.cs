using CloudEmuera.Application.Games;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Games;

/// <summary>
/// Reconciles the durable boundary between content renames and SQLite metadata.
/// It is deliberately conservative: an unknown tree is left in place and reported
/// as failed rather than recursively deleting a path that cannot be proved owned.
/// </summary>
public sealed class GameContentOperationMaintenance(
    CloudEmueraDbContext db,
    SqliteDatabaseOptions databaseOptions,
    TimeProvider timeProvider) : IGameContentOperationMaintenance
{
    private static readonly TimeSpan RetiredSafetyPeriod = TimeSpan.FromMinutes(10);

    public async Task<int> ReconcileAsync(int maxItems = 32, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItems);
        DateTimeOffset now = timeProvider.GetUtcNow();
        int handled = 0;
        string[] ready = await db.GameContentOperations.AsNoTracking()
            .Where(operation => operation.Status == GameContentOperationStatus.ContentReady)
            .OrderBy(operation => operation.CreatedAt).ThenBy(operation => operation.Id)
            .Take(maxItems)
            .Select(operation => operation.Id)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (string operationId in ready)
        {
            if (await CompleteContentReadyAsync(operationId, now, cancellationToken).ConfigureAwait(false)) handled++;
        }

        var expired = await db.GameContentOperations.AsNoTracking()
            .Where(operation => operation.Status == GameContentOperationStatus.Pending
                || operation.Status == GameContentOperationStatus.Running)
            .Where(operation => operation.LeaseExpiresAt <= now)
            .OrderBy(operation => operation.LeaseExpiresAt).ThenBy(operation => operation.Id)
            .Take(maxItems)
            .Select(operation => new { operation.Id, operation.GameId, operation.ExpectedGameStateVersion })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var operation in expired)
        {
            int changed = await db.GameContentOperations
                .Where(value => value.Id == operation.Id
                    && (value.Status == GameContentOperationStatus.Pending || value.Status == GameContentOperationStatus.Running)
                    && value.LeaseExpiresAt <= now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.Status, GameContentOperationStatus.Failed)
                    .SetProperty(value => value.ErrorCode, "OPERATION_LEASE_EXPIRED")
                    .SetProperty(value => value.UpdatedAt, now)
                    .SetProperty(value => value.CompletedAt, now)
                    .SetProperty(value => value.StateVersion, value => value.StateVersion + 1), cancellationToken)
                .ConfigureAwait(false);
            if (changed == 1)
            {
                await db.Games.Where(game => game.Id == operation.GameId
                        && game.WorkspaceStatus == GameWorkspaceStatus.Validating
                        && game.StateVersion >= operation.ExpectedGameStateVersion)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(game => game.WorkspaceStatus, GameWorkspaceStatus.Draft)
                        .SetProperty(game => game.UpdatedAt, now)
                        .SetProperty(game => game.StateVersion, game => game.StateVersion + 1), cancellationToken)
                    .ConfigureAwait(false);
                CleanupKnownWork(operation.GameId, operation.Id);
                handled++;
            }
        }

        handled += await ReapRetiredAsync(now, maxItems, cancellationToken).ConfigureAwait(false);
        handled += await ReapDeletedGamesAsync(now, maxItems, cancellationToken).ConfigureAwait(false);
        return handled;
    }

    private async Task<bool> CompleteContentReadyAsync(string operationId, DateTimeOffset now, CancellationToken token)
    {
        GameContentOperationRow? operation = await db.GameContentOperations.SingleOrDefaultAsync(value => value.Id == operationId
            && value.Status == GameContentOperationStatus.ContentReady, token).ConfigureAwait(false);
        if (operation is null) return false;
        GameRow? game = await db.Games.SingleOrDefaultAsync(value => value.Id == operation.GameId, token).ConfigureAwait(false);
        if (game is null) return false;
        await using FileStream? mutationLock = TryAcquireMutationLock(game.Id);
        if (mutationLock is null) return false;
        string gameDirectory = GameDirectory(game.Id);
        try
        {
            GameStorageOwnerMarker.Validate(gameDirectory, game.Id);
            string current = Path.Combine(gameDirectory, "content");
            ScannedGameTree tree = GameContentTreeScanner.Scan(current);
            if (operation.ContentDigest is not null && tree.ContentDigest is not null &&
                !string.Equals(tree.ContentDigest, operation.ContentDigest, StringComparison.Ordinal))
            {
                RestoreActivationTrees(gameDirectory, operation.Id);
                await MarkFailedAsync(operation.Id, "CONTENT_READY_TREE_MISMATCH", now, token).ConfigureAwait(false);
                return true;
            }

            if (game.ContentDigest == operation.ContentDigest && game.WorkspaceStatus == GameWorkspaceStatus.None)
            {
                await MarkCommittedAsync(operation.Id, now, token).ConfigureAwait(false);
                return true;
            }

            if (game.ContentRevision != operation.ExpectedContentRevision
                || game.WorkspaceStatus != GameWorkspaceStatus.Validating)
            {
                RestoreActivationTrees(gameDirectory, operation.Id);
                await MarkFailedAsync(operation.Id, "CONTENT_READY_STATE_CONFLICT", now, token).ConfigureAwait(false);
                return true;
            }

            game.CurrentContentPath = $"games/{game.Id}/content";
            game.ContentDigest = tree.ContentDigest;
            game.ContentRevision++;
            game.ManifestJson = tree.ManifestJson;
            game.RuntimeConfigJson = tree.RuntimeConfigJson;
            game.CompatibilitySummaryJson = "{}";
            game.ActivatedBy = game.OwnerUserId;
            game.ActivatedAt = now;
            game.Status = GameStatus.Active;
            game.WorkspacePath = null;
            game.WorkspaceStatus = GameWorkspaceStatus.None;
            game.UpdatedAt = now;
            game.StateVersion++;

            GameFileRow[] oldCurrent = await db.GameFiles.Where(file => file.GameId == game.Id && file.Scope == "CURRENT").ToArrayAsync(token).ConfigureAwait(false);
            db.GameFiles.RemoveRange(oldCurrent);
            db.GameFiles.AddRange(tree.Entries.Select(entry => new GameFileRow
            {
                GameId = game.Id,
                Scope = "CURRENT",
                LogicalPath = entry.Path,
                EntryKind = entry.EntryKind,
                ByteLength = entry.Bytes,
                ContentDigest = entry.Digest,
                FileKind = entry.FileKind,
                TextEncoding = entry.Encoding,
                HasBom = entry.HasBom,
            }));
            await MarkCommittedAsync(operation, now, token).ConfigureAwait(false);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            try { RestoreActivationTrees(gameDirectory, operation.Id); } catch (IOException) { }
            await MarkFailedAsync(operation.Id, "CONTENT_READY_RECOVERY_FAILED", now, token).ConfigureAwait(false);
            return true;
        }
    }

    private async Task<int> ReapRetiredAsync(DateTimeOffset now, int maxItems, CancellationToken token)
    {
        DateTimeOffset cutoff = now - RetiredSafetyPeriod;
        var candidates = await db.GameContentOperations.AsNoTracking()
            .Where(operation => operation.Status == GameContentOperationStatus.Committed
                && operation.CompletedAt != null && operation.CompletedAt <= cutoff)
            .OrderBy(operation => operation.CompletedAt).ThenBy(operation => operation.Id)
            .Take(maxItems)
            .Select(operation => new { operation.Id, operation.GameId, operation.ExpectedContentRevision })
            .ToArrayAsync(token).ConfigureAwait(false);
        int count = 0;
        foreach (var candidate in candidates)
        {
            bool leased = await db.GameContentCopyLeases.AnyAsync(lease => lease.GameId == candidate.GameId
                && lease.ContentRevision == candidate.ExpectedContentRevision && lease.ExpiresAt > now, token).ConfigureAwait(false);
            if (leased) continue;
            await using FileStream? mutationLock = TryAcquireMutationLock(candidate.GameId);
            if (mutationLock is null) continue;
            string gameDirectory = GameDirectory(candidate.GameId);
            try
            {
                GameStorageOwnerMarker.Validate(gameDirectory, candidate.GameId);
                using SafeFileHandle parent = LinuxFileOperations.OpenDirectory(gameDirectory);
                foreach (string name in new[] { $"content-retired-{candidate.Id}", $"workspace-activated-{candidate.Id}", $"workspace-retired-{candidate.Id}" })
                {
                    using SafeFileHandle? existing = LinuxFileOperations.TryOpenDirectoryAt(parent, name);
                    if (existing is null) continue;
                    if (LinuxFileOperations.TryDeleteTreeAt(parent, name, allowReadOnly: true)) count++;
                }
                LinuxFileOperations.Sync(parent);
            }
            catch (Exception exception) when (exception is IOException or GameLibraryException) { }
        }
        return count;
    }

    private async Task<int> ReapDeletedGamesAsync(DateTimeOffset now, int maxItems, CancellationToken token)
    {
        DateTimeOffset cutoff = now - RetiredSafetyPeriod;
        string[] games = await db.Games.AsNoTracking()
            .Where(game => game.Status == GameStatus.Deleted && game.DeletedAt != null && game.DeletedAt <= cutoff)
            .OrderBy(game => game.DeletedAt).ThenBy(game => game.Id)
            .Take(maxItems)
            .Select(game => game.Id)
            .ToArrayAsync(token).ConfigureAwait(false);
        int handled = 0;
        foreach (string gameId in games)
        {
            if (await db.GameContentCopyLeases.AnyAsync(lease => lease.GameId == gameId && lease.ExpiresAt > now, token).ConfigureAwait(false))
                continue;
            if (await db.GameContentOperations.AnyAsync(operation => operation.GameId == gameId
                    && (operation.Status == GameContentOperationStatus.Pending
                        || operation.Status == GameContentOperationStatus.Running
                        || operation.Status == GameContentOperationStatus.ContentReady), token).ConfigureAwait(false))
                continue;
            string[] operationIds = await db.GameContentOperations.AsNoTracking()
                .Where(operation => operation.GameId == gameId)
                .Select(operation => operation.Id)
                .ToArrayAsync(token).ConfigureAwait(false);
            await using FileStream? mutationLock = TryAcquireMutationLock(gameId);
            if (mutationLock is null) continue;
            string gameDirectory = GameDirectory(gameId);
            try
            {
                GameStorageOwnerMarker.Validate(gameDirectory, gameId);
                using SafeFileHandle parent = LinuxFileOperations.OpenDirectory(gameDirectory);
                var names = new HashSet<string>(StringComparer.Ordinal) { "content", "workspace" };
                foreach (string operationId in operationIds)
                {
                    names.Add($".content-{operationId}");
                    names.Add($".validate-{operationId}");
                    names.Add($".workspace-{operationId}");
                    names.Add($"content-retired-{operationId}");
                    names.Add($"workspace-retired-{operationId}");
                    names.Add($"workspace-activated-{operationId}");
                    names.Add($"content-recovery-{operationId}");
                }
                bool removed = false;
                foreach (string name in names)
                {
                    using SafeFileHandle? existing = LinuxFileOperations.TryOpenDirectoryAt(parent, name);
                    if (existing is null) continue;
                    if (LinuxFileOperations.TryDeleteTreeAt(parent, name, allowReadOnly: true)) removed = true;
                }
                LinuxFileOperations.Sync(parent);
                if (removed) handled++;
            }
            catch (Exception exception) when (exception is IOException or GameLibraryException) { }
        }
        return handled;
    }

    private void CleanupKnownWork(string gameId, string operationId)
    {
        try
        {
            string directory = GameDirectory(gameId);
            GameStorageOwnerMarker.Validate(directory, gameId);
            using SafeFileHandle parent = LinuxFileOperations.OpenDirectory(directory);
            foreach (string name in new[] { $".content-{operationId}", $".validate-{operationId}" })
                LinuxFileOperations.TryDeleteTreeAt(parent, name, allowReadOnly: true);
            LinuxFileOperations.Sync(parent);
        }
        catch (Exception exception) when (exception is IOException or GameLibraryException) { }
    }

    private async Task MarkFailedAsync(string operationId, string code, DateTimeOffset now, CancellationToken token) =>
        await db.GameContentOperations.Where(operation => operation.Id == operationId
                && operation.Status != GameContentOperationStatus.Committed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(operation => operation.Status, GameContentOperationStatus.Failed)
                .SetProperty(operation => operation.ErrorCode, code)
                .SetProperty(operation => operation.UpdatedAt, now)
                .SetProperty(operation => operation.CompletedAt, now)
                .SetProperty(operation => operation.StateVersion, operation => operation.StateVersion + 1), token)
            .ConfigureAwait(false);

    private async Task MarkCommittedAsync(string operationId, DateTimeOffset now, CancellationToken token) =>
        await db.GameContentOperations.Where(operation => operation.Id == operationId
                && operation.Status == GameContentOperationStatus.ContentReady)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(operation => operation.Status, GameContentOperationStatus.Committed)
                .SetProperty(operation => operation.UpdatedAt, now)
                .SetProperty(operation => operation.CompletedAt, now)
                .SetProperty(operation => operation.StateVersion, operation => operation.StateVersion + 1), token)
            .ConfigureAwait(false);

    private static Task MarkCommittedAsync(GameContentOperationRow operation, DateTimeOffset now, CancellationToken token)
    {
        operation.Status = GameContentOperationStatus.Committed;
        operation.UpdatedAt = now;
        operation.CompletedAt = now;
        operation.StateVersion++;
        return Task.CompletedTask;
    }

    private FileStream? TryAcquireMutationLock(string gameId)
    {
        try
        {
            string directory = GameDirectory(gameId);
            GameStorageOwnerMarker.Validate(directory, gameId);
            return new FileStream(Path.Combine(directory, ".mutation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or GameLibraryException) { return null; }
    }

    private string GameDirectory(string gameId)
    {
        string root = Path.GetFullPath(databaseOptions.DataRoot);
        string path = Path.GetFullPath(Path.Combine(root, "games", gameId));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("The game path escaped the data root.");
        return path;
    }

    private static void RestoreActivationTrees(string gameDirectory, string operationId)
    {
        if (!OperatingSystem.IsLinux()) return;
        using SafeFileHandle parent = LinuxFileOperations.OpenDirectory(gameDirectory);
        using SafeFileHandle? retired = LinuxFileOperations.TryOpenDirectoryAt(parent, $"content-retired-{operationId}");
        if (retired is not null)
        {
            string recoveryName = $"content-recovery-{operationId}";
            using SafeFileHandle? current = LinuxFileOperations.TryOpenDirectoryAt(parent, "content");
            if (current is not null) LinuxFileOperations.RenameAt(parent, "content", recoveryName);
            LinuxFileOperations.RenameAt(parent, $"content-retired-{operationId}", "content");
        }
        using SafeFileHandle? activatedWorkspace = LinuxFileOperations.TryOpenDirectoryAt(parent, $"workspace-activated-{operationId}");
        using SafeFileHandle? workspace = LinuxFileOperations.TryOpenDirectoryAt(parent, "workspace");
        if (activatedWorkspace is not null && workspace is null)
            LinuxFileOperations.RenameAt(parent, $"workspace-activated-{operationId}", "workspace");
        LinuxFileOperations.Sync(parent);
    }
}
