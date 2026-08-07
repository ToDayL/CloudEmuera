using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Persistence;

internal static class SqlitePathSecurity
{
    public static void EnsureNoSymlinkAncestors(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            EnsureNoSymlinkAncestorsOnLinux(path);
            return;
        }

        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? throw new SqlitePathException("Path has no filesystem root.");
        string relative = Path.GetRelativePath(root, fullPath);
        string current = root;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            DirectoryInfo directory = new(current);
            if (directory.Exists)
            {
                EnsureDirectoryIsSafe(current, "path component");
                continue;
            }

            FileInfo file = new(current);
            if (file.Exists || file.LinkTarget is not null)
            {
                EnsureFileIsSafe(current, "path component");
                continue;
            }

            break;
        }
    }

    public static void ValidateOptionalFile(string path, string description)
    {
        if (OperatingSystem.IsLinux())
        {
            if (!PathExists(path))
            {
                return;
            }

            string parentPath = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new SqlitePathException("The SQLite path has no parent directory.");
            string fileName = Path.GetFileName(path);
            using SafeFileHandle parentDirectory = LinuxFileOperations.OpenDirectory(parentPath);
            using SafeFileHandle? file = LinuxFileOperations.TryOpenRegularFileAt(parentDirectory, fileName, readOnly: true);
            return;
        }

        FileInfo info = new(path);
        if (info.Exists || info.LinkTarget is not null)
        {
            EnsureFileIsSafe(path, description);
        }
    }

    public static void ValidateOptionalDirectory(string path, string description)
    {
        if (OperatingSystem.IsLinux())
        {
            if (!PathExists(path))
            {
                return;
            }

            using SafeFileHandle directoryHandle = LinuxFileOperations.OpenDirectory(Path.GetFullPath(path));
            return;
        }

        DirectoryInfo directory = new(path);
        if (directory.Exists || directory.LinkTarget is not null)
        {
            EnsureDirectoryIsSafe(path, description);
            return;
        }

        FileInfo file = new(path);
        if (file.Exists || file.LinkTarget is not null)
        {
            throw new SqlitePathException($"The {description} must be a regular non-link directory.");
        }
    }

    private static void EnsureFileIsSafe(string path, string description)
    {
        FileInfo info = new(path);
        if (info.LinkTarget is not null
            || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            || Directory.Exists(path))
        {
            throw new SqlitePathException($"The {description} must be a regular non-link file.");
        }

    }

    private static void EnsureDirectoryIsSafe(string path, string description)
    {
        DirectoryInfo info = new(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SqlitePathException($"The {description} must be a regular non-link directory.");
        }
    }

    private static bool PathExists(string path)
    {
        FileInfo file = new(path);
        DirectoryInfo directory = new(path);
        return file.Exists || directory.Exists || file.LinkTarget is not null || directory.LinkTarget is not null;
    }

    private static void EnsureNoSymlinkAncestorsOnLinux(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? throw new SqlitePathException("Path has no filesystem root.");
        string relative = Path.GetRelativePath(root, fullPath);
        string current = root;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                using SafeFileHandle directory = LinuxFileOperations.OpenDirectory(current);
                continue;
            }

            if (PathExists(current))
            {
                ValidateOptionalFile(current, "path component");
                continue;
            }

            break;
        }
    }
}
