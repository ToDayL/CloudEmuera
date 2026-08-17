using System.Security.Cryptography;
using System.Text;
using CloudEmuera.Application.Games;
using CloudEmuera.Application.Identity;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Domain.Sessions;
using CloudEmuera.Infrastructure.Games;
using CloudEmuera.Infrastructure.Capacity;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Sessions;
using CloudEmuera.Infrastructure.Tests.Support;
using CloudEmuera.RuntimeAdapter;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CloudEmuera.Infrastructure.Tests.Sessions;

[Trait("Category", "SessionLifecycle")]
public sealed class SqliteSessionApplicationServiceTests
{
    [Fact]
    public async Task CreateCopiesFrozenCurrentContentAndReplaysTheSameSessionForTheSameKey()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await SeedGameAsync(database);
        await using ServiceProvider provider = BuildProvider(database.Options);
        ISessionApplicationService service = provider.GetRequiredService<ISessionApplicationService>();
        CurrentActor actor = new("usr_fixture", "PLAYER", "auth_fixture");

        SessionCommandResult created = await service.CreateAsync(
            actor,
            new CreateSessionCommand("game_fixture", "  一周目  ", "create-1"));

        Assert.True(created.Succeeded, created.Failure?.Code);
        Assert.Equal(201, created.StatusCode);
        SessionView first = created.Value!;
        Assert.Equal("一周目", first.Name);
        Assert.Equal(SessionState.Closed, first.State);
        Assert.Equal(1, first.SourceContentRevision);
        Assert.False(first.RuntimeVersion.Contains(Path.DirectorySeparatorChar));
        string root = Path.Combine(database.RootPath, "sessions", first.Id, "root");
        Assert.True(File.Exists(Path.Combine(root, "ERB", "START.ERB")));
        Assert.Equal("@SYSTEM_TITLE\n", await File.ReadAllTextAsync(Path.Combine(root, "ERB", "START.ERB")));
        Assert.DoesNotContain(first.GetType().GetProperties(), property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        File.Delete(Path.Combine(database.RootPath, "games", "game_fixture", ".mutation.lock"));

        SessionCommandResult replay = await service.CreateAsync(
            actor,
            new CreateSessionCommand("game_fixture", "一周目", "create-1"));

        Assert.True(replay.Succeeded, replay.Failure?.Code);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Id, replay.Value!.Id);

        await using DbContextScope verify = database.OpenContext();
        Assert.Equal(1, await verify.Context.Sessions.CountAsync());
        Assert.Equal(1, await verify.Context.SessionCreationOperations.CountAsync(row => row.Status == SessionCreationOperationStatus.Committed));
        Assert.Equal(IdempotencyRecordStatus.Succeeded, (await verify.Context.IdempotencyRecords.SingleAsync()).Status);
    }

    [Fact]
    public async Task ClosedAndCrashedSessionsCanBeDeletedButActiveSessionsCannot()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await SeedGameAsync(database);
        await using ServiceProvider provider = BuildProvider(database.Options);
        ISessionApplicationService service = provider.GetRequiredService<ISessionApplicationService>();
        CurrentActor actor = new("usr_fixture", "PLAYER", "auth_fixture");

        SessionView closed = (await service.CreateAsync(actor, new CreateSessionCommand("game_fixture", "待删除", "create-delete"))).Value!;
        string closedContainer = Path.Combine(database.RootPath, "sessions", closed.Id);
        Assert.True(Directory.Exists(closedContainer));

        SessionDeleteResult deleted = await service.DeleteAsync(actor, new SessionDeleteCommand(closed.Id, "delete-closed"));

        Assert.True(deleted.Succeeded, $"status={deleted.StatusCode}, pending={deleted.Pending}, failure={deleted.Failure?.Code}:{deleted.Failure?.Message}");
        Assert.Equal(204, deleted.StatusCode);
        Assert.False(Directory.Exists(closedContainer));
        Assert.Null(await service.GetAsync(actor, closed.Id));
        SessionDeleteResult replay = await service.DeleteAsync(actor, new SessionDeleteCommand(closed.Id, "delete-closed"));
        Assert.True(replay.Succeeded, replay.Failure?.Code);
        Assert.True(replay.Replayed);

        SessionView active = (await service.CreateAsync(actor, new CreateSessionCommand("game_fixture", "运行中不可删", "create-active"))).Value!;
        await using (DbContextScope scope = database.OpenContext())
        {
            SessionRow row = await scope.Context.Sessions.SingleAsync(value => value.Id == active.Id);
            row.State = SessionState.Running;
            row.StartedAt = row.CreatedAt;
            row.ClosedAt = null;
            row.StateVersion++;
            await scope.Context.SaveChangesAsync();
        }

        SessionDeleteResult rejected = await service.DeleteAsync(actor, new SessionDeleteCommand(active.Id, "delete-active"));

        Assert.False(rejected.Succeeded);
        Assert.Equal(SessionErrorCodes.SessionNotDeletable, rejected.Failure?.Code);
        Assert.True(Directory.Exists(Path.Combine(database.RootPath, "sessions", active.Id)));
        Assert.NotNull(await service.GetAsync(actor, active.Id));

        SessionView crashed = (await service.CreateAsync(actor, new CreateSessionCommand("game_fixture", "崩溃后删除", "create-crashed"))).Value!;
        await using (DbContextScope scope = database.OpenContext())
        {
            SessionRow row = await scope.Context.Sessions.SingleAsync(value => value.Id == crashed.Id);
            row.State = SessionState.Crashed;
            row.ClosedAt = row.LastActivityAt;
            row.StateVersion++;
            await scope.Context.SaveChangesAsync();
        }

        SessionDeleteResult crashedDeleted = await service.DeleteAsync(actor, new SessionDeleteCommand(crashed.Id, "delete-crashed"));

        Assert.True(crashedDeleted.Succeeded, crashedDeleted.Failure?.Code);
        Assert.False(Directory.Exists(Path.Combine(database.RootPath, "sessions", crashed.Id)));
        await using DbContextScope audit = database.OpenContext();
        Assert.Equal(2, await audit.Context.AuditEvents.CountAsync(row => row.Action == "SESSION_DELETED"));
    }

    [Fact]
    public async Task CreateRejectsWhenInactiveSessionLimitIsReached()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await SeedGameAsync(database);
        await using ServiceProvider provider = BuildProvider(database.Options, new InstanceCapacityOptions { MaxInactiveSessions = 1 });
        ISessionApplicationService service = provider.GetRequiredService<ISessionApplicationService>();
        CurrentActor actor = new("usr_fixture", "PLAYER", "auth_fixture");

        SessionCommandResult first = await service.CreateAsync(actor, new CreateSessionCommand("game_fixture", "第一个", "create-limit-1"));

        Assert.True(first.Succeeded, first.Failure?.Code);
        SessionApplicationException exception = await Assert.ThrowsAsync<SessionApplicationException>(() =>
            service.CreateAsync(actor, new CreateSessionCommand("game_fixture", "第二个", "create-limit-2")));

        Assert.Equal(SessionErrorCodes.InactiveSessionLimitExceeded, exception.Code);
        Assert.Equal(409, exception.StatusCode);
    }

    private static async Task SeedGameAsync(TemporarySqliteDatabase database)
    {
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        string content = Path.Combine(gameDirectory, "content");
        Directory.CreateDirectory(Path.Combine(content, "CSV"));
        Directory.CreateDirectory(Path.Combine(content, "ERB"));
        await File.WriteAllTextAsync(Path.Combine(content, "ERB", "START.ERB"), "@SYSTEM_TITLE\n");
        await File.WriteAllTextAsync(Path.Combine(content, "emuera.config"), "Use sav folder:NO\n");
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");

        string startDigest = Digest(await File.ReadAllBytesAsync(Path.Combine(content, "ERB", "START.ERB")));
        string configDigest = Digest(await File.ReadAllBytesAsync(Path.Combine(content, "emuera.config")));
        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        scope.Context.Games.Add(PersistenceFixtures.CreateGame());
        scope.Context.GameFiles.AddRange(
            new GameFileRow { GameId = "game_fixture", Scope = "CURRENT", LogicalPath = "CSV", EntryKind = "DIRECTORY" },
            new GameFileRow { GameId = "game_fixture", Scope = "CURRENT", LogicalPath = "ERB", EntryKind = "DIRECTORY" },
            new GameFileRow { GameId = "game_fixture", Scope = "CURRENT", LogicalPath = "ERB/START.ERB", EntryKind = "FILE", ByteLength = Encoding.UTF8.GetByteCount("@SYSTEM_TITLE\n"), ContentDigest = $"sha256:{startDigest}", FileKind = "TEXT", TextEncoding = "UTF8", HasBom = false },
            new GameFileRow { GameId = "game_fixture", Scope = "CURRENT", LogicalPath = "emuera.config", EntryKind = "FILE", ByteLength = Encoding.UTF8.GetByteCount("Use sav folder:NO\n"), ContentDigest = $"sha256:{configDigest}", FileKind = "TEXT", TextEncoding = "UTF8", HasBom = false });
        await scope.Context.SaveChangesAsync();
    }

    private static ServiceProvider BuildProvider(SqliteDatabaseOptions options, InstanceCapacityOptions? capacityOptions = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(capacityOptions ?? InstanceCapacityOptions.Default);
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddScoped(_ =>
        {
            SqliteConnectionFactory factory = new(options, createDataRoot: false);
            return new CloudEmueraDbContext(new DbContextOptionsBuilder<CloudEmueraDbContext>()
                .UseSqlite(factory.OpenConnection(SqliteConnectionAccess.ReadWrite), sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
                .Options);
        });
        services.AddScoped<IGameContentCopyLeaseStore, GameContentCopyLeaseStore>();
        services.AddSingleton<SqliteIdempotencyStore>();
        services.AddSingleton<ISessionLifecycleExecutor, NoopLifecycleExecutor>();
        services.AddSingleton<ISessionApplicationService, SqliteSessionApplicationService>();
        return services.BuildServiceProvider();
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class NoopLifecycleExecutor : ISessionLifecycleExecutor
    {
        public Task<SessionRuntimeOpenResult> OpenAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionRuntimeCloseResult> CloseAsync(string sessionId, string reasonCode = "requested", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

}
