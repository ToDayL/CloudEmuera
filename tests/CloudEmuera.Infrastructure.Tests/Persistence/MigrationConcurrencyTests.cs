using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Tests.Support;

namespace CloudEmuera.Infrastructure.Tests.Persistence;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707", Justification = "P1-01 scenario names use separators for requirement mapping.")]
public sealed class MigrationConcurrencyTests
{
    [Fact]
    [Trait("Category", "Migration")]
    public async Task TwoMigrators_OnlyOneOwnsMigrationLock()
    {
        using TemporarySqliteDatabase database = new();
        await CreateProbeDatabaseAsync(database.DatabasePath);
        BlockingBackup firstBackup = new();
        DatabaseMigrationRunner first = new(database.Options, firstBackup);
        Task<MigrationResult> firstTask = first.MigrateAsync();
        await firstBackup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        MigrationResult secondResult = await new DatabaseMigrationRunner(database.Options).MigrateAsync();

        Assert.Equal(MigrationExitCodes.LockBusy, secondResult.ExitCode);
        firstBackup.Release();
        MigrationResult firstResult = await firstTask;
        Assert.True(firstResult.Succeeded, firstResult.ErrorCode);
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task MigrationLock_IsReusableAfterHandleRelease()
    {
        using TemporarySqliteDatabase database = new();
        SqliteDatabasePaths paths = database.Options.ResolvePaths(createDataRoot: true);
        Assert.Equal(MigrationLockStatus.Acquired, MigrationLock.TryAcquire(paths.MigrationLockPath, out MigrationLock? first));
        Assert.NotNull(first);
        Assert.Equal(MigrationLockStatus.Busy, MigrationLock.TryAcquire(paths.MigrationLockPath, out MigrationLock? second));
        Assert.Null(second);
        first!.Dispose();
        Assert.Equal(MigrationLockStatus.Acquired, MigrationLock.TryAcquire(paths.MigrationLockPath, out MigrationLock? third));
        third!.Dispose();
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task MigrationLock_ReplacementRaceNeverFollowsSymlinkTarget()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using TemporarySqliteDatabase database = new();
        string lockPath = Path.Combine(database.RootPath, "race.migration.lock");
        string targetPath = Path.Combine(database.RootPath, "sentinel");
        File.WriteAllText(targetPath, "must-remain-unchanged");
        UnixFileMode targetMode = UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead
            | UnixFileMode.OtherRead;
        File.SetUnixFileMode(targetPath, targetMode);

        using CancellationTokenSource cancellation = new();
        Task attacker = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    File.Delete(lockPath);
                    File.CreateSymbolicLink(lockPath, targetPath);
                    File.Delete(lockPath);
                }
                catch (IOException)
                {
                    // The lock holder can own an unlinked inode while the attacker cycles names.
                }
                catch (UnauthorizedAccessException)
                {
                    // The bounded race is best effort under restrictive filesystems.
                }

                await Task.Yield();
            }
        });

        try
        {
            for (int index = 0; index < 5_000; index++)
            {
                MigrationLockStatus status = MigrationLock.TryAcquire(lockPath, out MigrationLock? migrationLock);
                migrationLock?.Dispose();
                Assert.Contains(status, new[] { MigrationLockStatus.Acquired, MigrationLockStatus.Busy, MigrationLockStatus.Invalid });
            }
        }
        finally
        {
            cancellation.Cancel();
            await attacker;
            File.Delete(lockPath);
        }

        Assert.Equal("must-remain-unchanged", File.ReadAllText(targetPath));
        Assert.Equal(targetMode, File.GetUnixFileMode(targetPath));
    }

    private static async Task CreateProbeDatabaseAsync(string path)
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection = new(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE probe (id INTEGER PRIMARY KEY);";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class BlockingBackup : ISqliteOnlineBackup
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> CreateAsync(SqliteDatabaseOptions options, SqliteDatabasePaths paths, string migrationId, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            Entered.SetResult(true);
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Path.Combine(paths.BackupDirectoryPath, "injected.sqlite");
        }

        public void Release() => _release.SetResult(true);
    }
}
