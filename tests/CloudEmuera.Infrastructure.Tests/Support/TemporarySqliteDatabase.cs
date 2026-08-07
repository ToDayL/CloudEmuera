using CloudEmuera.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Tests.Support;

public sealed class TemporarySqliteDatabase : IDisposable
{
    public TemporarySqliteDatabase()
    {
        RootPath = Directory.CreateTempSubdirectory("cloudemuera-p1-01-").FullName;
        Options = new SqliteDatabaseOptions { DataRoot = RootPath };
    }

    public string RootPath { get; }

    public SqliteDatabaseOptions Options { get; }

    public string DatabasePath => Path.Combine(RootPath, Options.DatabaseName);

    public string BackupDirectoryPath => Path.Combine(RootPath, SqliteStorageConventions.BackupDirectoryName);

    public async Task<MigrationResult> MigrateAsync(ISqliteOnlineBackup? backup = null, CancellationToken cancellationToken = default)
    {
        DatabaseMigrationRunner runner = new(Options, backup);
        return await runner.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MigrationResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        DatabaseMigrationRunner runner = new(Options);
        return await runner.CheckAsync(cancellationToken).ConfigureAwait(false);
    }

    public DbContextScope OpenContext(SqliteConnectionAccess access = SqliteConnectionAccess.ReadWrite)
    {
        SqliteConnectionFactory connectionFactory = new(Options, createDataRoot: false);
        SqliteConnection connection = connectionFactory.OpenConnection(access);
        DbContextOptions<CloudEmueraDbContext> contextOptions = new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
            .Options;
        return new DbContextScope(new CloudEmueraDbContext(contextOptions), connection);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch
        {
            // Test cleanup must not hide the assertion or migration failure.
        }
    }
}

public sealed class DbContextScope(CloudEmueraDbContext context, SqliteConnection connection) : IAsyncDisposable
{
    public CloudEmueraDbContext Context { get; } = context;

    public SqliteConnection Connection { get; } = connection;

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}
