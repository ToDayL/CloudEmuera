using System.IO.Compression;
using CloudEmuera.Application.GamePackages;
using CloudEmuera.Application.Games;
using CloudEmuera.Application.Identity;
using CloudEmuera.Infrastructure.Games;
using CloudEmuera.Infrastructure.GamePackages;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CloudEmuera.Infrastructure.Tests.Games;

public sealed class GameLibraryServiceTests
{
    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task ReadyIngestionBindsToTheSingleGameWorkspace()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        scope.Context.Games.Add(PersistenceFixtures.CreateGame(withContent: false));
        await scope.Context.SaveChangesAsync();
        Directory.CreateDirectory(Path.Combine(database.RootPath, "games", "game_fixture"));
        GameStorageOwnerMarker.Initialize(Path.Combine(database.RootPath, "games", "game_fixture"), "game_fixture", "usr_fixture");

        var options = new GamePackageStorageOptions { DataRoot = database.RootPath, MinDataRootFreeBytes = 0 };
        var ingestionService = new GamePackageIngestionService(scope.Context, options, TimeProvider.System);
        await using MemoryStream archive = CreateArchive();
        IngestedGamePackage ingestion = await ingestionService.IngestAsync(new("usr_fixture", archive));
        var library = new GameLibraryService(scope.Context, ingestionService, new AcceptingValidator(), database.Options, TimeProvider.System);

        GameLibraryItem bound = await library.BindPackageAsync(
            new CurrentActor("usr_fixture", "PLAYER", "auths_fixture"),
            "game_fixture", ingestion.IngestionId, ingestion.Manifest.ContentDigest, 0);

        Assert.Equal("DRAFT", bound.WorkspaceStatus);
        Assert.True(File.Exists(Path.Combine(database.RootPath, "games", "game_fixture", "workspace", "ERB", "START.ERB")));
        scope.Context.ChangeTracker.Clear();
        Assert.Equal(GamePackageIngestionStatus.Consumed, (await scope.Context.GamePackageIngestions.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(GameContentOperationStatus.Committed, (await scope.Context.GameContentOperations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(3, await scope.Context.GameFiles.AsNoTracking().CountAsync(file => file.Scope == "WORKSPACE"));
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task ValidateAndActivateKeepCurrentContentImmutable()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string workspace = Path.Combine(database.RootPath, "games", "game_fixture", "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "ERB"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "ERB", "START.ERB"), "@SYSTEM_TITLE\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "emuera.config"), "Use sav folder:NO\n");
        GameStorageOwnerMarker.Initialize(Path.Combine(database.RootPath, "games", "game_fixture"), "game_fixture", "usr_fixture");

        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame(withContent: false);
        game.WorkspaceStatus = GameWorkspaceStatus.Draft;
        game.WorkspacePath = "games/game_fixture/workspace";
        scope.Context.Games.Add(game);
        await scope.Context.SaveChangesAsync();

        var service = new GameLibraryService(scope.Context, new UnusedIngestionService(), new AcceptingValidator(), database.Options, TimeProvider.System);
        CurrentActor actor = new("usr_fixture", "PLAYER", "auths_fixture");
        GameValidationResult validation = await service.ValidateAsync(actor, game.Id, 0);
        Assert.True(validation.CanActivate);
        Assert.StartsWith("sha256:", validation.ContentDigest, StringComparison.Ordinal);

        GameLibraryItem activated = await service.ActivateAsync(actor, game.Id, validation.StateVersion);
        Assert.True(activated.HasCurrentContent);
        Assert.Equal(1, activated.ContentRevision);
        Assert.Equal("NONE", activated.WorkspaceStatus);
        string currentFile = Path.Combine(database.RootPath, "games", "game_fixture", "content", "ERB", "START.ERB");
        Assert.Equal("@SYSTEM_TITLE\n", await File.ReadAllTextAsync(currentFile));

        GameTextFile current = await service.ReadTextFileAsync(actor, game.Id, "CURRENT", "ERB/START.ERB");
        Assert.Equal("@SYSTEM_TITLE\n", current.Content);
        Assert.True(File.GetAttributes(currentFile).HasFlag(FileAttributes.ReadOnly) || !OperatingSystem.IsLinux());
        Assert.True(await scope.Context.AuditEvents.CountAsync() >= 2);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task CopyLeasePinsDirectoryAcrossRenameAndReleasesPersistentLease()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string content = Path.Combine(database.RootPath, "games", "game_fixture", "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "marker.txt"), "pinned");
        GameStorageOwnerMarker.Initialize(Path.Combine(database.RootPath, "games", "game_fixture"), "game_fixture", "usr_fixture");
        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame();
        scope.Context.Games.Add(game);
        await scope.Context.SaveChangesAsync();

        await using ServiceProvider provider = BuildContextProvider(database.Options);
        {
            var store = new GameContentCopyLeaseStore(scope.Context, database.Options, provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
            await using (GameContentCopyLease lease = await store.AcquireAsync(game.Id, game.ContentRevision, game.ContentDigest!, "SESSION_CREATE", "sess_copy"))
            {
                Assert.Equal(1, await scope.Context.GameContentCopyLeases.CountAsync());
                Directory.Move(content, Path.Combine(database.RootPath, "games", "game_fixture", "retired"));
                Assert.Equal("pinned", await File.ReadAllTextAsync(Path.Combine(lease.ContentRootPath, "marker.txt")));
            }
        }
        scope.Context.ChangeTracker.Clear();
        Assert.Equal(0, await scope.Context.GameContentCopyLeases.CountAsync());
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task TextReadAndDownloadExposeOnlyReadonlyContent()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        string workspace = Path.Combine(gameDirectory, "workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "a.TXT"), "needle one\nneedle two\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "b.TXT"), "needle three\nneedle four\n");
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");

        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame(withContent: false);
        game.WorkspaceStatus = GameWorkspaceStatus.Draft;
        game.WorkspacePath = "games/game_fixture/workspace";
        scope.Context.Games.Add(game);
        await scope.Context.SaveChangesAsync();

        var service = new GameLibraryService(scope.Context, new UnusedIngestionService(), new AcceptingValidator(), database.Options, TimeProvider.System);
        CurrentActor actor = new("usr_fixture", "PLAYER", "auths_fixture");
        GameTextFile read = await service.ReadTextFileAsync(actor, game.Id, "WORKSPACE", "a.TXT");
        Assert.StartsWith("sha256:", read.ETag, StringComparison.Ordinal);
        Assert.Equal("UTF8", read.Encoding);

        GameFileDownload download = await service.OpenDownloadAsync(actor, game.Id, "WORKSPACE", "a.TXT");
        await using (download.Content)
        using (var reader = new StreamReader(download.Content))
            Assert.Equal("needle one\nneedle two\n", await reader.ReadToEndAsync());
        Assert.StartsWith("sha256:", download.ETag, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task AdminCanOverrideOnlyAllowlistedCurrentBlockingDiagnostic()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        Directory.CreateDirectory(gameDirectory);
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");
        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame(withContent: false);
        scope.Context.Games.Add(game);
        scope.Context.CompatibilityDiagnostics.Add(new CompatibilityDiagnosticRow
        {
            Id = "diag_fixture",
            GameId = game.Id,
            WorkspaceRevision = game.StateVersion,
            Stage = "STRUCTURE",
            Severity = "ERROR",
            Code = "MISSING_RESOURCE",
            LogicalPath = "ERB/START.ERB",
            MessageKey = "game.validation.missing_resource",
            ActivationBlocking = true,
            OverridePolicy = "ADMIN",
            CreatedAt = PersistenceFixtures.CreatedAt,
        });
        await scope.Context.SaveChangesAsync();

        var service = new GameLibraryService(scope.Context, new UnusedIngestionService(), new AcceptingValidator(), database.Options, TimeProvider.System);
        GameDiagnosticItem overridden = await service.OverrideDiagnosticAsync(
            new CurrentActor("usr_fixture", "ADMIN", "auths_admin"), game.Id, "diag_fixture", game.StateVersion);

        Assert.False(overridden.ActivationBlocking);
        Assert.Equal("usr_fixture", overridden.OverriddenBy);
        scope.Context.ChangeTracker.Clear();
        CompatibilityDiagnosticRow stored = await scope.Context.CompatibilityDiagnostics.SingleAsync();
        Assert.NotNull(stored.OverriddenAt);

        GameRow current = await scope.Context.Games.SingleAsync(gameRow => gameRow.Id == game.Id);
        scope.Context.CompatibilityDiagnostics.Add(new CompatibilityDiagnosticRow
        {
            Id = "diag_forbidden",
            GameId = game.Id,
            WorkspaceRevision = current.StateVersion,
            Stage = "CAPABILITY",
            Severity = "ERROR",
            Code = "CALLSHARP_FORBIDDEN",
            MessageKey = "game.validation.callsharp_forbidden",
            ActivationBlocking = true,
            OverridePolicy = "ADMIN",
            CreatedAt = PersistenceFixtures.CreatedAt,
        });
        await scope.Context.SaveChangesAsync();

        GameLibraryException forbidden = await Assert.ThrowsAsync<GameLibraryException>(() => service.OverrideDiagnosticAsync(
            new CurrentActor("usr_fixture", "ADMIN", "auths_admin"), game.Id, "diag_forbidden", current.StateVersion));
        Assert.Equal(GameLibraryErrorCodes.DiagnosticOverrideNotAllowed, forbidden.Code);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task ListDiagnosticsReturnsResolvedMessagesOnlyToOwner()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        Directory.CreateDirectory(gameDirectory);
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");
        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame(withContent: false);
        scope.Context.Games.Add(game);
        scope.Context.CompatibilityDiagnostics.Add(new CompatibilityDiagnosticRow
        {
            Id = "diag_list",
            GameId = game.Id,
            WorkspaceRevision = game.StateVersion,
            Stage = "STRUCTURE",
            Severity = "ERROR",
            Code = "ERB_ENTRYPOINT_MISSING",
            LogicalPath = "ERB",
            MessageKey = "game.validation.erb_entrypoint_missing",
            ActivationBlocking = true,
            OverridePolicy = "NEVER",
            CreatedAt = PersistenceFixtures.CreatedAt,
        });
        await scope.Context.SaveChangesAsync();

        var service = new GameLibraryService(scope.Context, new UnusedIngestionService(), new AcceptingValidator(), database.Options, TimeProvider.System);
        IReadOnlyList<GameDiagnosticItem> diagnostics = await service.ListDiagnosticsAsync(
            new CurrentActor("usr_fixture", "PLAYER", "auths_fixture"), game.Id);

        GameDiagnosticItem item = Assert.Single(diagnostics);
        Assert.Equal("ERB_ENTRYPOINT_MISSING", item.Code);
        Assert.True(item.ActivationBlocking);
        Assert.Contains("at the package root", item.Message, StringComparison.Ordinal);

        GameLibraryException hidden = await Assert.ThrowsAsync<GameLibraryException>(() =>
            service.ListDiagnosticsAsync(new CurrentActor("usr_other", "PLAYER", "auths_other"), game.Id));
        Assert.Equal(GameLibraryErrorCodes.NotFound, hidden.Code);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task SharedGameExposesCurrentButNeverOwnerWorkspace()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        string current = Path.Combine(gameDirectory, "content");
        string workspace = Path.Combine(gameDirectory, "workspace");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(current, "public.TXT"), "published\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "private.TXT"), "draft\n");
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");

        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame();
        game.Visibility = GameVisibility.ServerShared;
        game.WorkspacePath = "games/game_fixture/workspace";
        game.WorkspaceStatus = GameWorkspaceStatus.Draft;
        scope.Context.Games.Add(game);
        await scope.Context.SaveChangesAsync();

        var service = new GameLibraryService(scope.Context, new UnusedIngestionService(), new AcceptingValidator(), database.Options, TimeProvider.System);
        CurrentActor other = new("usr_other", "PLAYER", "auths_other");
        IReadOnlyList<GameFileItem> visible = await service.ListFilesAsync(other, game.Id, "CURRENT", null);
        Assert.Contains(visible, item => item.Path == "public.TXT");
        Assert.Equal("published\n", (await service.ReadTextFileAsync(other, game.Id, "CURRENT", "public.TXT")).Content);

        GameLibraryException hidden = await Assert.ThrowsAsync<GameLibraryException>(() => service.ListFilesAsync(other, game.Id, "WORKSPACE", null));
        Assert.Equal(GameLibraryErrorCodes.NotFound, hidden.Code);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task BlockPreservesContentAndReferencedGameCannotBeDeleted()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        Directory.CreateDirectory(gameDirectory);
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");
        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame();
        scope.Context.Games.Add(game);
        scope.Context.Sessions.Add(PersistenceFixtures.CreateSession(gameId: game.Id));
        await scope.Context.SaveChangesAsync();

        var service = new GameLibraryService(scope.Context, new UnusedIngestionService(), new AcceptingValidator(), database.Options, TimeProvider.System);
        CurrentActor admin = new("usr_fixture", "ADMIN", "auths_admin");
        GameLibraryItem blocked = await service.SetBlockedAsync(admin, game.Id, true, game.StateVersion);
        Assert.Equal("BLOCKED", blocked.Status);
        Assert.Equal(game.ContentDigest, blocked.ContentDigest);
        Assert.Equal(game.ContentRevision, blocked.ContentRevision);

        GameLibraryException inUse = await Assert.ThrowsAsync<GameLibraryException>(() => service.DeleteAsync(
            new CurrentActor("usr_fixture", "PLAYER", "auths_fixture"), game.Id, blocked.StateVersion));
        Assert.Equal(GameLibraryErrorCodes.InUse, inUse.Code);

        GameLibraryItem active = await service.SetBlockedAsync(admin, game.Id, false, blocked.StateVersion);
        Assert.Equal("ACTIVE", active.Status);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task DeleteIsLogicalUntilRetiredSafetyCleanup()
    {
        DateTimeOffset deletedAt = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        string content = Path.Combine(gameDirectory, "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "keep.txt"), "keep");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(gameDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(content, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(content, "keep.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");

        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame();
        scope.Context.Games.Add(game);
        await scope.Context.SaveChangesAsync();

        var service = new GameLibraryService(scope.Context, new UnusedIngestionService(), new AcceptingValidator(), database.Options, new FixedTimeProvider(deletedAt));
        await service.DeleteAsync(new CurrentActor("usr_fixture", "PLAYER", "auths_fixture"), game.Id, game.StateVersion);
        Assert.True(Directory.Exists(content));

        var maintenance = new GameContentOperationMaintenance(scope.Context, database.Options, new FixedTimeProvider(deletedAt.AddMinutes(11)));
        Assert.Equal(1, await maintenance.ReconcileAsync());
        Assert.False(Directory.Exists(content));
        Assert.True(File.Exists(Path.Combine(gameDirectory, "owner.json")));
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task DeletedGameFreesItsNameForRecreation()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        Directory.CreateDirectory(gameDirectory);
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");
        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame(withContent: false);
        scope.Context.Games.Add(game);
        await scope.Context.SaveChangesAsync();

        var service = new GameLibraryService(scope.Context, new UnusedIngestionService(), new AcceptingValidator(), database.Options, TimeProvider.System);
        CurrentActor actor = new("usr_fixture", "PLAYER", "auths_fixture");

        GameLibraryException conflict = await Assert.ThrowsAsync<GameLibraryException>(() =>
            service.CreateAsync(actor, "Fixture Game", "PRIVATE"));
        Assert.Equal(GameLibraryErrorCodes.NameConflict, conflict.Code);
        Assert.Equal("同名游戏已存在。", conflict.Message);

        await service.DeleteAsync(actor, game.Id, game.StateVersion);
        GameLibraryItem recreated = await service.CreateAsync(actor, "Fixture Game", "PRIVATE");
        Assert.Equal("Fixture Game", recreated.Name);
        Assert.NotEqual(game.Id, recreated.Id);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task RenameToDeletedNameSucceedsButActiveNameConflicts()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        foreach (string id in new[] { "game_first", "game_second", "game_third" })
        {
            Directory.CreateDirectory(Path.Combine(database.RootPath, "games", id));
            GameStorageOwnerMarker.Initialize(Path.Combine(database.RootPath, "games", id), id, "usr_fixture");
        }
        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow first = PersistenceFixtures.CreateGame("game_first", name: "Alpha", withContent: false);
        GameRow second = PersistenceFixtures.CreateGame("game_second", name: "Beta", withContent: false);
        GameRow third = PersistenceFixtures.CreateGame("game_third", name: "Gamma", withContent: false);
        scope.Context.Games.AddRange(first, second, third);
        await scope.Context.SaveChangesAsync();

        var service = new GameLibraryService(scope.Context, new UnusedIngestionService(), new AcceptingValidator(), database.Options, TimeProvider.System);
        CurrentActor actor = new("usr_fixture", "PLAYER", "auths_fixture");
        await service.DeleteAsync(actor, first.Id, first.StateVersion);

        GameLibraryItem renamed = await service.UpdateAsync(actor, second.Id, "Alpha", null, second.StateVersion);
        Assert.Equal("Alpha", renamed.Name);

        GameLibraryException conflict = await Assert.ThrowsAsync<GameLibraryException>(() =>
            service.UpdateAsync(actor, second.Id, "Gamma", null, renamed.StateVersion));
        Assert.Equal(GameLibraryErrorCodes.NameConflict, conflict.Code);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task FailedValidationRestoresEditableWorkspaceImmediately()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        string workspace = Path.Combine(gameDirectory, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "ERB"));
        File.WriteAllText(Path.Combine(workspace, "ERB", "START.ERB"), "@SYSTEM_TITLE\n");
        File.WriteAllText(Path.Combine(workspace, "emuera.config"), "Use sav folder:NO\n");
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");

        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame(withContent: false);
        game.WorkspaceStatus = GameWorkspaceStatus.Draft;
        game.WorkspacePath = "games/game_fixture/workspace";
        scope.Context.Games.Add(game);
        await scope.Context.SaveChangesAsync();

        int expectedStateVersion = game.StateVersion + 1;
        var service = new GameLibraryService(scope.Context, new UnusedIngestionService(), new ThrowingValidator(), database.Options, TimeProvider.System);
        CurrentActor actor = new("usr_fixture", "PLAYER", "auths_fixture");
        GameLibraryException failure = await Assert.ThrowsAsync<GameLibraryException>(() =>
            service.ValidateAsync(actor, game.Id, game.StateVersion));
        Assert.Equal(GameLibraryErrorCodes.ValidationFailed, failure.Code);

        scope.Context.ChangeTracker.Clear();
        GameRow after = await scope.Context.Games.SingleAsync(row => row.Id == game.Id);
        Assert.Equal(GameWorkspaceStatus.Draft, after.WorkspaceStatus);
        Assert.Equal(expectedStateVersion, after.StateVersion);
        Assert.Equal(GameContentOperationStatus.Failed,
            (await scope.Context.GameContentOperations.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task ValidatorCrashIsConvertedToBlockingDiagnosticAndKeepsWorkspaceEditable()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        string workspace = Path.Combine(gameDirectory, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "ERB"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "ERB", "START.ERB"), "@SYSTEM_TITLE\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "emuera.config"), "Use sav folder:NO\n");
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");

        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        GameRow game = PersistenceFixtures.CreateGame(withContent: false);
        game.WorkspaceStatus = GameWorkspaceStatus.Draft;
        game.WorkspacePath = "games/game_fixture/workspace";
        scope.Context.Games.Add(game);
        await scope.Context.SaveChangesAsync();

        var service = new GameLibraryService(scope.Context, new UnusedIngestionService(), new CrashReportingValidator(), database.Options, TimeProvider.System);
        GameValidationResult result = await service.ValidateAsync(new CurrentActor("usr_fixture", "PLAYER", "auths_fixture"), game.Id, 0);

        Assert.False(result.CanActivate);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "VALIDATOR_CRASHED" && diagnostic.ActivationBlocking);
        scope.Context.ChangeTracker.Clear();
        GameRow stored = await scope.Context.Games.AsNoTracking().SingleAsync();
        Assert.Equal(GameWorkspaceStatus.Draft, stored.WorkspaceStatus);
        Assert.True(await scope.Context.CompatibilityDiagnostics.AsNoTracking().AnyAsync(diagnostic => diagnostic.Code == "VALIDATOR_CRASHED" && diagnostic.ActivationBlocking));
        Assert.True(File.Exists(Path.Combine(workspace, "ERB", "START.ERB")));
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task ManagedFallbackCopyTreeRejectsLinksAndCopiesRegularTrees()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cloudemuera-managed-copy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "source", "ERB"));
        await File.WriteAllTextAsync(Path.Combine(root, "source", "ERB", "START.ERB"), "@SYSTEM_TITLE\n");
        await File.WriteAllTextAsync(Path.Combine(root, "source", "emuera.config"), "Use sav folder:NO\n");
        try
        {
            GameLibraryService.CopyTreeManaged(Path.Combine(root, "source"), Path.Combine(root, "destination"));
            Assert.Equal("@SYSTEM_TITLE\n", await File.ReadAllTextAsync(Path.Combine(root, "destination", "ERB", "START.ERB")));

            Directory.CreateDirectory(Path.Combine(root, "linked"));
            File.CreateSymbolicLink(Path.Combine(root, "linked", "escape.txt"), "/etc/hostname");
            GameLibraryException rejected = Assert.Throws<GameLibraryException>(() =>
                GameLibraryService.CopyTreeManaged(Path.Combine(root, "linked"), Path.Combine(root, "rejected")));
            Assert.Equal(GameLibraryErrorCodes.UnsafePath, rejected.Code);
            Assert.False(File.Exists(Path.Combine(root, "rejected", "escape.txt")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "rejected")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task ManagedFallbackReplaceWorkspaceSwapsAndRetiresExistingWorkspace()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "workspace"));
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "workspace", "old.TXT"), "old\n");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "staging"));
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "staging", "new.TXT"), "new\n");
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");

        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        scope.Context.Games.Add(PersistenceFixtures.CreateGame(withContent: false));
        await scope.Context.SaveChangesAsync();

        var service = new GameLibraryService(scope.Context, new UnusedIngestionService(), new AcceptingValidator(), database.Options, TimeProvider.System);
        string? retired = service.ReplaceWorkspaceManaged("game_fixture", Path.Combine(gameDirectory, "staging"), "gop_fallback");

        Assert.NotNull(retired);
        Assert.Equal("old\n", await File.ReadAllTextAsync(Path.Combine(retired!, "old.TXT")));
        Assert.Equal("new\n", await File.ReadAllTextAsync(Path.Combine(gameDirectory, "workspace", "new.TXT")));
        Assert.False(File.Exists(Path.Combine(gameDirectory, "workspace", "old.TXT")));

        Directory.Delete(Path.Combine(gameDirectory, "workspace"), recursive: true);
        Directory.CreateDirectory(Path.Combine(gameDirectory, "staging2"));
        string? none = service.ReplaceWorkspaceManaged("game_fixture", Path.Combine(gameDirectory, "staging2"), "gop_none");
        Assert.Null(none);
        Assert.True(Directory.Exists(Path.Combine(gameDirectory, "workspace")));
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task ManagedFallbackPublishActivationSwapsCurrentAndRetiresWorkspace()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "content"));
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "content", "old.TXT"), "old-current\n");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "workspace"));
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "workspace", "draft.TXT"), "draft\n");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "staging"));
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "staging", "new.TXT"), "new-current\n");
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");

        (string? retiredContent, string retiredWorkspace) = GameLibraryService.PublishActivationTreeManaged(
            gameDirectory, "gop_fallback", Path.Combine(gameDirectory, "staging"), Path.Combine(gameDirectory, "workspace"));

        Assert.NotNull(retiredContent);
        Assert.Equal("old-current\n", await File.ReadAllTextAsync(Path.Combine(retiredContent!, "old.TXT")));
        Assert.Equal("new-current\n", await File.ReadAllTextAsync(Path.Combine(gameDirectory, "content", "new.TXT")));
        Assert.Equal("draft\n", await File.ReadAllTextAsync(Path.Combine(retiredWorkspace, "draft.TXT")));
        Assert.False(Directory.Exists(Path.Combine(gameDirectory, "workspace")));
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task ManagedFallbackDeleteKnownTreeRemovesTree()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cloudemuera-managed-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "tree", "ERB"));
        await File.WriteAllTextAsync(Path.Combine(root, "tree", "ERB", "START.ERB"), "@SYSTEM_TITLE\n");
        GameLibraryService.DeleteKnownTreeManaged(Path.Combine(root, "tree"));
        Assert.False(Directory.Exists(Path.Combine(root, "tree")));
        GameLibraryService.DeleteKnownTreeManaged(Path.Combine(root, "missing"));
        Directory.Delete(root);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task ReadTraversalRejectsSymlinksInsideContentTrees()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        string content = Path.Combine(gameDirectory, "content");
        Directory.CreateDirectory(Path.Combine(content, "ERB"));
        await File.WriteAllTextAsync(Path.Combine(content, "ERB", "START.ERB"), "@SYSTEM_TITLE\n");
        await File.WriteAllTextAsync(Path.Combine(content, "emuera.config"), "Use sav folder:NO\n");
        File.CreateSymbolicLink(Path.Combine(content, "ERB", "link.ERB"), "START.ERB");
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
        await scope.Context.SaveChangesAsync();

        var service = new GameLibraryService(scope.Context, new UnusedIngestionService(), new AcceptingValidator(), database.Options, TimeProvider.System);
        CurrentActor actor = new("usr_fixture", "PLAYER", "auths_fixture");

        GameLibraryException listed = await Assert.ThrowsAsync<GameLibraryException>(() =>
            service.ListFilesAsync(actor, game.Id, "CURRENT", "ERB"));
        Assert.Equal(GameLibraryErrorCodes.UnsafePath, listed.Code);

        GameLibraryException read = await Assert.ThrowsAsync<GameLibraryException>(() =>
            service.ReadTextFileAsync(actor, game.Id, "CURRENT", "ERB/link.ERB"));
        Assert.Equal(GameLibraryErrorCodes.UnsafePath, read.Code);

        GameLibraryException download = await Assert.ThrowsAsync<GameLibraryException>(() =>
            service.OpenDownloadAsync(actor, game.Id, "CURRENT", "ERB/link.ERB"));
        Assert.Equal(GameLibraryErrorCodes.UnsafePath, download.Code);
    }

    private static ServiceProvider BuildContextProvider(SqliteDatabaseOptions options)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            SqliteConnectionFactory factory = new(options, createDataRoot: false);
            var contextOptions = new DbContextOptionsBuilder<CloudEmueraDbContext>()
                .UseSqlite(factory.OpenConnection(SqliteConnectionAccess.ReadWrite), sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
                .Options;
            return new CloudEmueraDbContext(contextOptions);
        });
        return services.BuildServiceProvider();
    }

    private sealed class UnusedIngestionService : IGamePackageIngestionService
    {
        public Task<IngestedGamePackage> IngestAsync(GamePackageIngestionRequest request, GamePackageIngestionLimits? requestedLimits = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GamePackageConsumption> BeginConsumeAsync(string ingestionId, string ownerUserId, string expectedContentDigest, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DateTimeOffset> RenewConsumeAsync(string ingestionId, string ownerUserId, string expectedContentDigest, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteConsumeAsync(string ingestionId, string ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AbandonAsync(string ingestionId, string ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingValidator : IGameContentValidator
    {
        public Task<GameParserValidationResult> ValidateAsync(string snapshotRoot, CancellationToken cancellationToken = default) =>
            throw new GameLibraryException(GameLibraryErrorCodes.ValidationFailed, "simulated validator failure");
    }

    private sealed class CrashReportingValidator : IGameContentValidator
    {
        public Task<GameParserValidationResult> ValidateAsync(string snapshotRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameParserValidationResult(false, [new GameValidationDiagnostic("VALIDATOR_CRASHED", "ERROR", null, "The parser process was terminated.", true)]));
    }

    private sealed class AcceptingValidator : IGameContentValidator
    {
        public Task<GameParserValidationResult> ValidateAsync(string snapshotRoot, CancellationToken cancellationToken = default)
        {
            Assert.True(Directory.Exists(snapshotRoot));
            return Task.FromResult(new GameParserValidationResult(true, []));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static MemoryStream CreateArchive()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("ERB/START.ERB").Open())) writer.Write("@SYSTEM_TITLE\n");
            using (var writer = new StreamWriter(archive.CreateEntry("emuera.config").Open())) writer.Write("Use sav folder:NO\n");
        }
        stream.Position = 0;
        return stream;
    }
}
