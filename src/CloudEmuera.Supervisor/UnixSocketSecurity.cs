using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Supervisor;

internal static class UnixSocketSecurity
{
    private const int ErrnoNoSuchFile = 2;
    private const int ErrnoNoEntry = 2;
    private const int AtSymlinkNoFollow = 0x100;
    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixDirectory = 0x4000;
    private const uint UnixSocket = 0xC000;
    private const uint UnixSymbolicLink = 0xA000;
    private const uint UnixPrivateDirectoryMode = 0x1C0;
    private const uint UnixPrivateSocketMode = 0x180;

    public static void EnsurePrivateDirectory(string path, string description)
    {
        string fullPath = Normalize(path, description);
        EnsureNoSymlinkOrSpecialAncestors(fullPath, description);

        bool created = !TryReadMetadata(fullPath, out UnixMetadata metadata);
        if (created)
        {
            Directory.CreateDirectory(fullPath);
            SetUnixMode(fullPath, UnixPrivateDirectoryMode);
        }
        else if (metadata.Kind != UnixEntryKind.Directory)
        {
            throw new IOException($"The Supervisor {description} is not a directory.");
        }

        if (!TryReadMetadata(fullPath, out metadata) ||
            metadata.Kind != UnixEntryKind.Directory ||
            metadata.UserId != GetEffectiveUserId())
        {
            throw new UnauthorizedAccessException($"The Supervisor {description} is not owned by the service account.");
        }

        if (!TryReadMetadata(fullPath, out metadata) ||
            metadata.Kind != UnixEntryKind.Directory ||
            metadata.UserId != GetEffectiveUserId() ||
            metadata.Permissions != UnixPrivateDirectoryMode)
        {
            throw new UnauthorizedAccessException($"The Supervisor {description} is not private.");
        }
    }

    internal static UnixMetadata RequirePrivateSocket(string path, string description)
    {
        string fullPath = Normalize(path, description);
        EnsureNoSymlinkOrSpecialAncestors(fullPath, description);
        if (!TryReadMetadata(fullPath, out UnixMetadata metadata) ||
            metadata.Kind != UnixEntryKind.Socket)
        {
            throw new IOException($"The Supervisor {description} is not a Unix socket.");
        }

        EnsureOwnedSocket(metadata, description);
        return metadata;
    }

    internal static void SetPrivateSocketMode(string path, string description)
    {
        UnixMetadata metadata = RequirePrivateSocket(path, description);
        _ = metadata;
        SetUnixMode(path, UnixPrivateSocketMode);
        metadata = RequirePrivateSocket(path, description);
        if (metadata.Permissions != UnixPrivateSocketMode)
        {
            throw new UnauthorizedAccessException($"The Supervisor {description} permissions are too broad.");
        }
    }

    internal static StaleSocketProbe ProbeStaleSocket(string path, UnixMetadata expected)
    {
        if (!TryReadMetadata(path, out UnixMetadata current))
            return StaleSocketProbe.Missing;
        if (current != expected || current.Kind != UnixEntryKind.Socket)
            return StaleSocketProbe.Unsafe;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return StaleSocketProbe.Active;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return StaleSocketProbe.Stale;
        }
        catch (SocketException)
        {
            return StaleSocketProbe.Unsafe;
        }
        catch (IOException)
        {
            return StaleSocketProbe.Unsafe;
        }
    }

    internal static bool RemoveOwnedSocket(string path, UnixMetadata expected, string description)
    {
        string fullPath = Normalize(path, description);
        string parentPath = Directory.GetParent(fullPath)?.FullName
            ?? throw new IOException($"The Supervisor {description} has no parent directory.");
        string leafName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(leafName) || !TryReadMetadata(parentPath, out UnixMetadata parentMetadata) ||
            parentMetadata.Kind != UnixEntryKind.Directory ||
            parentMetadata.UserId != GetEffectiveUserId() ||
            parentMetadata.Permissions != UnixPrivateDirectoryMode)
        {
            throw new UnauthorizedAccessException($"The Supervisor {description} parent directory is not private.");
        }

        using SafeFileHandle parentHandle = OpenPrivateDirectory(parentPath, description);
        if (!TryFStat(parentHandle, out UnixMetadata openedParent) || openedParent != parentMetadata)
        {
            throw new IOException($"The Supervisor {description} parent directory changed during cleanup.");
        }

        if (!TryFStatAt(parentHandle, leafName, out UnixMetadata current) ||
            current != expected ||
            current.Kind != UnixEntryKind.Socket)
        {
            return false;
        }

        EnsureOwnedSocket(current, description);
        if (UnlinkAt(parentHandle, leafName) == 0)
            return true;

        int error = Marshal.GetLastWin32Error();
        if (error is ErrnoNoSuchFile or ErrnoNoEntry)
            return false;
        throw new IOException($"The Supervisor {description} could not be removed safely.");
    }

    private static SafeFileHandle OpenPrivateDirectory(string path, string description)
    {
        int descriptor = OpenDirectoryHandle(path);
        if (descriptor < 0)
        {
            throw new IOException($"The Supervisor {description} parent directory could not be opened safely.");
        }

        return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
    }

    private static void EnsureOwnedSocket(UnixMetadata metadata, string description)
    {
        if (metadata.Kind != UnixEntryKind.Socket ||
            metadata.LinkCount != 1 ||
            metadata.UserId != GetEffectiveUserId())
        {
            throw new UnauthorizedAccessException($"The Supervisor {description} is not an owned single-link Unix socket.");
        }
    }

    private static void EnsureNoSymlinkOrSpecialAncestors(string path, string description)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if (TryReadMetadata(current, out UnixMetadata metadata))
            {
                if (metadata.Kind == UnixEntryKind.SymbolicLink)
                    throw new IOException($"The Supervisor {description} path contains a symbolic link.");
                if (metadata.Kind != UnixEntryKind.Directory && !string.Equals(current, path, PathComparison))
                    throw new IOException($"The Supervisor {description} path contains a non-directory ancestor.");
            }

            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, PathComparison))
                break;
            current = parent;
        }
    }

    private static string Normalize(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path))
            throw new IOException($"The Supervisor {description} path is invalid.");
        return Path.GetFullPath(path);
    }

    internal static bool TryReadMetadataForLifecycle(string path, out UnixMetadata metadata) =>
        TryReadMetadata(path, out metadata);

    private static bool TryReadMetadata(string path, out UnixMetadata metadata)
    {
        metadata = default;
        if (!OperatingSystem.IsLinux())
        {
            FileSystemInfo info = new FileInfo(path);
            if (!info.Exists && info.LinkTarget is null)
                return false;
            metadata = new UnixMetadata(
                info.LinkTarget is not null ? UnixEntryKind.SymbolicLink :
                info is DirectoryInfo ? UnixEntryKind.Directory : UnixEntryKind.RegularFile,
                LinkCount: 1,
                UserId: 0,
                Permissions: 0,
                Device: 0,
                Inode: 0);
            return true;
        }

        if (LStat(path, out UnixStat stat) == 0)
        {
            metadata = new UnixMetadata(
                Kind: (stat.Mode & UnixFileTypeMask) switch
                {
                    UnixDirectory => UnixEntryKind.Directory,
                    UnixSocket => UnixEntryKind.Socket,
                    UnixSymbolicLink => UnixEntryKind.SymbolicLink,
                    _ => UnixEntryKind.RegularFile
                },
                LinkCount: stat.LinkCount,
                UserId: stat.UserId,
                Permissions: stat.Mode & 0x0FFF,
                Device: stat.Device,
                Inode: stat.Inode);
            return true;
        }

        int error = Marshal.GetLastWin32Error();
        if (error is ErrnoNoSuchFile or ErrnoNoEntry)
            return false;
        throw new IOException("The Supervisor path could not be inspected safely.");
    }

    private static bool TryFStat(SafeFileHandle handle, out UnixMetadata metadata)
    {
        metadata = default;
        if (!OperatingSystem.IsLinux() || handle.IsInvalid || FStat(handle.DangerousGetHandle().ToInt32(), out UnixStat stat) != 0)
            return false;
        metadata = FromStat(stat);
        return true;
    }

    private static bool TryFStatAt(SafeFileHandle parent, string leafName, out UnixMetadata metadata)
    {
        metadata = default;
        if (!OperatingSystem.IsLinux() ||
            FStatAt(parent.DangerousGetHandle().ToInt32(), leafName, out UnixStat stat, AtSymlinkNoFollow) != 0)
        {
            return false;
        }

        metadata = FromStat(stat);
        return true;
    }

    private static UnixMetadata FromStat(UnixStat stat) => new(
        Kind: (stat.Mode & UnixFileTypeMask) switch
        {
            UnixDirectory => UnixEntryKind.Directory,
            UnixSocket => UnixEntryKind.Socket,
            UnixSymbolicLink => UnixEntryKind.SymbolicLink,
            _ => UnixEntryKind.RegularFile
        },
        LinkCount: stat.LinkCount,
        UserId: stat.UserId,
        Permissions: stat.Mode & 0x0FFF,
        Device: stat.Device,
        Inode: stat.Inode);

    private static void SetUnixMode(string path, uint permissions)
    {
        if (!OperatingSystem.IsLinux())
            return;
        File.SetUnixFileMode(path, (UnixFileMode)permissions);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [SuppressMessage("Security", "CA2101", Justification = "All native paths use explicit UTF-8 marshaling.")]
    [DllImport("libc", EntryPoint = "lstat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out UnixStat stat);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [SuppressMessage("Security", "CA2101", Justification = "The directory path is explicitly UTF-8 marshaled.")]
    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int OpenDirectoryHandle([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags = OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int descriptor, out UnixStat stat);

    [SuppressMessage("Security", "CA2101", Justification = "The leaf name is explicitly UTF-8 marshaled and used with fstatat.")]
    [DllImport("libc", EntryPoint = "fstatat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int FStatAt(
        int descriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out UnixStat stat,
        int flags);

    [SuppressMessage("Security", "CA2101", Justification = "The leaf name is explicitly UTF-8 marshaled and used with unlinkat.")]
    [DllImport("libc", EntryPoint = "unlinkat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnlinkAt(
        SafeFileHandle descriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags = 0);

    internal readonly record struct UnixMetadata(
        UnixEntryKind Kind,
        ulong LinkCount,
        uint UserId,
        uint Permissions,
        ulong Device,
        ulong Inode);

    internal enum StaleSocketProbe
    {
        Missing,
        Active,
        Stale,
        Unsafe
    }

    internal enum UnixEntryKind
    {
        RegularFile,
        Directory,
        Socket,
        SymbolicLink
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
}
