using System.Text.Json;
using CloudEmuera.Application.Saves;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Sessions;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.RuntimeAdapter;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Saves;

/// <summary>
/// Descriptor-relative accessor for the private SessionRoot and its native
/// save root. User paths are parsed before every operation and are never used
/// to address a filesystem entry directly.
/// </summary>
public sealed class LinuxSessionSaveRootAccessor(SqliteDatabaseOptions databaseOptions) : ISessionSaveRootAccessor
{
    private const int MaximumListedFiles = 4096;
    private const long MaximumListBytes = 8L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<SessionSaveRootSnapshot> ListAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using RootAccess root = OpenRoot(sessionId, expectedLayout: null);
        List<SessionSaveItem> items = [];
        Enumerate(root.SaveRoot, root.Layout, prefix: null, depth: 0, items, cancellationToken);
        items.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        if (!EmueraSavePathPolicy.AreCollisionFree(items.Select(item => item.Path)))
            throw InvalidRoot("The SessionRoot save tree contains a Unicode or case collision.");
        return Task.FromResult(new SessionSaveRootSnapshot(root.Layout == RuntimeSaveLayout.Root ? SessionSaveLayout.Root : SessionSaveLayout.SavDirectory, items));
    }

    public Task<SessionSaveFileRead?> OpenReadAsync(string sessionId, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RootAccess root = OpenRoot(sessionId, expectedLayout: null);
        try
        {
            using SafeFileHandle parent = OpenSaveParent(root.SaveRoot, path, root.Layout, out string leaf, out EmueraSavePath parsed);
            SafeFileHandle? file = LinuxFileOperations.TryOpenRegularFileAt(parent, leaf, readOnly: true);
            if (file is null)
                return Task.FromResult<SessionSaveFileRead?>(null);
            try
            {
                LinuxFileOperations.FileIdentity identity = ValidateFile(file);
                FileStream content = LinuxFileOperations.CreateFileStream(file, FileAccess.Read, 64 * 1024, isAsync: false);
                file = null;
                return Task.FromResult<SessionSaveFileRead?>(new SessionSaveFileRead(
                    parsed.Value,
                    ToApplicationKind(parsed.Kind),
                    identity.Size,
                    ToDateTimeOffset(identity),
                    content));
            }
            finally
            {
                file?.Dispose();
            }
        }
        catch (SessionSaveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlitePathException or IOException or UnauthorizedAccessException)
        {
            throw InvalidRoot("The SessionRoot save entry could not be opened safely.", exception);
        }
        finally
        {
            // The stream owns the file descriptor; root handles are no longer
            // needed after open. A successful stream is returned with only its
            // fixed target fd alive.
            root.Dispose();
        }
    }

    public Task<SessionSaveFileObservation?> InspectFileAsync(string sessionId, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using RootAccess root = OpenRoot(sessionId, expectedLayout: null);
            using SafeFileHandle parent = OpenSaveParent(root.SaveRoot, path, root.Layout, out string leaf, out EmueraSavePath parsed);
            SafeFileHandle? file = LinuxFileOperations.TryOpenRegularFileAt(parent, leaf, readOnly: true);
            if (file is null)
                return Task.FromResult<SessionSaveFileObservation?>(null);
            using (file)
            {
                LinuxFileOperations.FileIdentity identity = ValidateFile(file);
                return Task.FromResult<SessionSaveFileObservation?>(ToObservation(parsed, identity));
            }
        }
        catch (SessionSaveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlitePathException or IOException or UnauthorizedAccessException)
        {
            throw InvalidRoot("The SessionRoot save entry could not be inspected safely.", exception);
        }
    }

    public Task<SessionSaveStaging> CreateStagingAsync(
        string sessionId,
        string operationId,
        string targetPath,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using RootAccess root = OpenRoot(sessionId, expectedLayout: null);
        _ = ParseUserPath(root.Layout, targetPath);
        ValidateOperationIdentity(sessionId, operationId, actorUserId);
        using SafeFileHandle dataRoot = LinuxFileOperations.OpenDirectory(databaseOptions.DataRoot);
        using SafeFileHandle container = LinuxFileOperations.OpenDirectoryPath(dataRoot, $"sessions/{sessionId}", create: false);
        using SafeFileHandle metadata = LinuxFileOperations.TryOpenDirectoryAt(container, "metadata")
            ?? throw InvalidRoot("The SessionRoot metadata directory is missing.");
        using SafeFileHandle operations = LinuxFileOperations.OpenOrCreateDirectoryAt(metadata, "save-operations");
        using SafeFileHandle operationDirectory = LinuxFileOperations.CreateDirectoryAt(operations, operationId);

        SaveOperationMarker marker = new(1, sessionId, actorUserId, operationId, targetPath, "IMPORT");
        using (SafeFileHandle markerHandle = LinuxFileOperations.OpenRegularFileAt(operationDirectory, "operation.json", readOnly: false, create: true, exclusive: true))
        using (FileStream markerStream = LinuxFileOperations.CreateFileStream(markerHandle, FileAccess.Write))
        {
            JsonSerializer.Serialize(markerStream, marker, JsonOptions);
            markerStream.WriteByte((byte)'\n');
            markerStream.Flush(flushToDisk: true);
        }
        LinuxFileOperations.Sync(operationDirectory);

        SafeFileHandle payloadHandle = LinuxFileOperations.OpenRegularFileAt(operationDirectory, "payload.tmp", readOnly: false, create: true, exclusive: true);
        FileStream payload = LinuxFileOperations.CreateFileStream(payloadHandle, FileAccess.Write, 64 * 1024, isAsync: false);
        return Task.FromResult(new SessionSaveStaging(
            operationId,
            $"metadata/save-operations/{operationId}/payload.tmp",
            payload));
    }

    public Task FinalizeStagingAsync(string sessionId, string operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SafeFileHandle operationDirectory = OpenOperationDirectory(sessionId, operationId, out SaveOperationMarker marker);
        if (!string.Equals(marker.SessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(marker.OperationId, operationId, StringComparison.Ordinal))
            throw InvalidRoot("The save operation marker identity is invalid.");
        using SafeFileHandle payload = LinuxFileOperations.OpenRegularFileAt(operationDirectory, "payload.tmp", readOnly: true, create: false, exclusive: false);
        ValidateFile(payload);
        LinuxFileOperations.Sync(payload);
        LinuxFileOperations.Sync(operationDirectory);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenStagingReadAsync(string sessionId, string operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SafeFileHandle operationDirectory = OpenOperationDirectory(sessionId, operationId, out _);
        SafeFileHandle? payload = LinuxFileOperations.OpenRegularFileAt(operationDirectory, "payload.tmp", readOnly: true, create: false, exclusive: false);
        try
        {
            ValidateFile(payload);
            FileStream stream = LinuxFileOperations.CreateFileStream(payload, FileAccess.Read, 64 * 1024, isAsync: false);
            payload = null;
            return Task.FromResult<Stream>(stream);
        }
        finally
        {
            payload?.Dispose();
        }
    }

    public Task<SessionSavePublishResult> PublishAsync(string sessionId, string operationId, string targetPath, bool replace, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using RootAccess root = OpenRoot(sessionId, expectedLayout: null);
            using SafeFileHandle operationDirectory = OpenOperationDirectory(sessionId, operationId, out SaveOperationMarker marker);
            if (!string.Equals(marker.SessionId, sessionId, StringComparison.Ordinal) ||
                !string.Equals(marker.OperationId, operationId, StringComparison.Ordinal) ||
                !string.Equals(marker.Type, "IMPORT", StringComparison.Ordinal) ||
                !string.Equals(marker.TargetPath, targetPath, StringComparison.Ordinal))
                throw InvalidRoot("The save operation marker identity is invalid.");
            EmueraSavePath parsed = ParseUserPath(root.Layout, targetPath);
            using SafeFileHandle targetParent = OpenSaveParent(root.SaveRoot, parsed, out string targetLeaf);
            using SafeFileHandle? existing = LinuxFileOperations.TryOpenRegularFileAt(targetParent, targetLeaf, readOnly: true);
            if (existing is not null)
                ValidateFile(existing);
            if (existing is not null && !replace)
                throw new SessionSaveException(SaveErrorCodes.TargetExists, "保存目标已存在。", 409);

            using SafeFileHandle payload = LinuxFileOperations.OpenRegularFileAt(operationDirectory, "payload.tmp", readOnly: false, create: false, exclusive: false);
            ValidateFile(payload);
            bool moved = false;
            try
            {
                LinuxFileOperations.RenameBetweenDirectories(operationDirectory, "payload.tmp", targetParent, targetLeaf);
                moved = true;
                LinuxFileOperations.Sync(operationDirectory);
                LinuxFileOperations.Sync(targetParent);
                using SafeFileHandle published = LinuxFileOperations.OpenRegularFileAt(targetParent, targetLeaf, readOnly: true, create: false, exclusive: false);
                LinuxFileOperations.FileIdentity identity = ValidateFile(published);
                return Task.FromResult(new SessionSavePublishResult(existing is null, ToObservation(parsed, identity)));
            }
            catch (Exception exception) when (moved)
            {
                throw new SessionSaveException(SaveErrorCodes.RecoveryRequired, "存档发布等待恢复。", 503, exception);
            }
        }
        catch (SessionSaveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlitePathException or IOException or UnauthorizedAccessException)
        {
            throw InvalidRoot("The SessionRoot save entry could not be published safely.", exception);
        }
    }

    public Task<SessionSaveRenameResult> RenameAsync(string sessionId, string sourcePath, string targetPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using RootAccess root = OpenRoot(sessionId, expectedLayout: null);
            EmueraSavePath source = ParseUserPath(root.Layout, sourcePath);
            EmueraSavePath target = ParseUserPath(root.Layout, targetPath);
            if (string.Equals(source.Value, target.Value, StringComparison.Ordinal))
            {
                using SafeFileHandle sameParent = OpenSaveParent(root.SaveRoot, source, out string sameLeaf);
                using SafeFileHandle sameFile = LinuxFileOperations.OpenRegularFileAt(sameParent, sameLeaf, readOnly: true, create: false, exclusive: false);
                return Task.FromResult(new SessionSaveRenameResult(ToObservation(source, ValidateFile(sameFile))));
            }

            using SafeFileHandle sourceParent = OpenSaveParent(root.SaveRoot, source, out string sourceLeaf);
            using SafeFileHandle sourceFile = LinuxFileOperations.OpenRegularFileAt(sourceParent, sourceLeaf, readOnly: true, create: false, exclusive: false);
            LinuxFileOperations.FileIdentity sourceIdentity = ValidateFile(sourceFile);
            using SafeFileHandle targetParent = OpenSaveParent(root.SaveRoot, target, out string targetLeaf);
            using SafeFileHandle? existing = LinuxFileOperations.TryOpenRegularFileAt(targetParent, targetLeaf, readOnly: true);
            if (existing is not null)
                throw new SessionSaveException(SaveErrorCodes.TargetExists, "重命名目标已存在。", 409);
            bool moved = false;
            try
            {
                LinuxFileOperations.RenameBetweenDirectoriesNoReplace(sourceParent, sourceLeaf, targetParent, targetLeaf);
                moved = true;
                LinuxFileOperations.Sync(sourceParent);
                if (!ReferenceEquals(sourceParent, targetParent))
                    LinuxFileOperations.Sync(targetParent);
            }
            catch (LinuxFileOperations.LinuxFileOperationException exception) when (exception.Error == 17)
            {
                throw new SessionSaveException(SaveErrorCodes.TargetExists, "重命名目标已存在。", 409, exception);
            }
            catch (Exception exception) when (moved)
            {
                throw new SessionSaveException(SaveErrorCodes.RecoveryRequired, "存档重命名等待恢复。", 503, exception);
            }
            try
            {
                using SafeFileHandle published = LinuxFileOperations.OpenRegularFileAt(targetParent, targetLeaf, readOnly: true, create: false, exclusive: false);
                LinuxFileOperations.FileIdentity targetIdentity = ValidateFile(published);
                if (!targetIdentity.SameObject(sourceIdentity))
                    throw InvalidRoot("The save file identity changed during rename.");
                return Task.FromResult(new SessionSaveRenameResult(ToObservation(target, targetIdentity)));
            }
            catch (Exception exception)
            {
                throw new SessionSaveException(SaveErrorCodes.RecoveryRequired, "存档重命名等待恢复。", 503, exception);
            }
        }
        catch (SessionSaveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlitePathException or IOException or UnauthorizedAccessException)
        {
            throw InvalidRoot("The SessionRoot save entry could not be renamed safely.", exception);
        }
    }

    public Task<bool> DeleteAsync(string sessionId, string sourcePath, string expectedIdentityJson, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using RootAccess root = OpenRoot(sessionId, expectedLayout: null);
            EmueraSavePath source = ParseUserPath(root.Layout, sourcePath);
            using SafeFileHandle sourceParent = OpenSaveParent(root.SaveRoot, source, out string sourceLeaf);
            LinuxFileOperations.FileIdentity? expected = ParseIdentity(expectedIdentityJson);
            bool removed = LinuxFileOperations.UnlinkRegularFileAt(sourceParent, sourceLeaf, expected);
            if (!removed)
                return Task.FromResult(false);
            try
            {
                LinuxFileOperations.Sync(sourceParent);
                return Task.FromResult(true);
            }
            catch (Exception exception)
            {
                throw new SessionSaveException(SaveErrorCodes.RecoveryRequired, "存档删除等待恢复。", 503, exception);
            }
        }
        catch (SessionSaveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlitePathException or IOException or UnauthorizedAccessException)
        {
            throw InvalidRoot("The SessionRoot save entry could not be deleted safely.", exception);
        }
    }

    public Task CleanupStagingAsync(string sessionId, string operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SafeFileHandle dataRoot = LinuxFileOperations.OpenDirectory(databaseOptions.DataRoot);
        using SafeFileHandle container = LinuxFileOperations.OpenDirectoryPath(dataRoot, $"sessions/{sessionId}", create: false);
        using SafeFileHandle metadata = LinuxFileOperations.TryOpenDirectoryAt(container, "metadata")
            ?? throw InvalidRoot("The SessionRoot metadata directory is missing.");
        SafeFileHandle? operations = LinuxFileOperations.TryOpenDirectoryAt(metadata, "save-operations");
        if (operations is null)
            return Task.CompletedTask;
        using (operations)
        {
            SafeFileHandle? operationDirectory = TryOpenOperationDirectory(operations, operationId, out SaveOperationMarker? marker);
            if (operationDirectory is null)
                return Task.CompletedTask;
            LinuxFileOperations.FileIdentity operationIdentity = LinuxFileOperations.ReadIdentity(operationDirectory);
            using (operationDirectory)
            {
                if (marker is null || !string.Equals(marker.SessionId, sessionId, StringComparison.Ordinal) || !string.Equals(marker.OperationId, operationId, StringComparison.Ordinal))
                    throw InvalidRoot("The save operation marker identity is invalid.");
            }
            LinuxFileOperations.TryDeleteTreeAt(operations, operationId, expectedIdentity: operationIdentity);
            LinuxFileOperations.Sync(operations);
        }
        return Task.CompletedTask;
    }

    private RootAccess OpenRoot(string sessionId, RuntimeSaveLayout? expectedLayout)
    {
        ValidateSessionId(sessionId);
        if (!OperatingSystem.IsLinux())
            throw InvalidRoot("Native save management requires the Linux descriptor boundary.");
        try
        {
            using SafeFileHandle dataRoot = LinuxFileOperations.OpenDirectory(databaseOptions.DataRoot);
            SafeFileHandle root = LinuxFileOperations.OpenDirectoryPath(dataRoot, $"sessions/{sessionId}/root", create: false);
            try
            {
                LinuxFileOperations.FileIdentity rootIdentity = LinuxFileOperations.ReadIdentity(root);
                if (!rootIdentity.IsDirectory || rootIdentity.UserId != LinuxFileOperations.CurrentUserId || !IsPrivateDirectory(rootIdentity))
                    throw InvalidRoot("The SessionRoot directory identity or mode is invalid.");
                SessionRootProtectedMarker marker = SessionRootProtectedMarkerStore.Read(databaseOptions, sessionId);
                if (!string.Equals(marker.SessionId, sessionId, StringComparison.Ordinal) ||
                    marker.RootInode != rootIdentity.Inode || marker.RootDeviceMajor != rootIdentity.DeviceMajor || marker.RootDeviceMinor != rootIdentity.DeviceMinor)
                    throw InvalidRoot("The protected SessionRoot marker does not match the root identity.");
                using SafeFileHandle configuration = LinuxFileOperations.OpenRegularFileAt(root, "emuera.config", readOnly: true, create: false, exclusive: false);
                using FileStream configurationStream = LinuxFileOperations.CreateFileStream(configuration, FileAccess.Read);
                RuntimeSaveLayout layout = EmueraSaveLayoutInspector.Inspect(configurationStream);
                if (marker.SaveLayout != layout || (expectedLayout is RuntimeSaveLayout expected && expected != layout))
                    throw InvalidRoot("The SessionRoot save layout does not match its protected binding.");
                SafeFileHandle saveRoot = layout == RuntimeSaveLayout.Root
                    ? LinuxFileOperations.DuplicateDirectory(root)
                    : LinuxFileOperations.TryOpenDirectoryAt(root, "sav")
                        ?? throw InvalidRoot("The SessionRoot sav directory is missing.");
                LinuxFileOperations.FileIdentity saveIdentity = LinuxFileOperations.ReadIdentity(saveRoot);
                if (!saveIdentity.IsDirectory || saveIdentity.UserId != LinuxFileOperations.CurrentUserId || !IsPrivateDirectory(saveIdentity))
                {
                    saveRoot.Dispose();
                    throw InvalidRoot("The native save root identity or mode is invalid.");
                }
                return new RootAccess(root, saveRoot, layout);
            }
            catch
            {
                root.Dispose();
                throw;
            }
        }
        catch (SessionSaveException)
        {
            throw;
        }
        catch (SessionRuntimeException exception)
        {
            throw InvalidRoot("The SessionRoot failed its save-file safety checks.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or RuntimePathException)
        {
            throw InvalidRoot("The SessionRoot failed its save-file safety checks.", exception);
        }
    }

    private static void Enumerate(
        SafeFileHandle directory,
        RuntimeSaveLayout layout,
        string? prefix,
        int depth,
        List<SessionSaveItem> items,
        CancellationToken cancellationToken)
    {
        if (depth > EmueraSavePathPolicy.MaximumDirectoryDepth)
            throw InvalidRoot("The native save directory depth is excessive.");
        string descriptorPath = LinuxFileOperations.GetProcFileDescriptorPath(directory);
        foreach (string entryPath in Directory.EnumerateFileSystemEntries(descriptorPath).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string leaf = Path.GetFileName(entryPath);
            if (string.IsNullOrEmpty(leaf))
                continue;
            using SafeFileHandle entry = LinuxFileOperations.OpenEntryAt(directory, leaf);
            LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(entry);
            string logicalPath = string.IsNullOrEmpty(prefix) ? leaf : $"{prefix}/{leaf}";
            if (identity.IsDirectory)
            {
                if (layout == RuntimeSaveLayout.SavDirectory && EmueraSavePathPolicy.IsAllowedSaveDirectorySegment(leaf))
                {
                    using SafeFileHandle child = LinuxFileOperations.TryOpenDirectoryAt(directory, leaf)
                        ?? throw InvalidRoot("The native save directory disappeared during enumeration.");
                    ValidateDirectory(child);
                    Enumerate(child, layout, logicalPath, depth + 1, items, cancellationToken);
                }
                continue;
            }

            if (!identity.IsRegularFile)
                throw InvalidRoot("The native save tree contains a link or special file.");
            if (identity.LinkCount != 1)
                throw InvalidRoot("The native save tree contains a multiply-linked file.");
            if (!EmueraSavePathPolicy.IsAllowedSaveFileName(leaf))
                continue;
            EmueraSavePathPolicy.Parse(layout, logicalPath);
            using SafeFileHandle file = LinuxFileOperations.OpenRegularFileAt(directory, leaf, readOnly: true, create: false, exclusive: false);
            LinuxFileOperations.FileIdentity fixedIdentity = ValidateFile(file);
            if (!identity.SameObject(fixedIdentity))
                throw InvalidRoot("A save file changed during enumeration.");
            items.Add(new SessionSaveItem(
                logicalPath,
                ToApplicationKind(EmueraSavePathPolicy.ClassifyFileName(leaf)),
                fixedIdentity.Size,
                ToDateTimeOffset(fixedIdentity)));
            if (items.Count > MaximumListedFiles || items.Sum(static item => (long)item.Path.Length) > MaximumListBytes)
                throw new SessionSaveException(SaveErrorCodes.StorageFailure, "存档列表超过容量限制。", 503);
        }
    }

    private static SafeFileHandle OpenSaveParent(SafeFileHandle saveRoot, string path, RuntimeSaveLayout layout, out string leaf, out EmueraSavePath parsed)
    {
        parsed = ParseUserPath(layout, path);
        return OpenSaveParent(saveRoot, parsed, out leaf);
    }

    private static EmueraSavePath ParseUserPath(RuntimeSaveLayout layout, string path)
    {
        try
        {
            return EmueraSavePathPolicy.Parse(layout, path);
        }
        catch (RuntimePathException exception)
        {
            throw new SessionSaveException(SaveErrorCodes.PathInvalid, "存档路径无效。", 400, exception);
        }
    }

    private static SafeFileHandle OpenSaveParent(SafeFileHandle saveRoot, EmueraSavePath parsed, out string leaf)
    {
        leaf = parsed.FileName;
        SafeFileHandle current = LinuxFileOperations.DuplicateDirectory(saveRoot);
        try
        {
            foreach (string segment in (parsed.ParentPath ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                SafeFileHandle next = LinuxFileOperations.TryOpenDirectoryAt(current, segment)
                    ?? throw new SessionSaveException(SaveErrorCodes.NotFound, "存档文件不存在。", 404);
                ValidateDirectory(next);
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

    private SafeFileHandle OpenOperationDirectory(string sessionId, string operationId, out SaveOperationMarker marker)
    {
        ValidateSessionId(sessionId);
        ValidateOperationId(operationId);
        using SafeFileHandle dataRoot = LinuxFileOperations.OpenDirectory(databaseOptions.DataRoot);
        using SafeFileHandle container = LinuxFileOperations.OpenDirectoryPath(dataRoot, $"sessions/{sessionId}", create: false);
        using SafeFileHandle metadata = LinuxFileOperations.TryOpenDirectoryAt(container, "metadata")
            ?? throw InvalidRoot("The SessionRoot metadata directory is missing.");
        using SafeFileHandle operations = LinuxFileOperations.TryOpenDirectoryAt(metadata, "save-operations")
            ?? throw new SessionSaveException(SaveErrorCodes.RecoveryRequired, "存档操作等待恢复。", 503);
        SafeFileHandle operationDirectory = LinuxFileOperations.TryOpenDirectoryAt(operations, operationId)
            ?? throw new SessionSaveException(SaveErrorCodes.RecoveryRequired, "存档操作暂存不存在。", 503);
        try
        {
            ValidateDirectory(operationDirectory);
            marker = ReadMarker(operationDirectory);
            return operationDirectory;
        }
        catch
        {
            operationDirectory.Dispose();
            throw;
        }
    }

    private static SafeFileHandle? TryOpenOperationDirectory(SafeFileHandle operations, string operationId, out SaveOperationMarker? marker)
    {
        ValidateOperationId(operationId);
        SafeFileHandle? operationDirectory = LinuxFileOperations.TryOpenDirectoryAt(operations, operationId);
        if (operationDirectory is null)
        {
            marker = null;
            return null;
        }
        try
        {
            ValidateDirectory(operationDirectory);
            marker = ReadMarker(operationDirectory);
        }
        catch
        {
            operationDirectory.Dispose();
            throw;
        }
        return operationDirectory;
    }

    private static SaveOperationMarker ReadMarker(SafeFileHandle operationDirectory)
    {
        using SafeFileHandle markerHandle = LinuxFileOperations.OpenRegularFileAt(operationDirectory, "operation.json", readOnly: true, create: false, exclusive: false);
        using FileStream stream = LinuxFileOperations.CreateFileStream(markerHandle, FileAccess.Read);
        return JsonSerializer.Deserialize<SaveOperationMarker>(stream, JsonOptions)
            ?? throw InvalidRoot("The save operation marker is empty.");
    }

    private static LinuxFileOperations.FileIdentity ValidateFile(SafeFileHandle file)
    {
        LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(file);
        int permissions = identity.Mode & 0x1FF;
        // Save files may retain a readable group/other mode from the native
        // runtime, but they must never be writable or executable by anyone
        // other than the API user (and must not be executable by the owner).
        if (!identity.IsRegularFile || identity.UserId != LinuxFileOperations.CurrentUserId || identity.LinkCount != 1 || (permissions & 0x05B) != 0)
            throw InvalidRoot("A native save entry is not a private regular file.");
        if (identity.Size < 0)
            throw InvalidRoot("A native save entry has an invalid size.");
        return identity;
    }

    private static void ValidateDirectory(SafeFileHandle directory)
    {
        LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(directory);
        if (!identity.IsDirectory || identity.UserId != LinuxFileOperations.CurrentUserId || !IsPrivateDirectory(identity))
            throw InvalidRoot("A native save directory is not private and regular.");
    }

    private static bool IsPrivateDirectory(LinuxFileOperations.FileIdentity identity) =>
        (identity.Mode & 0x1FF) == 0x1C0;

    private static DateTimeOffset ToDateTimeOffset(LinuxFileOperations.FileIdentity identity)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(identity.ModifyTimeSeconds).AddTicks(identity.ModifyTimeNanoseconds / 100);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static SessionSaveFileObservation ToObservation(EmueraSavePath path, LinuxFileOperations.FileIdentity identity) =>
        new(path.Value, ToApplicationKind(path.Kind), identity.Size, ToDateTimeOffset(identity), IdentityJson(identity));

    private static string IdentityJson(LinuxFileOperations.FileIdentity identity) =>
        JsonSerializer.Serialize(new { deviceMajor = identity.DeviceMajor, deviceMinor = identity.DeviceMinor, inode = identity.Inode, size = identity.Size }, JsonOptions);

    private static LinuxFileOperations.FileIdentity? ParseIdentity(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            uint major = root.GetProperty("deviceMajor").GetUInt32();
            uint minor = root.GetProperty("deviceMinor").GetUInt32();
            ulong inode = root.GetProperty("inode").GetUInt64();
            long size = root.GetProperty("size").GetInt64();
            return new LinuxFileOperations.FileIdentity(major, minor, inode, 0x8000, 1, LinuxFileOperations.CurrentUserId, size, 0, 0);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw InvalidRoot("The save operation identity marker is invalid.", exception);
        }
    }

    private static SessionSaveFileKind ToApplicationKind(EmueraSaveFileKind kind) => kind switch
    {
        EmueraSaveFileKind.Normal => SessionSaveFileKind.Normal,
        EmueraSaveFileKind.Global => SessionSaveFileKind.Global,
        EmueraSaveFileKind.AuxiliaryText => SessionSaveFileKind.AuxiliaryText,
        EmueraSaveFileKind.AuxiliaryImage => SessionSaveFileKind.AuxiliaryImage,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void ValidateSessionId(string sessionId)
    {
        if (sessionId.Length is < 6 or > 64 || !sessionId.StartsWith("sess_", StringComparison.Ordinal) ||
            sessionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new SessionSaveException(SaveErrorCodes.SessionRootInvalid, "SessionRoot 无效。", 503);
    }

    private static void ValidateOperationIdentity(string sessionId, string operationId, string actorUserId)
    {
        ValidateOperationId(operationId);
        if (actorUserId.Length is < 5 or > 64 || !actorUserId.StartsWith("usr_", StringComparison.Ordinal) ||
            actorUserId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new SessionSaveException(SaveErrorCodes.SessionRootInvalid, "存档操作身份无效。", 503);
        _ = sessionId;
    }

    private static void ValidateOperationId(string operationId)
    {
        if (operationId.Length is < 6 or > 64 || !operationId.StartsWith("sfop_", StringComparison.Ordinal) ||
            operationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new SessionSaveException(SaveErrorCodes.RecoveryRequired, "存档操作身份无效。", 503);
    }

    private static SessionSaveException InvalidRoot(string message, Exception? innerException = null) =>
        new(SaveErrorCodes.SessionRootInvalid, "SessionRoot 无效。", 503, innerException);

    private sealed record RootAccess(SafeFileHandle Root, SafeFileHandle SaveRoot, RuntimeSaveLayout Layout) : IDisposable
    {
        public void Dispose()
        {
            SaveRoot.Dispose();
            Root.Dispose();
        }
    }

    private sealed record SaveOperationMarker(
        int SchemaVersion,
        string SessionId,
        string ActorUserId,
        string OperationId,
        string TargetPath,
        string Type);
}
