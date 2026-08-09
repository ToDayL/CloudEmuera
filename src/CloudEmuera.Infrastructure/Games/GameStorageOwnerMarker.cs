using System.Text;
using System.Text.Json;
using CloudEmuera.Application.Games;
using CloudEmuera.Infrastructure.Persistence;
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
                stream.Flush(flushToDisk: true);
            }
            LinuxFileOperations.RenameAt(directoryHandle, temporaryName, "owner.json");
            LinuxFileOperations.Sync(directoryHandle);
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

    private static GameLibraryException Unsafe() => new(GameLibraryErrorCodes.UnsafePath, "The game storage owner marker is invalid.");
    private sealed record OwnerMarker(int SchemaVersion, string GameId, string OwnerUserId, uint DeviceMajor, uint DeviceMinor, ulong Inode);
}
