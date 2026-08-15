using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Persistence;

internal static class LinuxFileOperations
{
    private const int OpenReadOnly = 0;
    private const int OpenReadWrite = 2;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenNonBlocking = 0x800;
    private const int OpenDirectoryFlag = 0x10000;
    private const int OpenCloseOnExec = 0x80000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenPath = 0x200000;
    private const int AtRemoveDirectory = 0x200;
    private const int AtEmptyPath = 0x1000;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int ErrnoNoEntry = 2;
    private const int ErrnoExist = 17;
    private const int ErrnoAgain = 11;
    private const int ErrnoWouldBlock = 11;
    private const int ErrnoLoop = 40;
    private const int ErrnoNotDirectory = 20;
    private const int ErrnoIsDirectory = 21;
    private const int ErrnoNoSys = 38;
    private const int RegularFileType = 0x8000;
    private const int DirectoryFileType = 0x4000;
    private const uint StatxType = 0x00000001;
    private const uint StatxBasicStats = 0x000007ff;
    private const int StatxBufferSize = 256;

    public static SafeFileHandle OpenDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? throw new SqlitePathException("The SQLite parent path has no filesystem root.");
        SafeFileHandle handle = CreateHandle(
            NativeMethods.Open(root, OpenReadOnly | OpenDirectoryFlag | OpenCloseOnExec, 0),
            "open directory root");
        try
        {
            string relative = Path.GetRelativePath(root, fullPath);
            foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                SafeFileHandle next = TryOpenDirectoryAt(handle, segment)
                    ?? throw new SqlitePathException("The SQLite parent path disappeared during secure traversal.");
                handle.Dispose();
                handle = next;
            }

            FileIdentity identity = ReadIdentity(handle);
            if (!identity.IsDirectory)
            {
                throw new SqlitePathException("The SQLite parent path must be a directory.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static SafeFileHandle OpenOrCreateDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? throw new SqlitePathException("The SQLite parent path has no filesystem root.");
        SafeFileHandle handle = CreateHandle(
            NativeMethods.Open(root, OpenReadOnly | OpenDirectoryFlag | OpenCloseOnExec, 0),
            "open directory root");
        try
        {
            string relative = Path.GetRelativePath(root, fullPath);
            foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                SafeFileHandle? next = TryOpenDirectoryAt(handle, segment);
                if (next is null)
                {
                    if (NativeMethods.MkdirAt(handle, segment, 0x1C0) != 0)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        if (error != ErrnoExist)
                        {
                            throw CreateError("mkdirat directory path", error);
                        }
                    }

                    next = TryOpenDirectoryAt(handle, segment)
                        ?? throw new SqlitePathException("The SQLite directory disappeared during creation.");
                    if (NativeMethods.Fchmod(next, 0x1C0) != 0)
                    {
                        next.Dispose();
                        throw CreateError("fchmod directory path", Marshal.GetLastPInvokeError());
                    }
                }

                handle.Dispose();
                handle = next;
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static SafeFileHandle OpenOrCreateDirectoryAt(SafeFileHandle parentDirectory, string name)
    {
        ValidateLeafName(name);
        SafeFileHandle? existing = TryOpenDirectoryAt(parentDirectory, name);
        if (existing is not null)
        {
            return existing;
        }

        if (NativeMethods.MkdirAt(parentDirectory, name, 0x1C0) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != ErrnoExist)
            {
                throw CreateError("mkdirat", error);
            }
        }

        SafeFileHandle created = TryOpenDirectoryAt(parentDirectory, name)
            ?? throw new SqlitePathException("The SQLite directory disappeared during creation.");
        if (NativeMethods.Fchmod(created, 0x1C0) != 0)
        {
            created.Dispose();
            throw CreateError("fchmod directory", Marshal.GetLastPInvokeError());
        }
        return created;
    }

    public static SafeFileHandle CreateDirectoryAt(SafeFileHandle parentDirectory, string name)
    {
        ValidateLeafName(name);
        if (NativeMethods.MkdirAt(parentDirectory, name, 0x1C0) != 0)
        {
            throw CreateError("mkdirat exclusive", Marshal.GetLastPInvokeError());
        }

        SafeFileHandle created = TryOpenDirectoryAt(parentDirectory, name)
            ?? throw new IOException("The directory disappeared during creation.");
        try
        {
            if (NativeMethods.Fchmod(created, 0x1C0) != 0)
            {
                throw CreateError("fchmod directory", Marshal.GetLastPInvokeError());
            }
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    public static SafeFileHandle? TryOpenDirectoryAt(SafeFileHandle parentDirectory, string name)
    {
        ValidateLeafName(name);
        int descriptor = NativeMethods.OpenAt(parentDirectory, name, OpenReadOnly | OpenDirectoryFlag | OpenCloseOnExec | OpenNoFollow, 0);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrnoNoEntry)
            {
                return null;
            }

            throw CreatePathError("openat directory", error);
        }

        SafeFileHandle handle = new((IntPtr)descriptor, ownsHandle: true);
        try
        {
            FileIdentity identity = ReadIdentity(handle);
            if (!identity.IsDirectory)
            {
                throw new SqlitePathException("The SQLite directory must be a directory.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static SafeFileHandle DuplicateDirectory(SafeFileHandle directory)
    {
        int descriptor = NativeMethods.OpenAt(directory, ".", OpenReadOnly | OpenDirectoryFlag | OpenCloseOnExec | OpenNoFollow, 0);
        return CreateHandle(descriptor, "openat duplicate directory");
    }

    public static SafeFileHandle OpenEntryAt(SafeFileHandle parentDirectory, string name)
    {
        ValidateLeafName(name);
        return OpenPathAt(parentDirectory, name);
    }

    public static SafeFileHandle OpenDirectoryPath(SafeFileHandle root, string logicalPath, bool create)
    {
        SafeFileHandle current = DuplicateDirectory(root);
        try
        {
            foreach (string segment in logicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                ValidateLeafName(segment);
                SafeFileHandle next = create
                    ? OpenOrCreateDirectoryAt(current, segment)
                    : TryOpenDirectoryAt(current, segment)
                        ?? throw new IOException("The directory path is missing.");
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    public static void CopyTree(SafeFileHandle sourceDirectory, SafeFileHandle destinationParent, string destinationName)
    {
        using SafeFileHandle destination = CreateDirectoryAt(destinationParent, destinationName);
        CopyDirectoryContents(sourceDirectory, destination);
        Sync(destination);
        Sync(destinationParent);
    }

    private static void CopyDirectoryContents(SafeFileHandle sourceDirectory, SafeFileHandle destinationDirectory)
    {
        string descriptorPath = GetProcFileDescriptorPath(sourceDirectory);
        foreach (string path in Directory.EnumerateFileSystemEntries(descriptorPath).Order(StringComparer.Ordinal))
        {
            string leaf = Path.GetFileName(path);
            using SafeFileHandle entry = OpenEntryAt(sourceDirectory, leaf);
            FileIdentity identity = ReadIdentity(entry);
            if (identity.UserId != CurrentUserId) throw new IOException("Refusing to copy an entry owned by another user.");
            if (identity.IsDirectory)
            {
                using SafeFileHandle childSource = TryOpenDirectoryAt(sourceDirectory, leaf)
                    ?? throw new IOException("The source directory disappeared during copy.");
                using SafeFileHandle childDestination = CreateDirectoryAt(destinationDirectory, leaf);
                CopyDirectoryContents(childSource, childDestination);
                Sync(childDestination);
            }
            else if (identity.IsRegularFile)
            {
                if (identity.LinkCount != 1) throw new IOException("Refusing to copy a multiply-linked source file.");
                using SafeFileHandle sourceFile = OpenRegularFileAt(sourceDirectory, leaf, readOnly: true, create: false, exclusive: false);
                using SafeFileHandle destinationFile = OpenRegularFileAt(destinationDirectory, leaf, readOnly: false, create: true, exclusive: true);
                using FileStream input = CreateFileStream(sourceFile, FileAccess.Read);
                using FileStream output = CreateFileStream(destinationFile, FileAccess.Write);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
                Sync(destinationFile);
            }
            else
            {
                throw new IOException("Refusing to copy a link or special file.");
            }
        }
        Sync(destinationDirectory);
    }

    public static SafeFileHandle OpenRegularFileAt(
        SafeFileHandle parentDirectory,
        string name,
        bool readOnly,
        bool create,
        bool exclusive)
    {
        ValidateLeafName(name);
        int flags = (readOnly ? OpenReadOnly : OpenReadWrite) | OpenNonBlocking | OpenCloseOnExec | OpenNoFollow;
        if (create)
        {
            flags |= OpenCreate;
        }

        if (exclusive)
        {
            flags |= OpenExclusive;
        }

        int descriptor = NativeMethods.OpenAt(parentDirectory, name, flags, 0x180);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrnoExist && exclusive)
            {
                throw new IOException("The target file already exists.");
            }

            throw CreatePathError("openat file", error);
        }

        SafeFileHandle handle = new((IntPtr)descriptor, ownsHandle: true);
        try
        {
            FileIdentity identity = ReadIdentity(handle);
            if (!identity.IsRegularFile)
            {
                throw new SqlitePathException("The SQLite path must be a regular non-link file.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static SafeFileHandle? TryOpenRegularFileAt(SafeFileHandle parentDirectory, string name, bool readOnly)
    {
        ValidateLeafName(name);
        int flags = (readOnly ? OpenReadOnly : OpenReadWrite) | OpenNonBlocking | OpenCloseOnExec | OpenNoFollow;
        int descriptor = NativeMethods.OpenAt(parentDirectory, name, flags, 0);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrnoNoEntry)
            {
                return null;
            }

            throw CreatePathError("openat optional file", error);
        }

        SafeFileHandle handle = new((IntPtr)descriptor, ownsHandle: true);
        try
        {
            FileIdentity identity = ReadIdentity(handle);
            if (!identity.IsRegularFile)
            {
                throw new SqlitePathException("The SQLite path must be a regular non-link file.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static FileStream CreateFileStream(SafeFileHandle handle, FileAccess access, int bufferSize = 4096, bool isAsync = false) =>
        new(handle, access, bufferSize, isAsync);

    public static void RenameAt(SafeFileHandle directory, string oldName, string newName)
    {
        ValidateLeafName(oldName);
        ValidateLeafName(newName);
        if (NativeMethods.RenameAt(directory, oldName, directory, newName) != 0)
        {
            throw CreateError("renameat", Marshal.GetLastPInvokeError());
        }
    }

    public static void RenameBetweenDirectories(SafeFileHandle sourceDirectory, string sourceName, SafeFileHandle destinationDirectory, string destinationName)
    {
        ValidateLeafName(sourceName);
        ValidateLeafName(destinationName);
        if (NativeMethods.RenameAt(sourceDirectory, sourceName, destinationDirectory, destinationName) != 0)
            throw CreateError("renameat across directories", Marshal.GetLastPInvokeError());
    }

    public static void RenameBetweenDirectoriesNoReplace(SafeFileHandle sourceDirectory, string sourceName, SafeFileHandle destinationDirectory, string destinationName)
    {
        ValidateLeafName(sourceName);
        ValidateLeafName(destinationName);
        if (NativeMethods.RenameAtNoReplace(sourceDirectory, sourceName, destinationDirectory, destinationName) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrnoNoSys)
                throw new PlatformNotSupportedException("Linux renameat2(RENAME_NOREPLACE) is required for save rename.");
            throw CreateError("renameat2 no-replace", error);
        }
    }

    public static void Sync(SafeFileHandle handle)
    {
        if (NativeMethods.Fsync(handle) != 0) throw CreateError("fsync", Marshal.GetLastPInvokeError());
    }

    public static void DeleteTreeAt(
        SafeFileHandle parentDirectory,
        string name,
        int maxDepth = 64,
        FileIdentity? expectedIdentity = null,
        bool allowReadOnly = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDepth);
        ValidateLeafName(name);
        using SafeFileHandle entry = OpenPathAt(parentDirectory, name);
        FileIdentity identity = ReadIdentity(entry);
        if (expectedIdentity is FileIdentity expected
            && (identity.DeviceMajor != expected.DeviceMajor || identity.DeviceMinor != expected.DeviceMinor || identity.Inode != expected.Inode))
            throw new IOException("Refusing to remove a staging directory whose identity changed after validation.");
        if (identity.UserId != CurrentUserId) throw new IOException("Refusing to remove a staging entry owned by another user.");
        if (identity.IsRegularFile)
        {
            if (!IsSafeFileMode(identity.Mode, allowReadOnly)) throw new IOException("Refusing to remove a staging file with unsafe permissions.");
            if (identity.LinkCount != 1) throw new IOException("Refusing to remove a multiply-linked staging file.");
            UnlinkAtChecked(parentDirectory, name, isDirectory: false);
            return;
        }
        if (!identity.IsDirectory) throw new IOException("Refusing to remove a link or special staging entry.");
        if (!IsSafeDirectoryMode(identity.Mode, allowReadOnly)) throw new IOException("Refusing to remove a staging directory with unsafe permissions.");

        using SafeFileHandle directory = TryOpenDirectoryAt(parentDirectory, name)
            ?? throw new IOException("The staging directory disappeared during cleanup.");
        DeleteDirectoryContents(directory, maxDepth - 1, allowReadOnly);
        EnsureSameIdentity(entry, directory);
        UnlinkAtChecked(parentDirectory, name, isDirectory: true);
    }

    public static bool TryDeleteTreeAt(
        SafeFileHandle parentDirectory,
        string name,
        int maxDepth = 64,
        FileIdentity? expectedIdentity = null,
        bool allowReadOnly = false)
    {
        try
        {
            DeleteTreeAt(parentDirectory, name, maxDepth, expectedIdentity, allowReadOnly);
            return true;
        }
        catch (LinuxFileOperationException exception) when (exception.Error == ErrnoNoEntry)
        {
            return true;
        }
    }

    private static void DeleteDirectoryContents(SafeFileHandle directory, int remainingDepth, bool allowReadOnly)
    {
        if (remainingDepth < 0) throw new IOException("Staging cleanup exceeded its maximum depth.");
        string descriptorPath = GetProcFileDescriptorPath(directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(descriptorPath))
        {
            string leaf = Path.GetFileName(path);
            DeleteTreeAt(directory, leaf, remainingDepth, allowReadOnly: allowReadOnly);
        }
    }

    private static bool IsSafeFileMode(ushort mode, bool allowReadOnly)
    {
        int permissions = mode & 0x1FF;
        return permissions == 0x180 || (allowReadOnly && permissions is 0x100 or 0x124);
    }

    private static bool IsSafeDirectoryMode(ushort mode, bool allowReadOnly)
    {
        int permissions = mode & 0x1FF;
        return permissions == 0x1C0 || (allowReadOnly && permissions is 0x140 or 0x16D);
    }

    private static SafeFileHandle OpenPathAt(SafeFileHandle parentDirectory, string name)
    {
        int descriptor = NativeMethods.OpenAt(parentDirectory, name, OpenPath | OpenCloseOnExec | OpenNoFollow, 0);
        return CreateHandle(descriptor, "openat cleanup entry");
    }

    private static void UnlinkAtChecked(SafeFileHandle directory, string name, bool isDirectory)
    {
        if (NativeMethods.UnlinkAt(directory, name, isDirectory ? AtRemoveDirectory : 0) != 0)
        {
            throw CreateError("unlinkat cleanup entry", Marshal.GetLastPInvokeError());
        }
    }

    public static string GetProcFileDescriptorPath(SafeFileHandle handle)
    {
        if (handle.IsClosed || handle.IsInvalid)
        {
            throw new SqlitePathException("The SQLite file descriptor is no longer valid.");
        }

        if (!Directory.Exists("/proc/self/fd"))
        {
            throw new SqlitePathException("Linux procfs is required for descriptor-backed SQLite access.");
        }

        long descriptor = handle.DangerousGetHandle().ToInt64();
        return $"/proc/self/fd/{descriptor.ToString(CultureInfo.InvariantCulture)}";
    }

    public static void ApplyPrivateMode(SafeFileHandle handle)
    {
        if (NativeMethods.Fchmod(handle, 0x180) != 0)
        {
            throw CreateError("fchmod", Marshal.GetLastPInvokeError());
        }
    }

    public static bool TryAcquireExclusiveLock(SafeFileHandle handle)
    {
        if (NativeMethods.Flock(handle, LockExclusive | LockNonBlocking) == 0)
        {
            return true;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error is ErrnoAgain or ErrnoWouldBlock)
        {
            return false;
        }

        throw CreateError("flock", error);
    }

    public static FileIdentity ReadIdentity(SafeFileHandle handle)
    {
        IntPtr statBuffer = Marshal.AllocHGlobal(StatxBufferSize);
        try
        {
            if (NativeMethods.Statx(handle, string.Empty, AtEmptyPath, StatxBasicStats, statBuffer) != 0)
            {
                throw CreateError("statx", Marshal.GetLastPInvokeError());
            }

            LinuxStatx stat = Marshal.PtrToStructure<LinuxStatx>(statBuffer);
            if ((stat.Mask & StatxType) == 0)
            {
                throw new SqlitePathException("The filesystem did not provide a file type.");
            }

            return new FileIdentity(
                stat.FileSystemDeviceMajor,
                stat.FileSystemDeviceMinor,
                stat.Inode,
                stat.Mode,
                stat.LinkCount,
                stat.UserId,
                checked((long)stat.Size),
                stat.ModifyTime.Seconds,
                stat.ModifyTime.Nanoseconds);
        }
        finally
        {
            Marshal.FreeHGlobal(statBuffer);
        }
    }

    public static void EnsureSameIdentity(SafeFileHandle expected, SafeFileHandle actual)
    {
        FileIdentity expectedIdentity = ReadIdentity(expected);
        FileIdentity actualIdentity = ReadIdentity(actual);
        if (expectedIdentity.DeviceMajor != actualIdentity.DeviceMajor
            || expectedIdentity.DeviceMinor != actualIdentity.DeviceMinor
            || expectedIdentity.Inode != actualIdentity.Inode)
        {
            throw new SqlitePathException("A SQLite path changed during the operation.");
        }
    }

    public static void LinkAtFromName(
        SafeFileHandle sourceDirectory,
        string sourceName,
        SafeFileHandle destinationDirectory,
        string destinationName)
    {
        ValidateLeafName(sourceName);
        ValidateLeafName(destinationName);
        if (NativeMethods.LinkAt(sourceDirectory, sourceName, destinationDirectory, destinationName, 0) != 0)
        {
            throw CreateError("linkat", Marshal.GetLastPInvokeError());
        }
    }

    public static void UnlinkAtIfExists(SafeFileHandle directory, string name)
    {
        ValidateLeafName(name);
        if (NativeMethods.UnlinkAt(directory, name, 0) == 0)
        {
            return;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error != ErrnoNoEntry)
        {
            throw CreateError("unlinkat", error);
        }
    }

    public static bool UnlinkRegularFileAt(SafeFileHandle directory, string name, FileIdentity? expectedIdentity = null)
    {
        ValidateLeafName(name);
        SafeFileHandle entry;
        try
        {
            entry = OpenPathAt(directory, name);
        }
        catch (LinuxFileOperationException exception) when (exception.Error == ErrnoNoEntry)
        {
            return false;
        }

        using (entry)
        {
            FileIdentity identity = ReadIdentity(entry);
            if (!identity.IsRegularFile || identity.LinkCount != 1 || identity.UserId != CurrentUserId)
                throw new IOException("Refusing to remove a non-private regular save file.");
            if (expectedIdentity is FileIdentity expected && !identity.SameObject(expected))
                throw new IOException("The save file changed before deletion.");
            UnlinkAtChecked(directory, name, isDirectory: false);
            return true;
        }
    }

    private static SafeFileHandle CreateHandle(int descriptor, string operation)
    {
        if (descriptor >= 0)
        {
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }

        throw CreatePathError(operation, Marshal.GetLastPInvokeError());
    }

    private static void ValidateLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.Contains('/')
            || name.Contains('\\')
            || name.Contains('\0'))
        {
            throw new SqlitePathException("A native SQLite directory operation requires a safe leaf name.");
        }
    }

    private static Exception CreatePathError(string operation, int error) =>
        error is ErrnoLoop or ErrnoNotDirectory or ErrnoIsDirectory
            ? new SqlitePathException($"The SQLite path is unsafe for {operation}.")
            : CreateError(operation, error);

    private static LinuxFileOperationException CreateError(string operation, int error) =>
        new LinuxFileOperationException(operation, error);

    public static uint CurrentUserId => NativeMethods.GetUserId();

    internal readonly record struct FileIdentity(
        uint DeviceMajor,
        uint DeviceMinor,
        ulong Inode,
        ushort Mode,
        uint LinkCount,
        uint UserId,
        long Size,
        long ModifyTimeSeconds,
        uint ModifyTimeNanoseconds)
    {
        public bool IsRegularFile => (Mode & 0xF000) == RegularFileType;

        public bool IsDirectory => (Mode & 0xF000) == DirectoryFileType;

        public bool SameObject(FileIdentity other) =>
            DeviceMajor == other.DeviceMajor && DeviceMinor == other.DeviceMinor && Inode == other.Inode;
    }

    internal sealed class LinuxFileOperationException(string operation, int error)
        : IOException($"{operation} failed with errno {error}.")
    {
        public int Error { get; } = error;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct LinuxStatx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public LinuxStatxTimestamp AccessTime;
        public LinuxStatxTimestamp BirthTime;
        public LinuxStatxTimestamp ChangeTime;
        public LinuxStatxTimestamp ModifyTime;
        public uint DeviceMajor;
        public uint DeviceMinor;
        public uint FileSystemDeviceMajor;
        public uint FileSystemDeviceMinor;
        public ulong MountId;
        public uint DirectIoMemoryAlignment;
        public uint DirectIoOffsetAlignment;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct LinuxStatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    private static partial class NativeMethods
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "Linux openat operations require stable native flags and SafeFileHandle ownership.")]
        [SuppressMessage("Interoperability", "CA2101", Justification = "Linux path arguments are explicitly marshaled as UTF-8.")]
        public static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "Linux openat operations require stable native flags and SafeFileHandle ownership.")]
        [SuppressMessage("Interoperability", "CA2101", Justification = "Linux path arguments are explicitly marshaled as UTF-8.")]
        public static extern int OpenAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "Linux mkdirat is used beneath an already-open directory handle.")]
        [SuppressMessage("Interoperability", "CA2101", Justification = "Linux path arguments are explicitly marshaled as UTF-8.")]
        public static extern int MkdirAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

        [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "Linux fchmod operates on the already-open file descriptor.")]
        public static extern int Fchmod(SafeFileHandle file, uint mode);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "Linux fsync operates on an already-open file or directory descriptor.")]
        public static extern int Fsync(SafeFileHandle file);

        [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "Linux flock provides the cross-process migration lock.")]
        public static extern int Flock(SafeFileHandle file, int operation);

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "Linux statx uses the stable kernel UAPI struct marshaled below.")]
        [SuppressMessage("Interoperability", "CA2101", Justification = "The empty path is required with AT_EMPTY_PATH and has no user-controlled characters.")]
        public static extern int Statx(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, IntPtr buffer);

        [DllImport("libc", EntryPoint = "linkat", SetLastError = true)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "Linux linkat publishes a validated temporary backup name below protected directory handles.")]
        [SuppressMessage("Interoperability", "CA2101", Justification = "Validated source and destination leaves are explicitly marshaled as UTF-8.")]
        public static extern int LinkAt(SafeFileHandle source, [MarshalAs(UnmanagedType.LPUTF8Str)] string sourcePath, SafeFileHandle destinationDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string destinationPath, int flags);

        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "Linux unlinkat removes only a validated leaf below an open directory handle.")]
        [SuppressMessage("Interoperability", "CA2101", Justification = "Linux path arguments are explicitly marshaled as UTF-8.")]
        public static extern int UnlinkAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "Linux renameat publishes a bundle below an already-open staging directory.")]
        [SuppressMessage("Interoperability", "CA2101", Justification = "Validated leaf names are explicitly marshaled as UTF-8.")]
        public static extern int RenameAt(SafeFileHandle oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, SafeFileHandle newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

        [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "Linux renameat2 provides atomic no-replace semantics for native save rename.")]
        [SuppressMessage("Interoperability", "CA2101", Justification = "Validated leaf names are explicitly marshaled as UTF-8.")]
        public static extern int RenameAtNoReplace(SafeFileHandle oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, SafeFileHandle newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags = 1);

        [DllImport("libc", EntryPoint = "getuid")]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "Linux getuid has no parameters or marshaling concerns.")]
        public static extern uint GetUserId();
    }
}
