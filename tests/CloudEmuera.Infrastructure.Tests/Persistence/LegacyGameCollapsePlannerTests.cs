using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace CloudEmuera.Infrastructure.Tests.Persistence;

public sealed class LegacyGameCollapsePlannerTests
{
    [Fact]
    [Trait("Category", "Migration")]
    public async Task DifferentPublishedDigestsRequireExplicitSelection()
    {
        using TemporarySqliteDatabase database = new();
        await CreateLegacyGameAsync(database, differentPublishedDigests: true);

        LegacyGameCollapseReport report = await LegacyGameCollapsePlanner.PlanAsync(database.Options);

        Assert.True(report.HasAmbiguity);
        Assert.Equal("LEGACY_GAME_CURRENT_AMBIGUOUS", Assert.Single(report.Games).FailureCode);
        LegacyGameCollapseException exception = Assert.Throws<LegacyGameCollapseException>(() =>
            LegacyGameCollapsePlanner.AutomaticSelections(report));
        Assert.Equal("LEGACY_GAME_CURRENT_AMBIGUOUS", exception.Code);
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task SameDigestPlanProducesIdentityBoundSelectionTemplate()
    {
        using TemporarySqliteDatabase database = new();
        await CreateLegacyGameAsync(database, differentPublishedDigests: false);
        LegacyGameCollapseReport report = await LegacyGameCollapsePlanner.PlanAsync(database.Options);
        IReadOnlyList<LegacyGameCollapseSelection> automatic = LegacyGameCollapsePlanner.AutomaticSelections(report);
        Assert.Equal("gver_two", Assert.Single(automatic).CurrentVersionId);

        string planPath = Path.Combine(database.RootPath, "collapse-plan.json");
        await File.WriteAllTextAsync(planPath, LegacyGameCollapsePlanner.SerializeSelectionTemplate(report));
        IReadOnlyList<LegacyGameCollapseSelection> loaded = await LegacyGameCollapsePlanner.LoadAndValidateSelectionsAsync(planPath, report);
        Assert.Equal(automatic, loaded);

        await File.AppendAllTextAsync(planPath, "\n");
        LegacyGameCollapseException stale = await Assert.ThrowsAsync<LegacyGameCollapseException>(() =>
            LegacyGameCollapsePlanner.LoadAndValidateSelectionsAsync(planPath, report with { ReportDigest = "sha256:" + new string('f', 64) }));
        Assert.Equal("LEGACY_GAME_COLLAPSE_PLAN_STALE", stale.Code);
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task PhysicalCollapseCopiesSelectedContentAndBacksUpLegacyTree()
    {
        using TemporarySqliteDatabase database = new();
        await CreateLegacyGameAsync(database, differentPublishedDigests: false, withPhysicalContent: true);

        MigrationResult result = await database.MigrateAsync();

        Assert.True(result.Succeeded, result.ErrorCode);
        string gameRoot = Path.Combine(database.RootPath, "games", "game_legacy");
        Assert.True(File.Exists(Path.Combine(gameRoot, "content", "main.TXT")), string.Join(";", Directory.Exists(gameRoot) ? Directory.EnumerateFileSystemEntries(gameRoot, "*", SearchOption.AllDirectories) : []));
        Assert.False(Directory.Exists(Path.Combine(database.RootPath, "games", "game_legacy", "gver_two", "content")));
        string journal = Path.Combine(database.RootPath, "backups", "legacy-game-content", "20260809150000_CollapseGameVersionsIntoGames", "completion.json");
        Assert.True(File.Exists(journal));
        Assert.True(File.Exists(Path.Combine(database.RootPath, "backups", "legacy-game-content", "20260809150000_CollapseGameVersionsIntoGames", "game_legacy", "gver_two-CURRENT", "main.TXT")));
    }

    private static async Task CreateLegacyGameAsync(TemporarySqliteDatabase database, bool differentPublishedDigests, bool withPhysicalContent = false)
    {
        byte[] physicalBytes = Encoding.UTF8.GetBytes("legacy-content\n");
        string physicalDigest = ComputeDigest("main.TXT", physicalBytes);
        await using DbContextScope scope = database.OpenContext(SqliteConnectionAccess.ReadWriteCreate);
        await scope.Context.Database.MigrateAsync("20260807071428_InitialMetadata");
        await ExecuteAsync(scope.Connection, "INSERT INTO quota_profiles (id,name,max_active_sessions,max_game_package_bytes,max_session_bytes,max_output_bytes_per_second,created_at,updated_at,state_version) VALUES ('qtp_legacy','Legacy',4,1000000,1000000,100000,1,1,0);");
        await ExecuteAsync(scope.Connection, "INSERT INTO users (id,login_name,normalized_login_name,role,status,quota_profile_id,preferences_json,created_at,updated_at,state_version,password_hash,security_stamp,access_failed_count) VALUES ('usr_legacy','legacy','LEGACY','PLAYER','ACTIVE','qtp_legacy','{}',1,1,0,NULL,'stamp',0);");
        await ExecuteAsync(scope.Connection, "INSERT INTO games (id,owner_user_id,name,visibility,status,created_at,updated_at,state_version) VALUES ('game_legacy','usr_legacy','Legacy','PRIVATE','ACTIVE',1,1,0);");
        string firstDigest = withPhysicalContent ? physicalDigest : "sha256:" + new string('a', 64);
        string secondDigest = differentPublishedDigests ? "sha256:" + new string('b', 64) : firstDigest;
        if (differentPublishedDigests)
            await InsertVersionAsync(scope.Connection, "gver_one", firstDigest, 2, "games/game_legacy/gver_one/content");
        await InsertVersionAsync(scope.Connection, "gver_two", secondDigest, 3, "games/game_legacy/gver_two/content");
        if (withPhysicalContent)
        {
            string content = Path.Combine(database.RootPath, "games", "game_legacy", "gver_two", "content");
            Directory.CreateDirectory(content);
            await File.WriteAllBytesAsync(Path.Combine(content, "main.TXT"), physicalBytes);
        }
    }

    private static async Task InsertVersionAsync(SqliteConnection connection, string id, string digest, long publishedAt, string path)
    {
        await ExecuteAsync(connection, $"INSERT INTO game_versions (id,game_id,version_label,status,content_digest,content_path,manifest_json,runtime_config_json,compatibility_summary_json,created_by,created_at,published_at,state_version) VALUES ('{id}','game_legacy','current-{id}','PUBLISHED','{digest}','{path}','{{}}','{{}}','{{}}','usr_legacy',1,{publishedAt},0);");
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string ComputeDigest(string path, byte[] bytes)
    {
        string fileDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        byte[] aggregate = Encoding.UTF8.GetBytes($"{path}\0{bytes.LongLength}\0{fileDigest}\n");
        return $"sha256:{Convert.ToHexString(SHA256.HashData(aggregate)).ToLowerInvariant()}";
    }
}
