using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CloudEmuera.Infrastructure.Persistence;

public sealed class CloudEmueraDbContextFactory : IDesignTimeDbContextFactory<CloudEmueraDbContext>
{
    public CloudEmueraDbContext CreateDbContext(string[] args)
    {
        string databasePath = Path.Combine(Path.GetTempPath(), "cloudemuera-design-time.db");
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            databasePath = Path.GetFullPath(args[0]);
        }

        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString());

        DbContextOptions<CloudEmueraDbContext> options = new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
            .Options;
        return new CloudEmueraDbContext(options);
    }
}
