using System.Text.Json;
using System.Text.Json.Serialization;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.Infrastructure.Sessions;

/// <summary>
/// Revalidates a persisted SessionRoot immediately before a Worker receives
/// write access. The database stores a relative path; this component resolves
/// it below the configured data root and repeats the no-reparse/regular-file
/// checks instead of trusting an earlier create operation.
/// </summary>
public sealed class SessionRootRuntimeInspector(SqliteDatabaseOptions databaseOptions) : ISessionRootRuntimeInspector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Task<SessionRootRuntimeDescriptor> InspectAsync(
        SessionRuntimeLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            databaseOptions.Validate();
            string root = ResolveSessionRoot(lease.Binding.SessionRootPath);
            RuntimePathUtilities.ValidateNoReparsePointsAlongPath(root, "session-root", RuntimeFileArea.GameContent);
            RuntimePathUtilities.ThrowIfReparsePoint(root, "session-root", RuntimeFileArea.GameContent, missingIsAllowed: false);
            if (!Directory.Exists(root))
                throw InvalidRoot("The SessionRoot directory does not exist.");
            EnsurePrivateDirectory(root);
            SessionRootProtectedMarker protectedMarker = SessionRootProtectedMarkerStore.Read(databaseOptions, lease.Binding.SessionId);
            ValidateProtectedMarker(lease, root, protectedMarker);
            ValidateProtectedRuntimeManifest(lease, root);
            ValidateTree(root);

            string metadataPath = Path.Combine(root, SessionRootLayoutBuilder.BindingMetadataFileName);
            RuntimePathUtilities.ThrowIfReparsePoint(metadataPath, SessionRootLayoutBuilder.BindingMetadataFileName, RuntimeFileArea.GameContent, missingIsAllowed: false);
            RuntimePathUtilities.ThrowIfHardLink(metadataPath, SessionRootLayoutBuilder.BindingMetadataFileName, RuntimeFileArea.GameContent);
            BindingMetadata metadata = ReadMetadata(metadataPath);
            if (metadata.SchemaVersion is not (1 or 2) ||
                (metadata.ManifestDigest is not null && metadata.ManifestDigest.Length > 128) ||
                metadata.SaveLayout is not (RuntimeSaveLayout.Root or RuntimeSaveLayout.SavDirectory))
                throw InvalidRoot("The SessionRoot binding marker is invalid.");

            int saveLayout = (int)metadata.SaveLayout;
            if (lease.Binding.SaveLayout != saveLayout)
                throw InvalidRoot("The SessionRoot save layout does not match its persisted binding.");
            if (!string.IsNullOrWhiteSpace(lease.Binding.SessionRootManifestDigest) &&
                !string.Equals(lease.Binding.SessionRootManifestDigest, metadata.ManifestDigest, StringComparison.OrdinalIgnoreCase))
                throw InvalidRoot("The SessionRoot manifest digest does not match its persisted binding.");
            if ((protectedMarker.SourceManifestDigest is not null && metadata.ManifestDigest is not null &&
                 !string.Equals(protectedMarker.SourceManifestDigest, metadata.ManifestDigest, StringComparison.OrdinalIgnoreCase)) ||
                (protectedMarker.MaterializedManifestDigest is not null && metadata.ManifestDigest is not null &&
                 !string.Equals(protectedMarker.MaterializedManifestDigest, metadata.ManifestDigest, StringComparison.OrdinalIgnoreCase)))
                throw InvalidRoot("The protected and runtime SessionRoot markers disagree.");

            string configurationPath = Path.Combine(root, "emuera.config");
            RuntimePathUtilities.ThrowIfReparsePoint(configurationPath, "emuera.config", RuntimeFileArea.Configuration, missingIsAllowed: false);
            RuntimePathUtilities.ThrowIfHardLink(configurationPath, "emuera.config", RuntimeFileArea.Configuration);
            RuntimeSaveLayout actualLayout = EmueraSaveLayoutInspector.InspectFile(configurationPath);
            if (actualLayout != metadata.SaveLayout)
                throw InvalidRoot("The SessionRoot emuera.config layout does not match its binding marker.");

            _ = RuntimePaths.ForExistingSessionRoot(root, actualLayout);
            return Task.FromResult(new SessionRootRuntimeDescriptor(
                root,
                saveLayout,
                metadata.ManifestDigest ?? "path-v2",
                lease.Binding.CompatibilityProfile));
        }
        catch (SessionRuntimeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or RuntimePathException)
        {
            throw new SessionRuntimeException(
                SessionRuntimeResultCodes.SessionRootInvalid,
                "The SessionRoot failed its runtime safety checks.",
                exception);
        }
    }

    private string ResolveSessionRoot(string persistedPath)
    {
        if (string.IsNullOrWhiteSpace(persistedPath) || persistedPath.Contains('\0') || Path.IsPathRooted(persistedPath) ||
            persistedPath.Contains('\\') || persistedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            throw InvalidRoot("The persisted SessionRoot path is not a safe relative path.");

        string root = Path.GetFullPath(Path.Combine(databaseOptions.DataRoot, persistedPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!RuntimePathUtilities.IsStrictlyWithin(root, databaseOptions.DataRoot))
            throw InvalidRoot("The persisted SessionRoot path escapes the data root.");
        return root;
    }

    private static void ValidateTree(string root)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            RuntimePathUtilities.ThrowIfReparsePoint(entry.FullName, Path.GetRelativePath(root, entry.FullName), RuntimeFileArea.GameContent, missingIsAllowed: false);
            if (entry is FileInfo file)
                RuntimePathUtilities.ThrowIfHardLink(file.FullName, Path.GetRelativePath(root, file.FullName), RuntimeFileArea.GameContent);
            else if (entry is not DirectoryInfo)
                throw InvalidRoot("The SessionRoot contains a non-regular filesystem entry.");
        }
    }

    private static void ValidateProtectedMarker(
        SessionRuntimeLease lease,
        string root,
        SessionRootProtectedMarker marker)
    {
        if (marker.SchemaVersion is not (1 or 2) ||
            !string.Equals(marker.SessionId, lease.Binding.SessionId, StringComparison.Ordinal) ||
            (!string.IsNullOrEmpty(lease.Binding.OwnerUserId) && !string.Equals(marker.OwnerUserId, lease.Binding.OwnerUserId, StringComparison.Ordinal)) ||
            (!string.IsNullOrEmpty(lease.Binding.GameId) && !string.Equals(marker.GameId, lease.Binding.GameId, StringComparison.Ordinal)) ||
            (lease.Binding.SourceContentRevision > 0 && marker.SourceContentRevision != lease.Binding.SourceContentRevision) ||
            (!string.IsNullOrEmpty(lease.Binding.SourceContentDigest) && !string.Equals(marker.SourceContentDigest, lease.Binding.SourceContentDigest, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(lease.Binding.SessionRootManifestDigest) && !string.Equals(marker.MaterializedManifestDigest, lease.Binding.SessionRootManifestDigest, StringComparison.OrdinalIgnoreCase)) ||
            marker.SaveLayout != (RuntimeSaveLayout)lease.Binding.SaveLayout ||
            (!string.IsNullOrEmpty(lease.Binding.RuntimeVersion) && !string.Equals(marker.RuntimeVersion, lease.Binding.RuntimeVersion, StringComparison.Ordinal)))
            throw InvalidRoot("The protected SessionRoot marker does not match the persisted binding.");

        if (!SessionRootProtectedMarkerStore.SameRootIdentity(marker, root))
            throw InvalidRoot("The SessionRoot device or inode changed.");
    }

    private void ValidateProtectedRuntimeManifest(SessionRuntimeLease lease, string root)
    {
        string container = Directory.GetParent(root)?.FullName
            ?? throw InvalidRoot("The SessionRoot container is missing.");
        EnsurePrivateDirectory(container);
        string metadataDirectory = Path.Combine(container, "metadata");
        RuntimePathUtilities.ThrowIfReparsePoint(metadataDirectory, "protected-metadata", RuntimeFileArea.Configuration, missingIsAllowed: false);
        EnsurePrivateDirectory(metadataDirectory);
        using JsonDocument document = JsonDocument.Parse(
            SessionRootProtectedMarkerStore.ReadRuntimeManifest(databaseOptions, lease.Binding.SessionId));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw InvalidRoot("The protected runtime manifest is not an object.");
        string? manifestDigest = ReadString(document.RootElement, "sourceManifestDigest") ?? ReadString(document.RootElement, "manifestDigest");
        if (!string.IsNullOrWhiteSpace(lease.Binding.SessionRootManifestDigest) &&
            !string.Equals(manifestDigest, lease.Binding.SessionRootManifestDigest, StringComparison.OrdinalIgnoreCase))
            throw InvalidRoot("The protected runtime manifest digest does not match its persisted binding.");
        if (TryReadLayout(document.RootElement, out int saveLayout) && saveLayout != lease.Binding.SaveLayout)
            throw InvalidRoot("The protected runtime manifest save layout does not match its persisted binding.");
        string? compatibilityProfile = ReadString(document.RootElement, "compatibilityProfile");
        if (!string.IsNullOrWhiteSpace(compatibilityProfile) &&
            !string.Equals(compatibilityProfile, lease.Binding.CompatibilityProfile, StringComparison.Ordinal))
            throw InvalidRoot("The protected runtime manifest compatibility profile does not match its persisted binding.");
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadLayout(JsonElement root, out int layout)
    {
        layout = 0;
        if (!root.TryGetProperty("saveLayout", out JsonElement value))
            return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out layout))
            return layout is 0 or 1;
        if (value.ValueKind == JsonValueKind.String &&
            Enum.TryParse(value.GetString(), ignoreCase: true, out RuntimeSaveLayout enumLayout) &&
            enumLayout is RuntimeSaveLayout.Root or RuntimeSaveLayout.SavDirectory)
        {
            layout = (int)enumLayout;
            return true;
        }

        return false;
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
            return;
        UnixFileMode mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw InvalidRoot("The SessionRoot directory is not private.");
    }

    private static BindingMetadata ReadMetadata(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<BindingMetadata>(File.ReadAllText(path), JsonOptions)
                ?? throw InvalidRoot("The SessionRoot binding marker is empty.");
        }
        catch (JsonException exception)
        {
            throw InvalidRoot("The SessionRoot binding marker is malformed.", exception);
        }
    }

    private static SessionRuntimeException InvalidRoot(string message, Exception? innerException = null) =>
        new(SessionRuntimeResultCodes.SessionRootInvalid, message, innerException);

    private sealed record BindingMetadata
    {
        public int SchemaVersion { get; init; }
        public string? ManifestDigest { get; init; }
        public RuntimeSaveLayout SaveLayout { get; init; }
    }
}
