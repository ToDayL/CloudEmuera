using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Persistence;

public interface ISqliteOnlineBackup
{
    Task<string> CreateAsync(
        SqliteDatabaseOptions options,
        SqliteDatabasePaths paths,
        string migrationId,
        TimeProvider timeProvider,
        CancellationToken cancellationToken);
}

public sealed class SqliteOnlineBackup : ISqliteOnlineBackup
{
    public async Task<string> CreateAsync(
        SqliteDatabaseOptions options,
        SqliteDatabasePaths paths,
        string migrationId,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return OperatingSystem.IsLinux()
            ? await CreateOnLinuxAsync(options, paths, migrationId, timeProvider, cancellationToken).ConfigureAwait(false)
            : await CreatePortableAsync(options, paths, migrationId, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> CreatePortableAsync(
        SqliteDatabaseOptions options,
        SqliteDatabasePaths paths,
        string migrationId,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(paths.BackupDirectoryPath);
        SqlitePathSecurity.ValidateOptionalDirectory(paths.BackupDirectoryPath, "backup directory");

        DateTimeOffset timestamp = timeProvider.GetUtcNow().ToUniversalTime();
        string safeMigrationId = SanitizeMigrationId(migrationId);
        string fileName = $"{Path.GetFileName(paths.DatabasePath)}.before-{timestamp:yyyyMMdd'T'HHmmssfff'Z'}-{safeMigrationId}.sqlite";
        string finalPath = Path.Combine(paths.BackupDirectoryPath, fileName);
        try
        {
            SqlitePathSecurity.ValidateOptionalFile(finalPath, "backup file");
        }
        catch (SqlitePathException exception)
        {
            throw new SqliteBackupException("A backup with the same name already exists or is unsafe.", exception);
        }

        string temporaryPath = Path.Combine(paths.BackupDirectoryPath, $".{fileName}.tmp-{Guid.CreateVersion7():N}");
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                SqliteFileSecurity.ApplyPrivateMode(temporaryPath);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            SqliteConnectionStringBuilder sourceConnectionString = new()
            {
                DataSource = paths.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeoutMilliseconds / 1000d)),
            };
            SqliteConnectionStringBuilder destinationConnectionString = new()
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeoutMilliseconds / 1000d)),
            };

            await using (SqliteConnection source = new(sourceConnectionString.ToString()))
            await using (SqliteConnection destination = new(destinationConnectionString.ToString()))
            {
                source.Open();
                destination.Open();
                SetDeleteJournalMode(destination);
                source.BackupDatabase(destination);
                ExecuteNonQuery(destination, "PRAGMA wal_checkpoint(TRUNCATE);");
                SetDeleteJournalMode(destination);
            }

            await using (FileStream stream = new(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await using (SqliteConnection verification = new(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString()))
            {
                verification.Open();
                await SqliteIntegrityChecker.VerifyAsync(verification, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            DeleteTemporarySidecars(temporaryPath);
            File.Move(temporaryPath, finalPath, overwrite: false);
            SqliteFileSecurity.ApplyPrivateMode(finalPath);
            return finalPath;
        }
        catch (OperationCanceledException)
        {
            TryDeleteTemporary(temporaryPath);
            throw;
        }
        catch (SqliteBackupException)
        {
            TryDeleteTemporary(temporaryPath);
            throw;
        }
        catch (Exception exception)
        {
            TryDeleteTemporary(temporaryPath);
            throw new SqliteBackupException("SQLite online backup failed.", exception);
        }
    }

    private static async Task<string> CreateOnLinuxAsync(
        SqliteDatabaseOptions options,
        SqliteDatabasePaths paths,
        string migrationId,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using SafeFileHandle dataRoot = LinuxFileOperations.OpenDirectory(paths.DataRoot);
        using SafeFileHandle backupDirectory = LinuxFileOperations.OpenOrCreateDirectoryAt(
            dataRoot,
            Path.GetFileName(paths.BackupDirectoryPath));

        DateTimeOffset timestamp = timeProvider.GetUtcNow().ToUniversalTime();
        string databaseName = Path.GetFileName(paths.DatabasePath);
        string safeMigrationId = SanitizeMigrationId(migrationId);
        string fileName = $"{databaseName}.before-{timestamp:yyyyMMdd'T'HHmmssfff'Z'}-{safeMigrationId}.sqlite";
        string finalPath = Path.Combine(paths.BackupDirectoryPath, fileName);
        string temporaryName = $".{fileName}.tmp-{Guid.CreateVersion7():N}";
        string temporaryPath = Path.Combine(paths.BackupDirectoryPath, temporaryName);

        SafeFileHandle? sourceGuard = null;
        SafeFileHandle? sourceWalGuard = null;
        SafeFileHandle? sourceShmGuard = null;
        SafeFileHandle? temporaryHandle = null;
        FileStream? temporaryStream = null;
        bool finalLinked = false;
        bool finalized = false;
        try
        {
            try
            {
                using SafeFileHandle? existingFinal = LinuxFileOperations.TryOpenRegularFileAt(
                    backupDirectory,
                    fileName,
                    readOnly: true);
            }
            catch (SqlitePathException exception)
            {
                throw new SqliteBackupException("A backup with the same name already exists or is unsafe.", exception);
            }

            sourceGuard = LinuxFileOperations.OpenRegularFileAt(
                dataRoot,
                databaseName,
                readOnly: true,
                create: false,
                exclusive: false);
            sourceWalGuard = LinuxFileOperations.TryOpenRegularFileAt(dataRoot, databaseName + "-wal", readOnly: true);
            sourceShmGuard = LinuxFileOperations.TryOpenRegularFileAt(dataRoot, databaseName + "-shm", readOnly: true);

            temporaryHandle = LinuxFileOperations.OpenRegularFileAt(
                backupDirectory,
                temporaryName,
                readOnly: false,
                create: true,
                exclusive: true);
            SqliteFileSecurity.ApplyPrivateMode(temporaryHandle);
            temporaryStream = LinuxFileOperations.CreateFileStream(temporaryHandle, FileAccess.ReadWrite);
            temporaryHandle = null;
            await temporaryStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            SqliteConnectionStringBuilder sourceConnectionString = new()
            {
                // SQLite must open the guarded source descriptor, not re-resolve the database name.
                DataSource = LinuxFileOperations.GetProcFileDescriptorPath(sourceGuard!),
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeoutMilliseconds / 1000d)),
            };
            SqliteConnectionStringBuilder destinationConnectionString = new()
            {
                // The temporary stream pins the destination inode while SQLite opens it.
                DataSource = LinuxFileOperations.GetProcFileDescriptorPath(temporaryStream!.SafeFileHandle),
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeoutMilliseconds / 1000d)),
            };

            await using (SqliteConnection source = new(sourceConnectionString.ToString()))
            await using (SqliteConnection destination = new(destinationConnectionString.ToString()))
            {
                source.Open();
                destination.Open();
                EnsureSourcePathsUnchanged(dataRoot, databaseName, sourceGuard!, ref sourceWalGuard, ref sourceShmGuard);
                using (SafeFileHandle temporaryBeforeBackup = LinuxFileOperations.TryOpenRegularFileAt(
                    backupDirectory,
                    temporaryName,
                    readOnly: true)
                    ?? throw new SqlitePathException("The temporary backup changed during setup."))
                {
                    LinuxFileOperations.EnsureSameIdentity(temporaryStream.SafeFileHandle, temporaryBeforeBackup);
                }
                SetDeleteJournalMode(destination);
                source.BackupDatabase(destination);
                SqliteFileSecurity.ApplyPrivateModeAt(backupDirectory, temporaryName + "-wal");
                SqliteFileSecurity.ApplyPrivateModeAt(backupDirectory, temporaryName + "-shm");
                ExecuteNonQuery(destination, "PRAGMA wal_checkpoint(TRUNCATE);");
                SetDeleteJournalMode(destination);
                EnsureSourcePathsUnchanged(dataRoot, databaseName, sourceGuard!, ref sourceWalGuard, ref sourceShmGuard);
                using SafeFileHandle temporaryAfterBackup = LinuxFileOperations.TryOpenRegularFileAt(
                    backupDirectory,
                    temporaryName,
                    readOnly: true)
                    ?? throw new SqlitePathException("The temporary backup disappeared during backup.");
                LinuxFileOperations.EnsureSameIdentity(temporaryStream.SafeFileHandle, temporaryAfterBackup);
            }

            await temporaryStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            temporaryStream.Flush(flushToDisk: true);

            await using (SqliteConnection verification = new(new SqliteConnectionStringBuilder
            {
                DataSource = LinuxFileOperations.GetProcFileDescriptorPath(temporaryStream!.SafeFileHandle),
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString()))
            {
                verification.Open();
                using SafeFileHandle temporaryBeforeVerification = LinuxFileOperations.TryOpenRegularFileAt(
                    backupDirectory,
                    temporaryName,
                    readOnly: true)
                    ?? throw new SqlitePathException("The temporary backup disappeared before verification.");
                LinuxFileOperations.EnsureSameIdentity(temporaryStream.SafeFileHandle, temporaryBeforeVerification);
                await SqliteIntegrityChecker.VerifyAsync(verification, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            UnlinkTemporarySidecars(backupDirectory, temporaryName);
            LinuxFileOperations.LinkAtFromName(backupDirectory, temporaryName, backupDirectory, fileName);
            finalLinked = true;
            using (SafeFileHandle finalFile = LinuxFileOperations.TryOpenRegularFileAt(backupDirectory, fileName, readOnly: true)
                ?? throw new SqlitePathException("The finalized backup disappeared during publication."))
            {
                LinuxFileOperations.EnsureSameIdentity(temporaryStream.SafeFileHandle, finalFile);
            }
            LinuxFileOperations.UnlinkAtIfExists(backupDirectory, temporaryName);
            finalized = true;
            return finalPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteBackupException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SqliteBackupException("SQLite online backup failed.", exception);
        }
        finally
        {
            if (!finalized && finalLinked)
            {
                TryUnlink(backupDirectory, fileName);
            }

            if (!finalized)
            {
                TryUnlinkTemporary(backupDirectory, temporaryName);
            }

            if (temporaryStream is not null)
            {
                await temporaryStream.DisposeAsync().ConfigureAwait(false);
            }

            temporaryHandle?.Dispose();
            sourceShmGuard?.Dispose();
            sourceWalGuard?.Dispose();
            sourceGuard?.Dispose();
        }
    }

    private static string SanitizeMigrationId(string migrationId)
    {
        if (string.IsNullOrWhiteSpace(migrationId))
        {
            throw new SqliteBackupException("Migration id is required for a backup name.");
        }

        Span<char> buffer = stackalloc char[migrationId.Length];
        int count = 0;
        foreach (char character in migrationId)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
            {
                buffer[count++] = character;
            }
        }

        if (count == 0)
        {
            throw new SqliteBackupException("Migration id cannot produce a safe backup name.");
        }

        return new string(buffer[..count]);
    }

    private static void TryDeleteTemporary(string path)
    {
        foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(candidate) || new FileInfo(candidate).LinkTarget is not null)
                {
                    File.Delete(candidate);
                }
            }
            catch
            {
                // The original backup failure is more useful than cleanup failure.
            }
        }
    }

    private static void DeleteTemporarySidecars(string path)
    {
        foreach (string candidate in new[] { path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate) || new FileInfo(candidate).LinkTarget is not null)
            {
                File.Delete(candidate);
            }
        }
    }

    private static void EnsureSourcePathsUnchanged(
        SafeFileHandle dataRoot,
        string databaseName,
        SafeFileHandle source,
        ref SafeFileHandle? sourceWal,
        ref SafeFileHandle? sourceShm)
    {
        using SafeFileHandle currentSource = LinuxFileOperations.TryOpenRegularFileAt(dataRoot, databaseName, readOnly: true)
            ?? throw new SqlitePathException("The SQLite database disappeared during backup.");
        LinuxFileOperations.EnsureSameIdentity(source, currentSource);
        sourceWal = EnsureOptionalPathUnchanged(dataRoot, databaseName + "-wal", sourceWal);
        sourceShm = EnsureOptionalPathUnchanged(dataRoot, databaseName + "-shm", sourceShm);
    }

    private static SafeFileHandle? EnsureOptionalPathUnchanged(
        SafeFileHandle directory,
        string name,
        SafeFileHandle? expected)
    {
        SafeFileHandle? actual = LinuxFileOperations.TryOpenRegularFileAt(directory, name, readOnly: true);
        if (expected is null)
        {
            return actual;
        }

        if (actual is null)
        {
            throw new SqlitePathException("A SQLite sidecar disappeared during backup.");
        }

        try
        {
            LinuxFileOperations.EnsureSameIdentity(expected, actual);
            return expected;
        }
        finally
        {
            actual.Dispose();
        }
    }

    private static void TryUnlinkTemporary(SafeFileHandle backupDirectory, string temporaryName)
    {
        try
        {
            UnlinkTemporarySidecars(backupDirectory, temporaryName);
            LinuxFileOperations.UnlinkAtIfExists(backupDirectory, temporaryName);
        }
        catch
        {
            // The original backup failure is more useful than cleanup failure.
        }
    }

    private static void TryUnlink(SafeFileHandle directory, string name)
    {
        try
        {
            LinuxFileOperations.UnlinkAtIfExists(directory, name);
        }
        catch
        {
            // The original backup failure is more useful than cleanup failure.
        }
    }

    private static void UnlinkTemporarySidecars(SafeFileHandle backupDirectory, string temporaryName)
    {
        LinuxFileOperations.UnlinkAtIfExists(backupDirectory, temporaryName + "-wal");
        LinuxFileOperations.UnlinkAtIfExists(backupDirectory, temporaryName + "-shm");
    }

    private static void SetDeleteJournalMode(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = DELETE;";
        string journalMode = command.ExecuteScalar()?.ToString() ?? string.Empty;
        if (!string.Equals(journalMode, "delete", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteBackupException("Backup database could not leave WAL mode.");
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

public sealed class SqliteBackupException(string message, Exception? innerException = null) : Exception(message, innerException);
