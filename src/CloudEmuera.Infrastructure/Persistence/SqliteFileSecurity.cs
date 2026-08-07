using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Persistence;

internal static class SqliteFileSecurity
{
    public static void ApplyPrivateMode(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string fullPath = Path.GetFullPath(path);
        string parentPath = Path.GetDirectoryName(fullPath) ?? throw new SqlitePathException("The SQLite path has no parent directory.");
        using SafeFileHandle parentDirectory = LinuxFileOperations.OpenDirectory(parentPath);
        using SafeFileHandle? file = LinuxFileOperations.TryOpenRegularFileAt(parentDirectory, Path.GetFileName(fullPath), readOnly: false);
        if (file is not null)
        {
            ApplyPrivateMode(file);
        }
    }

    public static void ApplyPrivateModeToDatabase(string databasePath)
    {
        ApplyPrivateMode(databasePath);
        ApplyPrivateMode(databasePath + "-wal");
        ApplyPrivateMode(databasePath + "-shm");
    }

    public static void ApplyPrivateMode(SafeFileHandle file)
    {
        if (OperatingSystem.IsLinux())
        {
            LinuxFileOperations.ApplyPrivateMode(file);
        }
    }

    public static void ApplyPrivateModeAt(SafeFileHandle parentDirectory, string name)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using SafeFileHandle? file = LinuxFileOperations.TryOpenRegularFileAt(parentDirectory, name, readOnly: false);
        if (file is not null)
        {
            ApplyPrivateMode(file);
        }
    }
}
