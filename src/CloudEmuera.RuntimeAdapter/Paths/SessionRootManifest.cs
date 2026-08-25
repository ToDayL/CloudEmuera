using System.Text;

namespace CloudEmuera.RuntimeAdapter;

public enum SessionRootManifestEntryKind
{
    Directory = 0,
    File = 1
}

/// <summary>
/// One ordinary entry in a published GameContent manifest. Paths are
/// slash-separated and relative to the GameContentRoot.
/// </summary>
public sealed record SessionRootManifestEntry
{
    public SessionRootManifestEntry(
        string relativePath,
        SessionRootManifestEntryKind kind,
        long length,
        string? sha256 = null)
    {
        RelativePath = RuntimeRelativePath.Parse(relativePath).Value;
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (length < 0 || (kind == SessionRootManifestEntryKind.Directory && length != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (kind == SessionRootManifestEntryKind.Directory && !string.IsNullOrEmpty(sha256))
        {
            throw new ArgumentException("A directory manifest entry must not contain a content digest.", nameof(sha256));
        }

        Kind = kind;
        Length = length;
        Sha256 = sha256?.ToUpperInvariant();
    }

    public string RelativePath { get; }

    public SessionRootManifestEntryKind Kind { get; }

    public long Length { get; }

    /// <summary>
    /// Legacy file digest, retained only when reading an old manifest. New
    /// manifests intentionally leave this field null.
    /// </summary>
    public string? Sha256 { get; }

}

/// <summary>
/// Validated publication identity consumed by the SessionRoot materializer.
/// New manifests use an explicit GameId/revision identity. The old digest
/// property remains as a wire-compatible label for legacy SessionRoots; it is
/// never computed from file bytes.
/// </summary>
public sealed class SessionRootPublishedManifest
{
    public SessionRootPublishedManifest(
        IEnumerable<SessionRootManifestEntry> entries,
        string? gameContentIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var materialized = entries.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("A published manifest must contain at least one entry.", nameof(entries));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var caseCollisionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SessionRootManifestEntry entry in materialized)
        {
            if (!seen.Add(entry.RelativePath))
            {
                throw new ArgumentException(
                    "A published manifest contains duplicate or case-distinct colliding paths.",
                    nameof(entries));
            }

            string collisionKey = entry.RelativePath.Normalize(NormalizationForm.FormC);
            if (!caseCollisionKeys.Add(collisionKey))
            {
                throw new ArgumentException(
                    "A published manifest contains duplicate or case-distinct colliding paths.",
                    nameof(entries));
            }

            if (entry.RelativePath.Equals(SessionRootLayoutBuilder.BindingMetadataFileName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The SessionRoot binding metadata name is reserved.",
                    nameof(entries));
            }
        }

        Entries = materialized
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ThenBy(entry => entry.Kind)
            .ToArray();
        ManifestDigest = string.IsNullOrWhiteSpace(gameContentIdentity) ? "path-v2" : gameContentIdentity;
        GameContentIdentity = ManifestDigest;
    }

    public IReadOnlyList<SessionRootManifestEntry> Entries { get; }

    public string ManifestDigest { get; }

    public string GameContentIdentity { get; }

    public static SessionRootPublishedManifest FromDirectory(
        string gameContentRoot,
        string? gameContentIdentity = null)
    {
        string root = RuntimePathUtilities.NormalizeAbsolutePath(gameContentRoot, nameof(gameContentRoot));
        RuntimePathUtilities.ThrowIfReparsePoint(root, "<game-content-root>", missingIsAllowed: false);
        if (!Directory.Exists(root))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.EntryNotFound,
                "The GameContent root does not exist.",
                "<game-content-root>");
        }

        var entries = new List<SessionRootManifestEntry>();
        AddDirectory(root, root, entries);
        return new SessionRootPublishedManifest(entries, gameContentIdentity);
    }

    private static void AddDirectory(
        string root,
        string current,
        ICollection<SessionRootManifestEntry> entries)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(current).EnumerateFileSystemInfos()
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
            RuntimePathUtilities.ThrowIfReparsePoint(entry.FullName, relative, missingIsAllowed: false);
            if (entry is DirectoryInfo)
            {
                entries.Add(new SessionRootManifestEntry(
                    relative,
                    SessionRootManifestEntryKind.Directory,
                    0,
                    string.Empty));
                AddDirectory(root, entry.FullName, entries);
            }
            else if (entry is FileInfo file)
            {
                RuntimePathUtilities.ThrowIfHardLink(file.FullName, relative);
                entries.Add(new SessionRootManifestEntry(
                    relative,
                    SessionRootManifestEntryKind.File,
                    file.Length));
            }
            else
            {
                throw new RuntimeFileAccessException(
                    RuntimePathReasonCodes.UnsupportedRuntimeFile,
                    "A GameContent contains a non-regular filesystem entry.",
                    relative,
                    RuntimeFileArea.GameContent);
            }
        }
    }

}

/// <summary>
/// Limits reserved by the Session manager for one complete copy operation.
/// </summary>
public sealed class SessionRootCopyLimits
{
    public SessionRootCopyLimits(
        long maxFileCount = 100_000,
        long maxDirectoryCount = 100_000,
        long maxTotalBytes = 4L * 1024 * 1024 * 1024,
        long maxSingleFileBytes = 512L * 1024 * 1024)
    {
        if (maxFileCount <= 0 || maxDirectoryCount <= 0 || maxTotalBytes < 0 || maxSingleFileBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileCount));
        }

        MaxFileCount = maxFileCount;
        MaxDirectoryCount = maxDirectoryCount;
        MaxTotalBytes = maxTotalBytes;
        MaxSingleFileBytes = maxSingleFileBytes;
    }

    public long MaxFileCount { get; }

    public long MaxDirectoryCount { get; }

    public long MaxTotalBytes { get; }

    public long MaxSingleFileBytes { get; }
}
