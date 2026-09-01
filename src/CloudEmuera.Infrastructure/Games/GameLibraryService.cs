using System.Text;
using System.Text.Json;
using CloudEmuera.Application.GamePackages;
using CloudEmuera.Application.Games;
using CloudEmuera.Application.Identity;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.GamePackages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Games;

public sealed class GameLibraryService(
    CloudEmueraDbContext db,
    IGamePackageIngestionService ingestions,
    IGameContentValidator validator,
    SqliteDatabaseOptions databaseOptions,
    TimeProvider timeProvider) : IGameLibraryService
{
    private const int MaxTextPreviewBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan ProgressPersistInterval = TimeSpan.FromMilliseconds(250);
    private DateTimeOffset CurrentTime => timeProvider.GetUtcNow();

    public async Task<IReadOnlyList<GameLibraryItem>> ListAsync(CurrentActor actor, CancellationToken cancellationToken = default) =>
        await db.Games.AsNoTracking()
            .Where(game => game.Status != GameStatus.Deleted &&
                (game.OwnerUserId == actor.UserId || game.Visibility == GameVisibility.ServerShared))
            .OrderBy(game => game.Name)
            .Select(game => ToItem(game))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async Task<GameLibraryItem> UploadAsync(
        CurrentActor actor,
        string name,
        string visibility,
        Stream content,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        GameLibraryItem game = await CreateAsync(actor, name, visibility, cancellationToken).ConfigureAwait(false);
        GameContentOperationRow? uploadOperation = null;
        try
        {
            GameRow createdRow = await FindOwnedAsync(actor, game.Id, cancellationToken).ConfigureAwait(false);
            uploadOperation = await StartOperationAsync(
                createdRow,
                GameContentOperationType.Import,
                ingestionId: null,
                contentDigest: null,
                workPath: null,
                cancellationToken,
                requestId,
                GameContentOperationStage.Receiving).ConfigureAwait(false);
            var progress = new OperationProgressReporter(this, uploadOperation.Id, ProgressPersistInterval);
            IngestedGamePackage package = await ingestions.IngestAsync(
                new GamePackageIngestionRequest(actor.UserId, content, requestId, progress.ReportPackageAsync),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            // A public upload is already a complete content replacement.  The
            // staged ZIP content is copied once into a private content staging
            // directory, validated there, and renamed to `content`; it never
            // takes the legacy workspace -> content copy detour.
            return await ImportAndActivateAsync(
                actor,
                game.Id,
                package.IngestionId,
                game.StateVersion,
                uploadOperation.Id,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (uploadOperation is not null)
            {
                string errorCode = exception switch
                {
                    GamePackageIngestionException ingestionException => ingestionException.Code,
                    GameLibraryException libraryException => libraryException.Code,
                    _ => GameLibraryErrorCodes.Conflict,
                };
                await FailOperationAsync(uploadOperation.Id, errorCode).ConfigureAwait(false);
                // FailOperationAsync uses ExecuteUpdate and advances the durable
                // operation state without synchronizing EF's tracked instance.
                // Do not let that stale operation participate in the Game
                // tombstone SaveChanges below.
                db.ChangeTracker.Clear();
            }
            // A failed upload/load must not leave a user-visible empty Game that
            // can only be repaired through the removed multi-step workflow.
            try
            {
                GameRow? row = await db.Games.SingleOrDefaultAsync(
                    value => value.Id == game.Id && value.OwnerUserId == actor.UserId && value.Status != GameStatus.Deleted,
                    CancellationToken.None).ConfigureAwait(false);
                bool hasPublishedTree = row?.CurrentContentPath is not null || (row is not null && await db.GameContentOperations.AnyAsync(
                    operation => operation.GameId == row.Id && operation.Status == GameContentOperationStatus.ContentReady,
                    CancellationToken.None).ConfigureAwait(false));
                if (row is not null && !hasPublishedTree)
                {
                    row.Status = GameStatus.Deleted;
                    row.DeletedBy = actor.UserId;
                    row.DeletedAt = timeProvider.GetUtcNow();
                    Touch(row, row.DeletedAt.Value);
                    AddAudit(actor, "GAME_UPLOAD_REJECTED", row.Id, row.UpdatedAt);
                    await SaveAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception cleanupException) when (cleanupException is DbUpdateException or DbUpdateConcurrencyException)
            {
                // Recovery maintenance owns any durable intermediate operation.
            }
            throw;
        }
    }

    public async Task<GameLibraryItem> CreateAsync(CurrentActor actor, string name, string visibility, CancellationToken cancellationToken = default)
    {
        string normalizedName = NormalizeName(name);
        GameVisibility parsedVisibility = ParseVisibility(visibility);
        DateTimeOffset now = timeProvider.GetUtcNow();
        bool nameTaken = await db.Games.AnyAsync(game => game.OwnerUserId == actor.UserId && game.Name == normalizedName
            && game.Status != GameStatus.Deleted, cancellationToken).ConfigureAwait(false);
        if (nameTaken) throw new GameLibraryException(GameLibraryErrorCodes.NameConflict, "同名游戏已存在。");
        var row = new GameRow
        {
            Id = $"game_{Guid.CreateVersion7():N}",
            OwnerUserId = actor.UserId,
            Name = normalizedName,
            Visibility = parsedVisibility,
            Status = GameStatus.Active,
            WorkspaceStatus = GameWorkspaceStatus.None,
            CreatedAt = now,
            UpdatedAt = now,
        };
        string gameDirectory = GameDirectory(row.Id);
        InitializeGameDirectory(gameDirectory, row.Id, actor.UserId);
        db.Games.Add(row);
        AddAudit(actor, "GAME_CREATE", row.Id, now);
        try { await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateException)
        {
            Directory.Move(gameDirectory, $"{gameDirectory}.failed-{Guid.NewGuid():N}");
            throw new GameLibraryException(GameLibraryErrorCodes.NameConflict, "同名游戏已存在。");
        }
        return ToItem(row);
    }

    private async Task<GameLibraryItem> ImportAndActivateAsync(
        CurrentActor actor,
        string gameId,
        string ingestionId,
        int expectedStateVersion,
        string operationId,
        OperationProgressReporter progress,
        CancellationToken cancellationToken)
    {
        await using FileStream mutationLock = AcquireMutationLock(gameId);
        GameRow row = await FindOwnedAsync(actor, gameId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(row, expectedStateVersion);
        GameContentOperationRow operation = await db.GameContentOperations.SingleOrDefaultAsync(
            value => value.Id == operationId && value.GameId == gameId && value.Status == GameContentOperationStatus.Running,
            cancellationToken).ConfigureAwait(false) ?? throw Conflict("The game upload operation is no longer active.");
        operation.IngestionId = ingestionId;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await using GamePackageConsumption consumption = await ingestions.BeginConsumeAsync(
            ingestionId, actor.UserId, expectedContentDigest: null, cancellationToken).ConfigureAwait(false);
        await progress.ReportAsync(GameContentOperationStage.ConsumingStaging, null, cancellationToken).ConfigureAwait(false);

        string gameDirectory = GameDirectory(gameId);
        string staging = Path.Combine(gameDirectory, $".content-{Guid.CreateVersion7():N}");
        operation.WorkPath = Path.GetRelativePath(databaseOptions.DataRoot, staging).Replace('\\', '/');
        bool contentReady = false;
        try
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("Secure game content import requires Linux openat semantics.");

            await progress.ReportAsync(GameContentOperationStage.CopyingContent, null, cancellationToken).ConfigureAwait(false);
            using (SafeFileHandle gameDirectoryHandle = LinuxFileOperations.OpenDirectory(gameDirectory))
                LinuxFileOperations.CopyTree(consumption.ContentDirectoryHandle, gameDirectoryHandle, Path.GetFileName(staging), syncToDisk: false);
            await RenewOperationAsync(operation.Id, cancellationToken).ConfigureAwait(false);

            await progress.ReportAsync(GameContentOperationStage.ValidatingContent, null, cancellationToken).ConfigureAwait(false);
            MakeReadOnly(staging);
            WorkspaceInspection inspection = await InspectWithParserAsync(staging, progress, cancellationToken).ConfigureAwait(false);
            await RenewOperationAsync(operation.Id, cancellationToken).ConfigureAwait(false);
            if (!inspection.CanActivate)
            {
                MakeWritable(staging);
                DeleteKnownTree(staging);
                await FailOperationAsync(operation.Id, GameLibraryErrorCodes.ValidationFailed).ConfigureAwait(false);
                throw new GameLibraryException(GameLibraryErrorCodes.ActivationValidationFailed, "The uploaded game content failed validation.");
            }

            await progress.ReportAsync(GameContentOperationStage.PublishingContent, null, cancellationToken).ConfigureAwait(false);
            _ = PublishContentTree(gameDirectory, operation.Id, staging);
            DateTimeOffset now = timeProvider.GetUtcNow();
            operation.Status = GameContentOperationStatus.ContentReady;
            operation.ContentDigest = inspection.ContentDigest;
            operation.WorkPath = RelativeContent(gameId);
            operation.UpdatedAt = now;
            operation.StateVersion++;
            // The content rename completes before metadata is committed. If
            // the process exits after this save, maintenance can finish this
            // exact operation without copying or scanning a second tree. The
            // product does not promise power-loss durability for Game files.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            contentReady = true;

            row.CurrentContentPath = RelativeContent(gameId);
            row.ContentDigest = inspection.ContentDigest;
            row.ContentRevision++;
            row.CompatibilitySummaryJson = JsonSerializer.Serialize(new { inspection.CanActivate, inspection.Diagnostics });
            row.ActivatedBy = actor.UserId;
            row.ActivatedAt = now;
            row.Status = GameStatus.Active;
            row.WorkspacePath = null;
            row.WorkspaceStatus = GameWorkspaceStatus.None;
            GameFileRow[] oldFiles = await db.GameFiles.Where(file => file.GameId == gameId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            db.GameFiles.RemoveRange(oldFiles);
            await ReplaceDiagnosticsAsync(row, inspection.Diagnostics, cancellationToken).ConfigureAwait(false);
            Touch(row, now);
            AddAudit(actor, "GAME_UPLOAD_ACTIVATE", row.Id, now, JsonSerializer.Serialize(new { row.ContentRevision }));
            CompleteOperation(operation, inspection.ContentDigest, now);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await ingestions.CompleteConsumeAsync(ingestionId, actor.UserId, cancellationToken).ConfigureAwait(false);
            return ToItem(row);
        }
        catch
        {
            if (!contentReady)
            {
                if (Directory.Exists(staging))
                {
                    try { MakeWritable(staging); } catch (IOException) { }
                    try { DeleteKnownTree(staging); } catch (IOException) { }
                }
                await FailOperationAsync(operation.Id, GameLibraryErrorCodes.Conflict).ConfigureAwait(false);
                await ingestions.AbandonAsync(ingestionId, actor.UserId, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task<GameLibraryItem?> GetAsync(CurrentActor actor, string gameId, CancellationToken cancellationToken = default)
    {
        GameRow? row = await FindVisibleAsync(actor, gameId, tracking: false, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToItem(row);
    }

    public async Task<GameLibraryItem> UpdateAsync(CurrentActor actor, string gameId, string? name, string? visibility, int expectedStateVersion, CancellationToken cancellationToken = default)
    {
        await using FileStream mutationLock = AcquireMutationLock(gameId);
        GameRow row = await FindOwnedAsync(actor, gameId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(row, expectedStateVersion);
        if (name is not null)
        {
            string normalizedName = NormalizeName(name);
            bool nameTaken = await db.Games.AnyAsync(game => game.OwnerUserId == actor.UserId && game.Name == normalizedName
                && game.Id != gameId && game.Status != GameStatus.Deleted, cancellationToken).ConfigureAwait(false);
            if (nameTaken) throw new GameLibraryException(GameLibraryErrorCodes.NameConflict, "同名游戏已存在。");
            row.Name = normalizedName;
        }
        if (visibility is not null) row.Visibility = ParseVisibility(visibility);
        Touch(row);
        AddAudit(actor, "GAME_UPDATE", row.Id, row.UpdatedAt);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ToItem(row);
    }

    public async Task DeleteAsync(CurrentActor actor, string gameId, int expectedStateVersion, CancellationToken cancellationToken = default)
    {
        await using FileStream mutationLock = AcquireMutationLock(gameId);
        GameRow row = await FindOwnedAsync(actor, gameId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(row, expectedStateVersion);
        bool isReferenced = await db.Sessions.AnyAsync(session => session.GameId == gameId, cancellationToken).ConfigureAwait(false);
        if (isReferenced) throw new GameLibraryException(GameLibraryErrorCodes.InUse, "A game referenced by a session cannot be deleted.");
        DateTimeOffset now = timeProvider.GetUtcNow();
        await db.GameContentOperations
            .Where(operation => operation.GameId == gameId
                && (operation.Status == GameContentOperationStatus.Pending
                    || operation.Status == GameContentOperationStatus.Running
                    || operation.Status == GameContentOperationStatus.ContentReady))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(operation => operation.Status, GameContentOperationStatus.Failed)
                .SetProperty(operation => operation.ErrorCode, "GAME_DELETED")
                .SetProperty(operation => operation.UpdatedAt, now)
                .SetProperty(operation => operation.CompletedAt, now)
                .SetProperty(operation => operation.StateVersion, operation => operation.StateVersion + 1), cancellationToken)
            .ConfigureAwait(false);
        row.Status = GameStatus.Deleted;
        row.DeletedBy = actor.UserId;
        row.DeletedAt = now;
        Touch(row, now);
        AddAudit(actor, "GAME_DELETE", row.Id, row.UpdatedAt);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<GameLibraryItem> SetBlockedAsync(CurrentActor actor, string gameId, bool blocked, int expectedStateVersion, CancellationToken cancellationToken = default)
    {
        if (!actor.IsAdmin) throw NotFound();
        await using FileStream mutationLock = AcquireMutationLock(gameId);
        GameRow row = await db.Games.SingleOrDefaultAsync(game => game.Id == gameId && game.Status != GameStatus.Deleted, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
        EnsureVersion(row, expectedStateVersion);
        row.Status = blocked ? GameStatus.Blocked : GameStatus.Active;
        Touch(row);
        AddAudit(actor, blocked ? "GAME_BLOCK" : "GAME_UNBLOCK", row.Id, row.UpdatedAt);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ToItem(row);
    }

    public async Task<GameLibraryItem> BindPackageAsync(CurrentActor actor, string gameId, string ingestionId, string? contentDigest, int expectedStateVersion, CancellationToken cancellationToken = default)
    {
        await using FileStream mutationLock = AcquireMutationLock(gameId);
        GameRow row = await FindOwnedAsync(actor, gameId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(row, expectedStateVersion);
        await using GamePackageConsumption consumption = await ingestions.BeginConsumeAsync(ingestionId, actor.UserId, contentDigest, cancellationToken).ConfigureAwait(false);
        string staging = Path.Combine(GameDirectory(gameId), $".workspace-{Guid.NewGuid():N}");
        GameContentOperationRow operation = await StartOperationAsync(row, GameContentOperationType.Import, ingestionId, contentDigest,
            Path.GetRelativePath(databaseOptions.DataRoot, staging).Replace('\\', '/'), cancellationToken).ConfigureAwait(false);
        bool workspaceCommitted = false;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                using SafeFileHandle gameDirectoryHandle = LinuxFileOperations.OpenDirectory(GameDirectory(gameId));
                LinuxFileOperations.CopyTree(consumption.ContentDirectoryHandle, gameDirectoryHandle, Path.GetFileName(staging), syncToDisk: false);
            }
            else
            {
                throw new PlatformNotSupportedException("Secure game content binding requires Linux openat semantics.");
            }
            await RenewOperationAsync(operation.Id, cancellationToken).ConfigureAwait(false);
            string? retiredWorkspace = ReplaceWorkspace(gameId, staging, operation.Id);
            GameFileRow[] staleFiles = await db.GameFiles.Where(file => file.GameId == gameId && file.Scope == "WORKSPACE").ToArrayAsync(cancellationToken).ConfigureAwait(false);
            db.GameFiles.RemoveRange(staleFiles);
            row.WorkspaceStatus = GameWorkspaceStatus.Draft;
            row.WorkspacePath = RelativeWorkspace(gameId);
            row.CompatibilitySummaryJson = "{}";
            await ClearDiagnosticsAsync(gameId, cancellationToken).ConfigureAwait(false);
            Touch(row);
            AddAudit(actor, "GAME_PACKAGE_UPLOAD", row.Id, row.UpdatedAt, JsonSerializer.Serialize(new { ingestionId, contentDigest }));
            CompleteOperation(operation, contentDigest, row.UpdatedAt);
            try { await SaveAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                RestoreReplacedDirectory(Path.Combine(GameDirectory(gameId), "workspace"), retiredWorkspace);
                throw;
            }
            workspaceCommitted = true;
            await ingestions.CompleteConsumeAsync(ingestionId, actor.UserId, cancellationToken).ConfigureAwait(false);
            return ToItem(row);
        }
        catch
        {
            DeleteKnownTree(staging);
            if (!workspaceCommitted)
            {
                await FailOperationAsync(operation.Id, GameLibraryErrorCodes.Conflict).ConfigureAwait(false);
                await ingestions.AbandonAsync(ingestionId, actor.UserId, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<GameFileItem>> ListFilesAsync(CurrentActor actor, string gameId, string? scope, string? directory, CancellationToken cancellationToken = default)
    {
        GameRow row = await FindVisibleAsync(actor, gameId, tracking: false, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
        string root = ResolveReadableRoot(actor, row, scope);
        string logical = NormalizeRelativePath(directory ?? string.Empty, allowEmpty: true);
        string path = ResolvePath(root, logical, allowMissingLeaf: false);
        if (!Directory.Exists(path)) throw new GameLibraryException(GameLibraryErrorCodes.FileNotFound, "The requested game content directory does not exist.");
        return new DirectoryInfo(path).EnumerateFileSystemInfos()
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry =>
            {
                RejectLink(entry);
                string relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                return new GameFileItem(relative, entry is DirectoryInfo, entry is FileInfo file ? file.Length : 0,
                    null);
            }).ToArray();
    }

    public async Task<GameTextFile> ReadTextFileAsync(CurrentActor actor, string gameId, string? scope, string path, CancellationToken cancellationToken = default)
    {
        GameRow row = await FindVisibleAsync(actor, gameId, tracking: false, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
        string filePath = ResolvePath(ResolveReadableRoot(actor, row, scope), NormalizeRelativePath(path), allowMissingLeaf: false);
        var info = new FileInfo(filePath);
        RejectLink(info);
        if (!info.Exists) throw new GameLibraryException(GameLibraryErrorCodes.FileNotFound, "The requested game file does not exist.");
        if (info.Length > MaxTextPreviewBytes) throw new GameLibraryException(GameLibraryErrorCodes.FileTooLargeToRead, "The file is too large to view as text.");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (!TryReadGameText(bytes, out string text, out string? encoding, out bool hasBom))
            throw new GameLibraryException(GameLibraryErrorCodes.TextEncodingUnsupported, "The text encoding is unsupported.");
        return new GameTextFile(path, text, encoding!, hasBom, bytes.LongLength, null, row.StateVersion);
    }

    public async Task<GameFileDownload> OpenDownloadAsync(CurrentActor actor, string gameId, string? scope, string path, CancellationToken cancellationToken = default)
    {
        GameRow row = await FindVisibleAsync(actor, gameId, tracking: false, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
        string filePath = ResolvePath(ResolveReadableRoot(actor, row, scope), NormalizeRelativePath(path), allowMissingLeaf: false);
        var info = new FileInfo(filePath);
        RejectLink(info);
        if (!info.Exists) throw new GameLibraryException(GameLibraryErrorCodes.FileNotFound, "The requested game file does not exist.");
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (LinuxFileOperations.ReadIdentity(stream.SafeFileHandle).LinkCount != 1)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw UnsafePath();
        }
        return new GameFileDownload(info.Name, info.Length, null, stream);
    }

    public async Task<GameContentOperationItem?> GetOperationAsync(CurrentActor actor, string gameId, string operationId, CancellationToken cancellationToken = default)
    {
        bool ownsGame = await db.Games.AsNoTracking().AnyAsync(game => game.Id == gameId && game.OwnerUserId == actor.UserId && game.Status != GameStatus.Deleted, cancellationToken).ConfigureAwait(false);
        if (!ownsGame) return null;
        GameContentOperationRow? operation = await db.GameContentOperations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == operationId && value.GameId == gameId, cancellationToken).ConfigureAwait(false);
        return operation is null ? null : new GameContentOperationItem(
            operation.Id,
            OperationTypeText(operation.OperationType),
            OperationStatusText(operation.Status),
            OperationStageText(operation.Stage),
            operation.CurrentItem,
            operation.ContentDigest,
            operation.ErrorCode,
            operation.CreatedAt,
            operation.UpdatedAt,
            operation.CompletedAt);
    }

    public async Task<GameUploadProgressItem?> GetUploadProgressAsync(CurrentActor actor, string requestId, CancellationToken cancellationToken = default)
    {
        GameContentOperationRow? operation = await db.GameContentOperations.AsNoTracking()
            .Where(value => value.RequestId == requestId
                && value.OperationType == GameContentOperationType.Import
                && value.Game != null
                && value.Game.OwnerUserId == actor.UserId)
            .OrderByDescending(value => value.CreatedAt)
            .ThenByDescending(value => value.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return operation is null ? null : new GameUploadProgressItem(
            operation.GameId,
            operation.Id,
            OperationStatusText(operation.Status),
            OperationStageText(operation.Stage),
            operation.CurrentItem,
            operation.ErrorCode,
            operation.CreatedAt,
            operation.UpdatedAt,
            operation.CompletedAt);
    }

    public async Task<GameDiagnosticItem> OverrideDiagnosticAsync(CurrentActor actor, string gameId, string diagnosticId, int expectedStateVersion, CancellationToken cancellationToken = default)
    {
        if (!actor.IsAdmin) throw NotFound();
        await using FileStream mutationLock = AcquireMutationLock(gameId);
        GameRow row = await db.Games.SingleOrDefaultAsync(game => game.Id == gameId && game.Status != GameStatus.Deleted, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
        EnsureVersion(row, expectedStateVersion);
        CompatibilityDiagnosticRow diagnostic = await db.CompatibilityDiagnostics.SingleOrDefaultAsync(value => value.Id == diagnosticId && value.GameId == gameId, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
        if (!IsDiagnosticOverrideAllowed(diagnostic.Code) || !string.Equals(diagnostic.OverridePolicy, "ADMIN", StringComparison.Ordinal))
            throw new GameLibraryException(GameLibraryErrorCodes.DiagnosticOverrideNotAllowed, "This diagnostic cannot be overridden.");
        if (diagnostic.WorkspaceRevision != row.StateVersion)
            throw new GameLibraryException(GameLibraryErrorCodes.StateVersionConflict, "The diagnostic is no longer current.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        diagnostic.ActivationBlocking = false;
        diagnostic.OverriddenBy = actor.UserId;
        diagnostic.OverriddenAt = now;
        Touch(row, now);
        AddAudit(actor, "GAME_DIAGNOSTIC_OVERRIDE", row.Id, now, JsonSerializer.Serialize(new { diagnosticId, diagnostic.Code }));
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ToDiagnosticItem(diagnostic);
    }

    public async Task<IReadOnlyList<GameDiagnosticItem>> ListDiagnosticsAsync(CurrentActor actor, string gameId, CancellationToken cancellationToken = default)
    {
        GameRow row = await FindOwnedAsync(actor, gameId, cancellationToken).ConfigureAwait(false);
        CompatibilityDiagnosticRow[] diagnostics = await db.CompatibilityDiagnostics.AsNoTracking()
            .Where(diagnostic => diagnostic.GameId == row.Id)
            .OrderBy(diagnostic => diagnostic.CreatedAt)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return diagnostics.Select(ToDiagnosticItem).ToArray();
    }

    public async Task<GameValidationResult> ValidateAsync(CurrentActor actor, string gameId, int expectedStateVersion, CancellationToken cancellationToken = default)
    {
        await using FileStream mutationLock = AcquireMutationLock(gameId);
        GameRow row = await FindOwnedAsync(actor, gameId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(row, expectedStateVersion);
        GameContentOperationRow operation = await StartOperationAsync(row, GameContentOperationType.Validate, null, null, row.WorkspacePath, cancellationToken).ConfigureAwait(false);
        try
        {
            row.WorkspaceStatus = GameWorkspaceStatus.Validating;
            Touch(row);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            string workspace = RequireWorkspace(row);
            await RenewOperationAsync(operation.Id, cancellationToken).ConfigureAwait(false);
            // Validation is parser-only and the mutation lock prevents API
            // edits during this call.  Inspect the existing workspace directly;
            // creating a validation snapshot would be another full tree copy.
            WorkspaceInspection inspection = await InspectWithParserAsync(workspace, cancellationToken).ConfigureAwait(false);
            await RenewOperationAsync(operation.Id, cancellationToken).ConfigureAwait(false);
            MarkWorkspaceChanged(row);
            row.CompatibilitySummaryJson = JsonSerializer.Serialize(new { inspection.CanActivate, inspection.Diagnostics });
            await ReplaceDiagnosticsAsync(row, inspection.Diagnostics, cancellationToken).ConfigureAwait(false);
            AddAudit(actor, "GAME_VALIDATE", row.Id, row.UpdatedAt, JsonSerializer.Serialize(new { inspection.CanActivate, inspection.ContentDigest }));
            CompleteOperation(operation, inspection.ContentDigest, row.UpdatedAt);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return new GameValidationResult(inspection.CanActivate, inspection.ContentDigest, inspection.FileCount, inspection.TotalBytes, inspection.Diagnostics, row.StateVersion);
        }
        catch
        {
            await FailOperationAsync(operation.Id, GameLibraryErrorCodes.ValidationFailed).ConfigureAwait(false);
            // A failed validation must not leave the workspace stuck in VALIDATING
            // until the background reaper runs; restore DRAFT so the user can edit
            // or retry immediately. Best effort - the reaper reconciles anyway.
            try
            {
                row.WorkspaceStatus = GameWorkspaceStatus.Draft;
                await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is DbUpdateException or DbUpdateConcurrencyException)
            {
            }
            throw;
        }
    }

    public async Task<GameLibraryItem> ActivateAsync(CurrentActor actor, string gameId, int expectedStateVersion, CancellationToken cancellationToken = default)
    {
        await using FileStream mutationLock = AcquireMutationLock(gameId);
        GameRow row = await FindOwnedAsync(actor, gameId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(row, expectedStateVersion);
        string workspace = RequireWorkspace(row);
        GameContentOperationRow operation = await StartOperationAsync(row, GameContentOperationType.Activate, null, null, row.WorkspacePath, cancellationToken).ConfigureAwait(false);
        string gameDirectory = GameDirectory(gameId);
        string staging = Path.Combine(gameDirectory, $".content-{operation.Id}");
        row.WorkspaceStatus = GameWorkspaceStatus.Validating;
        Touch(row);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        CopyTree(workspace, staging);
        await RenewOperationAsync(operation.Id, cancellationToken).ConfigureAwait(false);
        MakeReadOnly(staging);
        WorkspaceInspection inspection = await InspectWithParserAsync(staging, cancellationToken).ConfigureAwait(false);
        await RenewOperationAsync(operation.Id, cancellationToken).ConfigureAwait(false);
        inspection = await ApplyDiagnosticOverridesAsync(operation, inspection, cancellationToken).ConfigureAwait(false);
        if (!inspection.CanActivate)
        {
            MakeWritable(staging);
            DeleteKnownTree(staging);
            MarkWorkspaceChanged(row);
            row.CompatibilitySummaryJson = JsonSerializer.Serialize(new { inspection.CanActivate, inspection.Diagnostics });
            await ReplaceDiagnosticsAsync(row, inspection.Diagnostics, cancellationToken).ConfigureAwait(false);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await FailOperationAsync(operation.Id, GameLibraryErrorCodes.ValidationFailed).ConfigureAwait(false);
            throw new GameLibraryException(GameLibraryErrorCodes.ActivationValidationFailed, "The workspace has activation-blocking diagnostics.");
        }
        _ = PublishActivationTree(gameDirectory, operation.Id, staging, workspace);
        DateTimeOffset now = timeProvider.GetUtcNow();
        operation.Status = GameContentOperationStatus.ContentReady;
        operation.ContentDigest = inspection.ContentDigest;
        operation.WorkPath = RelativeContent(gameId);
        operation.UpdatedAt = now;
        operation.StateVersion++;
        // CONTENT_READY is the operation boundary between the filesystem rename
        // and the metadata transaction. A restart can now finish or roll this
        // exact operation without guessing which tree was published. The
        // product does not promise power-loss durability for Game files.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        row.CurrentContentPath = RelativeContent(gameId);
        row.ContentDigest = inspection.ContentDigest;
        row.ContentRevision++;
        row.CompatibilitySummaryJson = JsonSerializer.Serialize(new { inspection.CanActivate, inspection.Diagnostics });
        row.ActivatedBy = actor.UserId;
        row.ActivatedAt = now;
        row.Status = GameStatus.Active;
        row.WorkspacePath = null;
        row.WorkspaceStatus = GameWorkspaceStatus.None;
        db.GameFiles.RemoveRange(await db.GameFiles.Where(file => file.GameId == gameId).ToArrayAsync(cancellationToken).ConfigureAwait(false));
        await ReplaceDiagnosticsAsync(row, inspection.Diagnostics, cancellationToken).ConfigureAwait(false);
        Touch(row, now);
        AddAudit(actor, "GAME_ACTIVATE", row.Id, now, JsonSerializer.Serialize(new { row.ContentDigest, row.ContentRevision }));
        CompleteOperation(operation, inspection.ContentDigest, now);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ToItem(row);
    }

    private Task<WorkspaceInspection> InspectWithParserAsync(string snapshot, CancellationToken token) =>
        InspectWithParserAsync(snapshot, progress: null, token);

    private async Task<WorkspaceInspection> InspectWithParserAsync(string snapshot, OperationProgressReporter? progress, CancellationToken token)
    {
        WorkspaceInspection inspection;
        try
        {
            inspection = await InspectWorkspaceAsync(snapshot, progress, token).ConfigureAwait(false);
        }
        catch (GameContentLimitException exception)
        {
            inspection = new WorkspaceInspection(
                false,
                null,
                0,
                0,
                [new GameValidationDiagnostic(exception.Code, "ERROR", null, "The game content exceeds a configured safety limit.", true)]);
        }
        if (progress is not null)
            await progress.ReportAsync(GameContentOperationStage.RunningValidator, null, token).ConfigureAwait(false);
        GameParserValidationResult parsed = await validator.ValidateAsync(snapshot, token).ConfigureAwait(false);
        IReadOnlyList<GameValidationDiagnostic> diagnostics = inspection.Diagnostics.Concat(parsed.Diagnostics).ToArray();
        // The structural scan only enforces storage limits. The parser remains
        // the authority for game compatibility diagnostics.
        return inspection with { CanActivate = inspection.CanActivate && parsed.CanActivate, Diagnostics = diagnostics };
    }

    private async Task<GameRow> FindOwnedAsync(CurrentActor actor, string gameId, CancellationToken token) =>
        await db.Games.SingleOrDefaultAsync(game => game.Id == gameId && game.OwnerUserId == actor.UserId && game.Status != GameStatus.Deleted, token).ConfigureAwait(false) ?? throw NotFound();

    private Task<GameRow?> FindVisibleAsync(CurrentActor actor, string gameId, bool tracking, CancellationToken token)
    {
        IQueryable<GameRow> query = tracking ? db.Games : db.Games.AsNoTracking();
        return query.SingleOrDefaultAsync(game => game.Id == gameId && game.Status != GameStatus.Deleted &&
            (game.OwnerUserId == actor.UserId || game.Visibility == GameVisibility.ServerShared), token);
    }

    private string ResolveReadableRoot(CurrentActor actor, GameRow row, string? requestedScope)
    {
        string scope = requestedScope?.ToUpperInvariant() ?? (row.OwnerUserId == actor.UserId && row.WorkspacePath is not null ? "WORKSPACE" : "CURRENT");
        return scope switch
        {
            "WORKSPACE" when row.OwnerUserId == actor.UserId && row.WorkspacePath is not null => AbsoluteDataPath(row.WorkspacePath),
            "CURRENT" when row.CurrentContentPath is not null => AbsoluteDataPath(row.CurrentContentPath),
            "WORKSPACE" when row.OwnerUserId != actor.UserId => throw NotFound(),
            "WORKSPACE" or "CURRENT" => throw new GameLibraryException(GameLibraryErrorCodes.NotFound, "The requested game content does not exist."),
            _ => throw new GameLibraryException(GameLibraryErrorCodes.InvalidInput, "The content scope is invalid."),
        };
    }

    private string RequireWorkspace(GameRow row) => row.WorkspacePath is null
        ? throw new GameLibraryException(GameLibraryErrorCodes.Conflict, "The game has no staged workspace.")
        : AbsoluteDataPath(row.WorkspacePath);

    private void MarkWorkspaceChanged(GameRow row)
    {
        row.WorkspaceStatus = GameWorkspaceStatus.Draft;
        row.CompatibilitySummaryJson = "{}";
        Touch(row);
    }

    private void Touch(GameRow row, DateTimeOffset? now = null)
    {
        row.UpdatedAt = now ?? timeProvider.GetUtcNow();
        row.StateVersion++;
    }

    private static void EnsureVersion(GameRow row, int expected)
    {
        if (row.StateVersion != expected) throw new GameLibraryException(GameLibraryErrorCodes.StateVersionConflict, "The game was changed by another request.");
    }

    private async Task SaveAsync(CancellationToken token)
    {
        try { await db.SaveChangesAsync(token).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException exception) { throw new GameLibraryException(GameLibraryErrorCodes.StateVersionConflict, exception.Message); }
        catch (DbUpdateException exception) { throw Conflict("The game update conflicts with existing data.", exception); }
    }

    private async Task<GameContentOperationRow> StartOperationAsync(
        GameRow game,
        GameContentOperationType type,
        string? ingestionId,
        string? contentDigest,
        string? workPath,
        CancellationToken token,
        string? requestId = null,
        GameContentOperationStage stage = GameContentOperationStage.Preparing)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var operation = new GameContentOperationRow
        {
            Id = $"gop_{Guid.CreateVersion7():N}",
            GameId = game.Id,
            OperationType = type,
            Status = GameContentOperationStatus.Running,
            Stage = stage,
            CurrentItem = null,
            ExpectedGameStateVersion = game.StateVersion,
            ExpectedContentRevision = game.ContentRevision,
            IngestionId = ingestionId,
            RequestId = requestId,
            WorkPath = workPath,
            ContentDigest = contentDigest,
            LeaseExpiresAt = now.AddMinutes(15),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.GameContentOperations.Add(operation);
        try { await db.SaveChangesAsync(token).ConfigureAwait(false); }
        catch (DbUpdateException exception)
        {
            string code = type == GameContentOperationType.Validate ? GameLibraryErrorCodes.ValidationInProgress
                : type == GameContentOperationType.Activate ? GameLibraryErrorCodes.ActivationInProgress
                : GameLibraryErrorCodes.Conflict;
            throw new GameLibraryException(code, $"Another game content operation is active. {exception.Message}");
        }
        return operation;
    }

    private static void CompleteOperation(GameContentOperationRow operation, string? digest, DateTimeOffset now)
    {
        operation.Status = GameContentOperationStatus.Committed;
        operation.Stage = GameContentOperationStage.Completed;
        operation.CurrentItem = null;
        operation.ContentDigest = digest ?? operation.ContentDigest;
        operation.UpdatedAt = now;
        operation.CompletedAt = now;
        operation.StateVersion++;
    }

    private async Task RenewOperationAsync(string operationId, CancellationToken token)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await db.GameContentOperations.Where(operation => operation.Id == operationId
                && operation.Status == GameContentOperationStatus.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(operation => operation.LeaseExpiresAt, now.AddMinutes(15))
                .SetProperty(operation => operation.UpdatedAt, now), token)
            .ConfigureAwait(false);
    }

    private async Task UpdateOperationProgressAsync(
        string operationId,
        GameContentOperationStage stage,
        string? currentItem,
        CancellationToken token)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string? normalizedItem = NormalizeProgressItem(currentItem);
        await db.GameContentOperations
            .Where(operation => operation.Id == operationId
                && (operation.Status == GameContentOperationStatus.Pending || operation.Status == GameContentOperationStatus.Running))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(operation => operation.Stage, stage)
                .SetProperty(operation => operation.CurrentItem, normalizedItem)
                .SetProperty(operation => operation.LeaseExpiresAt, now.AddMinutes(15))
                .SetProperty(operation => operation.UpdatedAt, now), token)
            .ConfigureAwait(false);
    }

    private async Task FailOperationAsync(string operationId, string errorCode)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        try
        {
            await db.GameContentOperations.Where(operation => operation.Id == operationId && operation.Status != GameContentOperationStatus.Committed)
                .ExecuteUpdateAsync(setters => setters
                .SetProperty(operation => operation.Status, GameContentOperationStatus.Failed)
                    .SetProperty(operation => operation.ErrorCode, errorCode)
                    .SetProperty(operation => operation.UpdatedAt, now)
                    .SetProperty(operation => operation.CompletedAt, now)
                    .SetProperty(operation => operation.StateVersion, operation => operation.StateVersion + 1), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
        {
            // The operation remains RUNNING for the startup reconciler to inspect.
        }
    }

    private sealed class OperationProgressReporter(
        GameLibraryService owner,
        string operationId,
        TimeSpan persistInterval)
    {
        private DateTimeOffset lastPersistedAt = DateTimeOffset.MinValue;
        private GameContentOperationStage? lastStage;

        public Task ReportPackageAsync(GamePackageProgressUpdate update, CancellationToken token) =>
            ReportAsync(Map(update.Stage), update.CurrentItem, token);

        public async Task ReportAsync(GameContentOperationStage stage, string? currentItem, CancellationToken token)
        {
            DateTimeOffset now = owner.CurrentTime;
            if (lastStage == stage && now - lastPersistedAt < persistInterval)
                return;
            await owner.UpdateOperationProgressAsync(operationId, stage, currentItem, token).ConfigureAwait(false);
            lastStage = stage;
            lastPersistedAt = now;
        }

        private static GameContentOperationStage Map(GamePackageProgressStage stage) => stage switch
        {
            GamePackageProgressStage.Receiving => GameContentOperationStage.Receiving,
            GamePackageProgressStage.InspectingArchive => GameContentOperationStage.InspectingArchive,
            GamePackageProgressStage.Extracting => GameContentOperationStage.Extracting,
            GamePackageProgressStage.NormalizingEncoding => GameContentOperationStage.NormalizingEncoding,
            GamePackageProgressStage.Analyzing => GameContentOperationStage.Analyzing,
            GamePackageProgressStage.Ready => GameContentOperationStage.ConsumingStaging,
            _ => GameContentOperationStage.Preparing,
        };
    }

    private async Task ReplaceDiagnosticsAsync(GameRow game, IReadOnlyList<GameValidationDiagnostic> diagnostics, CancellationToken token)
    {
        CompatibilityDiagnosticRow[] existing = await db.CompatibilityDiagnostics.Where(diagnostic => diagnostic.GameId == game.Id).ToArrayAsync(token).ConfigureAwait(false);
        db.CompatibilityDiagnostics.RemoveRange(existing);
        DateTimeOffset now = timeProvider.GetUtcNow();
        db.CompatibilityDiagnostics.AddRange(diagnostics.Select(diagnostic => new CompatibilityDiagnosticRow
        {
            Id = $"diag_{Guid.CreateVersion7():N}",
            GameId = game.Id,
            WorkspaceRevision = game.StateVersion,
            Stage = DiagnosticStage(diagnostic.Code),
            Severity = diagnostic.Severity,
            Code = diagnostic.Code,
            LogicalPath = diagnostic.Path,
            MessageKey = $"game.validation.{diagnostic.Code.ToLowerInvariant()}",
            ArgumentsJson = "{}",
            ActivationBlocking = diagnostic.ActivationBlocking,
            OverridePolicy = IsDiagnosticOverrideAllowed(diagnostic.Code) && diagnostic.ActivationBlocking ? "ADMIN" : "NEVER",
            CreatedAt = now,
        }));
    }

    private async Task ClearDiagnosticsAsync(string gameId, CancellationToken token)
    {
        CompatibilityDiagnosticRow[] existing = await db.CompatibilityDiagnostics.Where(diagnostic => diagnostic.GameId == gameId).ToArrayAsync(token).ConfigureAwait(false);
        db.CompatibilityDiagnostics.RemoveRange(existing);
    }

    private async Task<WorkspaceInspection> ApplyDiagnosticOverridesAsync(GameContentOperationRow operation, WorkspaceInspection inspection, CancellationToken token)
    {
        CompatibilityDiagnosticRow[] overrides = await db.CompatibilityDiagnostics.AsNoTracking()
            .Where(diagnostic => diagnostic.GameId == operation.GameId
                && diagnostic.WorkspaceRevision == operation.ExpectedGameStateVersion
                && diagnostic.OverridePolicy == "ADMIN" && diagnostic.OverriddenBy != null)
            .ToArrayAsync(token).ConfigureAwait(false);
        if (overrides.Length == 0) return inspection;
        var keys = overrides
            .Where(diagnostic => IsDiagnosticOverrideAllowed(diagnostic.Code))
            .Select(diagnostic => (diagnostic.Code, diagnostic.LogicalPath))
            .ToHashSet();
        GameValidationDiagnostic[] diagnostics = inspection.Diagnostics.Select(diagnostic =>
            keys.Contains((diagnostic.Code, diagnostic.Path)) ? diagnostic with { ActivationBlocking = false } : diagnostic).ToArray();
        return inspection with { CanActivate = !diagnostics.Any(diagnostic => diagnostic.ActivationBlocking), Diagnostics = diagnostics };
    }

    private static bool IsDiagnosticOverrideAllowed(string code) => code is
        "MISSING_RESOURCE" or "OPTIONAL_RESOURCE_MISSING" or "RESOURCE_CASE_MISMATCH";

    private static GameDiagnosticItem ToDiagnosticItem(CompatibilityDiagnosticRow diagnostic) => new(
        diagnostic.Id, diagnostic.Code, diagnostic.Severity, diagnostic.LogicalPath,
        GameDiagnosticMessages.Resolve(diagnostic.Code, diagnostic.LogicalPath, diagnostic.MessageKey),
        diagnostic.MessageKey,
        diagnostic.ActivationBlocking, diagnostic.OverridePolicy, diagnostic.OverriddenBy, diagnostic.OverriddenAt);

    private static string DiagnosticStage(string code) => code.StartsWith("TEXT_", StringComparison.Ordinal) ? "ENCODING"
        : code.StartsWith("CALLSHARP", StringComparison.Ordinal) ? "CAPABILITY"
        : code.StartsWith("RUNTIME_", StringComparison.Ordinal) ? "RUNTIME" : "STRUCTURE";

    private void AddAudit(CurrentActor actor, string action, string gameId, DateTimeOffset now, string metadata = "{}") => db.AuditEvents.Add(new AuditEventRow
    {
        Id = $"audit_{Guid.CreateVersion7():N}", OccurredAt = now, ActorUserId = actor.UserId,
        ActorType = actor.IsAdmin ? AuditActorType.Admin : AuditActorType.User, Action = action,
        ResourceType = "GAME", ResourceId = gameId, Result = AuditResult.Succeeded, MetadataJson = metadata,
    });

    private static async Task<WorkspaceInspection> InspectWorkspaceAsync(string root, OperationProgressReporter? progress, CancellationToken token)
    {
        if (!Directory.Exists(root)) throw new IOException("The game content directory is missing.");
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((new DirectoryInfo(root), 0));
        var diagnostics = new List<GameValidationDiagnostic>();
        int entryCount = 0;
        int fileCount = 0;
        long total = 0;
        while (pending.Count > 0)
        {
            (DirectoryInfo directory, int depth) = pending.Pop();
            RejectLink(directory);
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos().OrderByDescending(item => item.Name, StringComparer.Ordinal))
            {
                if (++entryCount > GameContentScanLimits.MaxEntryCount)
                    throw new GameContentLimitException("GAME_CONTENT_ENTRY_LIMIT");
                RejectLink(entry);
                string logical = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                if (logical.StartsWith(".cloudemuera-", StringComparison.Ordinal)) continue;
                if (entry is DirectoryInfo child)
                {
                    if (depth >= GameContentScanLimits.MaxDirectoryDepth)
                        throw new GameContentLimitException("GAME_CONTENT_DEPTH_LIMIT");
                    pending.Push((child, depth + 1));
                    continue;
                }
                if (entry is not FileInfo file) throw UnsafePath();
                if (file.Length > GameContentScanLimits.MaxSingleFileBytes)
                    throw new GameContentLimitException("GAME_CONTENT_FILE_LIMIT");
                if (progress is not null)
                    await progress.ReportAsync(GameContentOperationStage.ValidatingContent, logical, token).ConfigureAwait(false);
                if (file.Length > GameContentScanLimits.MaxTotalBytes - total)
                    throw new GameContentLimitException("GAME_CONTENT_TOTAL_LIMIT");
                total += file.Length;
                using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (LinuxFileOperations.ReadIdentity(handle).LinkCount != 1) throw UnsafePath();
                fileCount++;
            }
        }
        return new(!diagnostics.Any(item => item.ActivationBlocking), null, fileCount, total, diagnostics);
    }

    private static bool IsGameTextFile(string path) => Path.GetExtension(path).ToUpperInvariant() is
        ".ERB" or ".ERH" or ".CSV" or ".CONFIG" or ".TXT";

    private static bool TryReadGameText(string path, out string text, out string? encodingName, out bool hasBom)
    {
        return TryReadGameText(File.ReadAllBytes(path), out text, out encodingName, out hasBom);
    }

    private static bool TryReadGameText(ReadOnlySpan<byte> bytes, out string text, out string? encodingName, out bool hasBom)
    {
        hasBom = bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        try
        {
            text = new UTF8Encoding(false, true).GetString(hasBom ? bytes[3..] : bytes);
            encodingName = hasBom ? "UTF8_BOM" : "UTF8";
            return true;
        }
        catch (DecoderFallbackException)
        {
            try
            {
                text = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
                encodingName = "SHIFT_JIS";
                hasBom = false;
                return true;
            }
            catch (DecoderFallbackException)
            {
                text = string.Empty;
                encodingName = "UNKNOWN";
                hasBom = false;
                return false;
            }
        }
    }

    private static void CopyTree(string source, string destination)
    {
        if (OperatingSystem.IsLinux())
        {
            using SafeFileHandle sourceHandle = LinuxFileOperations.OpenDirectory(source);
            string? parentPath = Path.GetDirectoryName(destination);
            if (parentPath is null) throw UnsafePath();
            using SafeFileHandle destinationParent = LinuxFileOperations.OpenDirectory(parentPath);
            LinuxFileOperations.CopyTree(sourceHandle, destinationParent, Path.GetFileName(destination), syncToDisk: false);
            return;
        }
        CopyTreeManaged(source, destination);
    }

    /// <summary>
    /// Managed copy fallback used outside Linux.  It is also exercised directly by the
    /// platform-difference tests to keep the non-dirfd path free of symlink escapes.
    /// </summary>
    internal static void CopyTreeManaged(string source, string destination)
    {
        var root = new DirectoryInfo(source);
        RejectLink(root);
        Directory.CreateDirectory(destination);
        foreach (FileSystemInfo entry in root.EnumerateFileSystemInfos())
        {
            RejectLink(entry);
            string target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo directory) CopyTreeManaged(directory.FullName, target);
            else if (entry is FileInfo file)
            {
                if (file.LinkTarget is not null) throw UnsafePath();
                file.CopyTo(target, overwrite: false);
            }
            else throw UnsafePath();
        }
    }

    private static void MakeReadOnly(string root)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        if (OperatingSystem.IsLinux())
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetUnixFileMode(file, UnixFileMode.UserRead);
            foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root)) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    private static void MakeWritable(string root)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        if (OperatingSystem.IsLinux())
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root)) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private string? ReplaceWorkspace(string gameId, string staging, string operationId)
    {
        if (OperatingSystem.IsLinux())
        {
            string workspace = Path.Combine(GameDirectory(gameId), "workspace");
            string retiredName = $"workspace-retired-{operationId}";
            using SafeFileHandle gameDirectoryHandle = LinuxFileOperations.OpenDirectory(GameDirectory(gameId));
            using SafeFileHandle? existing = LinuxFileOperations.TryOpenDirectoryAt(gameDirectoryHandle, "workspace");
            string? retiredPath = existing is null ? null : Path.Combine(GameDirectory(gameId), retiredName);
            if (existing is not null) LinuxFileOperations.RenameAt(gameDirectoryHandle, "workspace", retiredName);
            LinuxFileOperations.RenameAt(gameDirectoryHandle, Path.GetFileName(staging), "workspace");
            return retiredPath;
        }
        return ReplaceWorkspaceManaged(gameId, staging, operationId);
    }

    internal string? ReplaceWorkspaceManaged(string gameId, string staging, string operationId)
    {
        string workspace = Path.Combine(GameDirectory(gameId), "workspace");
        string retiredName = $"workspace-retired-{operationId}";
        string? retired = null;
        if (Directory.Exists(workspace))
        {
            retired = Path.Combine(GameDirectory(gameId), retiredName);
            Directory.Move(workspace, retired);
        }
        Directory.Move(staging, workspace);
        return retired;
    }

    private static void RestoreReplacedDirectory(string current, string? retired)
    {
        if (OperatingSystem.IsLinux())
        {
            string? parentPath = Path.GetDirectoryName(current);
            if (parentPath is null) throw new IOException("The game directory has no parent.");
            string currentName = Path.GetFileName(current);
            string failedName = $"{currentName}.failed-{Guid.NewGuid():N}";
            using SafeFileHandle parent = LinuxFileOperations.OpenDirectory(parentPath);
            using SafeFileHandle? currentHandle = LinuxFileOperations.TryOpenDirectoryAt(parent, currentName);
            if (currentHandle is not null) LinuxFileOperations.RenameAt(parent, currentName, failedName);
            if (retired is not null)
            {
                string retiredName = Path.GetFileName(retired);
                using SafeFileHandle? retiredHandle = LinuxFileOperations.TryOpenDirectoryAt(parent, retiredName);
                if (retiredHandle is not null) LinuxFileOperations.RenameAt(parent, retiredName, currentName);
            }
            return;
        }
        if (Directory.Exists(current))
            Directory.Move(current, $"{current}.failed-{Guid.NewGuid():N}");
        if (retired is not null && Directory.Exists(retired)) Directory.Move(retired, current);
    }

    private static void DeleteKnownTree(string path)
    {
        if (!Directory.Exists(path)) return;
        if (!OperatingSystem.IsLinux())
        {
            DeleteKnownTreeManaged(path);
            return;
        }

        string? parentPath = Path.GetDirectoryName(path);
        if (parentPath is null) throw new IOException("The game tree has no parent.");
        using SafeFileHandle parent = LinuxFileOperations.OpenDirectory(parentPath);
        LinuxFileOperations.TryDeleteTreeAt(parent, Path.GetFileName(path), allowReadOnly: true);
    }

    internal static void DeleteKnownTreeManaged(string path)
    {
        if (!Directory.Exists(path)) return;
        Directory.Delete(path, recursive: true);
    }

    private static (string? RetiredContent, string RetiredWorkspace) PublishActivationTree(string gameDirectory, string operationId, string staging, string workspace)
    {
        if (!OperatingSystem.IsLinux())
            return PublishActivationTreeManaged(gameDirectory, operationId, staging, workspace);

        using SafeFileHandle gameDirectoryHandle = LinuxFileOperations.OpenDirectory(gameDirectory);
        string stagingName = Path.GetFileName(staging);
        string workspaceName = Path.GetFileName(workspace);
        string retiredContentName = $"content-retired-{operationId}";
        string retiredWorkspaceName = $"workspace-activated-{operationId}";
        using SafeFileHandle? existingContent = LinuxFileOperations.TryOpenDirectoryAt(gameDirectoryHandle, "content");
        string? retiredContent = existingContent is null ? null : retiredContentName;
        if (existingContent is not null)
        {
            LinuxFileOperations.RenameAt(gameDirectoryHandle, "content", retiredContentName);
        }
        LinuxFileOperations.RenameAt(gameDirectoryHandle, stagingName, "content");
        LinuxFileOperations.RenameAt(gameDirectoryHandle, workspaceName, retiredWorkspaceName);
        return (retiredContent is null ? null : Path.Combine(gameDirectory, retiredContentName), Path.Combine(gameDirectory, retiredWorkspaceName));
    }

    private static string? PublishContentTree(string gameDirectory, string operationId, string staging)
    {
        if (!OperatingSystem.IsLinux())
            return PublishContentTreeManaged(gameDirectory, operationId, staging);

        using SafeFileHandle gameDirectoryHandle = LinuxFileOperations.OpenDirectory(gameDirectory);
        string stagingName = Path.GetFileName(staging);
        string retiredContentName = $"content-retired-{operationId}";
        using SafeFileHandle? existingContent = LinuxFileOperations.TryOpenDirectoryAt(gameDirectoryHandle, "content");
        string? retiredContent = existingContent is null ? null : Path.Combine(gameDirectory, retiredContentName);
        if (existingContent is not null)
            LinuxFileOperations.RenameAt(gameDirectoryHandle, "content", retiredContentName);
        LinuxFileOperations.RenameAt(gameDirectoryHandle, stagingName, "content");
        return retiredContent;
    }

    internal static (string? RetiredContent, string RetiredWorkspace) PublishActivationTreeManaged(string gameDirectory, string operationId, string staging, string workspace)
    {
        string currentPath = Path.Combine(gameDirectory, "content");
        string? retired = null;
        if (Directory.Exists(currentPath))
        {
            retired = Path.Combine(gameDirectory, $"content-retired-{operationId}");
            Directory.Move(currentPath, retired);
        }
        Directory.Move(staging, currentPath);
        string retiredWorkspacePath = Path.Combine(gameDirectory, $"workspace-activated-{operationId}");
        Directory.Move(workspace, retiredWorkspacePath);
        return (retired, retiredWorkspacePath);
    }

    internal static string? PublishContentTreeManaged(string gameDirectory, string operationId, string staging)
    {
        string currentPath = Path.Combine(gameDirectory, "content");
        string? retired = null;
        if (Directory.Exists(currentPath))
        {
            retired = Path.Combine(gameDirectory, $"content-retired-{operationId}");
            Directory.Move(currentPath, retired);
        }
        Directory.Move(staging, currentPath);
        return retired;
    }

    private FileStream AcquireMutationLock(string gameId)
    {
        if (gameId.Length is < 6 or > 64 || !gameId.StartsWith("game_", StringComparison.Ordinal)
            || gameId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw NotFound();
        string directory = GameDirectory(gameId);
        if (!Directory.Exists(directory)) throw NotFound();
        GameStorageOwnerMarker.Validate(directory, gameId);
        try { return new FileStream(Path.Combine(directory, ".mutation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw Conflict("Another game content operation is in progress.", exception); }
    }

    private string AbsoluteDataPath(string relative)
    {
        string root = Path.GetFullPath(databaseOptions.DataRoot);
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw UnsafePath();
        return full;
    }

    private string GameDirectory(string id) => AbsoluteDataPath($"games/{id}");
    private static string RelativeWorkspace(string id) => $"games/{id}/workspace";
    private static string RelativeContent(string id) => $"games/{id}/content";

    private static void InitializeGameDirectory(string directory, string gameId, string ownerUserId)
    {
        GameStorageOwnerMarker.Initialize(directory, gameId, ownerUserId);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string ResolvePath(string root, string logical, bool allowMissingLeaf)
    {
        string full = logical.Length == 0 ? root : Path.GetFullPath(Path.Combine(root, logical.Replace('/', Path.DirectorySeparatorChar)));
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw UnsafePath();
        string current = root;
        string[] segments = logical.Length == 0 ? [] : logical.Split('/');
        for (int index = 0; index < segments.Length - (allowMissingLeaf ? 1 : 0); index++)
        {
            current = Path.Combine(current, segments[index]);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw UnsafePath();
        }
        return full;
    }

    private static string NormalizeRelativePath(string value, bool allowEmpty = false)
    {
        string normalized = value.Normalize(NormalizationForm.FormC).Replace('\\', '/');
        if (allowEmpty && normalized.Length == 0) return normalized;
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains('\0') || Encoding.UTF8.GetByteCount(normalized) > 1024) throw UnsafePath();
        string[] segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.StartsWith(".cloudemuera-", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(segment) > 255)) throw UnsafePath();
        return string.Join('/', segments);
    }

    private static string NormalizeName(string value)
    {
        string normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length is < 1 or > 200 || normalized.Contains('\0')) throw new GameLibraryException(GameLibraryErrorCodes.InvalidInput, "The game name is invalid.");
        return normalized;
    }

    private static GameVisibility ParseVisibility(string value) => value.ToUpperInvariant() switch
    {
        "PRIVATE" => GameVisibility.Private,
        "SERVER_SHARED" => GameVisibility.ServerShared,
        _ => throw new GameLibraryException(GameLibraryErrorCodes.InvalidInput, "The game visibility is invalid."),
    };

    private static void RejectLink(FileSystemInfo info)
    {
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw UnsafePath();
    }

    private static GameLibraryItem ToItem(GameRow game) => new(
        game.Id, game.Name, VisibilityText(game.Visibility), StatusText(game.Status), WorkspaceStatusText(game.WorkspaceStatus),
        game.CurrentContentPath is not null, game.ContentDigest, game.ContentRevision, game.StateVersion, game.CreatedAt, game.UpdatedAt);

    private static string VisibilityText(GameVisibility value) => value == GameVisibility.Private ? "PRIVATE" : "SERVER_SHARED";
    private static string StatusText(GameStatus value) => value switch { GameStatus.Active => "ACTIVE", GameStatus.Blocked => "BLOCKED", _ => "DELETED" };
    private static string WorkspaceStatusText(GameWorkspaceStatus value) => value switch { GameWorkspaceStatus.None => "NONE", GameWorkspaceStatus.Draft => "DRAFT", _ => "VALIDATING" };
    private static string OperationTypeText(GameContentOperationType value) => value switch { GameContentOperationType.Import => "IMPORT", GameContentOperationType.ResetWorkspace => "RESET_WORKSPACE", GameContentOperationType.Validate => "VALIDATE", _ => "ACTIVATE" };
    private static string OperationStatusText(GameContentOperationStatus value) => value switch { GameContentOperationStatus.Pending => "PENDING", GameContentOperationStatus.Running => "RUNNING", GameContentOperationStatus.ContentReady => "CONTENT_READY", GameContentOperationStatus.Committed => "COMMITTED", _ => "FAILED" };
    private static string OperationStageText(GameContentOperationStage value) => value switch
    {
        GameContentOperationStage.Preparing => "PREPARING",
        GameContentOperationStage.Receiving => "RECEIVING",
        GameContentOperationStage.InspectingArchive => "INSPECTING_ARCHIVE",
        GameContentOperationStage.Extracting => "EXTRACTING",
        GameContentOperationStage.NormalizingEncoding => "NORMALIZING_ENCODING",
        GameContentOperationStage.Analyzing => "ANALYZING",
        GameContentOperationStage.ConsumingStaging => "CONSUMING_STAGING",
        GameContentOperationStage.CopyingContent => "COPYING_CONTENT",
        GameContentOperationStage.ValidatingContent => "VALIDATING_CONTENT",
        GameContentOperationStage.RunningValidator => "RUNNING_VALIDATOR",
        GameContentOperationStage.PublishingContent => "PUBLISHING_CONTENT",
        GameContentOperationStage.Completed => "COMPLETED",
        _ => "PREPARING",
    };

    private static string? NormalizeProgressItem(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Normalize(NormalizationForm.FormC).Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains('\0')
            || normalized.Contains("../", StringComparison.Ordinal) || normalized.EndsWith("/..", StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(normalized) > PersistenceLimits.PathMaxLength)
            return null;
        return normalized;
    }

    private static GameLibraryException NotFound() => new(GameLibraryErrorCodes.NotFound, "The game was not found.");
    private static GameLibraryException Conflict(string message, Exception? inner = null) => new(GameLibraryErrorCodes.Conflict, inner is null ? message : $"{message} {inner.Message}");
    private static GameLibraryException UnsafePath() => new(GameLibraryErrorCodes.UnsafePath, "The game path is unsafe.");

    private sealed record WorkspaceInspection(bool CanActivate, string? ContentDigest, int FileCount, long TotalBytes,
        IReadOnlyList<GameValidationDiagnostic> Diagnostics);
}
