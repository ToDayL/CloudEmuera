using System.Text.Json;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.Infrastructure.Sessions;

internal sealed record SessionRootProtectedMarker
{
    public int SchemaVersion { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public string OwnerUserId { get; init; } = string.Empty;
    public string GameId { get; init; } = string.Empty;
    public long SourceContentRevision { get; init; }
    public string SourceContentDigest { get; init; } = string.Empty;
    public string SourceManifestDigest { get; init; } = string.Empty;
    public string MaterializedManifestDigest { get; init; } = string.Empty;
    public RuntimeSaveLayout SaveLayout { get; init; }
    public string RuntimeVersion { get; init; } = string.Empty;
    public uint RootDeviceMajor { get; init; }
    public uint RootDeviceMinor { get; init; }
    public ulong RootInode { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

internal static class SessionRootProtectedMarkerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ContainerPath(SqliteDatabaseOptions options, string sessionId)
    {
        ValidateSessionId(sessionId);
        string root = Path.GetFullPath(options.DataRoot);
        string container = Path.Combine(root, "sessions", sessionId);
        if (!RuntimePathUtilities.IsStrictlyWithin(container, root))
            throw new SessionRuntimeException(SessionRuntimeResultCodes.SessionRootInvalid, "The SessionRoot container escapes the data root.");
        return container;
    }

    public static string MetadataPath(SqliteDatabaseOptions options, string sessionId) =>
        Path.Combine(ContainerPath(options, sessionId), "metadata", "session-root.json");

    public static SessionRootProtectedMarker Read(SqliteDatabaseOptions options, string sessionId)
    {
        string path = MetadataPath(options, sessionId);
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(path, "protected-session-marker", RuntimeFileArea.Configuration);
        RuntimePathUtilities.ThrowIfReparsePoint(path, "protected-session-marker", RuntimeFileArea.Configuration, missingIsAllowed: false);
        RuntimePathUtilities.ThrowIfHardLink(path, "protected-session-marker", RuntimeFileArea.Configuration);
        try
        {
            return JsonSerializer.Deserialize<SessionRootProtectedMarker>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("The protected SessionRoot marker is empty.");
        }
        catch (JsonException exception)
        {
            throw new SessionRuntimeException(SessionRuntimeResultCodes.SessionRootInvalid, "The protected SessionRoot marker is malformed.", exception);
        }
        catch (IOException exception)
        {
            throw new SessionRuntimeException(SessionRuntimeResultCodes.SessionRootInvalid, "The protected SessionRoot marker cannot be read.", exception);
        }
    }

    /// <summary>
    /// Rebinds only the filesystem identity fields after an operator has
    /// restored a complete, offline DataRoot into a new directory tree. The
    /// caller must hold the migration lock and must validate the marker
    /// against the durable Session row before calling this method.
    /// </summary>
    internal static SessionRootProtectedMarker RebindRootIdentity(
        SqliteDatabaseOptions options,
        string sessionId,
        string rootPath)
    {
        ValidateSessionId(sessionId);
        SessionRootProtectedMarker current = Read(options, sessionId);
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(rootPath, "restored-session-root", RuntimeFileArea.GameContent);
        RuntimePathUtilities.ThrowIfReparsePoint(rootPath, "restored-session-root", RuntimeFileArea.GameContent, missingIsAllowed: false);

        SessionRootProtectedMarker rebound;
        if (OperatingSystem.IsLinux())
        {
            using Microsoft.Win32.SafeHandles.SafeFileHandle root = LinuxFileOperations.OpenDirectory(rootPath);
            LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(root);
            if (!identity.IsDirectory || identity.UserId != LinuxFileOperations.CurrentUserId || (identity.Mode & 0x1FF) != 0x1C0)
                throw new SessionRuntimeException(SessionRuntimeResultCodes.SessionRootInvalid, "The restored SessionRoot is not a private directory.");

            rebound = current with
            {
                RootDeviceMajor = identity.DeviceMajor,
                RootDeviceMinor = identity.DeviceMinor,
                RootInode = identity.Inode,
            };
        }
        else
        {
            if (!Directory.Exists(rootPath))
                throw new SessionRuntimeException(SessionRuntimeResultCodes.SessionRootInvalid, "The restored SessionRoot does not exist.");
            rebound = current;
        }

        WriteReboundMarker(options, sessionId, rebound);
        return rebound;
    }

    public static SessionRootProtectedMarker Write(
        SqliteDatabaseOptions options,
        string stagingContainer,
        string sessionId,
        string ownerUserId,
        string gameId,
        long sourceContentRevision,
        string sourceContentDigest,
        string sourceManifestDigest,
        string materializedManifestDigest,
        RuntimeSaveLayout saveLayout,
        string runtimeVersion,
        DateTimeOffset createdAt,
        string rootPath)
    {
        ValidateSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(ownerUserId) || string.IsNullOrWhiteSpace(gameId) ||
            sourceContentRevision <= 0 || string.IsNullOrWhiteSpace(sourceContentDigest) ||
            string.IsNullOrWhiteSpace(sourceManifestDigest) || string.IsNullOrWhiteSpace(materializedManifestDigest) ||
            string.IsNullOrWhiteSpace(runtimeVersion))
            throw new ArgumentException("Protected SessionRoot marker identity is incomplete.");

        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(stagingContainer, "session-container");
        string metadataDirectory = Path.Combine(stagingContainer, "metadata");
        Directory.CreateDirectory(metadataDirectory);
        RuntimePathUtilities.ThrowIfReparsePoint(metadataDirectory, "session-metadata", missingIsAllowed: false);
        SetPrivateDirectoryMode(metadataDirectory);

        SessionRootProtectedMarker marker;
        if (OperatingSystem.IsLinux())
        {
            using Microsoft.Win32.SafeHandles.SafeFileHandle handle = LinuxFileOperations.OpenDirectory(rootPath);
            LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(handle);
            marker = new SessionRootProtectedMarker
            {
                SchemaVersion = 1,
                SessionId = sessionId,
                OwnerUserId = ownerUserId,
                GameId = gameId,
                SourceContentRevision = sourceContentRevision,
                SourceContentDigest = sourceContentDigest,
                SourceManifestDigest = sourceManifestDigest,
                MaterializedManifestDigest = materializedManifestDigest,
                SaveLayout = saveLayout,
                RuntimeVersion = runtimeVersion,
                RootDeviceMajor = identity.DeviceMajor,
                RootDeviceMinor = identity.DeviceMinor,
                RootInode = identity.Inode,
                CreatedAt = createdAt,
            };
        }
        else
        {
            marker = new SessionRootProtectedMarker
            {
                SchemaVersion = 1,
                SessionId = sessionId,
                OwnerUserId = ownerUserId,
                GameId = gameId,
                SourceContentRevision = sourceContentRevision,
                SourceContentDigest = sourceContentDigest,
                SourceManifestDigest = sourceManifestDigest,
                MaterializedManifestDigest = materializedManifestDigest,
                SaveLayout = saveLayout,
                RuntimeVersion = runtimeVersion,
                CreatedAt = createdAt,
            };
        }

        string path = Path.Combine(metadataDirectory, "session-root.json");
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true))
        {
            writer.Write(JsonSerializer.Serialize(marker, JsonOptions));
            writer.Write('\n');
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        SetPrivateFileMode(path);
        return marker;
    }

    public static void WriteRuntimeManifest(string stagingContainer, string runtimeManifestJson)
    {
        if (string.IsNullOrWhiteSpace(runtimeManifestJson) || runtimeManifestJson.Length > PersistenceLimits.SessionRuntimeManifestMaxLength)
            throw new ArgumentException("The runtime manifest is outside the permitted size.", nameof(runtimeManifestJson));
        using JsonDocument _ = JsonDocument.Parse(runtimeManifestJson);
        string metadataDirectory = Path.Combine(stagingContainer, "metadata");
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(metadataDirectory, "session-metadata", RuntimeFileArea.Configuration);
        RuntimePathUtilities.ThrowIfReparsePoint(metadataDirectory, "session-metadata", RuntimeFileArea.Configuration, missingIsAllowed: false);
        string path = Path.Combine(metadataDirectory, "runtime-manifest.json");
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true);
        writer.Write(runtimeManifestJson);
        writer.Write('\n');
        writer.Flush();
        stream.Flush(flushToDisk: true);
        SetPrivateFileMode(path);
    }

    private static void WriteReboundMarker(
        SqliteDatabaseOptions options,
        string sessionId,
        SessionRootProtectedMarker marker)
    {
        string metadataDirectory = Path.Combine(ContainerPath(options, sessionId), "metadata");
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(metadataDirectory, "restored-session-metadata", RuntimeFileArea.Configuration);
        RuntimePathUtilities.ThrowIfReparsePoint(metadataDirectory, "restored-session-metadata", RuntimeFileArea.Configuration, missingIsAllowed: false);
        string path = Path.Combine(metadataDirectory, "session-root.json");

        if (OperatingSystem.IsLinux())
        {
            using Microsoft.Win32.SafeHandles.SafeFileHandle metadata = LinuxFileOperations.OpenDirectory(metadataDirectory);
            using Microsoft.Win32.SafeHandles.SafeFileHandle existing = LinuxFileOperations.OpenRegularFileAt(
                metadata,
                "session-root.json",
                readOnly: true,
                create: false,
                exclusive: false);
            LinuxFileOperations.FileIdentity existingIdentity = LinuxFileOperations.ReadIdentity(existing);
            if (existingIdentity.UserId != LinuxFileOperations.CurrentUserId || existingIdentity.LinkCount != 1)
                throw new SessionRuntimeException(SessionRuntimeResultCodes.SessionRootInvalid, "The protected SessionRoot marker is not private.");

            string temporaryName = $".session-root-{Guid.NewGuid():N}.part";
            try
            {
                using Microsoft.Win32.SafeHandles.SafeFileHandle temporary = LinuxFileOperations.OpenRegularFileAt(
                    metadata,
                    temporaryName,
                    readOnly: false,
                    create: true,
                    exclusive: true);
                LinuxFileOperations.ApplyPrivateMode(temporary);
                using (FileStream stream = LinuxFileOperations.CreateFileStream(temporary, FileAccess.Write))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true))
                {
                    writer.Write(JsonSerializer.Serialize(marker, JsonOptions));
                    writer.Write('\n');
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                    LinuxFileOperations.Sync(temporary);
                }

                LinuxFileOperations.RenameAt(metadata, temporaryName, "session-root.json");
                LinuxFileOperations.Sync(metadata);
            }
            finally
            {
                LinuxFileOperations.UnlinkAtIfExists(metadata, temporaryName);
            }

            return;
        }

        string temporaryPath = path + $".{Guid.NewGuid():N}.part";
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true))
            {
                writer.Write(JsonSerializer.Serialize(marker, JsonOptions));
                writer.Write('\n');
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static string ReadRuntimeManifest(SqliteDatabaseOptions options, string sessionId)
    {
        string path = RuntimeManifestPath(options, sessionId);
        try
        {
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json) || json.Length > PersistenceLimits.SessionRuntimeManifestMaxLength)
                throw new InvalidDataException("The runtime manifest is outside the permitted size.");
            using JsonDocument _ = JsonDocument.Parse(json);
            return json;
        }
        catch (JsonException exception)
        {
            throw new SessionRuntimeException(SessionRuntimeResultCodes.SessionRootInvalid, "The runtime manifest is malformed.", exception);
        }
        catch (IOException exception)
        {
            throw new SessionRuntimeException(SessionRuntimeResultCodes.SessionRootInvalid, "The runtime manifest cannot be read.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new SessionRuntimeException(SessionRuntimeResultCodes.SessionRootInvalid, "The runtime manifest cannot be read.", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new SessionRuntimeException(SessionRuntimeResultCodes.SessionRootInvalid, "The runtime manifest is outside the permitted size.", exception);
        }
    }

    public static void ValidateSessionId(string sessionId)
    {
        if (sessionId.Length is < 6 or > 64 || !sessionId.StartsWith("sess_", StringComparison.Ordinal) ||
            sessionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new ArgumentException("The Session ID is invalid.", nameof(sessionId));
    }

    private static string RuntimeManifestPath(SqliteDatabaseOptions options, string sessionId)
    {
        string container = ContainerPath(options, sessionId);
        string metadataDirectory = Path.Combine(container, "metadata");
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(metadataDirectory, "session-metadata", RuntimeFileArea.Configuration);
        RuntimePathUtilities.ThrowIfReparsePoint(metadataDirectory, "session-metadata", RuntimeFileArea.Configuration, missingIsAllowed: false);
        string path = Path.Combine(metadataDirectory, "runtime-manifest.json");
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(path, "protected-runtime-manifest", RuntimeFileArea.Configuration);
        RuntimePathUtilities.ThrowIfReparsePoint(path, "protected-runtime-manifest", RuntimeFileArea.Configuration, missingIsAllowed: false);
        RuntimePathUtilities.ThrowIfHardLink(path, "protected-runtime-manifest", RuntimeFileArea.Configuration);
        return path;
    }

    public static bool SameRootIdentity(SessionRootProtectedMarker marker, string rootPath)
    {
        if (!OperatingSystem.IsLinux()) return true;
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = LinuxFileOperations.OpenDirectory(rootPath);
        LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(handle);
        return identity.DeviceMajor == marker.RootDeviceMajor &&
            identity.DeviceMinor == marker.RootDeviceMinor &&
            identity.Inode == marker.RootInode;
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
