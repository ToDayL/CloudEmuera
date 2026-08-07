using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Persistence;

public enum MigrationLockStatus
{
    Acquired,
    Busy,
    Invalid,
}

public sealed class MigrationLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly SafeFileHandle? _parentDirectory;
    private bool _disposed;

    private MigrationLock(FileStream stream, string path, SafeFileHandle? parentDirectory)
    {
        _stream = stream;
        _parentDirectory = parentDirectory;
        Path = path;
    }

    public string Path { get; }

    public static MigrationLockStatus TryAcquire(string path, out MigrationLock? migrationLock)
    {
        migrationLock = null;
        if (OperatingSystem.IsLinux())
        {
            return TryAcquireOnLinux(path, out migrationLock);
        }

        try
        {
            ValidateLockPath(path);
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                return MigrationLockStatus.Busy;
            }

            try
            {
                SqliteFileSecurity.ApplyPrivateMode(path);
                migrationLock = new MigrationLock(stream, path, parentDirectory: null);
                return MigrationLockStatus.Acquired;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (SqlitePathException)
        {
            return MigrationLockStatus.Invalid;
        }
        catch (UnauthorizedAccessException)
        {
            return MigrationLockStatus.Invalid;
        }
    }

    private static MigrationLockStatus TryAcquireOnLinux(string path, out MigrationLock? migrationLock)
    {
        migrationLock = null;
        SafeFileHandle? parentDirectory = null;
        SafeFileHandle? file = null;
        try
        {
            ValidateLockPath(path);
            string fullPath = System.IO.Path.GetFullPath(path);
            string parentPath = System.IO.Path.GetDirectoryName(fullPath) ?? throw new SqlitePathException("The migration lock has no parent directory.");
            string fileName = System.IO.Path.GetFileName(fullPath);
            parentDirectory = LinuxFileOperations.OpenDirectory(parentPath);
            file = LinuxFileOperations.OpenRegularFileAt(parentDirectory, fileName, readOnly: false, create: true, exclusive: false);
            if (!LinuxFileOperations.TryAcquireExclusiveLock(file))
            {
                file.Dispose();
                file = null;
                parentDirectory.Dispose();
                parentDirectory = null;
                return MigrationLockStatus.Busy;
            }

            SqliteFileSecurity.ApplyPrivateMode(file);
            FileStream stream = LinuxFileOperations.CreateFileStream(file, FileAccess.ReadWrite, bufferSize: 1);
            file = null;
            migrationLock = new MigrationLock(stream, fullPath, parentDirectory);
            parentDirectory = null;
            return MigrationLockStatus.Acquired;
        }
        catch (SqlitePathException)
        {
            return MigrationLockStatus.Invalid;
        }
        catch (UnauthorizedAccessException)
        {
            return MigrationLockStatus.Invalid;
        }
        finally
        {
            file?.Dispose();
            parentDirectory?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        _parentDirectory?.Dispose();
    }

    private static void ValidateLockPath(string path)
    {
        SqlitePathSecurity.ValidateOptionalFile(path, "migration lock");
    }
}
