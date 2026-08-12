using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;

namespace CloudEmuera.RuntimeAdapter;

public static partial class RuntimePathUtilities
{
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string NormalizeAbsolutePath(string candidate, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains('\0'))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                $"The trusted {parameterName} root is invalid.");
        }

        if (!Path.IsPathFullyQualified(candidate))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                $"The trusted {parameterName} root must be absolute.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.LayoutConflict,
                $"The trusted {parameterName} root cannot be normalized.",
                innerException: exception);
        }

        return TrimTrailingSeparators(fullPath);
    }

    public static bool IsSameOrWithin(string candidate, string root)
    {
        string normalizedCandidate = NormalizeForComparison(candidate);
        string normalizedRoot = NormalizeForComparison(root);

        if (string.Equals(normalizedCandidate, normalizedRoot, PathComparison))
        {
            return true;
        }

        string? filesystemRoot = Path.GetPathRoot(normalizedRoot);
        if (string.Equals(filesystemRoot, normalizedRoot, PathComparison))
        {
            return normalizedCandidate.StartsWith(normalizedRoot, PathComparison);
        }

        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            PathComparison);
    }

    public static bool IsStrictlyWithin(string candidate, string root) =>
        IsSameOrWithin(candidate, root) &&
        !string.Equals(NormalizeForComparison(candidate), NormalizeForComparison(root), PathComparison);

    public static bool PathsOverlap(string first, string second) =>
        IsSameOrWithin(first, second) || IsSameOrWithin(second, first);

    public static string Combine(string root, RuntimeRelativePath relativePath)
    {
        string candidate = root;
        foreach (string segment in relativePath.Segments)
        {
            candidate = Path.Combine(candidate, segment);
        }

        return Path.GetFullPath(candidate);
    }

    public static string TrimTrailingSeparators(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        int minimumLength = root.Length;
        int length = path.Length;
        while (length > minimumLength && IsDirectorySeparator(path[length - 1]))
        {
            length--;
        }

        return length == path.Length ? path : path[..length];
    }

    public static string NormalizeForComparison(string path) =>
        TrimTrailingSeparators(Path.GetFullPath(path));

    public static bool IsDirectorySeparator(char character) => character is '/' or '\\';

    public static void ThrowIfOutside(
        string candidate,
        string root,
        string logicalPath,
        RuntimeFileArea area)
    {
        if (!IsStrictlyWithin(candidate, root))
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.PathOutsideArea,
                "The logical path is outside its runtime area.",
                logicalPath,
                area);
        }
    }

    public static void ThrowIfReparsePoint(
        string path,
        string logicalPath,
        RuntimeFileArea? area = null,
        bool missingIsAllowed = true)
    {
        // GameContentCopyLeaseStore exposes an already-open Linux directory
        // through /proc/self/fd/N. The fd was opened with O_DIRECTORY and
        // O_NOFOLLOW; treating that kernel-owned link as a normal path
        // reparse point would force the materializer to reopen mutable
        // Game.current by name and lose the copy-lease guarantee.
        if (IsTrustedProcDirectoryFd(path))
        {
            if (!Directory.Exists(path))
            {
                throw new RuntimeFileAccessException(
                    RuntimePathReasonCodes.EntryNotFound,
                    "The trusted directory descriptor is no longer available.",
                    logicalPath,
                    area);
            }

            return;
        }

        if (TryGetUnixEntryType(path, out UnixEntryType unixEntryType) &&
            unixEntryType == UnixEntryType.SymbolicLink)
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.SymbolicLinkRejected,
                "Symbolic links and reparse points are not allowed in a runtime area.",
                logicalPath,
                area);
        }

        if (unixEntryType is UnixEntryType.Special)
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.UnsupportedRuntimeFile,
                "Special filesystem entries are not allowed in a runtime area.",
                logicalPath,
                area);
        }

        FileSystemInfo info = CreateFileSystemInfo(path);
        if (!info.Exists && info.LinkTarget is null)
        {
            if (missingIsAllowed)
            {
                return;
            }

            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.EntryNotFound,
                "The runtime entry does not exist.",
                logicalPath,
                area);
        }

        FileAttributes attributes = info.Attributes;
        if (info.LinkTarget is not null || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.SymbolicLinkRejected,
                "Symbolic links and reparse points are not allowed in a runtime area.",
                logicalPath,
                area);
        }

        if ((attributes & FileAttributes.Device) != 0)
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.UnsupportedRuntimeFile,
                "Device entries are not allowed in a runtime area.",
                logicalPath,
                area);
        }
    }

    public static void ValidateNoReparsePointsAlongPath(
        string path,
        string logicalPath,
        RuntimeFileArea? area = null)
    {
        if (IsTrustedProcDirectoryFd(path))
            return;

        string fullPath = Path.GetFullPath(path);
        var existingAncestors = new List<string>();
        DirectoryInfo? current = new DirectoryInfo(fullPath);

        while (current is not null)
        {
            FileSystemInfo info = CreateFileSystemInfo(current.FullName);
            if (info.Exists || info.LinkTarget is not null)
            {
                existingAncestors.Add(current.FullName);
            }

            current = current.Parent;
        }

        for (int index = existingAncestors.Count - 1; index >= 0; index--)
        {
            ThrowIfReparsePoint(existingAncestors[index], logicalPath, area);
        }
    }

    public static bool IsReparsePoint(string path)
    {
        FileSystemInfo info = CreateFileSystemInfo(path);
        return info.LinkTarget is not null ||
            info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    public static void ThrowIfHardLink(
        string path,
        string logicalPath,
        RuntimeFileArea? area = null)
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8)
        {
            return;
        }

        try
        {
            if (LStat(path, out UnixStat stat) == 0 && stat.LinkCount > 1)
            {
                throw new RuntimeFileAccessException(
                    RuntimePathReasonCodes.UnsupportedRuntimeFile,
                    "Hard-linked runtime content is not allowed.",
                    logicalPath,
                    area);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private static bool IsTrustedProcDirectoryFd(string path)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }

        const string prefix = "/proc/self/fd/";
        string descriptor = fullPath.StartsWith(prefix, StringComparison.Ordinal)
            ? fullPath[prefix.Length..]
            : string.Empty;
        if (descriptor.Length == 0 || descriptor.Contains(Path.DirectorySeparatorChar) ||
            !int.TryParse(descriptor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int descriptorNumber) ||
            descriptorNumber < 0)
            return false;

        // Only a live descriptor that currently resolves to a directory is
        // eligible for the copy-lease fast path. A syntax-only exception would
        // let an arbitrary /proc/self/fd/N value bypass reparse validation.
        return Directory.Exists($"{prefix}{descriptorNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private static FileSystemInfo CreateFileSystemInfo(string path)
    {
        FileSystemInfo directoryInfo = new DirectoryInfo(path);
        if (directoryInfo.Exists || directoryInfo.LinkTarget is not null)
        {
            return directoryInfo;
        }

        return new FileInfo(path);
    }

    private static bool TryGetUnixEntryType(string path, out UnixEntryType entryType)
    {
        entryType = default;
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8)
        {
            return false;
        }

        try
        {
            if (LStat(path, out UnixStat stat) != 0)
            {
                return false;
            }

            entryType = (stat.Mode & UnixFileTypeMask) switch
            {
                UnixRegularFile => UnixEntryType.RegularFile,
                UnixDirectory => UnixEntryType.Directory,
                UnixSymbolicLink => UnixEntryType.SymbolicLink,
                _ => UnixEntryType.Special
            };
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [SuppressMessage("Security", "CA2101", Justification = "libc lstat receives an explicit UTF-8 marshaled path.")]
    [DllImport("libc", EntryPoint = "lstat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out UnixStat stat);

    private enum UnixEntryType
    {
        RegularFile,
        Directory,
        SymbolicLink,
        Special
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnixStat
    {
        public ulong Device;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        public uint Padding;
        public ulong SpecialDevice;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public UnixTimespec AccessTime;
        public UnixTimespec ModifyTime;
        public UnixTimespec ChangeTime;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnixTimespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;
    private const uint UnixDirectory = 0x4000;
    private const uint UnixSymbolicLink = 0xA000;
}
