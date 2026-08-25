using CloudEmuera.Infrastructure.Persistence;

namespace CloudEmuera.Infrastructure.Games;

internal sealed record ScannedGameTree(
    string? ContentDigest,
    int FileCount,
    long TotalBytes);

internal static class GameContentTreeScanner
{
    public static ScannedGameTree Scan(string root)
    {
        if (!Directory.Exists(root)) throw new IOException("The game content directory is missing.");
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((new DirectoryInfo(root), 0));
        int entryCount = 0;
        int fileCount = 0;
        long total = 0;
        while (pending.Count > 0)
        {
            (DirectoryInfo directory, int depth) = pending.Pop();
            Reject(directory);
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos().OrderByDescending(value => value.Name, StringComparer.Ordinal))
            {
                if (++entryCount > GameContentScanLimits.MaxEntryCount)
                    throw new GameContentLimitException("GAME_CONTENT_ENTRY_LIMIT");
                Reject(entry);
                string logical = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                if (logical.StartsWith(".cloudemuera-", StringComparison.Ordinal)) continue;
                if (entry is DirectoryInfo child)
                {
                    if (depth >= GameContentScanLimits.MaxDirectoryDepth)
                        throw new GameContentLimitException("GAME_CONTENT_DEPTH_LIMIT");
                    pending.Push((child, depth + 1));
                }
                else if (entry is FileInfo file)
                {
                    if (file.Length > GameContentScanLimits.MaxSingleFileBytes)
                        throw new GameContentLimitException("GAME_CONTENT_FILE_LIMIT");
                    total = checked(total + file.Length);
                    if (total > GameContentScanLimits.MaxTotalBytes)
                        throw new GameContentLimitException("GAME_CONTENT_TOTAL_LIMIT");
                    using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (LinuxFileOperations.ReadIdentity(handle).LinkCount != 1)
                        throw new IOException("The game content tree contains a hard link.");
                    fileCount++;
                }
                else throw new IOException("The game content tree contains a special file.");
            }
        }
        return new(null, fileCount, total);
    }

    private static void Reject(FileSystemInfo info)
    {
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The game content tree contains a link or disappeared entry.");
    }
}
