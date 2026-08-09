using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace CloudEmuera.Api.Security;

public static class DataProtectionKeyRing
{
    public static DirectoryInfo Prepare(string dataRoot)
    {
        string root = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(root);
        EnsureNoSymbolicLinkInPath(root);
        EnsureLinuxOwnedEntry(root, UnixDirectory, requireSingleLink: false);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string path = Path.Combine(root, "keys");
        if (File.Exists(path) && !Directory.Exists(path)) throw new InvalidOperationException("Data Protection key path is not a directory.");
        Directory.CreateDirectory(path);
        EnsureNoSymbolicLinkInPath(path);
        EnsureLinuxOwnedEntry(path, UnixDirectory, requireSingleLink: false);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new DirectoryInfo(path);
    }

    public static void HardenExistingKeyFiles(string dataRoot)
    {
        DirectoryInfo directory = Prepare(dataRoot);
        if (!OperatingSystem.IsLinux()) return;
        foreach (FileInfo key in directory.EnumerateFiles("*.xml", SearchOption.TopDirectoryOnly))
        {
            if ((key.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Data Protection key files must not be symbolic links.");
            EnsureLinuxOwnedEntry(key.FullName, UnixRegularFile, requireSingleLink: true);
            File.SetUnixFileMode(key.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void EnsureNoSymbolicLinkInPath(string path)
    {
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Data Protection key directory ancestors must not be symbolic links.");
        }
    }

    private static void EnsureLinuxOwnedEntry(string path, uint expectedType, bool requireSingleLink)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Data Protection key validation requires 64-bit Linux metadata.");
        if (LStat(path, out UnixStat stat) != 0)
            throw new IOException($"Data Protection path metadata could not be read: {Marshal.GetLastPInvokeError()}.");
        if ((stat.Mode & UnixFileTypeMask) != expectedType || stat.UserId != GetEffectiveUserId() || (requireSingleLink && stat.LinkCount != 1))
            throw new UnauthorizedAccessException("Data Protection paths must be owned by the service user; keys must be single-link regular files.");
    }

    [SuppressMessage("Security", "CA2101", Justification = "lstat receives an explicit UTF-8 marshaled absolute path.")]
    [DllImport("libc", EntryPoint = "lstat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out UnixStat stat);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

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
    private const uint UnixDirectory = 0x4000;
    private const uint UnixRegularFile = 0x8000;
}
