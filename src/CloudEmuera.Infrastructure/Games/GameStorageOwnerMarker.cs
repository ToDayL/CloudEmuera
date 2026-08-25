using System.Text;
using System.Text.Json;
using CloudEmuera.Application.Games;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.RuntimeAdapter;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Games;

public static class GameStorageOwnerMarker
{
    private static readonly JsonSerializerOptions MarkerJson = new(JsonSerializerDefaults.Web);

    public static void Initialize(string directory, string gameId, string ownerUserId)
    {
        Directory.CreateDirectory(directory);
        using SafeFileHandle directoryHandle = LinuxFileOperations.OpenDirectory(directory);
        LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(directoryHandle);
        var marker = new OwnerMarker(2, gameId, ownerUserId, identity.DeviceMajor, identity.DeviceMinor, identity.Inode);
        string temporaryName = $".owner-{Guid.NewGuid():N}.part";
        try
        {
            using SafeFileHandle temporary = LinuxFileOperations.OpenRegularFileAt(directoryHandle, temporaryName, readOnly: false, create: true, exclusive: true);
            LinuxFileOperations.ApplyPrivateMode(temporary);
            using (FileStream stream = LinuxFileOperations.CreateFileStream(temporary, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(JsonSerializer.Serialize(marker, MarkerJson));
                writer.Flush();
                stream.Flush(flushToDisk: false);
            }
            LinuxFileOperations.RenameAt(directoryHandle, temporaryName, "owner.json");
        }
        catch
        {
            LinuxFileOperations.UnlinkAtIfExists(directoryHandle, temporaryName);
            throw;
        }
    }

    public static void Validate(string directory, string gameId)
    {
        using SafeFileHandle directoryHandle = LinuxFileOperations.OpenDirectory(directory);
        using SafeFileHandle markerHandle = LinuxFileOperations.TryOpenRegularFileAt(directoryHandle, "owner.json", readOnly: true)
            ?? throw Unsafe();
        if (LinuxFileOperations.ReadIdentity(markerHandle).LinkCount != 1) throw Unsafe();
        OwnerMarker? marker;
        using (FileStream stream = LinuxFileOperations.CreateFileStream(markerHandle, FileAccess.Read))
            marker = JsonSerializer.Deserialize<OwnerMarker>(stream, MarkerJson);
        LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(directoryHandle);
        if (marker is null || marker.SchemaVersion != 2 || marker.GameId != gameId
            || marker.DeviceMajor != identity.DeviceMajor || marker.DeviceMinor != identity.DeviceMinor || marker.Inode != identity.Inode)
            throw Unsafe();
    }

    public static void ValidateForRestore(string directory, string gameId, string ownerUserId)
    {
        OwnerMarker marker = ReadAndValidateIdentity(directory, gameId, ownerUserId);
        if (string.IsNullOrWhiteSpace(marker.OwnerUserId))
            throw Unsafe();
    }

    /// <summary>
    /// Rebinds the directory device/inode recorded by the protected owner
    /// marker after an operator restores a complete, offline DataRoot.
    /// Durable Game identity is validated by the caller-provided Game ID and
    /// owner before only the filesystem identity fields are changed.
    /// </summary>
    public static void RebindDirectoryIdentity(string directory, string gameId, string ownerUserId)
    {
        OwnerMarker current = ReadAndValidateIdentity(directory, gameId, ownerUserId);
        using SafeFileHandle directoryHandle = LinuxFileOperations.OpenDirectory(directory);
        LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(directoryHandle);
        OwnerMarker rebound = current with
        {
            DeviceMajor = identity.DeviceMajor,
            DeviceMinor = identity.DeviceMinor,
            Inode = identity.Inode,
        };
        WriteReboundMarker(directoryHandle, rebound);
    }

    private static OwnerMarker ReadAndValidateIdentity(string directory, string gameId, string ownerUserId)
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(ownerUserId))
            throw Unsafe();
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(directory, "restored-game-storage", RuntimeFileArea.GameContent);
        using SafeFileHandle directoryHandle = LinuxFileOperations.OpenDirectory(directory);
        LinuxFileOperations.FileIdentity directoryIdentity = LinuxFileOperations.ReadIdentity(directoryHandle);
        if (!directoryIdentity.IsDirectory || directoryIdentity.UserId != LinuxFileOperations.CurrentUserId ||
            (directoryIdentity.Mode & 0x1FF) != 0x1C0)
            throw Unsafe();
        using SafeFileHandle markerHandle = LinuxFileOperations.TryOpenRegularFileAt(directoryHandle, "owner.json", readOnly: true)
            ?? throw Unsafe();
        LinuxFileOperations.FileIdentity markerIdentity = LinuxFileOperations.ReadIdentity(markerHandle);
        if (!markerIdentity.IsRegularFile || markerIdentity.UserId != LinuxFileOperations.CurrentUserId || markerIdentity.LinkCount != 1)
            throw Unsafe();
        OwnerMarker? marker;
        using (FileStream stream = LinuxFileOperations.CreateFileStream(markerHandle, FileAccess.Read))
            marker = JsonSerializer.Deserialize<OwnerMarker>(stream, MarkerJson);
        if (marker is null || marker.SchemaVersion != 2 || marker.GameId != gameId || marker.OwnerUserId != ownerUserId)
            throw Unsafe();
        return marker;
    }

    private static void WriteReboundMarker(SafeFileHandle directoryHandle, OwnerMarker marker)
    {
        string temporaryName = $".owner-{Guid.NewGuid():N}.part";
        try
        {
            using SafeFileHandle temporary = LinuxFileOperations.OpenRegularFileAt(directoryHandle, temporaryName, readOnly: false, create: true, exclusive: true);
            LinuxFileOperations.ApplyPrivateMode(temporary);
            using (FileStream stream = LinuxFileOperations.CreateFileStream(temporary, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(JsonSerializer.Serialize(marker, MarkerJson));
                writer.Write('\n');
                writer.Flush();
                stream.Flush(flushToDisk: false);
            }
            LinuxFileOperations.RenameAt(directoryHandle, temporaryName, "owner.json");
        }
        catch
        {
            LinuxFileOperations.UnlinkAtIfExists(directoryHandle, temporaryName);
            throw;
        }
    }

    private static GameLibraryException Unsafe() => new(GameLibraryErrorCodes.UnsafePath, "The game storage owner marker is invalid.");
    private sealed record OwnerMarker(int SchemaVersion, string GameId, string OwnerUserId, uint DeviceMajor, uint DeviceMinor, ulong Inode);
}
