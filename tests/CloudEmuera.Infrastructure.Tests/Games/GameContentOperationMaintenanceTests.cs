using System.Security.Cryptography;
using System.Text;
using CloudEmuera.Infrastructure.Games;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Tests.Games;

public sealed class GameContentOperationMaintenanceTests
{
    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task ContentReadyOperationIsCompletedAfterRestart()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        string content = Path.Combine(gameDirectory, "content");
        Directory.CreateDirectory(content);
        byte[] bytes = Encoding.UTF8.GetBytes("restart-safe\n");
        await File.WriteAllBytesAsync(Path.Combine(content, "main.TXT"), bytes);
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");

        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame(withContent: false);
        game.WorkspaceStatus = GameWorkspaceStatus.Validating;
        game.WorkspacePath = "games/game_fixture/workspace";
        scope.Context.Games.Add(game);
        scope.Context.GameContentOperations.Add(new GameContentOperationRow
        {
            Id = "gop_restart",
            GameId = game.Id,
            OperationType = GameContentOperationType.Activate,
            Status = GameContentOperationStatus.ContentReady,
            ContentDigest = null,
            WorkPath = "games/game_fixture/content",
            ExpectedGameStateVersion = game.StateVersion,
            ExpectedContentRevision = game.ContentRevision,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            CreatedAt = PersistenceFixtures.CreatedAt,
            UpdatedAt = PersistenceFixtures.CreatedAt,
        });
        await scope.Context.SaveChangesAsync();

        var maintenance = new GameContentOperationMaintenance(scope.Context, database.Options, TimeProvider.System);
        Assert.Equal(1, await maintenance.ReconcileAsync());

        scope.Context.ChangeTracker.Clear();
        GameRow recovered = await scope.Context.Games.AsNoTracking().SingleAsync();
        GameContentOperationRow operation = await scope.Context.GameContentOperations.AsNoTracking().SingleAsync();
        Assert.Equal(GameContentOperationStatus.Committed, operation.Status);
        Assert.Equal(GameStatus.Active, recovered.Status);
        Assert.Equal(GameWorkspaceStatus.None, recovered.WorkspaceStatus);
        Assert.Equal("games/game_fixture/content", recovered.CurrentContentPath);
        Assert.Null(recovered.ContentDigest);
        Assert.Equal(1, recovered.ContentRevision);
        Assert.Equal(1, await scope.Context.GameFiles.AsNoTracking().CountAsync(file => file.Scope == "CURRENT"));
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task ContentReadyReplacementIsAcceptedWithoutDigestVerification()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        Directory.CreateDirectory(gameDirectory);
        byte[] oldBytes = Encoding.UTF8.GetBytes("old-current\n");
        byte[] newBytes = Encoding.UTF8.GetBytes("new-current\n");
        string retired = Path.Combine(gameDirectory, "content-retired-gop_mismatch");
        string activatedWorkspace = Path.Combine(gameDirectory, "workspace-activated-gop_mismatch");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "content"));
        Directory.CreateDirectory(retired);
        Directory.CreateDirectory(activatedWorkspace);
        await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "content", "main.TXT"), newBytes);
        await File.WriteAllBytesAsync(Path.Combine(retired, "main.TXT"), oldBytes);
        await File.WriteAllTextAsync(Path.Combine(activatedWorkspace, "draft.TXT"), "draft\n");
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");
        string oldDigest = ComputeDigest("main.TXT", oldBytes);

        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame();
        game.ContentDigest = oldDigest;
        game.WorkspacePath = "games/game_fixture/workspace";
        game.WorkspaceStatus = GameWorkspaceStatus.Validating;
        scope.Context.Games.Add(game);
        scope.Context.GameContentOperations.Add(new GameContentOperationRow
        {
            Id = "gop_mismatch",
            GameId = game.Id,
            OperationType = GameContentOperationType.Activate,
            Status = GameContentOperationStatus.ContentReady,
            ContentDigest = oldDigest,
            WorkPath = "games/game_fixture/content",
            ExpectedGameStateVersion = game.StateVersion,
            ExpectedContentRevision = game.ContentRevision,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            CreatedAt = PersistenceFixtures.CreatedAt,
            UpdatedAt = PersistenceFixtures.CreatedAt,
        });
        await scope.Context.SaveChangesAsync();

        var maintenance = new GameContentOperationMaintenance(scope.Context, database.Options, TimeProvider.System);
        Assert.Equal(1, await maintenance.ReconcileAsync());

        Assert.Equal("new-current\n", await File.ReadAllTextAsync(Path.Combine(gameDirectory, "content", "main.TXT")));
        Assert.Equal("old-current\n", await File.ReadAllTextAsync(Path.Combine(retired, "main.TXT")));
        Assert.Equal("draft\n", await File.ReadAllTextAsync(Path.Combine(activatedWorkspace, "draft.TXT")));
        scope.Context.ChangeTracker.Clear();
        GameContentOperationRow operation = await scope.Context.GameContentOperations.AsNoTracking().SingleAsync();
        Assert.Equal(GameContentOperationStatus.Committed, operation.Status);
        Assert.Null(operation.ErrorCode);
        Assert.Null((await scope.Context.Games.AsNoTracking().SingleAsync()).ContentDigest);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task RetiredCleanupWaitsForCopyLeaseAndCountsOnlyExistingTrees()
    {
        DateTimeOffset now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "content"));
        Directory.CreateDirectory(Path.Combine(gameDirectory, "content-retired-gop_cleanup"));
        Directory.CreateDirectory(Path.Combine(gameDirectory, "workspace-activated-gop_cleanup"));
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "content-retired-gop_cleanup", "old.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "workspace-activated-gop_cleanup", "old.txt"), "old");
        if (OperatingSystem.IsLinux())
        {
            foreach (string directory in Directory.EnumerateDirectories(gameDirectory, "*", SearchOption.AllDirectories).Append(gameDirectory))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (string file in Directory.EnumerateFiles(gameDirectory, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");

        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame();
        scope.Context.Games.Add(game);
        scope.Context.GameContentOperations.Add(new GameContentOperationRow
        {
            Id = "gop_cleanup",
            GameId = game.Id,
            OperationType = GameContentOperationType.Activate,
            Status = GameContentOperationStatus.Committed,
            ExpectedContentRevision = game.ContentRevision,
            ContentDigest = game.ContentDigest,
            LeaseExpiresAt = now.AddMinutes(-20),
            CompletedAt = now.AddMinutes(-20),
            CreatedAt = now.AddMinutes(-30),
            UpdatedAt = now.AddMinutes(-20),
        });
        scope.Context.GameContentCopyLeases.Add(new GameContentCopyLeaseRow
        {
            Id = "gcl_cleanup",
            GameId = game.Id,
            ContentRevision = game.ContentRevision,
            ContentDigest = game.ContentDigest!,
            ConsumerType = "SESSION_CREATE",
            ConsumerId = "sess_cleanup",
            ExpiresAt = now.AddMinutes(5),
            CreatedAt = now.AddMinutes(-1),
        });
        await scope.Context.SaveChangesAsync();

        var maintenance = new GameContentOperationMaintenance(scope.Context, database.Options, new FixedTimeProvider(now));
        Assert.Equal(0, await maintenance.ReconcileAsync());
        Assert.True(Directory.Exists(Path.Combine(gameDirectory, "content-retired-gop_cleanup")));

        await scope.Context.GameContentCopyLeases.ExecuteDeleteAsync();
        Assert.Equal(2, await maintenance.ReconcileAsync());
        Assert.False(Directory.Exists(Path.Combine(gameDirectory, "content-retired-gop_cleanup")));
        Assert.False(Directory.Exists(Path.Combine(gameDirectory, "workspace-activated-gop_cleanup")));
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task DatabaseCommittedContentReadyOperationIsMarkedCommittedAfterTreeVerify()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        string content = Path.Combine(gameDirectory, "content");
        Directory.CreateDirectory(content);
        byte[] bytes = Encoding.UTF8.GetBytes("already-committed\n");
        await File.WriteAllBytesAsync(Path.Combine(content, "main.TXT"), bytes);
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");
        string digest = ComputeDigest("main.TXT", bytes);

        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame();
        game.ContentDigest = digest;
        scope.Context.Games.Add(game);
        scope.Context.GameContentOperations.Add(new GameContentOperationRow
        {
            Id = "gop_db_committed",
            GameId = game.Id,
            OperationType = GameContentOperationType.Activate,
            Status = GameContentOperationStatus.ContentReady,
            ContentDigest = digest,
            WorkPath = "games/game_fixture/content",
            ExpectedGameStateVersion = game.StateVersion,
            ExpectedContentRevision = 1,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            CreatedAt = PersistenceFixtures.CreatedAt,
            UpdatedAt = PersistenceFixtures.CreatedAt,
        });
        await scope.Context.SaveChangesAsync();

        var maintenance = new GameContentOperationMaintenance(scope.Context, database.Options, TimeProvider.System);
        Assert.Equal(1, await maintenance.ReconcileAsync());

        scope.Context.ChangeTracker.Clear();
        GameContentOperationRow operation = await scope.Context.GameContentOperations.AsNoTracking().SingleAsync();
        Assert.Equal(GameContentOperationStatus.Committed, operation.Status);
        GameRow recovered = await scope.Context.Games.AsNoTracking().SingleAsync();
        Assert.Equal(1, recovered.ContentRevision);
        Assert.Equal(digest, recovered.ContentDigest);
        Assert.Equal(GameWorkspaceStatus.None, recovered.WorkspaceStatus);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task ExpiredRunningOperationFailsAndReturnsWorkspaceToDraft()
    {
        DateTimeOffset now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "workspace"));
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "workspace", "draft.TXT"), "draft\n");
        Directory.CreateDirectory(Path.Combine(gameDirectory, ".validate-gop_expired"));
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, ".validate-gop_expired", "snapshot.TXT"), "snapshot\n");
        if (OperatingSystem.IsLinux())
        {
            foreach (string directory in Directory.EnumerateDirectories(gameDirectory, "*", SearchOption.AllDirectories).Append(gameDirectory))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (string file in Directory.EnumerateFiles(gameDirectory, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");

        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame(withContent: false);
        game.WorkspaceStatus = GameWorkspaceStatus.Validating;
        game.WorkspacePath = "games/game_fixture/workspace";
        scope.Context.Games.Add(game);
        scope.Context.GameContentOperations.Add(new GameContentOperationRow
        {
            Id = "gop_expired",
            GameId = game.Id,
            OperationType = GameContentOperationType.Validate,
            Status = GameContentOperationStatus.Running,
            ExpectedGameStateVersion = game.StateVersion,
            ExpectedContentRevision = game.ContentRevision,
            LeaseExpiresAt = now.AddMinutes(-5),
            CreatedAt = now.AddMinutes(-20),
            UpdatedAt = now.AddMinutes(-20),
        });
        await scope.Context.SaveChangesAsync();

        var maintenance = new GameContentOperationMaintenance(scope.Context, database.Options, new FixedTimeProvider(now));
        Assert.Equal(1, await maintenance.ReconcileAsync());

        scope.Context.ChangeTracker.Clear();
        GameContentOperationRow operation = await scope.Context.GameContentOperations.AsNoTracking().SingleAsync();
        Assert.Equal(GameContentOperationStatus.Failed, operation.Status);
        Assert.Equal("OPERATION_LEASE_EXPIRED", operation.ErrorCode);
        GameRow recovered = await scope.Context.Games.AsNoTracking().SingleAsync();
        Assert.Equal(GameWorkspaceStatus.Draft, recovered.WorkspaceStatus);
        Assert.Equal(1, recovered.StateVersion);
        Assert.True(Directory.Exists(Path.Combine(gameDirectory, "workspace")));
        Assert.False(Directory.Exists(Path.Combine(gameDirectory, ".validate-gop_expired")));
    }

    private static string ComputeDigest(string path, byte[] bytes)
    {
        string fileDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        byte[] aggregate = Encoding.UTF8.GetBytes($"{path}\0{bytes.LongLength}\0{fileDigest}\n");
        return $"sha256:{Convert.ToHexString(SHA256.HashData(aggregate)).ToLowerInvariant()}";
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
