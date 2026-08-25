using CloudEmuera.Application.GamePackages;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;

namespace CloudEmuera.Infrastructure.GamePackages;

/// <summary>Anchors every staging operation beneath verified directory descriptors.</summary>
internal sealed class LinuxGamePackageStagingStore : IDisposable
{
    private readonly SafeFileHandle dataRoot;
    private readonly SafeFileHandle gamesRoot;
    private readonly SafeFileHandle stagingRoot;

    public LinuxGamePackageStagingStore(GamePackageStorageOptions options)
    {
        options.Validate();
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Secure game-package staging requires Linux openat semantics.");
        dataRoot = LinuxFileOperations.OpenOrCreateDirectory(options.DataRoot);
        try
        {
            ValidateProtectedRoot(dataRoot);
            gamesRoot = LinuxFileOperations.OpenOrCreateDirectoryAt(dataRoot, "games");
            try
            {
                ValidateProtectedRoot(gamesRoot);
                stagingRoot = LinuxFileOperations.OpenOrCreateDirectoryAt(gamesRoot, "staging");
                ValidateProtectedRoot(stagingRoot);
            }
            catch
            {
                gamesRoot.Dispose();
                throw;
            }
        }
        catch
        {
            dataRoot.Dispose();
            throw;
        }
    }

    public SafeFileHandle CreateIngestion(string ingestionId)
    {
        SafeFileHandle handle = LinuxFileOperations.CreateDirectoryAt(stagingRoot, ValidateIngestionId(ingestionId));
        LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(handle);
        try
        {
            ValidatePrivateDirectory(handle);
            WriteLease(handle, ingestionId, identity);
            return handle;
        }
        catch
        {
            handle.Dispose();
            try { LinuxFileOperations.TryDeleteTreeAt(stagingRoot, ingestionId, expectedIdentity: identity); }
            catch (IOException) { }
            throw;
        }
    }

    public SafeFileHandle OpenIngestion(string ingestionId)
    {
        SafeFileHandle handle = LinuxFileOperations.TryOpenDirectoryAt(stagingRoot, ValidateIngestionId(ingestionId))
            ?? throw new GamePackageIngestionException(GamePackageRejectionCodes.StagedContentChanged, "The staging directory is missing.");
        try { ValidatePrivateDirectory(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    public bool DeleteIngestion(string ingestionId)
    {
        string validatedId = ValidateIngestionId(ingestionId);
        using SafeFileHandle? root = LinuxFileOperations.TryOpenDirectoryAt(stagingRoot, validatedId);
        if (root is null) return true;
        ValidatePrivateDirectory(root);
        LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(root);
        ValidateLease(root, validatedId, identity);
        return LinuxFileOperations.TryDeleteTreeAt(stagingRoot, validatedId, expectedIdentity: identity);
    }

    public static SafeFileHandle CreateDirectory(SafeFileHandle parent, string name)
    {
        SafeFileHandle handle = LinuxFileOperations.CreateDirectoryAt(parent, name);
        ValidatePrivateDirectory(handle);
        return handle;
    }

    public static SafeFileHandle OpenDirectory(SafeFileHandle parent, string name)
    {
        SafeFileHandle handle = LinuxFileOperations.TryOpenDirectoryAt(parent, name)
            ?? throw new GamePackageIngestionException(GamePackageRejectionCodes.StagedContentChanged, "A staging directory is missing.");
        try { ValidatePrivateDirectory(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    public static SafeFileHandle OpenOrCreateDirectory(SafeFileHandle parent, string name)
    {
        SafeFileHandle handle = LinuxFileOperations.OpenOrCreateDirectoryAt(parent, name);
        try { ValidatePrivateDirectory(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    public static SafeFileHandle CreateFile(SafeFileHandle parent, string name)
    {
        SafeFileHandle handle = LinuxFileOperations.OpenRegularFileAt(parent, name, readOnly: false, create: true, exclusive: true);
        try { LinuxFileOperations.ApplyPrivateMode(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    public static SafeFileHandle OpenFile(SafeFileHandle parent, string name) =>
        LinuxFileOperations.TryOpenRegularFileAt(parent, name, readOnly: true)
        ?? throw new GamePackageIngestionException(GamePackageRejectionCodes.StagedContentChanged, "A staging file is missing.");

    public static SafeFileHandle OpenDirectoryPath(SafeFileHandle root, string logicalPath, bool create)
    {
        SafeFileHandle current = Duplicate(root);
        try
        {
            foreach (string segment in Split(logicalPath))
            {
                SafeFileHandle next = create
                    ? OpenOrCreateDirectory(current, segment)
                    : OpenDirectory(current, segment);
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

    public static SafeFileHandle OpenFilePath(SafeFileHandle root, string logicalPath, bool create)
    {
        string[] segments = Split(logicalPath);
        if (segments.Length == 0) throw new GamePackageIngestionException(GamePackageRejectionCodes.PathInvalid, "A file path is empty.");
        string parentPath = string.Join('/', segments[..^1]);
        using SafeFileHandle parent = OpenDirectoryPath(root, parentPath, create);
        return create ? CreateFile(parent, segments[^1]) : OpenFile(parent, segments[^1]);
    }

    public static void Rename(SafeFileHandle parent, string oldName, string newName) =>
        LinuxFileOperations.RenameAt(parent, oldName, newName);

    public static FileStream Stream(SafeFileHandle handle, FileAccess access, bool async = false) =>
        LinuxFileOperations.CreateFileStream(handle, access, 64 * 1024, isAsync: false);

    public static string DescriptorPath(SafeFileHandle handle) => LinuxFileOperations.GetProcFileDescriptorPath(handle);

    public static LinuxFileOperations.FileIdentity Identity(SafeFileHandle handle) => LinuxFileOperations.ReadIdentity(handle);

    private static SafeFileHandle Duplicate(SafeFileHandle handle) => LinuxFileOperations.DuplicateDirectory(handle);

    private static string[] Split(string logicalPath) =>
        logicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string ValidateIngestionId(string ingestionId)
    {
        if (!ingestionId.StartsWith("ing_", StringComparison.Ordinal)
            || ingestionId.Length is < 5 or > 64
            || ingestionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new GamePackageIngestionException(GamePackageRejectionCodes.PathInvalid, "The ingestion identifier is invalid.");
        return ingestionId;
    }

    private static void ValidateProtectedRoot(SafeFileHandle handle)
    {
        LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(handle);
        if (!identity.IsDirectory || identity.UserId != LinuxFileOperations.CurrentUserId || (identity.Mode & 0x12) != 0)
            throw new GamePackageIngestionException(GamePackageRejectionCodes.StagingIoFailed,
                "A protected staging ancestor has unsafe ownership, type, or write permissions.");
    }

    private static void ValidatePrivateDirectory(SafeFileHandle handle)
    {
        LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(handle);
        if (!identity.IsDirectory || identity.UserId != LinuxFileOperations.CurrentUserId || (identity.Mode & 0x1FF) != 0x1C0)
            throw new GamePackageIngestionException(GamePackageRejectionCodes.StagedContentChanged,
                "A staging directory has unsafe ownership or permissions.");
    }

    private static void WriteLease(SafeFileHandle root, string ingestionId, LinuxFileOperations.FileIdentity identity)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new StagingLease(
            SchemaVersion: 1,
            IngestionId: ingestionId,
            DeviceMajor: identity.DeviceMajor,
            DeviceMinor: identity.DeviceMinor,
            Inode: identity.Inode));
        using (SafeFileHandle lease = CreateFile(root, "lease.json.part"))
        using (FileStream stream = Stream(lease, FileAccess.Write))
        {
            stream.Write(json);
            // The lease is a restart-recovery marker, not a power-loss
            // durability boundary. Keep the write buffered and publish it
            // with the following atomic rename.
            stream.Flush(flushToDisk: false);
        }
        Rename(root, "lease.json.part", "lease.json");
    }

    private static void ValidateLease(SafeFileHandle root, string ingestionId, LinuxFileOperations.FileIdentity rootIdentity)
    {
        using SafeFileHandle lease = OpenFile(root, "lease.json");
        LinuxFileOperations.FileIdentity leaseIdentity = LinuxFileOperations.ReadIdentity(lease);
        if (leaseIdentity.UserId != LinuxFileOperations.CurrentUserId || leaseIdentity.LinkCount != 1
            || (leaseIdentity.Mode & 0x1FF) != 0x180)
            throw new IOException("The staging lease has unsafe ownership, permissions, or link count.");
        using FileStream stream = Stream(lease, FileAccess.Read);
        if (stream.Length is <= 0 or > 4096) throw new IOException("The staging lease has an invalid length.");
        StagingLease value;
        try { value = JsonSerializer.Deserialize<StagingLease>(stream) ?? throw new IOException("The staging lease is empty."); }
        catch (JsonException exception) { throw new IOException("The staging lease is invalid.", exception); }
        if (value.SchemaVersion != 1 || !string.Equals(value.IngestionId, ingestionId, StringComparison.Ordinal)
            || value.DeviceMajor != rootIdentity.DeviceMajor || value.DeviceMinor != rootIdentity.DeviceMinor
            || value.Inode != rootIdentity.Inode)
            throw new IOException("The staging lease does not belong to this ingestion directory.");
    }

    private sealed record StagingLease(int SchemaVersion, string IngestionId, uint DeviceMajor, uint DeviceMinor, ulong Inode);

    public void Dispose()
    {
        stagingRoot.Dispose();
        gamesRoot.Dispose();
        dataRoot.Dispose();
    }
}
