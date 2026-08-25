namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Bounded reservation statistics for a GameContent root. This is an
/// ephemeral quota calculation only; it is deliberately not a manifest and
/// is never persisted or used as a runtime allowlist.
/// </summary>
public sealed record SessionRootContentStatistics(
    long FileCount,
    long DirectoryCount,
    long TotalBytes)
{
    public static SessionRootContentStatistics FromDirectory(string gameContentRoot)
    {
        string root = RuntimePathUtilities.NormalizeAbsolutePath(gameContentRoot, nameof(gameContentRoot));
        RuntimePathUtilities.ThrowIfReparsePoint(root, "<game-content-root>", RuntimeFileArea.GameContent, false);
        if (!Directory.Exists(root))
            throw new RuntimePathException(RuntimePathReasonCodes.EntryNotFound, "The GameContent root does not exist.", "<game-content-root>");

        var state = new ScanState();
        ScanDirectory(root, root, state);
        return new SessionRootContentStatistics(state.FileCount, state.DirectoryCount, state.TotalBytes);
    }

    private static void ScanDirectory(string root, string current, ScanState state)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(current).EnumerateFileSystemInfos()
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
            RuntimePathUtilities.ThrowIfReparsePoint(entry.FullName, relative, RuntimeFileArea.GameContent, false);
            if (entry is DirectoryInfo)
            {
                state.DirectoryCount = checked(state.DirectoryCount + 1);
                ScanDirectory(root, entry.FullName, state);
            }
            else if (entry is FileInfo file)
            {
                RuntimePathUtilities.ThrowIfHardLink(file.FullName, relative, RuntimeFileArea.GameContent);
                state.FileCount = checked(state.FileCount + 1);
                state.TotalBytes = checked(state.TotalBytes + file.Length);
            }
            else
            {
                throw new RuntimeFileAccessException(
                    RuntimePathReasonCodes.UnsupportedRuntimeFile,
                    "The GameContent contains a non-regular filesystem entry.",
                    relative,
                    RuntimeFileArea.GameContent);
            }
        }
    }

    private sealed class ScanState
    {
        public long FileCount { get; set; }
        public long DirectoryCount { get; set; }
        public long TotalBytes { get; set; }
    }
}
