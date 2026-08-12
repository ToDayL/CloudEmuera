using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Tests.Support;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Tests.Persistence;

public sealed class SqliteConfigurationTests
{
    [Fact]
    [Trait("Category", "Migration")]
    public async Task FactoryEnablesForeignKeysBusyTimeoutAndWal()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await using DbContextScope scope = database.OpenContext();

        Assert.Equal(1, await ScalarIntAsync(scope.Connection, "PRAGMA foreign_keys;"));
        Assert.Equal(PersistenceLimits.DefaultBusyTimeoutMilliseconds, await ScalarIntAsync(scope.Connection, "PRAGMA busy_timeout;"));
        Assert.Equal("wal", (await ScalarStringAsync(scope.Connection, "PRAGMA journal_mode;")).ToLowerInvariant());
        AssertPrivate(database.DatabasePath);
        AssertPrivate(Path.Combine(database.RootPath, SqliteStorageConventions.DatabaseFileName + SqliteStorageConventions.MigrationLockSuffix));
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task ModelHasOnlyExplicitBusinessTablesAndRestrictiveForeignKeys()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await using DbContextScope scope = database.OpenContext();
        var entityTypes = scope.Context.Model.GetEntityTypes().ToArray();

        Assert.DoesNotContain(entityTypes, entity => entity.GetTableName()?.StartsWith("AspNet", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(entityTypes.SelectMany(entity => entity.GetProperties()), property => property.IsShadowProperty());
        Assert.All(entityTypes.SelectMany(entity => entity.GetForeignKeys()), foreignKey => Assert.NotEqual(DeleteBehavior.Cascade, foreignKey.DeleteBehavior));
        Assert.Equal(
            16,
            entityTypes.Count(entity => entity.GetTableName() is not null));
    }

    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public async Task ForeignKeysUseRestrictForUpdateAndDelete()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await using DbContextScope scope = database.OpenContext();
        const string sql = "SELECT on_update || ':' || on_delete FROM pragma_foreign_key_list('users') UNION ALL SELECT on_update || ':' || on_delete FROM pragma_foreign_key_list('games') UNION ALL SELECT on_update || ':' || on_delete FROM pragma_foreign_key_list('sessions') UNION ALL SELECT on_update || ':' || on_delete FROM pragma_foreign_key_list('worker_leases') UNION ALL SELECT on_update || ':' || on_delete FROM pragma_foreign_key_list('idempotency_records') UNION ALL SELECT on_update || ':' || on_delete FROM pragma_foreign_key_list('game_package_ingestions') UNION ALL SELECT on_update || ':' || on_delete FROM pragma_foreign_key_list('game_content_operations') UNION ALL SELECT on_update || ':' || on_delete FROM pragma_foreign_key_list('game_files') UNION ALL SELECT on_update || ':' || on_delete FROM pragma_foreign_key_list('compatibility_diagnostics');";
        await using Microsoft.Data.Sqlite.SqliteCommand command = scope.Connection.CreateCommand();
        command.CommandText = sql;
        await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();
        int count = 0;
        while (await reader.ReadAsync())
        {
            count++;
            Assert.True(reader.GetString(0) is "RESTRICT:RESTRICT" or "NO ACTION:RESTRICT");
        }

        Assert.True(count >= 12);
    }

    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public async Task DateTimeOffsetStoresUnixMillisecondsAndNormalizesToUtc()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        DateTimeOffset instant = new(2026, 8, 7, 8, 0, 0, TimeSpan.FromHours(8));
        await using (DbContextScope scope = database.OpenContext())
        {
            QuotaProfileRow profile = PersistenceFixtures.CreateQuotaProfile();
            profile.CreatedAt = instant;
            profile.UpdatedAt = instant;
            CloudEmueraUser user = PersistenceFixtures.CreateUser();
            user.CreatedAt = instant;
            user.UpdatedAt = instant;
            scope.Context.AddRange(profile, user);
            await scope.Context.SaveChangesAsync();
            Assert.Equal(instant.ToUnixTimeMilliseconds(), await ScalarLongAsync(scope.Connection, "SELECT created_at FROM users WHERE id = 'usr_fixture';"));
        }

        await using DbContextScope verify = database.OpenContext();
        CloudEmueraUser loaded = await verify.Context.Users.SingleAsync(user => user.Id == "usr_fixture");
        Assert.Equal(instant.ToUniversalTime(), loaded.CreatedAt);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAt.Offset);
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task DatabaseSymlinkParentSymlinkAndPathEscapeFailClosed()
    {
        using TemporarySqliteDatabase database = new();
        string target = Path.Combine(database.RootPath, "target.db");
        File.WriteAllBytes(target, []);
        File.CreateSymbolicLink(database.DatabasePath, target);
        MigrationResult databaseSymlinkResult = await new DatabaseMigrationRunner(database.Options).MigrateAsync();
        Assert.Equal(MigrationExitCodes.InvalidConfiguration, databaseSymlinkResult.ExitCode);

        string realRoot = Path.Combine(database.RootPath, "real-data");
        Directory.CreateDirectory(realRoot);
        string linkedRoot = Path.Combine(database.RootPath, "linked-data");
        File.CreateSymbolicLink(linkedRoot, realRoot);
        MigrationResult parentSymlinkResult = await new DatabaseMigrationRunner(new SqliteDatabaseOptions { DataRoot = linkedRoot }).MigrateAsync();
        Assert.Equal(MigrationExitCodes.InvalidConfiguration, parentSymlinkResult.ExitCode);

        MigrationResult escapedDatabaseResult = await new DatabaseMigrationRunner(new SqliteDatabaseOptions
        {
            DataRoot = database.RootPath,
            DatabaseName = "../outside.db",
        }).MigrateAsync();
        Assert.Equal(MigrationExitCodes.InvalidConfiguration, escapedDatabaseResult.ExitCode);
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task SpecialDatabaseFileAndSidecarSymlinkFailClosed()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using TemporarySqliteDatabase fifoDatabase = new();
        Assert.Equal(0, UnixNativeMethods.MkFifo(fifoDatabase.DatabasePath, 0x180));
        MigrationResult fifoResult = await fifoDatabase.MigrateAsync();
        Assert.Equal(MigrationExitCodes.InvalidConfiguration, fifoResult.ExitCode);

        using TemporarySqliteDatabase sidecarDatabase = new();
        string sidecarTarget = Path.Combine(sidecarDatabase.RootPath, "sidecar-target");
        File.WriteAllBytes(sidecarTarget, []);
        File.CreateSymbolicLink(sidecarDatabase.DatabasePath + "-wal", sidecarTarget);
        MigrationResult sidecarResult = await sidecarDatabase.MigrateAsync();
        Assert.Equal(MigrationExitCodes.InvalidConfiguration, sidecarResult.ExitCode);
    }

    [Fact]
    [Trait("Category", "Migration")]
    public void LinuxStatxClassifiesFileTypesWithoutArchitectureSpecificStructOffsets()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using TemporarySqliteDatabase database = new();
        File.WriteAllBytes(Path.Combine(database.RootPath, "regular-file"), []);
        Assert.Equal(0, UnixNativeMethods.MkFifo(Path.Combine(database.RootPath, "fifo-file"), 0x180));

        using SafeFileHandle parent = LinuxFileOperations.OpenDirectory(database.RootPath);
        using SafeFileHandle regular = LinuxFileOperations.OpenRegularFileAt(
            parent,
            "regular-file",
            readOnly: true,
            create: false,
            exclusive: false);
        LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(regular);
        Assert.True(identity.IsRegularFile);
        Assert.False(identity.IsDirectory);
        Assert.Throws<SqlitePathException>(() => LinuxFileOperations.OpenRegularFileAt(
            parent,
            "fifo-file",
            readOnly: false,
            create: false,
            exclusive: false));
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task DescriptorBackedSqliteOpenUsesGuardedInodeAfterDatabaseNameReplacement()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using TemporarySqliteDatabase database = new();
        string replacementPath = Path.Combine(database.RootPath, "replacement.db");
        await CreateMarkerDatabaseAsync(database.DatabasePath, "original");
        await CreateMarkerDatabaseAsync(replacementPath, "replacement");
        string movedOriginalPath = Path.Combine(database.RootPath, "original-moved.db");

        using SafeFileHandle parent = LinuxFileOperations.OpenDirectory(database.RootPath);
        using SafeFileHandle guard = LinuxFileOperations.OpenRegularFileAt(
            parent,
            Path.GetFileName(database.DatabasePath),
            readOnly: true,
            create: false,
            exclusive: false);
        string descriptorPath = LinuxFileOperations.GetProcFileDescriptorPath(guard);

        File.Move(database.DatabasePath, movedOriginalPath);
        File.Move(replacementPath, database.DatabasePath);
        try
        {
            await using Microsoft.Data.Sqlite.SqliteConnection connection = new(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = descriptorPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync();
            await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM marker;";
            Assert.Equal("original", await command.ExecuteScalarAsync());
        }
        finally
        {
            File.Delete(database.DatabasePath);
            File.Move(movedOriginalPath, database.DatabasePath);
        }
    }

    private static async Task CreateMarkerDatabaseAsync(string path, string value)
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection = new(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE marker (value TEXT NOT NULL); INSERT INTO marker (value) VALUES ($value);";
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarIntAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql) =>
        Convert.ToInt32(await ScalarAsync(connection, sql), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<long> ScalarLongAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql) =>
        Convert.ToInt64(await ScalarAsync(connection, sql), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> ScalarStringAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql) =>
        (string)(await ScalarAsync(connection, sql) ?? throw new InvalidOperationException("Expected SQLite scalar."));

    private static async Task<object?> ScalarAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static void AssertPrivate(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(path))
        {
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode & (UnixFileMode)0x1FF);
    }

    private static partial class UnixNativeMethods
    {
        [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "The Linux-only test creates a FIFO to verify special-file rejection.")]
        [SuppressMessage("Interoperability", "CA2101", Justification = "The libc mkfifo path is explicitly marshaled as UTF-8 for Linux.")]
        public static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    }
}
