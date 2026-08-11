using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Api.Workers;

internal static class UnixSocketSecurity
{
    private const uint TypeMask = 0xF000;
    private const uint DirectoryType = 0x4000;
    private const uint SocketType = 0xC000;
    private const uint LinkType = 0xA000;
    private const uint PrivateDirectoryMode = 0x1C0;
    private const uint PrivateSocketMode = 0x180;

    public static void PreparePrivateTree(WorkerManagerOptions options)
    {
        EnsurePrivateDirectory(options.RuntimeDirectory, "runtime directory");
        EnsurePrivateDirectory(options.BootstrapDirectory, "bootstrap directory");
        string socket = options.ControlSocketPath;
        if (!TryReadMetadata(socket, out UnixMetadata metadata))
            return;
        if (metadata.Kind != UnixEntryKind.Socket)
            throw new IOException("The API Worker control endpoint already exists as a non-socket entry.");
        EnsureOwnedPrivateSocket(socket, metadata, "Worker control endpoint");
        try
        {
            using var probe = new Socket(AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(socket));
            throw new IOException("The API Worker control endpoint is already active.");
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.ConnectionRefused or SocketError.NotConnected)
        {
            File.Delete(socket);
        }
    }

    public static void SealSocket(string path)
    {
        if (!TryReadMetadata(path, out UnixMetadata metadata) || metadata.Kind != UnixEntryKind.Socket)
            throw new IOException("Kestrel did not create the API Worker control endpoint.");
        EnsureOwnedPrivateSocket(path, metadata, "Worker control endpoint");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (!TryReadMetadata(path, out metadata) || metadata.Kind != UnixEntryKind.Socket || metadata.Permissions != PrivateSocketMode)
            throw new UnauthorizedAccessException("The API Worker control endpoint is not private.");
    }

    public static void RemoveOwnedSocket(string path)
    {
        if (!TryReadMetadata(path, out UnixMetadata metadata) || metadata.Kind != UnixEntryKind.Socket)
            return;
        try
        {
            EnsureOwnedPrivateSocket(path, metadata, "owned Worker control endpoint");
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Never delete a path whose identity changed during cleanup.
        }
    }

    private static void EnsurePrivateDirectory(string path, string description)
    {
        EnsureNoSymlinkAncestors(path, description);
        if (!TryReadMetadata(path, out UnixMetadata metadata))
        {
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else if (metadata.Kind != UnixEntryKind.Directory)
        {
            throw new IOException($"The {description} is not a directory.");
        }

        if (!TryReadMetadata(path, out metadata) || metadata.Kind != UnixEntryKind.Directory || metadata.UserId != EffectiveUserId())
            throw new UnauthorizedAccessException($"The {description} is not owned by the service account.");
        if (OperatingSystem.IsLinux() && metadata.Permissions != PrivateDirectoryMode)
        {
            throw new UnauthorizedAccessException($"The {description} is not private.");
        }
    }

    private static void EnsureOwnedPrivateSocket(string path, UnixMetadata metadata, string description)
    {
        if (metadata.Kind != UnixEntryKind.Socket || metadata.UserId != EffectiveUserId() || metadata.LinkCount != 1)
            throw new UnauthorizedAccessException($"The {description} is not owned by the service account.");
        if (OperatingSystem.IsLinux() && metadata.Permissions != PrivateSocketMode)
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (!TryReadMetadata(path, out metadata) || metadata.Permissions != PrivateSocketMode)
                throw new UnauthorizedAccessException($"The {description} is not private.");
        }
    }

    private static void EnsureNoSymlinkAncestors(string path, string description)
    {
        string current = Path.GetFullPath(path);
        while (true)
        {
            if (TryReadMetadata(current, out UnixMetadata metadata))
            {
                if (metadata.Kind == UnixEntryKind.SymbolicLink ||
                    (metadata.Kind != UnixEntryKind.Directory && !string.Equals(current, path, StringComparison.Ordinal)))
                    throw new IOException($"The {description} path contains an unsafe ancestor.");
            }

            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
                return;
            current = parent;
        }
    }

    private static bool TryReadMetadata(string path, out UnixMetadata metadata)
    {
        metadata = default;
        if (!OperatingSystem.IsLinux())
        {
            FileSystemInfo info = new FileInfo(path);
            if (!info.Exists && info.LinkTarget is null)
                return false;
            metadata = new UnixMetadata(
                info.LinkTarget is not null ? UnixEntryKind.SymbolicLink : info is DirectoryInfo ? UnixEntryKind.Directory : UnixEntryKind.Regular,
                1,
                0,
                0);
            return true;
        }

        if (LStat(path, out UnixStat stat) != 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 2)
                return false;
            throw new IOException("The API Worker control path could not be inspected safely.");
        }

        metadata = new UnixMetadata(
            (stat.Mode & TypeMask) switch
            {
                DirectoryType => UnixEntryKind.Directory,
                SocketType => UnixEntryKind.Socket,
                LinkType => UnixEntryKind.SymbolicLink,
                _ => UnixEntryKind.Regular
            },
            stat.LinkCount,
            stat.UserId,
            stat.Mode & 0x0FFF);
        return true;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUserId();

    [SuppressMessage("Security", "CA2101", Justification = "The path is explicitly UTF-8 marshaled for lstat.")]
    [DllImport("libc", EntryPoint = "lstat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out UnixStat stat);

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
        public Timespec AccessTime;
        public Timespec ModifyTime;
        public Timespec ChangeTime;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    private readonly record struct UnixMetadata(UnixEntryKind Kind, ulong LinkCount, uint UserId, uint Permissions);

    private enum UnixEntryKind
    {
        Regular,
        Directory,
        Socket,
        SymbolicLink,
    }
}
